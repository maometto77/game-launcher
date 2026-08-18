#!/usr/bin/env python3
"""Builds a shared catalogue feed from a directory of games.

Scans one folder per game, hashes what it finds, and writes the catalog.json
that Don reads:

    ./build-feed.py --name "The shelf"

Layout it expects, and the only convention it imposes:

    games/
      Quake (1996)/
        quake.zip            the download
        cover.jpg            optional
        screenshot-1.png     optional
        game.json            optional, overrides anything guessed below

The title and year come from the folder name. Everything else is guessed from
the files present, and every guess can be overridden by game.json:

    { "developer": "id Software", "genres": ["Action"], "description": "..." }

Addresses in the feed are relative to the feed itself, so the whole directory
can be moved to another domain without editing a single entry.

Written in Python rather than shell because the two things this does that are
easy to get wrong -- escaping a title into JSON and percent-encoding a filename
into a URL -- are one stdlib call each here, and a subtle bug in a hand-rolled
version would corrupt the catalogue of whoever ran it.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote

# Anything a launcher could plausibly install. Deliberately not a guess at what
# is "the" download: a folder may hold a game, its patch and its soundtrack, and
# the publisher's ordering is preserved rather than second-guessed.
ARCHIVE_SUFFIXES = {
    ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz",
    ".iso", ".bin", ".cue", ".img", ".exe", ".msi",
    ".adf", ".d64", ".dsk", ".nes", ".sfc", ".gb", ".gba", ".n64", ".z64",
}

IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png", ".webp", ".gif"}

TORRENT_SUFFIX = ".torrent"

# "Quake (1996)" -> ("Quake", 1996). The most common way a games folder is
# already named, so most libraries need no metadata files at all.
TITLE_YEAR = re.compile(r"^(?P<title>.+?)\s*[\(\[](?P<year>1[89]\d{2}|20\d{2})[\)\]]\s*$")

CACHE_NAME = ".build-feed-cache.json"


def slug(value: str) -> str:
    """Turns a folder name into a stable identifier."""
    value = value.lower()
    value = re.sub(r"[^a-z0-9]+", "-", value)
    return value.strip("-") or "untitled"


def digest(path: Path, cache: dict, progress: bool) -> str:
    """Returns a file's SHA-256, reusing the cached value when it still applies.

    Re-hashing an untouched library on every run would read every byte of it,
    which for a real shelf is tens of minutes of disk for an answer that has not
    changed. Size and modification time together are enough to notice a file
    being replaced.
    """
    stat = path.stat()
    key = str(path)
    entry = cache.get(key)

    if entry and entry["size"] == stat.st_size and entry["mtime"] == int(stat.st_mtime):
        return entry["sha256"]

    if progress:
        print(f"    hashing {path.name} ({stat.st_size / 1048576:.0f} MB)", flush=True)

    hasher = hashlib.sha256()

    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(chunk)

    value = hasher.hexdigest()
    cache[key] = {"size": stat.st_size, "mtime": int(stat.st_mtime), "sha256": value}

    return value


def relative_url(root: Path, path: Path) -> str:
    """Builds a percent-encoded URL for a file, relative to the feed."""
    parts = path.relative_to(root).parts

    # Encoded per segment: quote() would otherwise escape the separators too and
    # turn a path into one long filename.
    return "/".join(quote(part) for part in parts)


def image_kind(name: str) -> str:
    """Classifies an image by its file name."""
    stem = name.lower()

    if stem.startswith("cover") or stem.startswith("box"):
        return "cover"
    if stem.startswith("hero") or stem.startswith("banner"):
        return "hero"

    return "screenshot"


def read_metadata(folder: Path) -> dict:
    """Reads game.json, if there is one."""
    path = folder / "game.json"

    if not path.is_file():
        return {}

    try:
        with path.open(encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, json.JSONDecodeError) as error:
        print(f"  ! {path}: {error}", file=sys.stderr)
        return {}

    return data if isinstance(data, dict) else {}


def build_entry(folder: Path, root: Path, cache: dict, progress: bool) -> dict | None:
    """Builds one feed entry, or returns None when the folder holds nothing."""
    metadata = read_metadata(folder)

    title = metadata.get("title")
    year = metadata.get("year")

    if not title:
        match = TITLE_YEAR.match(folder.name)

        if match:
            title = match.group("title")
            year = year or int(match.group("year"))
        else:
            title = folder.name

    downloads: list[dict] = []
    images: list[dict] = []

    # Sorted so a rebuild produces the same document when nothing has changed,
    # which keeps the feed diffable and stops a no-op run looking like an update.
    for path in sorted(folder.rglob("*")):
        if not path.is_file() or path.name == "game.json":
            continue

        suffix = path.suffix.lower()

        if suffix in IMAGE_SUFFIXES:
            images.append({"url": relative_url(root, path), "kind": image_kind(path.name)})
            continue

        if suffix == TORRENT_SUFFIX:
            downloads.append({
                "url": relative_url(root, path),
                "fileName": path.name,
                "kind": "torrent",
                "format": "Torrent",
            })
            continue

        if suffix not in ARCHIVE_SUFFIXES:
            continue

        downloads.append({
            "url": relative_url(root, path),
            "fileName": path.name,
            "size": path.stat().st_size,
            "sha256": digest(path, cache, progress),
            "kind": "game",
        })

    if not downloads and not images:
        return None

    # Direct addresses before torrents, because document order becomes the
    # order Don tries them and a torrent needs aria2c, which may not be
    # installed. Alphabetically "doom2.torrent" sorts before "doom2.zip", so
    # without this the one download that can fail outright would be offered
    # first.
    downloads.sort(key=lambda d: d["kind"] == "torrent")

    entry = {
        "id": metadata.get("id") or slug(folder.name),
        "title": title,
    }

    if year:
        entry["year"] = int(year)

    for field in ("description", "developer", "publisher", "requirements", "page"):
        if metadata.get(field):
            entry[field] = metadata[field]

    for field in ("genres", "platforms", "tags"):
        if metadata.get(field):
            entry[field] = list(metadata[field])

    # The newest thing in the folder. Don skips entries whose timestamp has not
    # moved since the last import, so this is what makes a rebuild cheap for
    # everyone reading the feed.
    #
    # Second precision, so that rebuilding an unchanged library produces a
    # byte-identical document. A feed that differs on every run is one nobody
    # can diff to see what actually changed.
    newest = max((p.stat().st_mtime for p in folder.rglob("*") if p.is_file()), default=0)

    entry["updated"] = (
        datetime.fromtimestamp(newest, timezone.utc)
        .isoformat(timespec="seconds")
        .replace("+00:00", "Z")
    )

    if images:
        entry["images"] = images
    if downloads:
        entry["downloads"] = downloads

    return entry


def main() -> int:
    parser = argparse.ArgumentParser(description="Builds a Don shared catalogue feed.")
    parser.add_argument("--games", type=Path, help="Directory of game folders (default: ./games)")
    parser.add_argument("--out", type=Path, help="Where to write the feed (default: ./catalog.json)")
    parser.add_argument("--name", default="Shared catalogue", help="Name shown for this feed")
    parser.add_argument("--quiet", action="store_true", help="Only report the summary")
    arguments = parser.parse_args()

    here = Path(__file__).resolve().parent

    games = (arguments.games or here / "games").resolve()
    out = (arguments.out or here / "catalog.json").resolve()

    # The feed's own directory is what relative addresses resolve against, so
    # every path in the document is built relative to it.
    root = out.parent

    if not games.is_dir():
        print(f"No games directory at {games}", file=sys.stderr)
        return 1

    try:
        games.relative_to(root)
    except ValueError:
        print(
            f"The games directory ({games}) is not inside the feed's directory ({root}),\n"
            "so nothing could be addressed relative to the feed. Move it under the feed, "
            "or point --out somewhere above it.",
            file=sys.stderr,
        )
        return 1

    cache_path = root / CACHE_NAME
    cache: dict = {}

    if cache_path.is_file():
        try:
            with cache_path.open(encoding="utf-8") as handle:
                cache = json.load(handle)
        except (OSError, json.JSONDecodeError):
            # A corrupt cache costs time, not correctness: everything is re-hashed.
            cache = {}

    started = time.monotonic()
    entries = []
    skipped = []

    for folder in sorted(p for p in games.iterdir() if p.is_dir()):
        if not arguments.quiet:
            print(f"  {folder.name}", flush=True)

        entry = build_entry(folder, root, cache, progress=not arguments.quiet)

        if entry is None:
            skipped.append(folder.name)
            continue

        entries.append(entry)

    seen: dict[str, str] = {}

    for entry in entries:
        if entry["id"] in seen:
            print(
                f"  ! '{entry['title']}' and '{seen[entry['id']]}' both reduce to the id "
                f"\"{entry['id']}\". Give one of them an \"id\" in its game.json.",
                file=sys.stderr,
            )
        seen[entry["id"]] = entry["title"]

    feed = {
        "feed": "don-catalog",
        "version": 1,
        "name": arguments.name,
        "updated": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "entries": entries,
    }

    out.parent.mkdir(parents=True, exist_ok=True)

    # ensure_ascii off so titles stay readable in the file; the parser reads
    # either form, but a human editing this should see the title they typed.
    with out.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(feed, handle, indent=2, ensure_ascii=False)
        handle.write("\n")

    with cache_path.open("w", encoding="utf-8") as handle:
        json.dump(cache, handle)

    total = sum(d.get("size", 0) for e in entries for d in e.get("downloads", []))

    print(
        f"\nWrote {out}\n"
        f"  {len(entries)} entries, {total / 1073741824:.1f} GB, "
        f"in {time.monotonic() - started:.1f}s"
    )

    if skipped:
        print(f"  skipped {len(skipped)} folder(s) with nothing in them: {', '.join(skipped[:5])}"
              + (" ..." if len(skipped) > 5 else ""))

    return 0


if __name__ == "__main__":
    sys.exit(main())
