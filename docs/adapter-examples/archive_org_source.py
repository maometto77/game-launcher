#!/usr/bin/env python3
"""Catalogue indexing adapter for the Internet Archive.

Two ways in, one record shape out.

    search("need for speed")   -> matching releases, for a user query
    crawl_library()            -> every release in the collection, as a generator

Both yield the seven-field record the launcher's manifest maps. The shared
machinery -- rate limiting, retries, size parsing, title normalisation, the
record contract, the command line -- lives in `_adapter_base.py`; this file is
only what is specific to the Archive.

--------------------------------------------------------------------------
Running as a launcher transform
--------------------------------------------------------------------------

Named by archive-org-library.yaml, which fetches the first page of results
itself and pipes it here on standard input. This continues from that page,
enriches each item, and writes {"results": [...]} to standard output.

    python archive_org_source.py --uploader you@example.com

Reading page one from stdin rather than fetching it again is deliberate: the
launcher checks the site's robots.txt before its own requests, so letting it
make the first one keeps that gate in the path. With no usable stdin -- run
directly from a shell, say -- page one is fetched here instead.

--------------------------------------------------------------------------
Why no BeautifulSoup
--------------------------------------------------------------------------

The Archive publishes a documented JSON API for exactly this, so there is no
HTML to parse. Scraping rendered pages would be slower, would break on every
redesign, and would produce worse data: the API gives per-file SHA-1 and exact
byte counts, which no amount of HTML parsing recovers.
"""

from __future__ import annotations

import json
import re
import sys
import urllib.parse
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Dict, Iterator, List, Optional, Sequence, Tuple

from _adapter_base import (
    AdapterError,
    AdapterSource,
    CatalogRecord,
    HttpClient,
    build_parser,
    emit,
    make_record,
    normalise_title,
    parse_human_size,
)


#: Offset-paged search.
#:
#: Chosen over ``services/search/v1/scrape`` after the latter was measured
#: re-serving the same page for every cursor: three successive cursor requests
#: returned an identical set of 100 identifiers, and a full walk produced 27,327
#: records from a 327-item collection before the token went stale. This endpoint
#: pages by offset, which is verifiable -- four pages of a 327-item library
#: overlap by zero and union to exactly ``numFound``.
SEARCH_ENDPOINT = "https://archive.org/advancedsearch.php"

METADATA_ENDPOINT = "https://archive.org/metadata"
DOWNLOAD_ENDPOINT = "https://archive.org/download"

#: Fields the list view can return, which is most of a record already.
SEARCH_FIELDS: Tuple[str, ...] = (
    "identifier", "title", "addeddate", "publicdate", "item_size",
)

#: Sort applied to every page.
#:
#: Offset paging is only coherent over a stable order. Without an explicit sort
#: the server is free to return rows in whatever order it likes per request, and
#: page two of an unordered result set may repeat or skip rows from page one.
SEARCH_SORT = "addeddate desc"

#: Rows per page.
PAGE_SIZE = 100

#: Extensions worth offering as a game download. The same set the launcher's
#: own Archive code uses; offering a .xml would queue a metadata file.
GAME_EXTENSIONS: Tuple[str, ...] = (
    ".zip", ".7z", ".rar", ".tar", ".gz", ".iso",
    ".exe", ".img", ".dsk", ".d64", ".adf",
)

_HEX40 = re.compile(r"^[0-9a-f]{40}$")


class ArchiveOrgCatalogSource(AdapterSource):
    """Reads a collection of Archive items as launcher catalogue records."""

    source_name = "archive.org"

    def __init__(
        self,
        uploader: Optional[str] = None,
        collections: Optional[Sequence[str]] = None,
        *,
        media_type: str = "software",
        deep_workers: int = 4,
        client: Optional[HttpClient] = None,
        **client_options: Any,
    ) -> None:
        """Initialise a source.

        Args:
            uploader: Account whose uploads to read, by email address. The
                Archive indexes uploaders by address, so a screen name matches
                nothing.
            collections: Curated collections to read, such as
                ``softwarelibrary_msdos_games``.
            media_type: Pinned so a software collection's stray text and image
                items are left out.
            deep_workers: Concurrent metadata lookups. Four matches what the
                launcher's own Archive source allows itself. They share one rate
                limiter, so this bounds concurrency without loosening politeness.
            client: An existing HTTP client, or ``None`` to build one.
            **client_options: Passed on when building a client.

        Raises:
            ValueError: Neither an uploader nor a collection was given, or
                ``deep_workers`` is below one.
        """
        if not uploader and not collections:
            raise ValueError("Give an uploader, one or more collections, or both.")

        if deep_workers < 1:
            raise ValueError("deep_workers must be at least 1.")

        super().__init__(client=client, **client_options)

        self.uploader = uploader.strip() if uploader else None
        self.collections = [name.strip() for name in (collections or []) if name and name.strip()]
        self.media_type = media_type
        self.deep_workers = deep_workers

    # ------------------------------------------------------------------
    # Public surface
    # ------------------------------------------------------------------

    @property
    def query(self) -> str:
        """The fielded query this source reads.

        Collections and the uploader are combined with OR rather than replacing
        one another, so one pass can cover a curated library and a particular
        person's uploads.
        """
        terms: List[str] = [f'collection:"{name}"' for name in self.collections]

        if self.uploader:
            terms.append(f'uploader:"{self.uploader}"')

        return f"({' OR '.join(terms)}) AND mediatype:{self.media_type}"

    def search(
        self,
        query: str,
        strict_title_match: bool = True,
        *,
        limit: int = 50,
        deep: bool = True,
        **_: Any,
    ) -> List[CatalogRecord]:
        """Find releases matching what someone typed.

        Args:
            query: Free text. Punctuation and case are ignored.
            strict_title_match: Keep only items whose title actually contains
                the query once both are normalised. The Archive's own relevance
                ranking is broad, and a search for one game returning fifty
                loosely related ones is a worse answer than a short list.
            limit: Most records to return.
            deep: Whether to look up each match's files.

        Returns:
            Records, best match first.

        Raises:
            AdapterError: The Archive could not be reached.
        """
        wanted = normalise_title(query) if strict_title_match else ""
        collected: List[Dict[str, Any]] = []

        for document in self._documents(self._search_query(query)):
            if wanted:
                title = str(document.get("title") or document.get("identifier") or "")

                if wanted not in normalise_title(title):
                    continue

            collected.append(document)

            if len(collected) >= max(limit, 0):
                break

        return self._to_records(collected, deep=deep)

    def crawl_library(
        self,
        max_pages: Optional[int] = None,
        *,
        deep: bool = True,
        first_page: Optional[Dict[str, Any]] = None,
        **_: Any,
    ) -> Iterator[CatalogRecord]:
        """Walk the whole collection, yielding records as they are resolved.

        A generator rather than a list, so a caller can begin writing rows while
        later pages are still being fetched. A full library is a few hundred
        items and several hundred requests; holding all of it before emitting
        the first record would make the wait look like a hang.

        Args:
            max_pages: Stop after this many pages. ``None`` reads all of them.
            deep: Whether to look up each item's files.
            first_page: A page already fetched by someone else -- the launcher
                hands one over on standard input -- used rather than requested
                again.

        Yields:
            One record per item, in the order the Archive returned them.

        Raises:
            AdapterError: The Archive could not be reached.
        """
        for page in self._pages(self.query, max_pages=max_pages, first_page=first_page):
            for record in self._to_records(page, deep=deep):
                yield record

    def fetch_metadata(self, identifier: str) -> Optional[Dict[str, Any]]:
        """Read one item's full metadata document.

        Args:
            identifier: The Archive's identifier for the item.

        Returns:
            The document, or ``None`` when the item is gone or unreadable.
        """
        if not identifier:
            return None

        url = f"{METADATA_ENDPOINT}/{urllib.parse.quote(identifier, safe='')}"

        try:
            payload = self.client.get_json(url)
        except AdapterError:
            # One unreadable item must not end a crawl of several hundred.
            return None

        if not isinstance(payload, dict) or not payload.get("files"):
            # A removed item answers with an empty object rather than a 404,
            # which is why presence is checked and not just the status.
            return None

        return payload

    # ------------------------------------------------------------------
    # Record building
    # ------------------------------------------------------------------

    def _to_records(self, documents: Sequence[Dict[str, Any]], *, deep: bool) -> List[CatalogRecord]:
        """Turn list-view documents into finished records.

        Args:
            documents: Documents as the search endpoint returned them.
            deep: Whether to enrich each from its metadata document.

        Returns:
            Records, in the order the documents arrived.
        """
        records: List[CatalogRecord] = []

        for document in documents:
            built = self._base_record(document)

            if built is not None:
                records.append(built)

        if not deep or not records:
            return records

        # Concurrently, because each is an independent request and a few hundred
        # one after another is the difference between a crawl that finishes
        # inside a transform's timeout and one that does not. They share the
        # client's rate limiter, so the site still sees one paced conversation.
        with ThreadPoolExecutor(max_workers=self.deep_workers) as pool:
            documents_by_record = list(pool.map(
                lambda record: self.fetch_metadata(str(record["source_id"])),
                records,
            ))

        for record, metadata in zip(records, documents_by_record):
            if metadata is not None:
                self._apply_metadata(record, metadata)

        return records

    @staticmethod
    def _base_record(document: Dict[str, Any]) -> Optional[CatalogRecord]:
        """Build what the list view alone can tell us.

        Args:
            document: One search result.

        Returns:
            A record, or ``None`` when it has no identifier.
        """
        identifier = _text(document.get("identifier"))

        if not identifier:
            return None

        quoted = urllib.parse.quote(identifier, safe="")

        return make_record(
            title=_first_text(document.get("title")) or identifier,
            url=f"https://archive.org/details/{quoted}",
            source_id=identifier,

            # The whole item, screenshots and metadata included. Replaced by the
            # chosen file's own size once the document is read, because that is
            # what a progress bar should be measured against.
            size_bytes=parse_human_size(document.get("item_size")),
            pub_date=_text(document.get("publicdate")) or _text(document.get("addeddate")) or None,
        )

    def _apply_metadata(self, record: CatalogRecord, document: Dict[str, Any]) -> None:
        """Fill in the address, digest and exact size from a metadata document.

        Args:
            record: The record to complete, modified in place.
            document: The item's metadata document.
        """
        metadata = document.get("metadata")
        metadata = metadata if isinstance(metadata, dict) else {}

        if _first_text(metadata.get("title")):
            record["title"] = _first_text(metadata.get("title"))

        if _text(metadata.get("publicdate")):
            record["pub_date"] = _text(metadata.get("publicdate"))

        # An item the Archive will show but not release. Its addresses answer
        # 403, so offering one would turn a clear explanation into a failed
        # download.
        if _text(metadata.get("access-restricted-item")).lower() == "true":
            return

        chosen = self._pick_file(document.get("files"))

        if chosen is None:
            return

        name = _text(chosen.get("name"))
        identifier = str(record["source_id"])

        record["download_url"] = (
            f"{DOWNLOAD_ENDPOINT}/"
            f"{urllib.parse.quote(identifier, safe='')}/"
            f"{urllib.parse.quote(name, safe='')}"
        )

        digest = _text(chosen.get("sha1")).lower()

        # Only a syntactically valid digest is reported. A field holding
        # "unknown" would fail every transfer with a mismatch that is really a
        # feed typo.
        if _HEX40.match(digest):
            record["sha1"] = digest

        size = parse_human_size(chosen.get("size"))

        if size is not None:
            record["size_bytes"] = size

    @staticmethod
    def _pick_file(files: Any) -> Optional[Dict[str, Any]]:
        """Choose the file that is the game.

        Args:
            files: The ``files`` array from a metadata document.

        Returns:
            The chosen file, or ``None`` when the item holds no game archive.

        Notes:
            The largest original with a game extension. Largest because an item
            frequently carries both the release and a small extra -- a patch, a
            manual, a soundtrack -- and the release is reliably the big one.
            Originals only, because the Archive's derivatives are its own
            re-encodings of those same files and taking both would offer the
            game twice.
        """
        if not isinstance(files, list):
            return None

        candidates: List[Tuple[int, str, Dict[str, Any]]] = []

        for entry in files:
            if not isinstance(entry, dict) or entry.get("source") != "original":
                continue

            name = _text(entry.get("name"))

            if not name or not name.lower().endswith(GAME_EXTENSIONS):
                continue

            candidates.append((parse_human_size(entry.get("size")) or 0, name, entry))

        if not candidates:
            return None

        # Name as the tie-break, so the same item always yields the same choice.
        candidates.sort(key=lambda triple: (-triple[0], triple[1]))

        return candidates[0][2]

    # ------------------------------------------------------------------
    # Enumeration
    # ------------------------------------------------------------------

    def _search_query(self, text: str) -> str:
        """Add a title term to the collection query.

        Args:
            text: What the user typed.

        Returns:
            The fielded query, narrowed by title when there is anything usable.

        Notes:
            An allow-list, not an escape. The index speaks a Lucene-like syntax,
            so a quotation mark in a search box does not merely fail to match --
            it closes the term this is substituted into and opens another, which
            would widen a query the configuration deliberately narrowed.
        """
        safe = re.sub(r"[^0-9A-Za-z\-_'.&]+", " ", text or "").strip()

        return f"{self.query} AND title:({safe})" if safe else self.query

    def _documents(self, query: str) -> Iterator[Dict[str, Any]]:
        """Yield every document a query matches, page by page.

        Args:
            query: The fielded query.

        Yields:
            Documents as the search endpoint returned them.
        """
        for page in self._pages(query, max_pages=None, first_page=None):
            for document in page:
                yield document

    def _pages(
        self,
        query: str,
        *,
        max_pages: Optional[int],
        first_page: Optional[Dict[str, Any]],
    ) -> Iterator[List[Dict[str, Any]]]:
        """Walk the offsets, yielding one page of fresh documents at a time.

        Args:
            query: The fielded query.
            max_pages: Stop after this many pages, or ``None`` for all.
            first_page: A page someone else already fetched.

        Yields:
            The fresh documents of each page, deduplicated across the walk.

        Raises:
            AdapterError: A page could not be read.

        Notes:
            Three independent stopping conditions, because a paginated remote
            endpoint is not owed the benefit of the doubt: the ``numFound`` the
            first page reports, a page that returns nothing, and a page that
            returns only identifiers already seen. The last is what caught the
            cursor endpoint re-serving one page forever, and it stays so that no
            change on the far side can turn a bounded crawl into an unbounded
            one.
        """
        page = first_page if isinstance(first_page, dict) else None
        number = 1
        pages = 0
        seen: set = set()
        expected: Optional[int] = None

        while max_pages is None or pages < max_pages:
            if page is None:
                page = self.client.get_json(self._search_url(query, number))

            documents, found = self._read_page(page)

            if expected is None and found is not None:
                expected = found

            fresh: List[Dict[str, Any]] = []

            for document in documents:
                if not isinstance(document, dict):
                    continue

                identifier = _text(document.get("identifier"))

                if not identifier or identifier in seen:
                    continue

                seen.add(identifier)
                fresh.append(document)

            # Nothing new: the result set is exhausted, or the far side has
            # started repeating itself. Both mean stop, neither is an error.
            if not fresh:
                return

            yield fresh
            pages += 1

            if expected is not None and len(seen) >= expected:
                return

            number += 1
            page = None

    @staticmethod
    def _read_page(page: Any) -> Tuple[List[Any], Optional[int]]:
        """Read the documents and the result count out of one response.

        Args:
            page: A decoded response body.

        Returns:
            The documents, and the total the server reported when it gave one.
        """
        if not isinstance(page, dict):
            return [], None

        response = page.get("response")

        if isinstance(response, dict):
            documents = response.get("docs")
            found = response.get("numFound")

            return (
                documents if isinstance(documents, list) else [],
                found if isinstance(found, int) and found >= 0 else None,
            )

        # The cursor endpoint's shape, still accepted so a page piped in from a
        # manifest pointed at the older address is read rather than discarded.
        items = page.get("items")
        total = page.get("total")

        return (
            items if isinstance(items, list) else [],
            total if isinstance(total, int) and total >= 0 else None,
        )

    @staticmethod
    def _search_url(query: str, page: int) -> str:
        """Build one search request.

        Args:
            query: The fielded query.
            page: One-based page number.

        Returns:
            The absolute address.
        """
        parameters: List[Tuple[str, str]] = [("q", query)]

        # Repeated fl[] rather than one comma-joined value, which is the form
        # this endpoint documents and the only one it reads reliably.
        parameters.extend(("fl[]", field) for field in SEARCH_FIELDS)

        parameters.append(("sort[]", SEARCH_SORT))
        parameters.append(("rows", str(PAGE_SIZE)))
        parameters.append(("page", str(max(page, 1))))
        parameters.append(("output", "json"))

        # Spaces as %20 rather than '+'. Both are accepted here; %20 is used
        # because it is also correct in a path and in a header, so the same
        # encoding is safe everywhere and there is one less rule to remember.
        return (
            f"{SEARCH_ENDPOINT}?"
            f"{urllib.parse.urlencode(parameters, quote_via=urllib.parse.quote)}"
        )


# ----------------------------------------------------------------------
# Field reading
# ----------------------------------------------------------------------


def _text(value: Any) -> str:
    """Read a value the payload may have published as anything."""
    return value.strip() if isinstance(value, str) else ""


def _first_text(value: Any) -> str:
    """Read a field the Archive returns as a list when it has two values."""
    if isinstance(value, list):
        for entry in value:
            if isinstance(entry, str) and entry.strip():
                return entry.strip()

        return ""

    return value.strip() if isinstance(value, str) else ""


# ----------------------------------------------------------------------
# Command line, and the launcher's transform contract
# ----------------------------------------------------------------------


def read_first_page(stream: Any = None) -> Optional[Dict[str, Any]]:
    """Read the page the launcher fetched, when it piped one in.

    Args:
        stream: Where to read from, defaulting to standard input.

    Returns:
        The decoded page, or ``None`` when nothing usable was piped in.

    Notes:
        Anything unreadable is reported on standard error and treated as absent
        rather than fatal. The crawl can fetch page one itself, and a working
        import is worth more than a strict complaint about a pipe.
    """
    source = stream if stream is not None else sys.stdin

    if source is None:
        return None

    try:
        if source.isatty():
            return None
    except (AttributeError, ValueError):
        pass

    raw = source.read()

    if not raw or not raw.strip():
        return None

    try:
        page = json.loads(raw)
    except json.JSONDecodeError as error:
        print(f"stdin was not JSON ({error}); fetching page one instead", file=sys.stderr)
        return None

    if isinstance(page, dict):
        response = page.get("response")

        if isinstance(response, dict) and isinstance(response.get("docs"), list):
            return page

        if isinstance(page.get("items"), list):
            return page

    print("stdin held no page of results; fetching page one instead", file=sys.stderr)

    return None


def main(argv: Optional[Sequence[str]] = None) -> int:
    """Run as a launcher transform or from a shell.

    Args:
        argv: Arguments, or ``None`` to read ``sys.argv``.

    Returns:
        Zero on success, one on a failure worth reporting.
    """
    parser = build_parser(
        "Index an Internet Archive collection as launcher catalogue records.",
        prog="archive_org_source.py",
    )

    parser.add_argument("--uploader", help="Account whose uploads to read, by email address.")
    parser.add_argument(
        "--collection",
        action="append",
        default=[],
        dest="collections",
        metavar="NAME",
        help="A curated collection to read. Repeat for several.",
    )
    parser.add_argument(
        "--workers", type=int, default=4, help="Concurrent metadata lookups. Default 4.",
    )

    arguments = parser.parse_args(argv)

    if not arguments.uploader and not arguments.collections:
        print(
            "give --uploader and/or --collection; "
            "reading the whole Archive is not what this is for",
            file=sys.stderr,
        )
        return 1

    try:
        source = ArchiveOrgCatalogSource(
            uploader=arguments.uploader,
            collections=arguments.collections,
            deep_workers=max(arguments.workers, 1),
            delay=arguments.delay,
            timeout=arguments.timeout,
            retries=arguments.retries,
        )
    except ValueError as error:
        print(str(error), file=sys.stderr)
        return 1

    deep = not arguments.no_deep
    records: List[CatalogRecord] = []

    try:
        with source:
            if arguments.search:
                records = source.search(
                    arguments.search,
                    strict_title_match=not arguments.loose,
                    limit=arguments.limit,
                    deep=deep,
                )
            else:
                for index, record in enumerate(
                    source.crawl_library(
                        max_pages=arguments.max_pages,
                        deep=deep,
                        first_page=read_first_page(),
                    ),
                    start=1,
                ):
                    records.append(record)

                    # Progress on standard error, which the launcher reads at
                    # debug level. Standard out carries the document alone.
                    if index % 100 == 0:
                        print(f"{index} record(s) so far", file=sys.stderr)
    except AdapterError as error:
        print(str(error), file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("interrupted", file=sys.stderr)
        return 1

    with_address = sum(1 for record in records if record["download_url"])
    with_digest = sum(1 for record in records if record["sha1"])

    print(
        f"{len(records)} record(s); {with_address} with an address, {with_digest} with a SHA-1",
        file=sys.stderr,
    )

    emit(records)

    return 0


if __name__ == "__main__":
    sys.exit(main())
