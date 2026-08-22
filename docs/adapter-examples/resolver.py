#!/usr/bin/env python3
"""Example external resolver for a manifest's `sourcing` section.

Named by a manifest like this:

    sourcing:
      strategy: external-script
      script:
        command: python
        args: [resolver.py]
        timeoutSeconds: 30

--------------------------------------------------------------------------
The contract
--------------------------------------------------------------------------

stdin   One JSON object describing the listing to resolve:

            {
              "listingId": "lst_1a2b",
              "title": "Example Game",
              "year": 1993,
              "sourceUrl": "https://example.test/games/example",
              "sourceKey": "example-site"
            }

stdout  One JSON object listing the addresses you found. Nothing else --
        a progress line printed to stdout is part of the document as far as
        the launcher is concerned. Write progress to stderr, which is read
        at debug level and never parsed.

            {"candidates": [
              {
                "url": "https://example.test/files/game.zip",
                "fileName": "game.zip",
                "sizeBytes": 1048576,
                "sha256": "...", "sha1": "...", "md5": "...",
                "mimeType": "application/zip",
                "format": "ZIP",
                "priority": 10
              }
            ]}

exit 0  Anything else is reported as a failed resolution, with stderr.

Only `url` is required. Every other field is passed through when present and
omitted when not -- a checksum you do not have is better absent than invented,
because the launcher verifies whatever you give it.

`priority` orders candidates against each other; higher is tried first. Leave
it out and they are tried in the order you listed them.

--------------------------------------------------------------------------
What a resolver cannot do
--------------------------------------------------------------------------

Return an address and nothing else. Every URL you hand back is checked before
it reaches the download stack: the scheme must be one the launcher fetches,
the host must be one the manifest allows, and private or loopback addresses
are refused unless the manifest opted into them. Running as a program the user
chose is a good reason to let you do the resolving; it is not a reason to let
you nominate an address the manifest's own rules would have refused.

Nothing here downloads anything. A resolver says where a file is; the existing
download stack decides everything after that.
"""

from __future__ import annotations

import json
import sys
from typing import Any, Dict, List
from urllib.parse import urljoin


def resolve(listing: Dict[str, Any]) -> List[Dict[str, Any]]:
    """Work out where one listing can be fetched from.

    Replace the body. This example derives a predictable address from the page
    it was given, which is the shape of a site that serves files from a path
    parallel to its pages.

    Args:
        listing: The object the launcher wrote to standard input.

    Returns:
        Candidate download descriptions, best first.
    """
    page = str(listing.get("sourceUrl") or "")

    if not page:
        return []

    # A real resolver would fetch and read something here. Whatever it does,
    # it returns addresses rather than bytes.
    return [
        {
            "url": urljoin(page.rstrip("/") + "/", "download.zip"),
            "format": "ZIP",
            "priority": 10,
        }
    ]


def main() -> int:
    """Read one listing, write its candidates."""
    raw = sys.stdin.read()

    try:
        listing = json.loads(raw) if raw.strip() else {}
    except json.JSONDecodeError as error:
        # Refused with a non-zero exit so the launcher reports a failed
        # resolution rather than an empty one. "No download found" and "the
        # resolver broke" are different answers and want different fixes.
        print(f"input was not JSON: {error}", file=sys.stderr)
        return 1

    if not isinstance(listing, dict):
        print(f"expected a JSON object, got {type(listing).__name__}", file=sys.stderr)
        return 1

    candidates = resolve(listing)

    print(f"{len(candidates)} candidate(s)", file=sys.stderr)

    # Always a document, even when empty. Writing nothing is reported as a
    # broken hook, whereas "this game has no download" is an ordinary answer.
    json.dump({"candidates": candidates}, sys.stdout, ensure_ascii=False)

    return 0


if __name__ == "__main__":
    sys.exit(main())
