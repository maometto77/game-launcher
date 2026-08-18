# Shared catalogue

A catalogue one person hosts and everyone points at, so a group's launchers all
show the same shelf.

It is the only source in Don that is not somebody else's website. The other
sources read what a site published about files it happens to host; here you host
the files, which is why this is the only source that can state a SHA-256 it
actually computed — and why a download from it is verified rather than merely
hoped for.

---

## Publishing one

Put your games under `games/`, one folder each, and run the generator:

```bash
./build-feed.py --name "The shelf"
```

That writes `catalog.json` beside it. Caddy already serves this directory at
`/feed`, so your friends set **Settings → Discovery → Shared catalogue feed** to:

```
https://your-domain/feed/catalog.json
```

Re-run it whenever you add something. It is cheap to re-run: hashes are cached
by size and modification time, so an untouched library is not read again.

---

## The layout

```
feed/
  build-feed.py
  catalog.json          generated
  games/
    Quake (1996)/
      quake.zip
      cover.jpg
      screenshot-1.png
      game.json         optional
    Doom II [1994]/
      doom2.zip
      doom2.torrent
```

**The folder name is the metadata.** `Quake (1996)` becomes the title *Quake* and
the year *1996*; square brackets work too. Most libraries are already named this
way, which is the point — a shelf of two hundred games should not need two
hundred metadata files.

What the generator does with the rest of a folder:

| | |
|---|---|
| `.zip .7z .rar .iso .exe .adf .d64 …` | A download, hashed and sized |
| `.torrent` | A download, offered **after** the direct ones — it needs aria2c, which the person installing may not have |
| `cover.*`, `box.*` | The tile artwork |
| `hero.*`, `banner.*` | The banner on the details page |
| any other image | A screenshot |
| `game.json` | Overrides all of the above |
| anything else | Ignored |

A folder with no downloads and no images is skipped and reported.

### game.json

Only for what the folder name cannot say. Every field is optional:

```json
{
  "title": "Quake",
  "year": 1996,
  "developer": "id Software",
  "publisher": "GT Interactive",
  "description": "Shooting, but vertical.",
  "genres": ["Action"],
  "platforms": ["DOS"],
  "tags": ["fps", "id-tech"],
  "id": "quake-1996"
}
```

`id` is what ties an entry to the row it produced last time. It is derived from
the folder name unless you set it, so **renaming a folder creates a new entry**
and orphans the old one. Set `id` explicitly on anything you might rename.

---

## Why the addresses are relative

Everything in the generated feed is written relative to `catalog.json`:

```json
{ "url": "games/Quake%20%281996%29/quake.zip", "sha256": "…" }
```

So the whole directory can move to another domain, or be served from a different
path, without editing a single entry. Don resolves them against wherever it
fetched the feed from.

Absolute `https://` addresses work too, and are how you point at a file you do
not host — a mirror, or something already on archive.org.

---

## What a reader is protected from

A feed is remote content, and every address in it gets followed. Two rules
enforced by the parser, both covered by tests:

- **Only `http` and `https`.** Without this a published feed could name
  `file://` and have someone's launcher read from their own machine.
- **A digest that is not a recognised length is dropped, not carried.** Passed
  on, it would fail verification on a file that downloaded perfectly, and the
  reader would be told their download was corrupt when the feed was wrong.

Entries that cannot be read are skipped individually and logged. One malformed
row does not cost a reader the other four hundred.

A feed that is the wrong *kind* of document is refused outright, rather than
parsed to zero entries — otherwise pointing the setting at `release.json`, which
is a real file on the same server, would look exactly like an empty catalogue.

---

## The format

Hand-written feeds are fine; the generator is a convenience, not the definition.

```json
{
  "feed": "don-catalog",
  "version": 1,
  "name": "The shelf",
  "updated": "2026-08-17T22:00:00Z",
  "entries": [
    {
      "id": "quake-1996",
      "title": "Quake",
      "year": 1996,
      "description": "…",
      "developer": "id Software",
      "publisher": "GT Interactive",
      "genres": ["Action"],
      "platforms": ["DOS"],
      "tags": ["fps"],
      "page": "https://example.com/games/quake",
      "updated": "2026-08-01T09:00:00Z",
      "images": [
        { "url": "…", "kind": "cover|hero|screenshot", "width": 600, "height": 800 }
      ],
      "downloads": [
        {
          "url": "…",
          "fileName": "quake.zip",
          "size": 41943040,
          "sha256": "…",
          "kind": "game|manual|extra|torrent",
          "format": "ZIP"
        }
      ]
    }
  ]
}
```

`feed` and each entry's `id` and `title` are required. Everything else is
optional. Numbers may be quoted. `sha1` and `md5` are accepted where a SHA-256 is
not available, and the strongest one present is what gets verified.

**`updated` is worth setting.** Don skips entries whose timestamp has not moved
since its last import, so it is what makes a re-import cheap for everyone reading
your feed. The generator takes it from the newest file in each folder.

Download order is preference order — you know which of your mirrors is nearest,
and nothing on the reader's machine does.

---

## Privacy

`/feed` is served without a directory listing, unlike `/download`. That is not
access control: **anyone who knows the URL can read the catalogue and fetch the
files.** If that matters, put Caddy basic auth in front of the `/feed` route, or
serve it on a name you do not publish.

Nothing here is encrypted and nothing checks who is asking.
