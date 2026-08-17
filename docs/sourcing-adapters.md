# Custom sourcing feeds

The launcher can take download addresses from feeds you describe yourself. A
feed is one file in the adapter directory; no code, no rebuild.

```
%LOCALAPPDATA%\GameLauncher\adapters\
```

Anything named `*.yaml`, `*.yml` or `*.json` there is read at the moment it is
needed. Adding, editing or deleting one takes effect on the next install — there
is nothing to restart.

A manifest that will not parse, or that is missing something required, is
reported in the log and skipped. The others still load. These are files people
edit by hand, so a typo in one is the expected case rather than an exceptional
one.

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
| `match.hosts` | yes | Hosts this feed answers for, matched on suffix — `example.org` also claims `files.example.org`. |
| `match.pathContains` | no | Narrows it further: only addresses containing one of these. |
| `request.url` | yes | What to fetch. A value with no scheme is read from a file beside the manifest. |
| `request.headers` | no | Extra request headers. |
| `format` | no | `json`, `yaml` or `feed` (RSS *and* Atom). Defaults to `json`. |
| `items` | no | Path to the list of downloads in the payload. Empty means the payload is the list. |
| `map.*` | `url` only | Which field of an item supplies each part of a download. |
| `transform` | no | An external program run over the payload first. See below. |

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
| `sha1`, `md5` | Verified after the transfer, by the same code every other download uses. |
| `format` | A label such as `ZIP`. |
| `title` | The game's title, where the feed also names it. |

A checksum that is not 32, 40 or 64 hex characters is discarded rather than
used. A field holding `unknown` or a sentence is worse than an absent one: it
would fail every transfer with a mismatch that is really a feed typo.

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

## Built-in adapters, for comparison

| Adapter | Handles | Notes |
|---|---|---|
| Internet Archive | any `archive.org` address naming an item | Reads the public metadata API. Direct addresses, both node-host mirrors, per-file SHA-1 and MD5, and the item's `.torrent` last. Access-restricted items are explained rather than offered. |
| MyAbandonware | `myabandonware.com` | Refuses. The site's own `robots.txt` disallows `/download/*`, so there is no download it can honestly produce. Checked against the live rules on every attempt, not hardcoded. |
| Custom feeds | whatever your manifests claim | This document. |

When no adapter can supply a download, the launcher looks for the same game
described by another listing in the catalogue and uses its address instead. A
game MyAbandonware describes and the Archive also holds is installable through
the Archive and better described because of MyAbandonware.
