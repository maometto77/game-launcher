# Custom sourcing feeds

The launcher can take download addresses from feeds you describe yourself. A
feed is one file in the adapter directory; no code, no rebuild.

```
%LOCALAPPDATA%\Don\adapters\
```

Anything named `*.yaml`, `*.yml` or `*.json` there is read at the moment it is
needed. Adding, editing or deleting one takes effect on the next install — there
is nothing to restart.

A manifest that will not parse, or that is missing something required, is
reported in the log and skipped. The others still load. These are files people
edit by hand, so a typo in one is the expected case rather than an exceptional
one.

---

## The two halves of a manifest

A manifest answers up to two different questions, and most confusion about them
comes from not noticing there are two:

| Section | Answers |
|---|---|
| `catalog` | What games are there. Fills the Discover grid. |
| `crawler` | The same question, for a site with no feed. See below. |
| `match` / `request` / `map` | Given one of them, what can be downloaded. |
| `sourcing` | The same question, read off the game's own page. See below. |

The first column pairs off: `catalog` and `crawler` both fill the grid, and
`match`/`request`/`map` and `sourcing` both answer Install. Which of each pair
you write depends on whether the site publishes a feed or only pages.

**A manifest with only the second does nothing on its own.** It needs the
catalogue to already hold listings from a host it claims, so a file that looks
correct can sit there having no visible effect. If you are starting from an
empty catalogue, write the `catalog` half first.

Both together is the useful combination for a site nobody else covers: one
section finds the games, the other works out how to fetch them.

### Sites with no feed at all

`crawler:` and `sourcing:` are for the common case where a site publishes pages
rather than a feed. The shortest form is one address:

```yaml
key: my-site
crawler:
  url: https://example.org/games/
```

The crawler infers the listing blocks, the link to each game's page, the
pagination, and the metadata on the page it reaches; `sourcing:` reads the
download addresses off that same page when somebody presses Install. Selector
overrides correct any guess it gets wrong, and every limit, every address check
and `robots.txt` apply exactly as they do here.

**`docs/generic-crawler.md` documents both sections in full** — every field,
the three sourcing strategies, the external-resolver contract, checksums, the
security rules, and what to do when a site does not parse. The rest of this
document is about feeds.

### Filling the catalogue

```yaml
key: my-catalogue
catalog:
  request:
    url: catalog.json        # no scheme: a file beside the manifest
  items: games
  map:
    title: title             # the only required field
    id: id
    year: year
    page: page               # the address a sourcing adapter will dispatch on
    downloadUrl: download
```

| Field | Required | Meaning |
|---|---|---|
| `catalog.enabled` | no | `false` parks this half only. Defaults to `true`. |
| `catalog.request.url` | yes | What to fetch, or a file beside the manifest. |
| `catalog.format` | no | `json`, `yaml` or `feed`. Defaults to `json`. |
| `catalog.items` | no | Path to the list of games. Empty means the payload is the list. |
| `catalog.transform` | no | An external program run over the payload first, same contract as above. |
| `catalog.pageTemplate` | no | Builds a page address from an id — `https://archive.org/details/{id}`. |
| `catalog.map.title` | yes | Everything else is optional. |
| `catalog.map.*` | no | `id`, `year`, `description`, `developer`, `publisher`, `coverUrl`, `page`, `downloadUrl`. |

`page` matters more than it looks. It is the address the sourcing adapters
dispatch on, so a feed that lists nothing but names and Archive item pages still
produces installable games — the built-in Archive adapter finds the files.

`downloadUrl` is optional because of that. A listing with no address of its own
is still offered for installation when an adapter certainly handles the page it
points at — the addresses get worked out at install time. A feed listing nothing
but names and Archive item pages is a complete, installable catalogue.

Point `page` somewhere no adapter recognises and the listing is described but not
offered, the same as any metadata-only source. Mapping `downloadUrl` is what
settles it either way.

### Duplicates

Nothing in a manifest has to worry about them. Titles are normalised —
punctuation, accents, articles, edition markers, version strings and roman
numerals all folded away — and a game your feed names that another source
already described lands on the same card, with both sources badged on it.
Mirrors are additive, so the second feed contributes a fallback address rather
than replacing the first.

That is why `title` is the one required field: it is what the matching is done
on.

---

## The shortest useful manifest

```yaml
key: home-nas
displayName: Home NAS
match:
  hosts: [nas.lan]
request:
  url: https://nas.lan/games/{slug}.json
format: json
items: files
map:
  url: download_url
```

`match.hosts` decides which catalogue listings this feed is asked about;
`request.url` is what gets fetched for one of them; `items` and `map` say where
the addresses are in the answer.

---

## Every field

| Field | Required | Meaning |
|---|---|---|
| `key` | yes | Unique name. Lands on the download row, so you can see which feed supplied a file. |
| `displayName` | no | What a person sees. Defaults to `key`. |
| `enabled` | no | `false` parks a manifest without deleting it. Defaults to `true`. |
| `priority` | no | Where this feed's addresses sit in the mirror list. Defaults to `100`. |
| `match.hosts` | for feeds | Hosts this feed answers for, matched on suffix — `example.org` also claims `files.example.org`. Not needed by a `crawler`-only or `sourcing`-only manifest. |
| `match.pathContains` | no | Narrows it further: only addresses containing one of these. |
| `request.url` | for feeds | What to fetch. A value with no scheme is read from a file beside the manifest. Not needed when `crawler` or `sourcing` does the resolving. |
| `request.headers` | no | Extra request headers. |
| `format` | no | `json`, `yaml` or `feed` (RSS *and* Atom). Defaults to `json`. |
| `items` | no | Path to the list of downloads in the payload. Empty means the payload is the list. |
| `map.*` | `url` only | Which field of an item supplies each part of a download. |
| `transform` | no | An external program run over the payload first. See below. |
| `crawler.*` | no | Crawl a site that publishes no feed. See `docs/generic-crawler.md`. |
| `sourcing.*` | no | Resolve downloads off a game's own page. See `docs/generic-crawler.md`. |

### Paths

One syntax everywhere: full stops walk objects and arrays alike.

```
files.0.name        first file's name
enclosure.@url      an XML attribute
link.@href          an Atom link's target
```

Two conveniences, because publishers are inconsistent about both:

- A path that reaches a single object where you expected a list is treated as a
  list of one.
- An index against something that is not a list still means "the first thing".

A field name containing a full stop is not addressable. There is no escape
syntax; feeds that do this are rare enough that adding one would cost every
other author more than it saved.

### `map`

| Key | Goes to |
|---|---|
| `url` | The download address. The only required one. |
| `fileName` | Suggested file name. Derived from the address when absent. |
| `sizeBytes` | Size, for the progress bar. |
| `sha256`, `sha1`, `md5` | Verified after the transfer, by the same code every other download uses. |
| `format` | A label such as `ZIP`. |
| `title` | The game's title, where the feed also names it. |

A checksum that is not 32, 40, 64 or 128 hex characters is discarded rather than
used. A field holding `unknown` or a sentence is worse than an absent one: it
would fail every transfer with a mismatch that is really a feed typo.

Which of the three you map is only a label: an `md5:` or `sha256:` prefix is
stripped, and the algorithm is inferred from the digest's length when the
transfer is checked. A feed publishing one `checksum` field can be mapped to any
of them. The file name that `sha256sum` prints after the digest is dropped too,
so a field holding a whole line of that output still works.

One implementation decides this for every source — feeds, crawled pages, shared
catalogues and external resolvers alike — because they all feed one verification
path, and disagreeing about what counts as a digest would mean the same
published value verifying a download from one source and failing it from
another.

---

## What an address may be

`http`, `https` and `magnet` — the three the download stack can fetch. Anything
else in a feed is skipped.

A `magnet:` URI, or an address ending `.torrent`, is recorded as a torrent and
handed to aria2c. Those are the same two rules the download service applies when
it picks a transport, so a row classified by a feed reaches aria2 for exactly the
addresses aria2 is needed for. **Torrents need aria2c installed and enabled**; a
launcher without it simply never reaches for them.

`file://` is refused. A feed that could name a local path would turn "add this
manifest" into "copy anything on this machine".

---

## Substitutions in `request.url`

| Token | Becomes |
|---|---|
| `{url}` | The full listing address that matched |
| `{host}` | Its host |
| `{path}` | Its path |
| `{slug}` | Its last path segment |
| `{title}` | The game's title |
| `{year}` | Its release year, or empty |
| `{id}` | The catalogue listing id |

Values are escaped as they are substituted, so a title containing an ampersand
cannot end one query parameter and begin another.

---

## A local catalogue, with no server

`request.url` with no scheme names a file beside the manifest:

```yaml
key: shelf
displayName: The shelf
match:
  hosts: [shelf.local]
request:
  url: shelf.json          # sits in the adapters folder
format: json
items: games
map:
  url: url
  fileName: file
  sha1: sha1
```

Paths that climb out of the adapter directory are refused.

---

## Script hooks

When mapping is not enough, `transform` names a program. It is handed the
fetched payload on standard input and must write JSON to standard output; that
JSON is what `items` and `map` then read.

```yaml
transform:
  command: node
  args: [parse.js]
  timeoutSeconds: 30
```

Lua, JavaScript, Python and compiled binaries all satisfy this, because the
contract is a pipe rather than an embedded interpreter.

**The launcher bundles no scripting engine.** A hook only ever runs a program you
already have, named in a file you wrote. That is not only about dependencies: an
in-process interpreter would run a manifest's code with the launcher's own file
handles, database connection and user token, whereas a child process is a
separate program the operating system can account for and you can see in a task
list. For code arriving from outside the application, the weaker coupling is the
point.

Arguments that name a file in the adapter directory are resolved to it, so
`args: [parse.js]` means the file next to the manifest and keeps working when the
folder moves. A non-zero exit, a timeout or empty output is reported as a failed
feed, not silently ignored.

### When you need one

The mapping language *walks* a payload; it does not build strings. A feed
publishing whole addresses needs no hook. A feed publishing the parts of one —
a file name here, a host there — cannot be mapped declaratively at all, because
there is nowhere to write the join.

That is the usual reason to reach for a script, and it is exactly the Internet
Archive's shape: its metadata endpoint gives `Doom_1993.zip` and the identifier
separately, never the address itself.

`docs/adapter-examples/template-scraper.py` is a commented skeleton of the
contract, whose worked example is that case. Replace one function to point it at
another site.

---

## Rules a feed does not get to skip

`robots.txt` is checked before any HTTP request a manifest makes, exactly as it
is for the sources the launcher ships with. A manifest is your instruction to
this launcher, not a dispensation from the site's.

That is deliberate and it is the whole difference between an extension point and
a way around a decision the rest of the code takes seriously. If a site's rules
disallow the path, the feed is refused with `DisallowedByRobots` and the
launcher looks for the same game somewhere it is allowed to fetch it.

Feeds reading a local file are not subject to this. There is no site to ask.

---

## Worked examples

### A JSON index

```json
{ "files": [
  { "url": "https://cdn.example.org/doom.zip",
    "name": "doom.zip", "size": 2359527,
    "sha1": "dddddddddddddddddddddddddddddddddddddddd" }
] }
```

```yaml
items: files
map:
  url: url
  fileName: name
  sizeBytes: size
  sha1: sha1
```

### An RSS feed of releases

```xml
<rss version="2.0"><channel>
  <item>
    <title>Doom</title>
    <enclosure url="https://cdn.example.org/doom.zip" length="2359527" />
  </item>
</channel></rss>
```

```yaml
format: feed
items: channel.item
map:
  url: enclosure.@url
  sizeBytes: enclosure.@length
  title: title
```

### An Atom feed

```yaml
format: feed
items: entry
map:
  url: link.@href
  title: title
```

Namespaces are ignored, so an Atom `entry` is reached as `entry` — you never
have to write an XML namespace into a YAML file.

### A torrent index

```yaml
items: torrents
map:
  url: magnet          # magnet: URIs are recognised automatically
  title: name
```

---

## Which adapter answers

**All of them.** Every adapter that claims one of a listing's addresses is asked,
and their answers are merged into a single list of mirrors rather than one
winning. A download that dies halfway is the ordinary case for these hosts, and
the transfer only survives it if the next address is already on the row.

`priority` decides the order they are tried in:

| Value | Meaning |
|---|---|
| `100` | The default for a manifest. Ahead of everything built in. |
| `0` | Where every adapter shipped with this launcher sits. |
| `-10` | Behind the built-ins: only reached if they failed. |

So a manifest is an override by default — writing one for a host the launcher
already handles was meant to change what happens, and having to say so twice
would be a poor default. Lowering it below zero turns the same feed into a last
resort, which is what a slow mirror or a home server that is sometimes off is
actually worth.

Equal numbers are broken in favour of your manifest, on the grounds that
something you wrote yourself is the better guess at what you meant.

An address offered by two adapters appears once. Keeping both would have aria2c
retry a URL that just failed and count it as a fallback.

Among manifests, the highest `priority` claims a shared host, and file-name order
breaks a tie.

---

## Built-in adapters, for comparison

Asked after your manifests, in this order.

| Adapter | Handles | Notes |
|---|---|---|
| Internet Archive | any `archive.org` address naming an item | Reads the public metadata API. Direct addresses, both node-host mirrors, per-file SHA-1 and MD5, and the item's `.torrent` last. Access-restricted items are explained rather than offered. |
| MyAbandonware | `myabandonware.com` | Refuses. The site's own `robots.txt` disallows `/download/*`, so there is no download it can honestly produce. Checked against the live rules on every attempt, not hardcoded. |
| Custom feeds | whatever your manifests claim | This document. |
| Manifest sourcing | pages a `sourcing:` block claims | Reads addresses off the game's own page, uses one the catalogue recorded, or asks a program you nominate. See `docs/generic-crawler.md`. |

When no adapter can supply a download, the launcher looks for the same game
described by another listing in the catalogue and uses its address instead. A
game MyAbandonware describes and the Archive also holds is installable through
the Archive and better described because of MyAbandonware.

---

## See also

- `docs/generic-crawler.md` — sites with no feed: the `crawler:` and `sourcing:`
  sections, in full
- `docs/catalog-import-design.md` — how listings become catalogue entries
- `docs/adapter-examples/` — commented manifests and script skeletons
