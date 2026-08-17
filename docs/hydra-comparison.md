# Hydra Launcher — architectural comparison

Audit of [`hydralauncher/hydra`](https://github.com/hydralauncher/hydra) against
this launcher. Read at commit `2632da8` (2026-08-14), release **v4.1.1**.

Every claim below is from that tree or from measurements taken here. File paths
in `code font` are Hydra's unless prefixed with our project name.

---

## 0. Three corrections to the brief

Worth stating up front, because two of them change the analysis.

1. **Hydra uses Ludusavi too.** The brief framed this as "our Ludusavi manifest"
   versus "Hydra Cloud save path resolution". In fact Hydra ships the Ludusavi
   *binary* as an `extraResource`, drives it over its `--api` CLI
   (`src/main/services/ludusavi.ts`), and its Rust module parses the **same
   manifest schema** we do — `rawPath`, `tags`, `when: [{os, store}]`,
   `<winAppData>`, `<home>`. This is not two designs; it is the same data
   source consumed two ways.
2. **`hydra-native` is not the torrent engine.** The Rust crate is cloud saves
   and image processing — hashing, manifest indexing, snapshot building, upload.
   Torrenting is entirely the Python `libtorrent` worker.
3. **Our test count is 493**, not ~414. That figure is several rounds old.

---

## 1. Key architectural differences

| | This launcher | Hydra |
|---|---|---|
| **Runtimes** | One (.NET 8) | Four (Node/Electron, Chromium, Python, Rust `.node` addon) |
| **Torrent engine** | `aria2c` subprocess, one per transfer | Embedded `libtorrent` in a long-lived Python daemon |
| **HTTP engine** | `HttpClient`, or aria2 multi-connection | Bespoke 1,000-line JS downloader, single-connection ranged |
| **Sourcing** | Entirely local: manifests in `adapters/` | Server-side: a URL is POSTed to Hydra's API, which indexes it |
| **Metadata** | Local import into SQLite | Hydra's API (HLTB, catalogue, artwork selection) |
| **Save paths** | Ludusavi manifest parsed in-process | Ludusavi binary shelled out to, plus a Rust manifest indexer |
| **Store** | SQLite + Dapper, FTS5 | LevelDB sublevels |
| **Windows installer** | 172 MB self-contained / 34 MB framework-dependent | 176.9 MB |
| **Dependencies** | 10 NuGet | 78 runtime npm + 38 dev, plus `libtorrent`, `cx_Freeze` |
| **Tests** | 493 | 416 TS cases (73 files) + 129 Rust `#[test]`; **zero Python tests** |

### The difference that matters most

**Hydra is a client for a service; ours is a program.** Download sources, HLTB
times, catalogue metadata and artwork selection all round-trip through
`HydraApi`. `add-download-source.ts` does not parse a feed — it POSTs the URL and
stores the `{id, name, status, downloadCount, fingerprint}` the server returns.

That buys Hydra a great deal: server-side parsing that can be fixed without
shipping a client, deduplication across users, and a "new download options"
badge computed centrally. It costs them the property our launcher is built
around — that everything works with nothing configured and no account.

Neither is wrong. But it means most of Hydra's sourcing engine is not portable
to us even in principle; the interesting parts to copy are elsewhere.

### Scope, stated plainly

Hydra's `hosters/` directory (`gofile`, `mediafire`, `pixeldrain`, `datanodes`,
`fuckingfast`, `vikingfile`, `rootz`) and its debrid integrations (Real-Debrid,
AllDebrid, TorBox, Premiumize) exist to resolve links from repack distribution.
That is the scope our README deliberately excludes. Nothing in section 3 below
recommends adopting it — the recommendations are confined to engineering that
transfers regardless of what is being downloaded.

---

## 2. Comparison matrix

### 2.1 Torrent transport — `aria2c` vs embedded `libtorrent`

| Dimension | Ours (aria2c) | Hydra (libtorrent) | Better |
|---|---|---|---|
| Memory | Zero when idle; process dies with the transfer | Python + libtorrent resident for the app's life | **Ours** |
| Startup | ~200 ms process spawn per transfer | Paid once at launch | **Hydra** |
| Rate limiting | Not implemented | `download_rate_limit` via `apply_settings` | **Hydra** |
| Peer/seed accuracy | `connections` + `numSeeders` polled at 500 ms | `num_peers` + `num_seeds` direct from session | **Hydra**, marginally |
| Upload/seeding | None — `--seed-time=0`, exits on completion | Seeds after completion if the user opts in | **Hydra** |
| Multi-file selection | None — whole torrent or nothing | `prioritize_files` with sanitised indices | **Hydra** |
| Tracker injection | None | 94 hardcoded public trackers appended at a fallback tier | **Hydra** |
| Resume | Needs aria2's `.aria2` control file | libtorrent re-hashes existing pieces; no control file needed | **Hydra** |
| Crash blast radius | One transfer | **Every** transfer — one daemon, all handles lost | **Ours** |
| Testability | Stub process, deterministic | Untested (zero Python tests) | **Ours** |

**The resume difference is the sharpest.** Our design has a real fragility: kill
aria2 without letting it write `.aria2` and the next attempt restarts from zero.
That is precisely why the last round added a graceful `aria2.shutdown` before
killing. libtorrent needs none of that — it verifies pieces against the data
already on disk, so *any* death is recoverable. Our mitigation is good; their
property is better.

**The crash difference cuts the other way.** `python-rpc.ts` runs one daemon for
everything. `handleProcessExit` rejects every pending request and nulls the
process; the next call respawns it. A libtorrent segfault during one download
takes all of them down at once. Our per-transfer isolation means a failure is
contained to the file that caused it — which is the whole reason the current
design was chosen, and the audit does not disturb it.

**One honest correction to our own docs.** Our design note says the RPC daemon
approach means "owning a long-lived background process, a port, and a secret".
Hydra shows a middle path we did not consider: the Python worker talks over
**stdin/stdout** with an `rpc_password` in each request, no socket at all. That
removes the port and the port-collision risk entirely. It is a better transport
than a loopback HTTP port for a child process we own — though aria2c does not
offer it, so it is not available to us without changing engines.

### 2.2 Save detection

Both parse the same Ludusavi manifest. The differences are in what they do
after parsing.

| | Ours | Hydra |
|---|---|---|
| Manifest access | Downloaded and parsed in-process (`LudusaviSavePathResolver`) | Ludusavi binary shelled out to with `--api` |
| Manifest source | Ludusavi upstream | Upstream **disabled**; a secondary at `cdn.losbroxas.org` |
| Placeholders | 15 handled; `<storeUserId>` returns null | Dedicated `identity/` module resolving store user IDs |
| Native env vars | `%APPDATA%`, `%USERPROFILE%` expanded, and a path with an *unresolvable* one is rejected rather than half-expanded | Left to Ludusavi |
| Unicode | Not normalised | NFC/NFD normalisation, separator folding (`hashing/aggregate.rs`) |
| Overlapping custom paths | Not handled | `custom_path_overlap.rs` detects and reconciles |
| Wine/Proton prefixes | Not handled | `--wine-prefix` passed through |
| Sync | None — resolution only | Full cloud sync: blake3 hashes, snapshots, upload |

**Hydra's save handling is materially more mature than ours**, and it is not
close. `<storeUserId>` alone matters: rules like
`<winAppData>/Sekiro/<storeUserId>/S0000.sl2` are common, and we return null for
them today. The Unicode normalisation is the kind of detail you only write after
being bitten — a `Café` folder can be NFC or NFD on disk, and comparing the two
naively reports a save as changed on every scan.

Against that, two things are ours. We parse the manifest in-process in ~4
seconds and depend on no external binary, where Hydra ships a Ludusavi
executable per platform and copies it into user data on first run. And on the
brief's specific question about `%APPDATA%` and `%USERPROFILE%`: we expand
native environment variables ourselves *and* reject a path still containing an
unresolvable one, rather than handing a half-expanded string to a file API.
Hydra delegates that to Ludusavi entirely, which is reasonable but means the
behaviour is not theirs to fix.

Note their `manifest.enable: false`. Hydra has switched *off* the upstream
Ludusavi manifest in favour of a CDN they control. Whatever the reason, it means
their save coverage is only as current as that mirror.

### 2.3 Sourcing and extension model

Not comparable as like for like — theirs is a server, ours is a file format.

| | Ours | Hydra |
|---|---|---|
| Where parsing happens | Client, in `FeedDownloadMapper` | Hydra's servers |
| Extension unit | A YAML/JSON manifest in `adapters/` | A URL submitted to the API |
| Formats | JSON, YAML, RSS, Atom via one node tree | Whatever the server accepts |
| Custom code | External process, stdin/stdout | None client-side; `hosters/` are compiled in |
| Works offline | Yes, entirely | No |
| Fixable without a release | Yes, by editing a file | Yes, by the vendor |
| `robots.txt` | Enforced on every manifest fetch | Not applicable client-side |

Ours is more flexible for an individual and useless for a community. Theirs is
the reverse. The one concrete idea worth stealing is their **`fingerprint`**
field on a download source — a cheap way to know a feed changed without
re-parsing it, which our `FeedManifestStore` currently approximates with
directory timestamps.

### 2.4 Footprint

The measurement that most contradicted my expectation:

| | Size |
|---|---|
| Hydra v4.1.1 Windows installer | **176.9 MB** |
| Ours, self-contained `win-x64` | **172 MB** |
| Ours, framework-dependent | **34 MB** (needs .NET 8 Desktop Runtime) |

**A self-contained WPF build is the same size as an Electron app that embeds
Chromium, Node, a frozen Python and a Rust addon.** WPF does not trim, so the
whole framework ships. The "our stack is leaner" claim only holds if we
distribute framework-dependent and require the runtime — a real 5× win, but one
that pushes an install step onto the user.

Where we are genuinely leaner is *running*: one process tree, no Chromium
renderer, no Python interpreter resident, and no IPC hop between a renderer and
a main process for every operation.

---

## 3. What Hydra does better

Ordered by what I would actually adopt.

### 3.1 Torrent file selection

`get-torrent-files.ts` fetches the file list from metadata, and
`download-settings-modal.tsx` lets the user tick what they want;
`_set_selected_file_priorities` then sets libtorrent priorities to zero for
everything unselected. We download the whole torrent or none of it. For an
Internet Archive item bundling a game with a 2 GB video walkthrough, that is a
real cost to the user.

### 3.2 Tracker injection

94 public trackers appended to every magnet, at a tier *below* the ones the
magnet carried (`_build_add_torrent_params`). Cheap, and the difference between
a dead magnet and a working one for older content. Notably their list includes
`bt1.archive.org` and `bt2.archive.org` — the exact trackers our Archive.org
torrents announce to.

### 3.3 Seeding after completion

Opt-in `seedAfterDownloadComplete`. We hard-code `--seed-time=0` and exit. For a
launcher pulling from the Internet Archive — which explicitly asks large
downloads to use torrents to spare its servers — giving back is the polite
default we currently cannot offer at all.

### 3.4 Download rate limiting

`set_download_limit` applies `download_rate_limit` to the libtorrent session. We
have no throttle anywhere: a download saturates the connection and there is no
setting to stop it. aria2 supports `--max-overall-download-limit`, so this is a
setting and one argument away.

### 3.5 A speed chart, and artwork cache-control

`SpeedChart` in `download-group.tsx` plots recent throughput per download; our
Downloads table shows an instantaneous number. Separately,
`steam-grid-db-cache.ts` rewrites `Cache-Control` on SteamGridDB image responses
to `public, max-age=259200` — overriding the origin's headers to force a 3-day
local cache. That is a neat trick for artwork that never changes.

### 3.6 Save-path identity resolution

Covered in 2.2 and worth repeating as an adoption item: `<storeUserId>` support
and Unicode normalisation are the two gaps that would most improve our save
resolver.

---

## 4. Top five engineering takeaways

1. **Add torrent file selection.** aria2 supports `--select-file` with index
   ranges, and `.torrent` metadata gives us the file list. This is the largest
   user-visible gap and the mechanism is already there.
2. **Inject a tracker list into magnets and Archive torrents.** Small, contained,
   and the difference between a working download and a stalled one.
3. **Offer seeding and a rate limit.** Two aria2 arguments and two settings.
   Seeding in particular is owed to the Archive, whose torrents we consume.
4. **Close the `<storeUserId>` and Unicode gaps in the save resolver.** Our
   expander returns null for a placeholder that appears in real rules, and we
   compare paths without normalising them.
5. **Adopt a source fingerprint.** A content hash per feed, stored beside the
   manifest, would let us skip re-parsing unchanged sources and detect a changed
   one that kept its timestamp.

Deliberately **not** on this list: adopting a server-side sourcing API, the
file-host resolvers, or debrid integration. The first would break the offline
guarantee; the others are outside our stated scope.

---

## 5. Verdict

### Download system — **our model holds up, with two named gaps**

Per-transfer `aria2c` isolation is defensible and, on crash resilience, better
than a shared daemon whose death takes every transfer with it. The JSON-RPC work
closed the statistics gap that motivated this comparison: we now report peers,
seeds, real totals and real rates, which is what libtorrent was giving them.

Two things they genuinely have that we do not, both fixable without changing
engines: **file selection within a torrent**, and **seeding**. A third — resume
that survives an unclean kill — is inherent to libtorrent and not something aria2
can match; our graceful-shutdown mitigation narrows it but does not close it.

The one design idea worth borrowing is their **stdio transport**. A loopback
port with a secret is more machinery than a pipe to a process we already own.
aria2 does not offer stdio control, so this is filed as a reason to revisit
engines if we ever outgrow aria2 — not a change to make now.

### Save system — **theirs is better and we should catch up**

We were both right to build on Ludusavi's manifest; that is the strongest
validation in this audit. But Hydra does more with it: store-identity
resolution, Unicode-safe comparison, overlap detection, Wine prefixes, and a
full sync pipeline on top.

Our resolver is sound and dependency-free where theirs ships a binary. It is also
plainly less complete. Items 4 in the takeaways is the cheapest way to narrow
that, and cloud sync is a much larger question about whether this launcher wants
a server at all — which, so far, it has deliberately not.

### On testing

We have 493 tests against one runtime. Hydra has 545 across three, and **none at
all** for the Python torrent worker — the component most likely to break and the
hardest to debug when it does. Our stub-driven transport tests, which exercise
process launch, RPC polling and fallback without needing aria2 installed, are a
genuine advantage worth keeping as the download layer grows.
