#!/usr/bin/env python3
"""Process-scraper template for a Don sourcing feed.

Copy this file into %LOCALAPPDATA%\\Don\\adapters\\ next to the manifest that
names it, and point that manifest at it:

    transform:
      command: python
      args: [template-scraper.py]
      timeoutSeconds: 30

--------------------------------------------------------------------------
The contract
--------------------------------------------------------------------------

stdin   The payload the manifest's `request.url` returned, verbatim.
stdout  JSON. Nothing else -- a progress line printed to stdout is part of
        the document as far as the launcher is concerned. Write progress to
        stderr instead, which is read at debug level and never parsed.
exit 0  Anything else is reported as a failed feed, along with stderr.

Whatever this writes is what the manifest's `items` and `map` then read, so
the two have to agree. This template emits:

    {"results": [ {title, file_name, download_url, sha1, md5, checksum,
                   size_bytes, format}, ... ]}

which the companion manifest reads as `items: results`.

--------------------------------------------------------------------------
One record is one address
--------------------------------------------------------------------------

The mapper builds one download row per record, from that record's single
`download_url`. So an item's `.torrent` is emitted as its own record rather
than as a `torrent_url` field beside the HTTP address -- there is no second
slot on a row for it to occupy.

Torrent records are emitted last, and that ordering is load-bearing: feed
order becomes mirror rank, and a torrent only works when aria2c is installed
and enabled. Ranked first it would be the address an install reached for
before the HTTP one that always works. The built-in Archive adapter orders
its rows the same way, for the same reason.

--------------------------------------------------------------------------
Adapting this to another site
--------------------------------------------------------------------------

Replace `scrape`. Everything above it is the contract and everything below
it is plumbing; that one function is the part that knows about a site. The
standard library is all that is used here on purpose -- the launcher bundles
no interpreter and installs no packages, so a hook that needed `requests`
would be a hook that ran on your machine and nobody else's.
"""

from __future__ import annotations

import json
import sys
import urllib.parse

# Extensions worth offering as a game download. The same list the launcher's
# own Archive code uses; a scraper that offered .xml would queue a metadata
# file for installation.
GAME_EXTENSIONS = (
    ".zip", ".7z", ".rar", ".tar", ".gz", ".iso",
    ".exe", ".img", ".dsk", ".d64", ".adf",
)

DOWNLOAD_ENDPOINT = "https://archive.org/download"


def scrape(payload: dict) -> list[dict]:
    """Turn one fetched payload into normalised records.

    The worked example is the Internet Archive, because it is the case this
    template ships for. Two payload shapes are recognised, because the two
    Archive endpoints worth pointing a manifest at return different things:

    * `https://archive.org/metadata/{identifier}` -- the full file list, with
      per-file sizes and checksums. This is the good one: real addresses, and
      integrity verification for free.

    * `https://archive.org/advancedsearch.php?q=...&output=json` -- search
      results, which carry identifiers and no file list at all. All that can
      be built from an identifier without a second request is the item's
      `.torrent`, whose address is deterministic. That is a real answer, not
      a guess, but it needs aria2c to be of any use.
    """
    if "response" in payload:
        return _from_search(payload)

    return _from_metadata(payload)


def _from_metadata(payload: dict) -> list[dict]:
    """Read an /metadata/{identifier} document."""
    metadata = payload.get("metadata") or {}

    identifier = _text(metadata.get("identifier"))
    if not identifier:
        return []

    # An item the Archive will show but not release. Its addresses answer 403,
    # so offering them would turn a clear explanation into a failed download.
    if _text(metadata.get("access-restricted-item")).lower() == "true":
        return []

    title = _text(metadata.get("title")) or identifier
    files = payload.get("files") or []

    records: list[dict] = []
    torrents: list[dict] = []

    for entry in files:
        if not isinstance(entry, dict):
            continue

        name = _text(entry.get("name"))
        if not name:
            continue

        if name.endswith("_archive.torrent"):
            # Checked before the filter below, not after it. The Archive
            # publishes the item's torrent with source "metadata" rather than
            # "original", so a torrent looked for among the originals is never
            # found -- on a real item, though not in every captured fixture.
            #
            # The size and digests are deliberately dropped: they belong to the
            # .torrent file itself, not to what it delivers, and reporting them
            # would have the download service verify the pointer instead of the
            # payload.
            torrents.append(_record(
                title=title,
                file_name=name,
                url=f"{DOWNLOAD_ENDPOINT}/{_quote(identifier)}/{_quote(name)}",
                sha1="",
                md5="",
                size="",
                fmt="Torrent",
            ))
            continue

        # Derivatives are the Archive's own re-encodings of the originals.
        # Taking both would offer the same game twice.
        if entry.get("source") != "original":
            continue

        if not name.lower().endswith(GAME_EXTENSIONS):
            continue

        records.append(_record(
            title=title,
            file_name=name,
            url=f"{DOWNLOAD_ENDPOINT}/{_quote(identifier)}/{_quote(name)}",
            sha1=_text(entry.get("sha1")),
            md5=_text(entry.get("md5")),
            size=_text(entry.get("size")),
            fmt=_text(entry.get("format")),
        ))

    # Some items opt out of torrents, and the flag is the Archive saying so.
    if records and _text(metadata.get("noarchivetorrent")).lower() != "true":
        records.extend(torrents)

    return records


def _from_search(payload: dict) -> list[dict]:
    """Read an advancedsearch.php document."""
    docs = ((payload.get("response") or {}).get("docs")) or []

    records = []

    for doc in docs:
        if not isinstance(doc, dict):
            continue

        identifier = _text(doc.get("identifier"))
        if not identifier:
            continue

        item = _quote(identifier)

        records.append(_record(
            title=_first(doc.get("title")) or identifier,
            file_name=f"{identifier}_archive.torrent",
            url=f"{DOWNLOAD_ENDPOINT}/{item}/{item}_archive.torrent",
            sha1="",
            md5="",
            size="",
            fmt="Torrent",
        ))

    return records


def _record(*, title, file_name, url, sha1, md5, size, fmt) -> dict:
    """Build one normalised record."""
    # `checksum` carries whichever digest is stronger, algorithm-prefixed. The
    # launcher strips the prefix and infers the algorithm from the digest's
    # length, so a manifest can map this one field instead of picking between
    # sha1 and md5 -- both are emitted as well, for manifests that would
    # rather be explicit.
    checksum = f"sha1:{sha1}" if sha1 else (f"md5:{md5}" if md5 else None)

    return {
        "title": title,
        "file_name": file_name,
        "download_url": url,
        "sha1": sha1 or None,
        "md5": md5 or None,
        "checksum": checksum,
        "size_bytes": _number(size),
        "format": fmt or None,
    }


def _text(value) -> str:
    """Read a value the payload may have published as anything."""
    return value.strip() if isinstance(value, str) else ""


def _first(value) -> str:
    """Read a field the Archive returns as a list whenever it has two values."""
    if isinstance(value, list):
        return _text(value[0]) if value else ""

    return _text(value)


def _number(value) -> int | None:
    """Read a size the Archive publishes as a string."""
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _quote(value: str) -> str:
    """Escape one path segment."""
    return urllib.parse.quote(value, safe="")


def main() -> int:
    raw = sys.stdin.read()

    try:
        payload = json.loads(raw)
    except json.JSONDecodeError as error:
        # Named on stderr and refused with a non-zero exit, so the launcher
        # reports a failed feed rather than an empty one. "The feed returned
        # nothing" and "the feed returned something unreadable" are different
        # problems and want different fixes.
        print(f"payload was not JSON: {error}", file=sys.stderr)
        return 1

    if not isinstance(payload, dict):
        print(f"expected a JSON object, got {type(payload).__name__}", file=sys.stderr)
        return 1

    results = scrape(payload)

    print(f"{len(results)} record(s)", file=sys.stderr)

    # Always a document, even when empty. Writing nothing at all is reported
    # as a broken hook, and "this item has no download" is an ordinary answer
    # rather than a breakage.
    json.dump({"results": results}, sys.stdout, ensure_ascii=False)

    return 0


if __name__ == "__main__":
    sys.exit(main())
