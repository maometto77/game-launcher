# Crawling a site you have no feed for

Most sites publish no feed. They publish pages: a listing of games, a page per
game, a "next" link at the bottom. The crawler reads those pages the way a
person reading them would, and turns them into catalogue listings.

The whole of a working manifest can be one address:

```yaml
key: my-site
crawler:
  url: https://example.org/games/
```

Everything else in this document exists to correct a guess that address was not
enough to get right on one particular site.

---

## Where this sits

Two questions, answered by two halves of the system, joined by the catalogue:

```
              what games are there?              where can this be fetched?
              ---------------------              --------------------------

  a website
      |
      v
  GenericWebCrawler
      |
      v
  SourceListing
      |
      v
  CatalogImportService
      |
      v
  CatalogListing  ------ someone presses Install ------> ListingInstallService
                                                                  |
                                                                  v
                                                        DownloadSourceResolver
                                                                  |
                                                                  v
                                                           ISourcingAdapter
                                                        (ManifestSourcingAdapter)
                                                                  |
                                                                  v
                                                         DownloadCandidate(s)
                                                                  |
                                                                  v
                                                           DownloadService
                                                                  |
                                                                  v
                                                       InstallFromUrlService
                                                    (verify, extract, detect)
```

The line down the middle is the important one. **The crawler never downloads a
game.** It produces listings with no addresses on them at all, and the addresses
are worked out later by the sourcing half — usually at the moment somebody
presses Install, on the one game they actually wanted.

That split is why a crawl of five thousand games costs five thousand page reads
rather than ten thousand, and why an address that has gone stale since the
import is noticed at install time instead of being served out of a database.

Both halves are described in one manifest, and either half alone is a complete,
useful file:

| Section | Answers | Feeds |
|---|---|---|
| `crawler:` | What games are there | The Discover grid |
| `sourcing:` | Given one, what can be fetched | The Install button |

A site can be worth indexing without being somewhere anything is fetched from,
and a site can supply downloads for listings some other source found. Writing
only the half you need is the expected case.

Nothing here is a second download system. A resolved candidate becomes an
ordinary `ListingDownload` on an ordinary row, and every transfer, resume,
checksum and install after that is the code the rest of the launcher already
used.

---

## Getting one running

1. Drop a `.yaml` file into the adapter directory:

   ```
   %LOCALAPPDATA%\Don\adapters\
   ```

2. Turn discovery on under **Settings → Discovery**, and press **Refresh** on
   the Discover page.

3. Look at the source list in that same section. Each source names itself, says
   whether it is ready, and says what its last pass did — how many items it saw,
   or that it found nothing.

There is nothing to rebuild and nothing to restart. Files are read when they are
needed, so editing one and pressing Refresh again is the whole edit loop.

`docs/adapter-examples/crawled-site.yaml` is a commented copy of everything
below. It ships with `enabled: false`, because the address in it is not a real
site.

---

## What the crawler works out on its own

Given only a starting address, it looks for:

| Thing | How |
|---|---|
| The repeated block, one per game | The most common repeated container holding a link and a heading |
| The link to a game's own page | The heading's link, preferred over any other link in the block |
| The next listing page | `link[rel=next]`, then `a[rel=next]`, then a `.next`/`.pagination` convention, then a link whose text says so |
| Title | JSON-LD `name`, then OpenGraph `og:title`, then the page's `<h1>`, then `<title>` with the site's name trimmed off |
| Description | JSON-LD `description`, then `og:description`, then `meta[name=description]`, then the first substantial paragraph |
| Cover image | JSON-LD `image`, then `og:image`, then the most prominent image that is not an icon or a sprite |
| Screenshots | A gallery near the cover |
| Date and year | `<time datetime>`, JSON-LD `datePublished`, then a four-digit year beside a label such as "Released" |
| Genres and tags | A tag list, a `genre` property, or a labelled row |
| Developer, publisher, platforms | Labelled rows — `<dl>`/`<dt>`/`<dd>`, a two-cell `<tr>`, or plain "Label: value" prose |
| Source address | The page it was read from, canonicalised |
| Stable source id | Derived from host, path and query, with the scheme and fragment dropped |

Structured data wins over guesswork: a page carrying JSON-LD or OpenGraph is
read from those, because a publisher who wrote them meant them. Heuristics are
the fallback, not the first move.

Genres are mapped through the same vocabulary the rest of the catalogue uses, so
a site's "Shoot 'em up" lands on the same genre as another site's "Shmup".
Anything unrecognised is kept as a tag rather than discarded.

**The stable id is why re-importing is safe.** The same page yields the same id
on every pass, so a second crawl updates the listings the first one created
rather than duplicating them — and the catalogue's own title matching folds a
game two different sites both describe onto one card with both sources badged.

### When a guess is wrong

Name a selector. Each one corrects one guess, and you never have to describe the
rest of the page:

```yaml
crawler:
  url: https://example.org/games/
  selectors:
    item: "article.game"
    detailLink: "h2 a"
    nextPage: "a.next"
    title: "h1.game-title"
    description: ".game-description"
    cover: "img.box-art"
    screenshots: ".gallery img"
    date: "time.released"
    genres: ".tags a"
    developer: ".developer"
    publisher: ".publisher"
    platforms: ".platforms"
    requirements: ".sysreq"
```

These are ordinary CSS selectors, evaluated the way a browser's
`querySelectorAll` evaluates them. Work one out in your browser's inspector and
paste it in. A selector that does not parse is ignored and the crawler falls
back to inference for that field, rather than failing the whole crawl.

Every selector is optional, including the block as a whole.

---

## Every `crawler:` field

| Field | Default | Meaning |
|---|---|---|
| `url` | — | **Required.** Where the crawl starts. |
| `enabled` | `true` | `false` parks this half without deleting the file. |
| `maxPages` | `100` | Listing and detail pages read in one pass. Ceiling `10000`. |
| `maxItems` | `5000` | Games emitted in one pass. Ceiling `200000`. |
| `maxDepth` | `2` | How far from the starting page links are followed. Ceiling `50`. |
| `concurrency` | `1` | Requests in flight at once. Ceiling `8`. |
| `delayMilliseconds` | `1000` | Minimum gap between requests. Raised, never lowered, by the site's own `Crawl-delay`. |
| `timeoutSeconds` | `30` | Per request. |
| `retries` | `3` | Attempts on a timeout, a 429 or a 5xx, backing off exponentially to 15s. |
| `maxPageBytes` | `4194304` | Largest page read. Ceiling `16777216`. |
| `readDetailPages` | `true` | Read each game's own page as well as the listing. |
| `allowedHosts` | the start host | Widen the crawl to a CDN or a second domain. |
| `allowPrivateHosts` | `false` | Permit private and loopback addresses. |
| `selectors` | inferred | The overrides above. |

Out-of-range values are **clamped, not rejected**. `maxPages: 999999` becomes
`10000` and the crawl runs; a manifest is a file somebody typed, and refusing to
start over an ambitious number would be the wrong trade.

### `readDetailPages`

On, a crawl reads the listing and then each game's own page — which is where the
description, the cover, the developer and the date actually live. Off, it reads
listing pages only, and produces listings carrying a title and a link.

Turning it off makes an import dramatically faster and dramatically thinner.
That is the right trade for a first look at an unfamiliar site: run it off, see
whether the titles are right, then turn it on.

### Depth, and what it means

Depth 0 is the starting page. Depth 1 is a page linked from it. Pagination does
**not** consume depth — a "next" link is the same listing continued, not a step
away from it — so the default of 2 covers a paginated listing and the game pages
hanging off it, which is the shape of most sites.

---

## Limits, and why every one of them is finite

A crawler follows links written by somebody else. "The site will stop
eventually" is not a design: a paginator that always offers a next page and a
category tree that reaches the whole domain are ordinary bugs on ordinary sites,
not adversarial behaviour.

So every dimension has a ceiling that a manifest cannot raise:

| Guard | What it stops |
|---|---|
| `maxPages`, `maxItems`, `maxDepth` | A crawl that never ends |
| Already-walked page detection | A paginator whose "next" link points back into the sequence |
| Canonicalised addresses | The same page counted twice as `/a`, `/a?`, `/a#top`, `//host/a` |
| `maxPageBytes`, enforced while streaming | A huge page, and a small page that decompresses to a huge one |
| Declared `Content-Length` checked first | Starting to read a body that already said it was too big |
| Content-type check | Downloading a disc image because a link claimed to be a page |
| Max links per page (500) | A page offering an enormous number of links |
| Max consecutive barren pages (5) | Grinding through a site that has started refusing everything |
| Per-request timeout, bounded retries, exponential backoff | One slow page stalling a pass |
| Script output cap (512 KB) | A resolver that writes forever |
| Script timeout | A resolver that hangs |

A crawl that hits a limit **stops cleanly and keeps what it found**. Its cursor
is recorded, so the next pass resumes from the page it stopped on rather than
starting over.

### Resuming and incremental passes

Each item carries the address of the page it was found on. That page — not the
item — is the cursor, so resuming replays one listing page rather than skipping
past whatever it had not finished emitting.

A cursor is only resumed when the pass covers exactly one crawler manifest.
Several manifests share one source key, and a cursor from one of them means
nothing to the others.

Every pass records what it did: pages fetched, pages failed, items found, items
skipped, duplicates skipped, links refused, robots refusals and retries. A pass
that fetched pages and produced no items is reported as unhealthy rather than as
a success with nothing in it, which is the difference between "the site has
nothing new" and "the selectors stopped matching".

---

## Rules a manifest does not get to skip

**`robots.txt` is honoured on every request, and no setting overrides it.**

The rules are fetched from the site and checked before each fetch, including
before the first. A disallowed path is not crawled, the refusal is counted and
logged, and the crawl carries on with the paths that are allowed. A site's
`Crawl-delay` raises the manifest's delay; it never lowers it.

There is no `ignoreRobots` field and there will not be one. An extension point
that could turn that off would not be an extension point — it would be a way
around a decision the rest of the launcher takes seriously, reachable by editing
a text file.

Everything else follows from the same principle:

- **Addresses are checked before they are fetched, every time.** `http` and
  `https` only. `file://` is refused, so a manifest can never turn "add this
  file" into "read anything on this machine". `magnet:` is refused for sourcing
  unless the manifest sets `allowMagnet: true`, because it needs an external
  engine that may not be installed.
- **Private and loopback addresses are refused** unless `allowPrivateHosts` is
  set: `127.0.0.0/8`, `10/8`, `172.16–31`, `192.168`, `169.254`, `100.64/10`,
  the IPv6 loopback, link-local, unique-local and IPv4-mapped ranges, and
  `.local` / `.internal` / `.localhost` names. Crawled HTML is untrusted input,
  and a link is the cheapest way to ask a program to fetch something behind the
  firewall it happens to be inside. This is a check on the literal address, not
  a DNS lookup — a hostname that resolves into a private range is not caught
  here, and the launcher makes no claim that it is.
- **A redirect is a new address**, re-checked against the same policy, with
  automatic redirects capped at five.
- **The crawl is confined to the starting page's host** unless `allowedHosts`
  says otherwise. Images are the one relaxation: a cover on a CDN is accepted
  when the scheme and privacy checks pass, because splitting images onto another
  host is what sites do.
- **No JavaScript from a crawled page is ever executed.** The crawler is a
  parser (AngleSharp), not a browser: it does not run scripts, does not fetch
  subresources, and cannot be made to. A page that only renders under script is
  a page this crawler does not read, and that is the correct outcome rather than
  a gap to fill with a headless browser.
- **A script hook is a child process, never an embedded interpreter**, and the
  addresses it returns go through the same checks as addresses found in HTML. A
  resolver cannot hand back something the crawler itself would have refused, and
  it cannot reach the launcher's file handles, database connection or token.

### What this is for

Sources where indexing and downloading are permitted: a project's own release
pages, a self-hosted repository, an open-source or freeware archive, a
preservation collection, a shelf of files on a server you run. Point it at a
site you are entitled to index.

---

## Resolving a download

The `sourcing:` block answers the second question. It runs when somebody presses
Install, on one game.

```yaml
sourcing:
  enabled: true
  strategy: direct-link
  resolution: lazy
  priority: 100
```

| Field | Default | Meaning |
|---|---|---|
| `enabled` | `true` | `false` parks this half. |
| `strategy` | `direct-link` | How addresses are found. Below. |
| `resolution` | `lazy` | When they are found. Below. |
| `priority` | `100` | Where these addresses sit in the merged mirror list. |
| `allowedHosts` | the page's host | A separate file host. The page's own host is always allowed. |
| `allowPrivateHosts` | `false` | As the crawler's. |
| `allowMagnet` | `false` | Permit `magnet:` addresses. |
| `selectors` | inferred | Below. |
| `script` | — | Required by `external-script`. |

Names are forgiving: `direct-link`, `directLink`, `DirectLink` and `direct_link`
are the same value. An unrecognised one is reported with the valid options
named, rather than silently defaulting to something you did not ask for.

### The three strategies

**`direct-link`** — read the addresses off the game's own page. The page is
fetched through the same stack the crawler uses, so robots.txt applies. Without
selectors it looks for links naming an archive (`.zip`, `.7z`, `.rar`, `.iso`,
`.exe`, `.tar.gz`, and the rest), then for links whose wording says download,
and for a checksum and a size printed beside each one — in the same list item,
table row, paragraph or definition. At most 12 candidates come off one page.

**`mapped-field`** — use the address the catalogue already recorded, from a feed
or from a crawl that captured one. No request is made to find it, but it is
re-checked against the address policy before use: a stored address is still
untrusted input, and the policy may have changed since it was stored.

**`external-script`** — ask a program you nominate. For a site whose addresses
are assembled rather than published: a file name here, a host there, an id in a
third place. The mapping language walks a payload; it does not build strings,
and this is the escape hatch for when that is not enough.

```yaml
sourcing:
  strategy: external-script
  script:
    command: python
    args: [resolver.py]
    timeoutSeconds: 30
```

The program is handed one JSON object on standard input:

```json
{
  "listingId": "lst_1a2b",
  "title": "Doom",
  "year": 1993,
  "sourceUrl": "https://example.org/games/doom",
  "sourceKey": "my-site"
}
```

and writes one on standard output:

```json
{
  "candidates": [
    {
      "url": "https://files.example.org/doom.zip",
      "fileName": "doom.zip",
      "sizeBytes": 2359527,
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "format": "ZIP",
      "priority": 10
    }
  ]
}
```

`listingId` is opaque — the catalogue's own handle for the game, useful to echo
back in a log and meaningless to interpret. `title`, `year` and `sourceUrl` are
what a resolver actually works from.

`url` is the only required key in the answer. `sha1`, `md5` and `mimeType` are
accepted too.
Exit zero. A non-zero exit, a timeout, empty output, unparseable output, or more
than 512 KB of it, is reported as a failed resolution — the adapter declines and
the launcher looks for the game somewhere else.

Arguments naming a file in the adapter directory resolve to it, so
`args: [resolver.py]` means the file next to the manifest and keeps working when
the folder moves.

`docs/adapter-examples/resolver.py` is a working example of this contract.

### `lazy` against `eager`

**Lazy is the default and is almost always right.** A catalogue of several
thousand games costs several thousand extra page fetches to answer eagerly, to
answer a question about the one game somebody eventually clicks — and the
addresses would be stale by the time they did.

Eager is worth it for a small shelf where seeing the file size before clicking
is useful. With `direct-link` it is nearly free: the detail page is already open
and parsed during the import, so the candidates come off it at no extra request.
With the other two strategies, eager means real extra work per game.

### Selectors, when the guessing is wrong

```yaml
sourcing:
  selectors:
    downloadLink: "a.download-button"
    checksum: ".checksum"
    sha256: ".sha256"
    sha1: ".sha1"
    md5: ".md5"
    size: ".filesize"
    fileName: ".filename"
```

`size` reads `1.4 GB`, `700 MB`, `512 KiB` and the rest — both the decimal and
the binary conventions.

### Checksums

A checksum found on a page — or returned by a script — reaches exactly the
verification every other download in the launcher uses. Nothing separate was
built for this.

- SHA-256 is preferred, then SHA-1, then MD5. A page publishing all three is
  verified against the strongest.
- A `sha256:` or `md5:` prefix is stripped, and a trailing file name after the
  digest is dropped, because that is how `sha256sum` output looks.
- A value that is not 32, 40, 64 or 128 hexadecimal characters is **discarded**.
  A field holding `unknown`, `n/a` or a sentence is worse than an absent one: it
  would fail every transfer with a mismatch that is really a typo on a web page.
- **Nothing is ever invented.** A page that publishes no checksum produces a
  download with none, which transfers unverified, the same as any other
  unverified source. A fabricated digest would be worse than an honest absence.

### Which candidate is used

All of them, in order. Every adapter that claims a listing's address is asked,
and their answers are **merged into one list of mirrors** rather than one
winning. A transfer that dies halfway only survives if the next address is
already on the row.

Ordering, highest first:

| Value | Meaning |
|---|---|
| `100` | A manifest's default. Ahead of everything built in. |
| `0` | Where every adapter shipped with the launcher sits. |
| `-10` | Behind the built-ins: reached only if they failed. |

Within one manifest's answer, a candidate's own `priority` orders it against its
siblings. An address offered twice appears once. An adapter with nothing to say
declines and the next one is asked; declining is ordinary and is not an error.

---

## A whole site, start to finish

```yaml
key: retro-shelf
displayName: Retro Shelf
enabled: true
priority: 100

crawler:
  url: https://retro.example.org/games/
  maxPages: 200
  maxItems: 2000
  delayMilliseconds: 1500
  readDetailPages: true
  allowedHosts: [cdn.retro.example.org]
  selectors:
    item: "li.game-card"
    detailLink: "a.game-link"
    nextPage: "a[rel=next]"

sourcing:
  enabled: true
  strategy: direct-link
  resolution: lazy
  priority: 100
  allowedHosts: [files.retro.example.org]
  selectors:
    downloadLink: "a.dl"
    sha256: "code.sha256"
    size: "span.size"
```

That is a complete source. No C# was written, nothing was rebuilt, and the games
appear in Discover badged `Retro Shelf` with the Install button live.

---

## Adding a source without touching the code

The whole procedure:

1. Open the site's listing page in a browser.
2. Copy its address into a new `.yaml` file in the adapter directory, under
   `crawler:` / `url:`.
3. Press Refresh. Look at Discover.
4. If the titles are right, add `sourcing:` and try Install on one game.
5. If something is wrong, open the inspector, work out the selector for the
   thing that is wrong, and name it. Only that one.

There is no registration step, no interface to implement and no build. A file
appearing in the folder is the registration.

---

## When it does not work

**Discover is empty after a refresh.**
Open **Settings → Discovery** and read the source list. A source that says *Not
configured* has no enabled manifest with a `crawler.url`. A source that says
*nothing found* ran and read no items, which is the next case.

**The pass ran and found nothing.**
The listing container was not recognised. Name `item` and `detailLink`
explicitly. If the page renders its list with JavaScript, no selector will help
— the crawler does not run scripts, and that site needs a feed or a script hook
instead.

**A source is flagged as needing attention.**
It read items and stored none, which is the shape of a site whose markup changed
under a selector that used to work. Compare `item` against the page as it is now.

**Pagination links imported as games.**
The crawler excludes links inside navigation, headers, footers and pagination
containers, but a site can put its paginator somewhere unusual. Name `item` to
scope the search to the real blocks.

**Only the first page was read.**
The next link was not recognised. Name `nextPage`. If the site paginates by
script rather than by link, there is nothing to follow.

**The log says a path was disallowed.**
The site's `robots.txt` refuses it. That is the end of it — index a part of the
site that is allowed, or a different site.

**Install says no download could be resolved.**
Either there is no `sourcing:` block, or the page's addresses were not
recognised. Name `downloadLink`. Check the log for addresses that were refused:
a file host that is not the page's own host needs naming in `allowedHosts`, and
a magnet needs `allowMagnet: true`.

**A crawl is slower than expected.**
`delayMilliseconds` is a floor, and the site's own `Crawl-delay` can raise it. A
polite crawl of a large site takes as long as it takes; `maxPages` bounds one
pass, and the next resumes where it stopped.

---

## See also

- `docs/sourcing-adapters.md` — feed-based manifests, for sites that publish one
- `docs/catalog-import-design.md` — how listings become catalogue entries
- `docs/adapter-examples/crawled-site.yaml` — a commented manifest
- `docs/adapter-examples/resolver.py` — a working external resolver
