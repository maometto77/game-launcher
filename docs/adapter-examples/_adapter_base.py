#!/usr/bin/env python3
"""Shared machinery for launcher catalogue adapters.

Everything here is source-agnostic: the bits every adapter needs and none of
the bits that know about a particular site. Import it, subclass
:class:`AdapterSource`, and implement the two methods that actually differ.

    from _adapter_base import AdapterSource, CatalogRecord, make_record

    class MySource(AdapterSource):
        source_name = "my-source"

        def search(self, query, strict_title_match=True, **kw):
            ...

        def crawl_library(self, max_pages=None, **kw):
            ...

--------------------------------------------------------------------------
What it provides
--------------------------------------------------------------------------

* :func:`make_record` -- the seven-field record contract, in one place, so a
  typo in one adapter cannot quietly produce a differently shaped row.
* :func:`normalise_title` -- punctuation-agnostic comparison form.
* :func:`parse_human_size` -- "1.8 GB", "from 14.5 GB", "2359527", 2359527.
* :class:`RateLimiter` -- one shared, thread-safe minimum interval.
* :class:`HttpClient` -- ``requests`` when installed, the standard library when
  not, with retries, exponential back-off, ``Retry-After``, and the empty-200
  case that a busy endpoint answers with instead of a 429.
* :func:`build_parser` / :func:`emit` -- the common command line and the
  stdout contract the launcher's ``transform`` hook expects.

--------------------------------------------------------------------------
Why the standard library fallback
--------------------------------------------------------------------------

The launcher bundles no interpreter and installs no packages, so a hook that
required ``requests`` would run on its author's machine and nobody else's.
``requests`` is used when it is there because its session handling is better;
nothing here depends on it being there.

Tests: ``python -m unittest discover -s docs/adapter-examples -p 'test_*.py'``
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import threading
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

try:  # pragma: no cover - whichever branch this machine has is the tested one
    import requests
except ImportError:
    requests = None  # type: ignore[assignment]


__all__ = [
    "AdapterError",
    "AdapterSource",
    "CatalogRecord",
    "DESKTOP_USER_AGENT",
    "HttpClient",
    "RateLimiter",
    "RECORD_FIELDS",
    "build_parser",
    "emit",
    "make_record",
    "normalise_title",
    "parse_human_size",
]


DESKTOP_USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Don/1.0"
)

#: The record contract, in declaration order.
#:
#: Named here rather than repeated per adapter so that adding a field is one
#: edit and a missing field is a test failure instead of a listing that quietly
#: imports without a checksum.
RECORD_FIELDS: Tuple[str, ...] = (
    "title", "url", "download_url", "sha1", "size_bytes", "pub_date", "source_id",
)

CatalogRecord = Dict[str, Any]

#: Multipliers for human-readable sizes. Decimal for the plain units because
#: that is what sites print next to "GB"; binary for the explicit ones.
_SIZE_UNITS: Dict[str, int] = {
    "b": 1,
    "kb": 1000,
    "mb": 1000 ** 2,
    "gb": 1000 ** 3,
    "tb": 1000 ** 4,
    "kib": 1024,
    "mib": 1024 ** 2,
    "gib": 1024 ** 3,
    "tib": 1024 ** 4,
}

_SIZE_PATTERN = re.compile(
    r"(?P<value>\d+(?:[.,]\d+)?)\s*(?P<unit>[kmgt]i?b|b)\b",
    re.IGNORECASE,
)

_ARTICLES: Tuple[str, ...] = ("the ", "a ", "an ")

_HEX40 = re.compile(r"^[0-9a-f]{40}$")


class AdapterError(RuntimeError):
    """Raised when a source cannot be read at all."""


# ----------------------------------------------------------------------
# Field helpers
# ----------------------------------------------------------------------


def normalise_title(value: Optional[str]) -> str:
    """Reduce a title to the form titles are compared in.

    Lowercased, de-accented, stripped of punctuation and a leading article, and
    with the spaces removed.

    Removing the spaces is the part that matters. It is what makes
    ``"need for speed"`` a plain substring of ``"Need for Speed: The Run"`` --
    a word-by-word comparison against a colon-separated subtitle does not give
    you that, and neither does stripping punctuation alone.

    Args:
        value: A title as a source published it.

    Returns:
        The comparison form, possibly empty.
    """
    if not value:
        return ""

    folded = unicodedata.normalize("NFKD", value.casefold())
    folded = "".join(char for char in folded if not unicodedata.combining(char))

    for article in _ARTICLES:
        if folded.startswith(article):
            folded = folded[len(article):]
            break

    return re.sub(r"[^a-z0-9]+", "", folded)


def parse_human_size(value: Any) -> Optional[int]:
    """Convert a size into whole bytes.

    Handles the shapes these payloads actually contain: an integer, a string of
    digits, and a human-readable phrase. A phrase naming a range -- "from 14.5
    GB" -- yields the first figure, because the smallest complete download is
    the one a caller can rely on being able to finish.

    Args:
        value: A number, a digit string, or a phrase containing one.

    Returns:
        Bytes, or ``None`` when nothing numeric could be read.
    """
    if isinstance(value, bool):
        # bool is an int subclass, and True is not a size.
        return None

    if isinstance(value, int):
        return value if value >= 0 else None

    if isinstance(value, float):
        return int(value) if value >= 0 else None

    if not isinstance(value, str):
        return None

    text = value.strip()

    if not text:
        return None

    if text.isdigit():
        return int(text)

    match = _SIZE_PATTERN.search(text)

    if match is None:
        return None

    amount = float(match.group("value").replace(",", "."))

    return int(amount * _SIZE_UNITS.get(match.group("unit").lower(), 1))


def make_record(
    *,
    title: str,
    url: str,
    source_id: str,
    download_url: Optional[str] = None,
    sha1: Optional[str] = None,
    size_bytes: Optional[int] = None,
    pub_date: Optional[str] = None,
) -> CatalogRecord:
    """Build one record in the launcher's schema.

    Args:
        title: The game's name. Required.
        url: The page this came from. Required.
        source_id: Stable identifier within the source. Required.
        download_url: A direct address, when there is one.
        sha1: A hex SHA-1. Anything that is not forty hex characters is
            dropped, because a field holding "unknown" fails every transfer
            with a mismatch that is really a source's typo.
        size_bytes: Size of what ``download_url`` points at.
        pub_date: When the entry was published.

    Returns:
        A record with every contract field present.

    Raises:
        ValueError: A required field is blank.
    """
    if not title or not title.strip():
        raise ValueError("title is required.")

    if not url or not url.strip():
        raise ValueError("url is required.")

    if not source_id or not str(source_id).strip():
        raise ValueError("source_id is required.")

    digest = str(sha1).strip().lower() if sha1 else ""

    return {
        "title": title.strip(),
        "url": url.strip(),
        "download_url": download_url or None,
        "sha1": digest if _HEX40.match(digest) else None,
        "size_bytes": parse_human_size(size_bytes),
        "pub_date": pub_date.strip() if isinstance(pub_date, str) and pub_date.strip() else None,
        "source_id": str(source_id).strip(),
    }


# ----------------------------------------------------------------------
# Rate limiting and transport
# ----------------------------------------------------------------------


class RateLimiter:
    """A shared minimum interval between requests.

    Notes:
        Locked, and the sleep happens inside the lock. That is deliberate: it
        serialises callers, which is what one shared rate limit means. Holding
        the lock only long enough to compute a delay and sleeping outside it
        would let every worker in a pool sleep concurrently and then fire
        together, so the limit would be observed by each thread individually
        and by the site not at all.
    """

    def __init__(self, delay: float) -> None:
        """Initialise a limiter.

        Args:
            delay: Minimum seconds between the start of two requests.

        Raises:
            ValueError: The delay is negative.
        """
        if delay < 0:
            raise ValueError("delay cannot be negative.")

        self.delay = delay
        self._lock = threading.Lock()
        self._last = 0.0

    def wait(self) -> None:
        """Block until the caller's turn."""
        if self.delay <= 0:
            return

        with self._lock:
            remaining = self.delay - (time.monotonic() - self._last)

            if remaining > 0:
                time.sleep(remaining)

            self._last = time.monotonic()


class HttpClient:
    """Fetches documents, politely and with retries.

    One instance owns one session and one rate limit, so a crawl of several
    hundred pages is a single well-behaved conversation with the site rather
    than several hundred unrelated ones.
    """

    def __init__(
        self,
        *,
        user_agent: str = DESKTOP_USER_AGENT,
        accept: str = "application/json",
        delay: float = 0.34,
        timeout: float = 30.0,
        retries: int = 3,
        max_backoff: float = 30.0,
        sleeper: Any = time.sleep,
    ) -> None:
        """Initialise a client.

        Args:
            user_agent: Sent on every request.
            accept: ``Accept`` header value.
            delay: Minimum seconds between requests.
            timeout: Per-request timeout.
            retries: Attempts per request before giving up.
            max_backoff: Longest a single back-off may wait.
            sleeper: Injected for tests, so a back-off costs no real time.

        Raises:
            ValueError: A tuning argument is outside a usable range.
        """
        if timeout <= 0:
            raise ValueError("timeout must be positive.")

        if retries < 1:
            raise ValueError("retries must be at least 1.")

        self.user_agent = user_agent
        self.accept = accept
        self.timeout = timeout
        self.retries = retries
        self.max_backoff = max_backoff
        self.limiter = RateLimiter(delay)
        self._sleep = sleeper

        self._session: Any = None

        if requests is not None:
            self._session = requests.Session()
            self._session.headers.update({"User-Agent": user_agent, "Accept": accept})

    # -- public ------------------------------------------------------

    def get_json(self, url: str) -> Any:
        """Fetch and decode one JSON document.

        Args:
            url: The absolute address to read.

        Returns:
            The decoded document.

        Raises:
            AdapterError: Every attempt failed, or the body was not JSON.
        """
        body = self.get_text(url)

        try:
            return json.loads(body)
        except json.JSONDecodeError as error:
            raise AdapterError(f"{url} returned unreadable JSON: {error}") from error

    def get_text(self, url: str) -> str:
        """Fetch one document as text, retrying what is worth retrying.

        Args:
            url: The absolute address to read.

        Returns:
            The body.

        Raises:
            AdapterError: Every attempt failed.
        """
        last = "no attempt was made"

        for attempt in range(1, self.retries + 1):
            self.limiter.wait()

            try:
                body, status, retry_after = self.get(url)
            except Exception as error:  # noqa: BLE001 - transports differ, handling does not
                last = f"{type(error).__name__}: {error}"
                self.back_off(attempt, None)
                continue

            if status == 200:
                if body and body.strip():
                    return body

                # An empty 200 is how a busy endpoint sheds load: it answers,
                # but with nothing. Worth retrying -- it is a rate limit
                # wearing a success code.
                last = "empty body (the endpoint is shedding load)"
                self.back_off(attempt, retry_after)
                continue

            last = f"HTTP {status}"

            # 429 and 5xx are worth another try. A 404 or a 403 is an answer.
            if status != 429 and not 500 <= status < 600:
                raise AdapterError(f"{url} returned HTTP {status}.")

            self.back_off(attempt, retry_after)

        raise AdapterError(f"{url} could not be read after {self.retries} attempts ({last}).")

    def get(self, url: str) -> Tuple[str, int, Optional[float]]:
        """Perform one GET, with whichever transport is available.

        Args:
            url: The absolute address.

        Returns:
            The body, the status code, and any ``Retry-After`` in seconds.
        """
        if self._session is not None:
            response = self._session.get(url, timeout=self.timeout)

            return response.text, response.status_code, self.retry_after(response.headers)

        request = urllib.request.Request(
            url,
            headers={"User-Agent": self.user_agent, "Accept": self.accept},
        )

        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                charset = response.headers.get_content_charset() or "utf-8"

                return response.read().decode(charset, errors="replace"), response.status, None
        except urllib.error.HTTPError as error:
            charset = (error.headers.get_content_charset() or "utf-8") if error.headers else "utf-8"

            return (
                error.read().decode(charset, errors="replace"),
                error.code,
                self.retry_after(error.headers),
            )

    def back_off(self, attempt: int, retry_after: Optional[float]) -> None:
        """Wait before retrying.

        Args:
            attempt: Which attempt just failed, counting from one.
            retry_after: What the server asked for, when it said.

        Notes:
            The server's own figure wins when it gives one: a 429 carrying
            ``Retry-After`` is the site saying exactly what it wants, and
            guessing something shorter is how a rate limit becomes a ban.
            Capped either way, so a mistaken header cannot stall a crawl.
        """
        if retry_after is not None:
            self._sleep(min(max(retry_after, 0.0), 60.0))
            return

        base = self.limiter.delay if self.limiter.delay > 0 else 0.5

        self._sleep(min(base * (2 ** attempt), self.max_backoff))

    @staticmethod
    def retry_after(headers: Any) -> Optional[float]:
        """Read a ``Retry-After`` expressed in seconds.

        Args:
            headers: The response headers.

        Returns:
            Seconds, or ``None`` when absent or given as a date.
        """
        if headers is None:
            return None

        try:
            raw = headers.get("Retry-After")
        except AttributeError:
            return None

        if not raw:
            return None

        try:
            return float(str(raw).strip())
        except ValueError:
            # The header also permits an HTTP date. The caller's own back-off
            # handles that well enough without a date parser.
            return None

    def close(self) -> None:
        """Release the session."""
        if self._session is not None:
            self._session.close()
            self._session = None

    def __enter__(self) -> "HttpClient":
        return self

    def __exit__(self, *_: Any) -> None:
        self.close()


# ----------------------------------------------------------------------
# Source base
# ----------------------------------------------------------------------


class AdapterSource:
    """Base for a source that can be searched and crawled.

    Subclasses implement :meth:`search` and :meth:`crawl_library`. Everything
    else -- the client, the rate limit, the record shape, the filter -- is
    here.
    """

    #: Name used in messages. Subclasses should override it.
    source_name = "adapter"

    def __init__(self, client: Optional[HttpClient] = None, **client_options: Any) -> None:
        """Initialise a source.

        Args:
            client: An existing client to use, or ``None`` to build one.
            **client_options: Passed to :class:`HttpClient` when building one.
        """
        self.client = client if client is not None else HttpClient(**client_options)

    def search(
        self,
        query: str,
        strict_title_match: bool = True,
        **options: Any,
    ) -> List[CatalogRecord]:
        """Find entries matching what someone typed.

        Args:
            query: Free text.
            strict_title_match: Whether to keep only real title matches.
            **options: Subclass-specific.

        Returns:
            Records, best match first.

        Raises:
            NotImplementedError: Always, on the base class.
        """
        raise NotImplementedError

    def crawl_library(self, max_pages: Optional[int] = None, **options: Any) -> Iterable[CatalogRecord]:
        """Walk the whole source.

        Args:
            max_pages: Stop after this many pages, or ``None`` for all.
            **options: Subclass-specific.

        Returns:
            Records, in the order the source returned them.

        Raises:
            NotImplementedError: Always, on the base class.
        """
        raise NotImplementedError

    @staticmethod
    def matches(query: str, title: str) -> bool:
        """Determine whether a title matches a query, ignoring punctuation.

        Args:
            query: What the user typed.
            title: A candidate title.

        Returns:
            ``True`` when it matches, and for an empty query always.
        """
        wanted = normalise_title(query)

        return not wanted or wanted in normalise_title(title)

    def filter_titles(self, query: str, records: Sequence[CatalogRecord]) -> List[CatalogRecord]:
        """Keep only the records whose titles match a query.

        Args:
            query: What the user typed.
            records: Candidates.

        Returns:
            The matching records, in their original order.
        """
        return [record for record in records if self.matches(query, str(record.get("title") or ""))]

    def close(self) -> None:
        """Release the client."""
        self.client.close()

    def __enter__(self) -> "AdapterSource":
        return self

    def __exit__(self, *_: Any) -> None:
        self.close()


# ----------------------------------------------------------------------
# Command line and output
# ----------------------------------------------------------------------


def build_parser(description: str, *, prog: Optional[str] = None) -> argparse.ArgumentParser:
    """Build the command line every adapter shares.

    Args:
        description: Shown in ``--help``.
        prog: Program name, or ``None`` to let argparse decide.

    Returns:
        A parser carrying the common options. Add source-specific ones to it.
    """
    parser = argparse.ArgumentParser(prog=prog, description=description)

    parser.add_argument("--search", metavar="TEXT", help="Return matches for this text.")
    parser.add_argument(
        "--crawl",
        action="store_true",
        help="Walk the whole source instead of searching.",
    )
    parser.add_argument(
        "--loose",
        action="store_true",
        help="With --search, keep the source's own broader ranking.",
    )
    parser.add_argument(
        "--max-pages", type=int, default=None, help="Stop a crawl after this many pages.",
    )
    parser.add_argument(
        "--limit", type=int, default=50, help="Most records to return from --search. Default 50.",
    )
    parser.add_argument(
        "--delay", type=float, default=0.34, help="Seconds between requests. Default 0.34.",
    )
    parser.add_argument(
        "--timeout", type=float, default=30.0, help="Per-request timeout. Default 30.",
    )
    parser.add_argument(
        "--retries", type=int, default=3, help="Attempts per request. Default 3.",
    )
    parser.add_argument(
        "--no-deep",
        action="store_true",
        help="Skip detail lookups: faster, but no digests and coarser sizes.",
    )

    return parser


def emit(records: Sequence[CatalogRecord], stream: Any = None) -> None:
    """Write records in the shape the launcher's transform contract expects.

    Args:
        records: The records to write.
        stream: Where to write, defaulting to standard output.

    Notes:
        Always a document, even when empty. Writing nothing at all is reported
        by the launcher as a broken hook, whereas "this source has no games" is
        an ordinary answer that should import cleanly as zero rows.
    """
    json.dump(
        {"results": list(records)},
        stream if stream is not None else sys.stdout,
        ensure_ascii=False,
    )
