# Catalog Import — Architecture and Implementation Plan

**Status: built.** Schema **v7** is applied, both sources are implemented, and
the suite is at **343 tests, 0 warnings**. §14 records where the built system
differs from the plan below and why — read it before trusting any earlier
section that contradicts it.

This document designs automatic population of a browsable game catalog from
external sources, starting with the Internet Archive and MyAbandonware.

Companion documents: [`project-handoff.md`](project-handoff.md) (the authoritative
record), [`catalog-identity.md`](catalog-identity.md) (what "catalog" currently
means), [`relay-architecture.md`](relay-architecture.md).

---

## 1. Analysis of the current architecture

### 1.1 The most important finding: "catalog" already means something else

`CatalogEntry` is **not** a discovery catalog. It is an *identity registry*. Its
entire field list is `CatalogId`, `Source`, `IsProvisional`, `CanonicalTitle`,
`MatchFingerprint`, `SupersededByCatalogId`, and three timestamps. There is no
description, year, publisher, developer, genre, artwork or download link, and
that is deliberate — its job is to answer "are these two installations the same
title?" so achievements can synchronise across users.

Three properties of that registry make it the wrong place to put imported
metadata:

| Property | Consequence for imported metadata |
|---|---|
| Rows are minted from a fingerprint of an **installed executable** (`ICatalogService.EnsureEntryAsync`) | A discovery listing has no executable. There is nothing to fingerprint until after install |
| `CatalogId` is **rewritten** by `PromoteAsync` and `DemoteForeignEntriesAsync`, with `ON UPDATE CASCADE` carrying every reference | Scraped metadata would be dragged through every relay migration. Pointing at a different relay demotes entries and would churn metadata that has nothing to do with the relay |
| The relay is **authoritative** for it, and everything on it flows through `POST /catalog/resolve` | Third-party scraped metadata would enter the relay sync path, changing what the relay is and adding a redistribution dimension to §7 |

Volume compounds this. `CatalogEntry` holds one row per *installed* game — tens.
A discovery catalog holds thousands of titles the user will never install.
Different lifecycle, different cardinality, different query patterns.

**Recommendation: a separate `CatalogListing` aggregate in a new
`Services.Discovery` namespace.** The two meet exactly once, at install time,
described in §5.6.

### 1.2 What already exists and should be reused unchanged

The download half of this feature is essentially already built.

| Existing piece | Fit |
|---|---|
| `IDownloadService` / `DownloadRequest` | `Url` + `ExpectedChecksum` + `AllowResume` is precisely what an imported listing supplies. **No change needed** |
| `ChecksumAlgorithm.Auto` (infers from hex length) | Internet Archive supplies `md5` (32 hex) and `sha1` (40 hex) per file. Both infer correctly. **No change needed** |
| `IArchiveExtractionService` | Already handles zip/7z/rar with traversal guards and the solid-archive fast path |
| `IInstallFromUrlService` | Download → verify → extract → detect → *stop and let the user confirm*. Already the correct shape |
| `IGameImportService` | Already the single door into the library |
| `ExponentialBackoffRetryPolicy.CalculateDelay(long)` | Public, jittered, capped, never gives up, already has 5 tests. **Use this, do not add Polly** |
| `IHttpClientFactory` named clients | Established pattern with three named clients and per-client timeout reasoning |
| `ArtworkService` download hardening | `.part` + move, size cap, `ResponseHeadersRead`, filename never taken from the remote server |
| `LoopbackFileServer` (tests) | Real Kestrel on a loopback port — can serve HTML fixtures for scraper tests |

### 1.3 The two provider patterns already in the codebase

| Pattern | Shape | Used by |
|---|---|---|
| Single seam | `ArtworkService` takes **one** `IArtworkProvider` | Artwork |
| Open set | Engine resolves `IEnumerable<IAchievementProvider>`, dispatches on a string `Key`, **throws at construction on duplicate keys** | Achievements |

Catalog sources need the second. `IArtworkProvider`'s own XML documentation
already states the governing rule:

> Providers only find and describe images. Downloading them, deciding where they
> live on disk, and updating the library are the artwork service's job — for the
> same reason achievement providers only decide and never persist.

**That rule is the backbone of this design.** A catalog source fetches and
describes. It never writes to the database, never downloads an image, never
decides that two listings are the same game.

### 1.4 Conventions the new code must satisfy

- Nullable enabled, `LangVersion 12.0`, file-scoped namespaces.
- `GenerateDocumentationFile` is on and **CS1591 is deliberately visible**. Every
  public member needs XML docs or the build stops being at 0 warnings.
- Constructor injection with `?? throw new ArgumentNullException`.
- Repositories: one per aggregate, Dapper, each method opens its own connection;
  multi-statement operations use an explicit transaction.
- No business logic in view models; data loading in `OnNavigatedToAsync`.
- Schema changes **append** to `DatabaseInitializer.Migrations`; existing entries
  are never edited.
- Comments explain *why*.

---

## 2. Verified facts about the two sources

These were confirmed against the live APIs while writing this document, not
assumed.

### 2.1 Internet Archive — confirmed

**`GET https://archive.org/metadata/{identifier}`** returns, for
`msdos_Oregon_Trail_The_1990`:

- Top level: `metadata`, `files`, `files_count`, `item_size`,
  `item_last_updated`, `created`, `dir`, `d1`, `d2`, `alternate_locations`,
  `workable_servers`, `reviews`.
- `metadata`: `identifier`, `title`, `description`, `creator`, `date`, `year`,
  `publicdate`, `addeddate`, `uploader`, `mediatype`, `collection[]`, and —
  significantly — a block of **MobyGames-derived fields**:
  `mobygames_genre`, `mobygames_developed_by`, `mobygames_published_by`,
  `mobygames_released`, `mobygames_also_for`, `mobygames_perspective`,
  `mobygames_setting`, `mobygames_pacing`, `mobygames_gameplay`.
- `files[]` per entry: `name`, `source` (`original` / `derivative`), `size`,
  `format`, **`md5`, `sha1`, `crc32`**, `mtime`, and for derivatives an
  `original` back-pointer.

Three consequences worth stating plainly:

1. **Integrity verification comes free.** `sha1` per file drops straight into
   `DownloadRequest.ExpectedChecksum` with `ChecksumAlgorithm.Auto`.
2. **Mirrors come free.** `d1`, `d2` and `dir` compose into alternate download
   hosts serving byte-identical files. See §6.2.
3. **Genre, developer and publisher are structured** on library items, not prose
   to be parsed.

**`GET https://archive.org/advancedsearch.php?q=…&fl[]=…&rows=…&page=…&output=json`**
returns `{responseHeader, response:{numFound, start, docs[]}}`. Confirmed working.

**`GET https://archive.org/services/search/v1/scrape?q=…&fields=…&count=…`**
returns `{items[], count, total}` and supports `cursor` for deep pagination.
Confirmed working — but it **rejects a bare free-text `q` with HTTP 400**. The
query must be fielded.

**`GET https://archive.org/services/img/{identifier}`** serves a ready-made
thumbnail. Use it for grid tiles instead of downloading full covers (§8.1).

**Two restrictions discovered in the real response, both load-bearing:**

- The sample item carries `"access-restricted-item": "true"` and belongs to the
  `stream_only` collection. Such items are **not downloadable** — an install
  attempt will fail with a 403. The importer must detect this and mark the
  listing accordingly rather than offering a Play button that cannot work.
- `https://archive.org/details/@rohankar` is a **client-rendered page**. Fetching
  it as HTML yields nothing but the site chrome. Scraping the account page is not
  an option; the API is the only route.

### 2.1a Step 0 resolved — the confirmed enumeration contract

`GET https://archive.org/services/search/v1/scrape` is the enumeration endpoint.
Confirmed constraints, each established by a failing probe:

| Constraint | Evidence |
|---|---|
| `q` must be **fielded** | `q=rohankar` → HTTP 400. `q=collection:"…"` → 200 |
| `count` must be **≥ 100** | `count=5` → HTTP 400. `count=100` → 200 |
| Response is `{items[], count, total, cursor}` | Confirmed |
| `cursor` is an **opaque base64 continuation token** | Observed: `W3siaWRlbnRpZmllclNvcnRlciI6IkJMQUNLVEhOIn1d` |
| `fields` is comma-separated | Confirmed |

Scale confirmed against the real index:

| Query | Total |
|---|---|
| `collection:"softwarelibrary_msdos_games"` | **8 898** |
| `mediatype:software AND collection:softwarelibrary` | **237 657** |

8 898 is squarely the "several thousand" target, which validates the performance
assumptions in §9.

### 2.1b The `@rohankar` account does not hold games

The nominated source does not exist in a usable form. Four independent checks:

| Check | Result |
|---|---|
| `https://archive.org/details/@rohankar` in a real browser | **Redirects to the archive.org homepage.** No uploads page |
| `GET /metadata/@rohankar` | An account stub: `mediatype: account`, `collection: users`, registered 2022-09-07, `access-restricted-item: true`. No item list |
| `collection:"fav-rohankar"` (favourites) | 0 results |
| `uploader:*rohankar*` (wildcard) | 4 items, **none of them games** — a favourites collection belonging to a different account (`rohan_kar`), two travel photographs, and a courier-service text |

The `uploader` field holds an email address that is not exposed anywhere public,
and the account's own metadata does not list its items. There is no query that
reaches this account's uploads, and the evidence suggests there are none to
reach.

**Decision: the Internet Archive source targets configurable collection queries,
defaulting to the software libraries.** This is strictly better than the original
premise — 8 898 curated DOS games with structured MobyGames metadata beats one
account's uploads — and the query lives in settings, so pointing it at a specific
account or collection later is a configuration change, not a code change.

### 2.2 MyAbandonware — no public API

Scraping is required. Pages needed:

| Page | Purpose |
|---|---|
| `/browse/name/{letter}` or `/browse/year/{year}` | Enumeration with pagination |
| `/game/{slug}` | Full metadata: description, year, developer, publisher, genre, perspective, platform, system requirements |
| `/game/{slug}/screenshots` (where present) | Screenshot set |
| Download link block on the game page | Mirror URLs, file names, sizes, platform per file |
| `/robots.txt` | Must be fetched and honoured before any of the above |

Selector stability strategy is in §3.5. The short version: prefer OpenGraph and
JSON-LD metadata over CSS classes, keep every selector in one overridable map,
and fail loudly when a batch stops parsing.

---

## 3. Recommended architecture

### 3.1 Deviations from the proposed shape, and why

Your sketch was `ICatalogProvider` / `InternetArchiveCatalogProvider` /
`MyAbandonwareCatalogProvider` / normalization / `GameMetadata` / image
downloader / download abstraction / pipeline / duplicate detection / update
detection. That decomposition is right. Four changes:

| Proposed | Recommended | Why |
|---|---|---|
| `ICatalogProvider` | **`ICatalogSource`**, in `Services.Discovery` | "Provider" already means two different things here (`IArtworkProvider`, `IAchievementProvider`), and "Catalog" already means identity. Two collisions in one name |
| One `GameMetadata` model | **`SourceListing`** *and* **`CatalogListing`** | One type cannot be both "what MyAbandonware said" and "the merged truth". The split is what makes re-merging possible without re-crawling |
| Image downloader in the pipeline | **Image cache outside the pipeline, lazy** | Importing 3 000 games × 6 images is 18 000 requests during an import that should take minutes, not hours |
| Download source abstraction | **Reuse `IDownloadService` unchanged**; add only a mirror selector | The abstraction already exists and is well tested. A second one would be the "duplicate service" your rules forbid |

### 3.2 Component map

```
Services/Discovery/
├── ICatalogSource.cs              seam: one per site
├── Sources/
│   ├── InternetArchiveCatalogSource.cs
│   └── MyAbandonwareCatalogSource.cs
├── Model/
│   ├── SourceListingRef.cs        cheap: id + title + change stamp
│   ├── SourceListing.cs           full: what one source said
│   ├── CatalogListing.cs          merged: what the UI shows
│   ├── ListingDownload.cs
│   └── ListingImageRef.cs
├── Normalization/
│   ├── IListingNormalizer.cs      pure
│   ├── TitleNormalizer.cs         pure
│   └── GenreVocabulary.cs         pure
├── Matching/
│   ├── IListingMatcher.cs         pure — duplicate detection
│   └── IListingMerger.cs          pure — field precedence
├── Import/
│   ├── ICatalogImportService.cs   the pipeline
│   ├── CatalogImportService.cs
│   └── ImportRunState.cs
├── Images/
│   ├── IListingImageCache.cs      lazy, content-addressed
│   └── ListingImageCache.cs
├── Install/
│   ├── IListingInstallService.cs  listing → InstallFromUrlRequest
│   └── IMirrorSelector.cs
└── Http/
    ├── IRobotsPolicy.cs           robots.txt, cached
    └── ISourceRateLimiter.cs      per-source politeness gate

Services/Database/
├── ICatalogListingRepository.cs
└── CatalogListingRepository.cs
```

Everything under `Normalization/` and `Matching/` is a pure function: no
database, no network, no clock. That is what makes the interesting logic
testable, and it is the same discipline `IAchievementProvider` follows.

### 3.3 The pipeline

`ICatalogImportService` is the only component that touches more than one layer.
Its pass is:

```
for each source (sequentially — sources have different rate limits):
    1. EnumerateAsync        → IAsyncEnumerable<SourceListingRef>   cheap
    2. filter                → refs whose change stamp beats the stored one
    3. FetchAsync (gated)    → SourceListing                        expensive
    4. Normalize             → canonical title, year, genre vocabulary
    5. Upsert source row     → CatalogListingSource (+ raw payload)
    6. Match                 → find or create the CatalogListing
    7. Merge                 → recompute the merged row from ALL its source rows
    8. Upsert listing        → CatalogListing (+ downloads, images, genres)
    9. checkpoint            → CatalogImportRun.Cursor, every N items
```

Two properties inherited deliberately from the existing sync design:

- **The work queue is a predicate over stored data, not a queue object.** "What
  still needs fetching" is `WHERE FetchedAt IS NULL OR SourceUpdatedAt >
  FetchedAt` — the same shape as `AchievementUnlock WHERE SyncedAt IS NULL`.
  Killing the process mid-import loses nothing and there is no queue file to
  corrupt.
- **Every step is idempotent**, so blind re-run is safe.

Step 7 recomputing from *all* source rows rather than patching in the new one is
what makes the merged row a pure function of the source rows. It costs one extra
query per item and buys a rebuildable derived table (§5.5).

### 3.4 Proposed interfaces

Signatures only — no bodies, and XML docs omitted here for brevity (they are
mandatory in the real code).

```csharp
/// One external site the catalog can be populated from.
public interface ICatalogSource
{
    /// Dispatch key stored in CatalogListingSource.SourceKey.
    /// Declare as a public const string SourceKey on the implementation.
    string Key { get; }

    string DisplayName { get; }

    /// False when the source needs configuration it does not have, or when
    /// robots.txt currently disallows it. Not an error — the source is skipped.
    bool IsAvailable { get; }

    /// Cheap listing of what exists. Yields as it pages, so a caller can
    /// checkpoint without waiting for the whole site.
    IAsyncEnumerable<SourceListingRef> EnumerateAsync(
        SourceEnumerationOptions options,
        CancellationToken cancellationToken = default);

    /// Full metadata for one item. Returns null when the item has vanished
    /// or is not a game; throws only on transport failure.
    Task<SourceListing?> FetchAsync(
        SourceListingRef reference,
        CancellationToken cancellationToken = default);
}

public sealed record SourceEnumerationOptions
{
    /// Only items changed since this point, where the source can express it.
    public DateTimeOffset? ChangedSince { get; init; }

    /// Opaque per-source resume token from the last run.
    public string? Cursor { get; init; }

    /// Stop after this many refs. Zero means no limit.
    public int MaxItems { get; init; }
}

/// Cheap identity of a source item — never requires the expensive fetch.
public sealed record SourceListingRef(
    string SourceKey,
    string SourceItemId,
    string Title,
    DateTimeOffset? SourceUpdatedAt,
    string? Cursor);

/// What one source said about one game, after normalization.
public sealed record SourceListing
{
    public required string SourceKey { get; init; }
    public required string SourceItemId { get; init; }
    public required Uri SourceUrl { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public string? Description { get; init; }
    public string? Developer { get; init; }
    public string? Publisher { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? SystemRequirements { get; init; }
    public IReadOnlyList<ListingImageRef> Images { get; init; } = [];
    public IReadOnlyList<ListingDownload> Downloads { get; init; } = [];

    /// False for stream-only or access-restricted items.
    public bool IsDownloadable { get; init; } = true;

    public DateTimeOffset? SourceUpdatedAt { get; init; }

    /// The unmodified payload, for re-parsing after a parser fix.
    public required string RawPayload { get; init; }
}

public sealed record ListingDownload(
    Uri Url, string? FileName, long? SizeBytes,
    string? Md5, string? Sha1, string? Format,
    DownloadKind Kind, int MirrorRank);

public sealed record ListingImageRef(
    Uri Url, ListingImageKind Kind, int Width, int Height, int SortOrder);

/// Pure. No database, no network, no clock.
public interface IListingNormalizer
{
    SourceListing Normalize(SourceListing raw);

    /// "Oregon Trail, The" (1990) → "oregon trail|1990"
    string ComputeMatchKey(string title, int? year);
}

/// Pure. Decides whether a source listing belongs to an existing listing.
public interface IListingMatcher
{
    ListingMatch Match(SourceListing candidate, IReadOnlyList<CatalogListing> nearby);
}

/// Pure. Collapses every source row for one game into the merged row.
public interface IListingMerger
{
    /// captureTrace populates MergeResult.Trace for debugging merge rules;
    /// it is discarded in the normal path (§4.3, layer 3).
    MergeResult Merge(
        string listingId,
        IReadOnlyList<SourceListing> sources,
        bool captureTrace = false);
}

public sealed record MergeResult(
    CatalogListing Listing,
    IReadOnlyDictionary<string, string> FieldProvenance,
    IReadOnlyList<MergeTraceEntry>? Trace);

public interface ICatalogImportService
{
    Task<ImportRunResult> RunAsync(
        ImportRunOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record ImportRunOptions
{
    /// Null runs every available source.
    public IReadOnlyList<string>? SourceKeys { get; init; }
    public ImportMode Mode { get; init; } = ImportMode.Incremental;
    public int MaxItems { get; init; }
}

public enum ImportMode
{
    /// Only items whose change stamp beats the stored one.
    Incremental = 0,

    /// Re-fetch everything, ignoring watermarks.
    Full = 1,

    /// Re-normalize and re-merge stored raw payloads. No network at all.
    Remerge = 2
}
```

`ImportMode.Remerge` is the mode you will use most. Every normalization or
merge-rule change can be applied to the whole catalog in seconds, offline,
without touching either site. It exists only because raw payloads are stored
(§5.5).

### 3.5 Keeping the scraper maintainable

Three mechanisms, in order of value.

**1. Selector precedence.** Prefer, in this order:

| Rank | Anchor | Volatility |
|---|---|---|
| 1 | JSON-LD (`<script type="application/ld+json">`) | Lowest — semantic, survives redesigns |
| 2 | OpenGraph / `<meta name="…">` | Low — SEO-driven, rarely churned |
| 3 | `itemprop` microdata | Low |
| 4 | Structural anchors on visible labels ("Developer:" → next sibling) | Medium |
| 5 | CSS class names | **Highest — last resort** |

**2. Typed extractors in code, not a configuration format.** Selectors are
`private const string` fields on the parser; the *logic* around them — try
JSON-LD, fall back to OpenGraph, fall back to a labelled row, then normalise — is
ordinary strongly-typed C# with unit tests.

This was reconsidered and reversed during design review. The earlier proposal was
an overridable JSON selector map. It is the wrong trade for this codebase:

- A selector alone is rarely the fix. Real extraction needs fallback chains,
  conditional parsing, per-field normalisation and type conversion. Expressing
  that as configuration means inventing a rule language — a scraping framework
  nobody asked for, and one that cannot be unit tested or refactored.
- It would put parsing logic outside the compiler, outside the test suite and
  outside `GenerateDocumentationFile`. Everything else here is strongly typed on
  purpose.
- The project's own rules forbid unnecessary abstraction. A configurable scraper
  engine is exactly that.

The extraction contract is therefore a small typed seam per field:

```csharp
// Ordered fallback, first non-empty wins. Pure, no I/O.
private static string? ExtractTitle(IDocument document) =>
    JsonLd.String(document, "name")
    ?? Meta(document, "og:title")
    ?? Text(document, TitleSelector);
```

A layout change is a one-line constant edit plus a fixture update, which is a
smaller and safer diff than a config schema migration would have been.

**3. Batch health check — the important one.** Track the parse success rate per
run. If more than 20 % of a batch fails to yield a title, **abort the run and log
loudly**. A scraper that silently degrades to empty records is far worse than one
that stops: the empty records get merged, overwrite nothing (§4.2 forbids that),
but consume the crawl budget and mask the breakage for weeks.

This mirrors the "prove a test can fail" discipline already in §11 of the
handoff: the failure has to be visible or the safeguard is theatre.

---

## 4. Duplicate handling

### 4.1 Detection

A two-stage match, deliberately conservative.

**Stage 1 — normalize the title.** Lowercase; strip diacritics; move a trailing
article to the front (`"Oregon Trail, The"` → `"the oregon trail"`) then drop it;
strip edition and media suffixes (`gold edition`, `cd version`, `floppy`,
`v1.2`, `remastered`, `[collector's]`); normalise roman numerals to arabic
consistently; strip punctuation; collapse whitespace.

The `"Oregon Trail, The"` form is not hypothetical — it is exactly how the title
arrives from the Internet Archive item confirmed in §2.1.

**Stage 2 — key and window.**

```
MatchKey = normalizedTitle + "|" + (year ?? 0)

exact MatchKey hit                          → same game, merge
normalizedTitle hit, |Δyear| ≤ 1            → same game, merge
normalizedTitle hit, |Δyear| > 1            → DO NOT merge; flag for review
normalizedTitle hit, one side has no year   → same game, merge; adopt the year
no hit                                      → new listing
```

The ±1 year window exists because sources routinely disagree by a year — one
records first release, the other a regional or re-release date. A wider window
starts merging genuine sequels and remakes, which is unrecoverable without the
raw payloads.

**Recommend a `ListingAlias` table** in the same spirit as the existing
`CatalogAlias`: many match keys → one listing, so a manual "these two are the
same game" decision is expressible and survives re-import. The parallel to the
existing design is exact — one title legitimately has several keys, and a single
column cannot hold them.

### 4.2 Choosing the preferred metadata

**The whole merge policy in one sentence: scalar fields resolve by per-field
source precedence; collection fields union.**

Per field, not per record — neither source is better at everything.

| Field | Rule | Reasoning |
|---|---|---|
| Title | MyAbandonware, then de-comma'd IA title | IA titles carry catalogue artefacts (`", The"`, emulator suffixes) |
| Year | **Earlier** of the two when both present | Re-release dates inflate; the earlier is nearly always the original |
| Description | Longest non-empty, HTML stripped | Neither source is reliably richer |
| Developer / Publisher | IA `mobygames_developed_by` / `mobygames_published_by`, then MyAbandonware | Structured MobyGames fields beat parsed prose |
| Genres | IA `mobygames_genre`, mapped through `GenreVocabulary` | Controlled vocabulary; MyAbandonware genres are free-form |
| Platforms | **Union** | A game genuinely is DOS *and* Windows 3.x |
| Tags | **Union**, deduped | Additive by nature |
| System requirements | MyAbandonware only | IA does not carry it |
| Screenshots | **Union**, deduped by URL hash, ordered by source rank | More screenshots is strictly better |
| Cover | Highest-ranked source that has one; keep the others as alternates | One cover shows; the rest are fallbacks if it 404s |
| Downloads | **Union, always** | Mirrors are additive. A mirror never replaces a mirror |

**A null never overwrites a value.** A source that omits a field abstains; it
does not vote for empty. This is what stops a degraded scraper (§3.5) from
hollowing out good data.

### 4.3 Provenance — three layers, each cheap

The requirement is to be able to answer "why does this listing say 1993?" months
later, without full field-level history machinery. Three layers do that, and each
costs almost nothing because the data is already being written.

**Layer 1 — the source rows are the evidence.** `CatalogListingSource` already
holds every source's normalised view plus its raw payload. Whatever the merge
concluded, the inputs are on disk and inspectable.

**Layer 2 — `FieldProvenance`, a JSON map on the merged row.** `field →
sourceKey`, written by the merger as a by-product of resolving each field. It is
one small column, not a table, because it is read by a human debugging a rule,
never joined or filtered.

```json
{"title":"myabandonware","year":"internetarchive","developer":"internetarchive"}
```

**Layer 3 — `MergeTrace`, populated only on demand.** `IListingMerger` returns a
`MergeResult` carrying, per field, the candidate values from every source and
which rule selected the winner. The pipeline discards it normally and persists it
when `ImportRunOptions.CaptureMergeTrace` is set. Debugging a merge rule then
means re-running `ImportMode.Remerge` with the flag on — offline, no network, over
the raw payloads already stored.

This is the layer that makes merge rules genuinely debuggable, and it costs
nothing in the normal path because it is computed and thrown away.

**Why not field-level history?** Storing every value a field has ever held, with
timestamps, is a table that grows without bound and answers a question nobody
asks. Layers 1 and 3 reconstruct any historical merge exactly, from data that is
already there.

### 4.4 Attribution and updating

Updating an existing entry is not a special path: re-fetch → replace that
source's row → re-merge from all rows → upsert. Because the merge is a pure
function of the source rows, an update cannot leave a field stranded from a
source that no longer reports it.

**User edits are the one exception.** If the user corrects a title, that must
survive the next import. Recommend a `UserOverride` JSON column on
`CatalogListing`, applied *after* the merge. It keeps hand-editing out of the
merge function, which stays pure.

---

## 5. Database recommendations

**Recommendations only. No migration is proposed for immediate application.**

### 5.1 What already exists and is sufficient

`Game`, `Collection`, `CatalogEntry`, `CatalogAlias` all stay exactly as they
are. Nothing in this design changes an existing table except one nullable column
in §5.6.

### 5.2 New tables

Schema **v7**, appended to `DatabaseInitializer.Migrations` — never editing an
existing entry.

| Table | Key columns | Purpose |
|---|---|---|
| `CatalogListing` | `ListingId` TEXT PK (`lst_<32 hex>`, locally minted), `Title`, `SortTitle`, `Year`, `Developer`, `Publisher`, `Description`, `MatchKey`, `CoverImageUrl`, `CoverImagePath`, `PrimarySourceKey`, `FieldSources`, `UserOverride`, `IsDownloadable`, `IsHidden`, `ContentHash`, `CreatedAt`, `UpdatedAt` | The merged, browsable row |
| `CatalogListingSource` | PK `(ListingId, SourceKey, SourceItemId)`, `SourceUrl`, `NormalizedJson`, `RawPayload`, `RawPayloadEncoding`, `SourceUpdatedAt`, `FetchedAt`, `SourceContentHash`, `Rank` | One observation per source. **The real input to the merge** |
| `CatalogListingDownload` | `ListingId`, `SourceKey`, `Url`, `FileName`, `SizeBytes`, `Md5`, `Sha1`, `Format`, `Kind`, `MirrorRank` | Mirrors, unioned across sources |
| `CatalogListingImage` | `ListingId`, `SourceKey`, `Kind`, `RemoteUrl`, `LocalPath`, `Width`, `Height`, `SortOrder`, `UrlHash` | Cover / screenshot / hero references |
| `Genre` / `Developer` / `Publisher` / `Platform` | `Id` INTEGER PK, `Name` TEXT UNIQUE NOCASE, `NormalizedName` | Normalised lookup entities (§5.4) |
| `ListingGenre` / `ListingPlatform` | `(ListingId, GenreId)` / `(ListingId, PlatformId)` | Many-to-many joins |
| `ListingTag` | `(ListingId, Tag)` | Free-form; no lookup entity (§5.4) |
| `ListingAlias` | `MatchKey` PK, `ListingId`, `Source`, `CreatedAt` | Many keys → one listing (§4.1) |
| `CatalogImportRun` | `RunId`, `SourceKey`, `Mode`, `StartedAt`, `CompletedAt`, `Cursor`, `ItemsSeen`, `ItemsChanged`, `ItemsFailed`, `ParseSuccessRate`, `LastError` | Run bookkeeping and resume |
| `CatalogListingSearch` | FTS5 virtual table | Full-text search (§5.8) |

`CatalogListing.DeveloperId` and `PublisherId` are nullable FKs to the lookup
tables — a listing has at most one of each, so a join table would be wrong.
Genres and platforms are genuinely many-to-many.

Indexes: `MatchKey`, `SortTitle COLLATE NOCASE`, `Year`,
`CatalogListingSource.FetchedAt`, `ListingGenre.GenreId`,
`ListingPlatform.PlatformId`, `CatalogListing.DeveloperId`, `PublisherId`.

Cascades: everything hangs off `CatalogListing` with `ON DELETE CASCADE`.
`ListingId` is **never rewritten**, unlike `CatalogId` — which removes the entire
class of problem that made promotion and demotion delicate.

### 5.3 Should a separate Sources table exist?

Yes and no, and the distinction matters.

- **The source registry is code, not data.** One class per source, keyed by a
  string constant, resolved as `IEnumerable<ICatalogSource>` — identical to how
  achievement providers are registered. A database table listing "Internet
  Archive, MyAbandonware" would be a second place to keep in sync with the
  container for no gain.
- **Source *observations* need a table.** That is `CatalogListingSource`, and it
  is the single most valuable table in the design.

### 5.4 Normalised lookup entities, and where normalisation stops

`Game.Tags` is a JSON column, and the handoff explains why: tags are always read
and written as a complete set alongside the game, so a join table would add joins
without buying anything.

**Discovery does not follow that precedent, and the reason is the query, not the
data.** A library holds tens of games and its tags are *displayed*. The catalog
holds thousands of listings and its genres, developers, publishers and platforms
are *filtered and faceted across the whole set*. JSON storage answers that with a
full scan and a per-row parse; an indexed join answers it directly.

Normalise: **Genre, Developer, Publisher, Platform.** Each gets an `Id`/`Name`
table with a `NormalizedName` for matching, so `"MicroProse"`,
`"Microprose Software"` and `"MICROPROSE"` collapse to one row that can be
renamed once and corrected everywhere.

Four things this buys beyond filtering:

1. **Facet counts are a `GROUP BY`**, not an aggregation over parsed JSON.
2. **A misspelling is fixed in one row**, not across 400 listings.
3. **"More from this developer" is a join**, not a `LIKE`.
4. **Merge conflicts collapse.** Two sources spelling a publisher differently
   resolve to the same `PublisherId` at write time.

**Normalisation stops at tags.** They are free-form, open-vocabulary, arrive
from different sources with no shared meaning, and are displayed rather than
faceted. A `Tag` lookup table would accumulate thousands of single-use rows for
no query benefit. `ListingTag(ListingId, Tag)` — a plain join with the string
inline — is the right middle ground: still queryable, no lookup entity to
maintain.

### 5.5 Should raw source metadata be stored?

**Yes — and this is the highest-value recommendation in the document.**

Store the unmodified payload per source row: the IA metadata JSON, the
MyAbandonware page HTML. Roughly 20–40 KB per item; at 5 000 items that is
100–200 MB uncompressed, so gzip it into a BLOB and it is 20–40 MB.

What it buys:

- **Re-parse after a bug fix without re-crawling.** Fix a genre parser at 09:00,
  have the whole catalogue corrected by 09:01, with zero requests to either site.
  This is `ImportMode.Remerge`.
- **Normalization and merge rules become tunable.** You will iterate on these a
  dozen times. Each iteration is otherwise a full re-crawl.
- **Scraper breakage is diagnosable.** The HTML that failed is on disk.
- **It is the politest possible design.** The single biggest reduction in load on
  a small site is not crawling it twice for the same data.

The precedent already exists: `AchievementDefinition.TriggerConfigJson` is opaque
JSON that round-trips untouched so a provider can carry its own shape without a
migration.

### 5.6 The one change to an existing table

`Game.ListingId` — a nullable TEXT column, FK to `CatalogListing(ListingId)`
`ON DELETE SET NULL`.

This is the *only* join between discovery and the library, written once when a
game is installed from a listing. It lets the details page show the imported
description and screenshots for an installed game.

It does **not** interact with `CatalogId`. Installing from a listing runs the
existing flow untouched: `IInstallFromUrlService` → user confirms →
`IGameImportService.ImportAsync` → `ICatalogService.EnsureEntryAsync` fingerprints
the now-present executable and mints or matches a `CatalogEntry` exactly as it
does today. Discovery never touches identity, and the relay never sees a listing.

### 5.7 Should downloaded metadata be versioned?

**No.** Full field-level version history is significant machinery for a personal
launcher and nobody will read it. `CatalogListingSource.SourceContentHash` plus
`CatalogImportRun` history already answers "did this change, and when", and the
stored raw payload answers "what did it say". That is the useful 90 % at a
fraction of the cost.

### 5.8 Full-text search — built in from v7

`LIKE '%term%'` cannot use an index, so it degrades linearly and cannot rank.
At 8 898 listings it is survivable; it is still the wrong foundation, and
retrofitting FTS later means a second migration plus backfilling every row.

**Recommendation: an FTS5 external-content table, created in v7.**

```sql
CREATE VIRTUAL TABLE CatalogListingSearch USING fts5 (
    Title, Developer, Publisher, Genres, Description,
    content    = 'CatalogListing',
    content_rowid = 'RowId',
    tokenize   = 'unicode61 remove_diacritics 2'
);
```

Four decisions worth recording:

| Decision | Reasoning |
|---|---|
| **External content** (`content=`) | The text is not duplicated. FTS5 stores only the index and reads columns from `CatalogListing`, roughly halving the storage cost |
| **Denormalised `Developer` / `Publisher` / `Genres` columns in the index** | Deliberate. Search wants one flat document per listing; joins at query time would defeat the index. The lookup tables remain authoritative — these are a projection, rebuilt by the same triggers |
| `unicode61 remove_diacritics 2` | "Pokémon" must be found by typing "Pokemon". The same reason `TitleNormalizer` strips diacritics |
| Kept in sync by **triggers**, not application code | A listing written by any path stays searchable. Application-side sync is a rule every future caller has to remember |

`CatalogListing` needs an explicit `RowId INTEGER PRIMARY KEY` for the external
content link, since `ListingId` is TEXT. That is the one schema concession FTS5
imposes, and it is why it belongs in v7 rather than a later migration.

Queries use `bm25()` for relevance ordering, with a `porter`-free tokenizer so
exact game titles are not stemmed into each other. A prefix query (`term*`)
serves type-ahead.

**Fallback:** if a user's SQLite build lacks FTS5, the repository detects it at
initialisation and degrades to `LIKE`. `Microsoft.Data.Sqlite` bundles SQLite
with FTS5 enabled, so this is defensive rather than expected — but a hard failure
at startup over a search feature would be the wrong trade.

---

## 6. Download integration

### 6.1 It already works

An imported listing produces an `InstallFromUrlRequest` directly:

```
CatalogListingDownload  →  InstallFromUrlRequest
    Url                        Url
    Sha1 (or Md5)              ExpectedChecksum   (ChecksumAlgorithm.Auto)
    listing SortTitle          InstallFolderName
```

`IDownloadService`, `IArchiveExtractionService` and `IInstallFromUrlService` need
**no changes**. `IListingInstallService` is a thin translator, and the user still
confirms the detected executable before anything enters the library — the
existing behaviour, and the right one.

### 6.2 Mirrors

The Internet Archive exposes `d1`, `d2` and `dir`, which compose into alternate
hosts for byte-identical files:

```
https://{d1}{dir}/{filename}
https://{d2}{dir}/{filename}
https://archive.org/download/{identifier}/{filename}      (redirector, fallback)
```

`IMirrorSelector` yields these in order. On `HttpRequestException` the install
service advances to the next mirror.

**A property worth exploiting:** because the mirrors serve identical bytes, the
existing `.part` file resumes across a mirror switch. A download that dies at
60 % on `d1` continues from 60 % on `d2`. Nothing in `DownloadService` needs to
change for this — it already resumes by file length — and the final checksum
verification is what makes it safe to rely on.

### 6.3 Formats and restrictions

- IA game items are predominantly `.zip`; MyAbandonware serves `.zip`, `.7z`,
  `.rar` and occasionally a bare `.exe` installer. `IsSupportedArchive` already
  gates this, and a non-archive falls through the existing `WasArchive = false`
  path.
- `crc32` is present in IA metadata but is **not** a supported
  `ChecksumAlgorithm` and its 8-hex length would confuse `Auto`. Prefer
  `sha1` > `md5` and ignore `crc32`.
- **Respect `access-restricted-item` and `stream_only`.** Set
  `IsDownloadable = false`, show the listing, and disable install with an
  explanation. Offering a button that always 403s is worse than not offering one.

---

## 7. Legal and technical risks

Stated factually so the design can account for them; you have said this is a
personal-use launcher, and the recommendations below are the ones that keep it
that way.

### Legal

1. **"Abandonware" is not a legal category.** Copyright persists regardless of
   whether a title is commercially available. Availability on a website is not
   evidence of permission.
2. **Internet Archive items vary enormously in status.** Some are public domain
   or permissively licensed; many are hosted under IA's own arrangements. The
   `access-restricted-item` and `stream_only` markers are IA telling you an item
   is not for download — honour them, both legally and because ignoring them just
   produces 403s.
3. **MyAbandonware's Terms of Service and `robots.txt` govern automated access.**
   Fetch `robots.txt` at runtime and honour it. If it disallows a path, the source
   reports `IsAvailable = false` for that path rather than proceeding.
4. **Personal use and redistribution are different.** Importing metadata onto
   your own machine is a materially different act from shipping a pre-populated
   catalogue database with the application. **Do not bundle a scraped catalogue
   in the installer** — import at runtime, on the user's machine, under their
   control. This also keeps the relay clean: §5.6 keeps listings off it entirely.
5. **Attribution.** Keeping `SourceUrl` per source row and showing it in the UI
   costs nothing and is the right default.

### Technical

| Risk | Mitigation |
|---|---|
| Layout change silently breaks the scraper | Batch health check aborts at >20 % parse failure (§3.5) |
| IP blocking from aggressive crawling | 1 concurrent request and a 1–2 s delay for MyAbandonware; honour `Retry-After` |
| Identifying User-Agent | Send a real, contactable UA — the existing clients already set `GameLauncher/1.0` |
| Import corrupts good data | Nulls never overwrite (§4.2); merge is pure and rebuildable from raw payloads |
| Over-merging distinct titles | ±1 year window, no merge beyond it (§4.1) |
| Huge catalogue slows the UI | Paged queries + virtualization (§8.4); never load 5 000 rows into an `ObservableCollection` |
| Disk exhaustion from images | Lazy fetch, thumbnails via IA's service, LRU sweep with a settings cap |
| A source disappears entirely | Listings persist; the source row simply stops updating |

---

## 8. Images

### 8.1 Do not download during import

Importing 3 000 games with six images each is 18 000 requests. It turns a
five-minute import into an hours-long one and fills the disk with screenshots for
games the user will never open.

**Import stores URLs. Images fetch on first display.**

For grid tiles, use the Internet Archive's own thumbnail service —
`https://archive.org/services/img/{identifier}` — which avoids downloading the
full cover at all.

An optional background warm pass may pre-fetch **covers only**, for listings the
user has actually browsed to. Never screenshots.

### 8.2 Cache design

- New `IAppPaths.ListingImageDirectory` (`%LOCALAPPDATA%\GameLauncher\listings`).
- **Content-addressed filenames**: `SHA256(remoteUrl)` + extension. This makes
  invalidation automatic — a changed URL is a different file — and it continues
  the rule `ArtworkService` already states, that a name chosen by a remote server
  has no business deciding what gets written to disk.
- Reuse `ArtworkService`'s hardening verbatim: `.part` then move, `MaxImageBytes`
  cap, `ResponseHeadersRead`, and PNG/JPEG only. **WPF cannot decode WebP**, and
  a WebP downloads successfully then renders as nothing — a far more confusing
  failure than a missing image. MyAbandonware serves WebP in places.

### 8.3 Thumbnails

**Do not generate thumbnail files.** WPF's `BitmapImage.DecodePixelWidth` decodes
straight to the target size, which is the idiomatic answer and costs no extra
files, no extra invalidation and no extra code. Revisit only if profiling shows
decode cost actually matters at scroll speed.

### 8.4 Eviction

LRU by last-access time, with a cache size cap in settings (suggest 500 MB
default). Sweep on startup, off the UI thread. Covers for listings linked to an
installed `Game` are pinned and never evicted.

---

## 9. Scheduling and performance

### 9.1 Background imports

**Nothing about discovery may block startup.** This is the existing offline-first
rule applied to a new subsystem, and the launcher already has the pattern:
`RelayCoordinatorService` is a hosted service registered last, whose
`StartAsync` returns immediately and does its work on a background loop.

`CatalogImportBackgroundService` follows it exactly:

```
Registration order (ServiceRegistration.AddInfrastructure):
  1. SettingsStartupService
  2. DatabaseStartupService              ← schema v7 applied here
  3. AchievementNotificationService
  4. AchievementWatcherService
  5. RelayCoordinatorService
  6. CatalogImportBackgroundService      ← new, last: depends on 1 and 2,
                                           nothing depends on it
StartAsync:
  └─ launch loop on a background task, return immediately
       ├─ initial delay (30 s) so it never competes with first paint
       ├─ due?  = now - LastRunAt > RefreshInterval   (default 24 h)
       ├─ run   = CatalogImportService.RunAsync(Incremental)
       ├─ raise CatalogUpdated(new, updated) when the run changed anything
       └─ sleep to the next check; honour the stopping token throughout
```

Four properties, each inherited from an existing decision rather than invented:

| Property | Precedent |
|---|---|
| Registered last, blocks nothing | `RelayCoordinatorService` |
| Resumes from `CatalogImportRun.Cursor` after a kill | The sync watermarks — a predicate over stored data, not a queue file |
| Never runs two imports concurrently (a gate, not a lock file) | `PresenceTracker`'s single-owner counting |
| Reports through an event, not by touching a view model | `IAchievementNotificationService` |

**The Discover page stays fully usable during a run.** Imports write in batched
transactions and SQLite is in WAL mode, so readers are never blocked by the
writer. This is the same reason the library stays responsive during a scan.

### 9.2 Notification

Reuse the toast overlay rather than building a second notification channel. But
**not** through `IAchievementNotificationService` — that service's queue,
ordering and dwell semantics are specific to achievements, and widening it to
carry unrelated events would break the single-pump invariant its tests protect.

Instead: `ICatalogImportService` raises `CatalogUpdated`, the shell view model
subscribes through `IUiDispatcher`, and shows a non-blocking banner with a count
and a link to the Discover page filtered to `DateAdded desc`. One event, one
subscriber, no interference with the achievement pump.

A catalog refresh finishing is ambient information, not an interruption — a
banner is the right weight for it, and a modal or a stealing toast would be
wrong.

### 9.3 Performance

| Concern | Recommendation |
|---|---|
| Batching | One connection and one transaction per 500 upserts, not per row. Their existing "each method opens its own connection" rule is right for interactive use and wrong for bulk — add explicit bulk methods rather than looping the single-row ones |
| Parallelism — IA | 4 concurrent. It is a large CDN and the API is designed for programmatic access |
| Parallelism — MyAbandonware | **1 concurrent, 1–2 s between requests.** Politeness, and the thing that keeps you un-blocked |
| Mechanism | `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`, plus a `SemaphoreSlim` per source. .NET 8 built-ins; no package |
| Retry | `ExponentialBackoffRetryPolicy.CalculateDelay` — already jittered, capped, tested. Retry 5xx/408/429/socket; do not retry 404/403 |
| Incremental | IA exposes `item_last_updated` and the scrape API supports `cursor` — store the high-water mark in `CatalogImportRun.Cursor`. MyAbandonware has no change stamp, so short-circuit on `SourceContentHash` before parsing |
| Full refresh | Same pipeline, watermark ignored. Rare |
| Re-merge | `ImportMode.Remerge` — no network at all. The mode you will actually use while tuning |
| Search | **FTS5 external-content table, created in v7** (§5.8). Kept in sync by triggers; `bm25()` ranking; `LIKE` retained only as a fallback if FTS5 is unavailable |
| UI | Paged repository queries + `VirtualizingStackPanel` |

---

## 10. Data flow

### Import

```
CatalogImportService.RunAsync(mode: Incremental)
│
├─ for each ICatalogSource where IsAvailable        (sequential — differing rate limits)
│  │
│  ├─ IRobotsPolicy.IsAllowed(path)?         ── no ──→ skip source, log, continue
│  │
│  ├─ EnumerateAsync(ChangedSince, Cursor)   ── IAsyncEnumerable<SourceListingRef>
│  │     └─ yields as it pages; caller checkpoints without waiting for the whole site
│  │
│  ├─ for each ref, gated by ISourceRateLimiter:
│  │  │
│  │  ├─ stored SourceUpdatedAt >= ref.SourceUpdatedAt ? ──→ skip (incremental)
│  │  │
│  │  ├─ FetchAsync(ref)                     ── retry transient via backoff policy
│  │  │     └─ null / permanent failure ──→ record on source row, continue
│  │  │
│  │  ├─ IListingNormalizer.Normalize        ── pure
│  │  ├─ SourceContentHash unchanged ?       ──→ touch FetchedAt only, continue
│  │  │
│  │  ├─ repo.UpsertSourceAsync(listing + RawPayload)
│  │  │
│  │  ├─ IListingMatcher.Match(candidate, repo.FindByMatchKeyWindow(...))
│  │  │     ├─ Exact / Fuzzy≤1yr ──→ existing ListingId
│  │  │     ├─ Ambiguous         ──→ flag for review, do NOT merge
│  │  │     └─ None              ──→ mint lst_<32 hex>
│  │  │
│  │  ├─ repo.GetSourcesForListing(ListingId)        ── ALL sources, not just this one
│  │  ├─ IListingMerger.Merge(id, allSources)        ── pure; scalars by precedence,
│  │  │                                                 collections unioned
│  │  ├─ apply UserOverride                          ── after merge, keeps merge pure
│  │  └─ repo.UpsertListingAsync(batched, 500/txn)
│  │
│  └─ every N items: repo.CheckpointRunAsync(Cursor, counters)
│
└─ parse success rate < 80 % ?  ──→ ABORT, log loudly, mark run failed
```

### Browse and install

```
DiscoverViewModel.OnNavigatedToAsync
   └─ repo.QueryListingsAsync(filter, page)     ── paged; never all rows
        └─ tile binds CoverImageUrl
             └─ IListingImageCache.GetAsync(url)
                  ├─ cached (SHA256(url) on disk) ──→ local path
                  └─ miss ──→ fetch (.part → move) ──→ local path
                       └─ WPF decodes at DecodePixelWidth; no thumbnail file

User clicks Install
   └─ IListingInstallService.InstallAsync(listingId)
        ├─ IsDownloadable == false ──→ explain, stop
        ├─ IMirrorSelector.GetMirrors(listing)   ── d1, d2, archive.org/download
        └─ for each mirror until one succeeds:
             InstallFromUrlRequest { Url, ExpectedChecksum = Sha1, InstallFolderName }
             └─ IInstallFromUrlService.PrepareAsync         ←── UNCHANGED
                  download (.part, resumes across mirrors) → verify → extract → detect
                       └─ user confirms executable          ←── UNCHANGED
                            └─ IGameImportService.ImportAsync ←── UNCHANGED
                                 ├─ ICatalogService.EnsureEntryAsync  (identity, as today)
                                 └─ Game.ListingId = listingId        (the only new write)
```

Note where the arrows stop. Discovery hands a URL and a checksum to machinery
that already exists, and identity is minted by the existing service from the
executable that is now on disk. The relay never learns that a listing existed.

---

## 11. Libraries

| Need | Recommendation | Alternative rejected |
|---|---|---|
| HTML parsing | **AngleSharp** — one new package | HtmlAgilityPack: XPath-first, weaker HTML5 conformance. Regex: not a viable HTML parser |
| JSON | `System.Text.Json` — already in use | Newtonsoft: unnecessary |
| HTTP | `IHttpClientFactory` — already in use, three named clients | — |
| Retry | **`ExponentialBackoffRetryPolicy`, already written and tested** | Polly: a dependency for something already solved. Your rules forbid this |
| Parallelism | `Parallel.ForEachAsync` + `SemaphoreSlim` — BCL | TPL Dataflow: heavier than the problem |
| robots.txt | **Hand-rolled, ~40 lines** | A package for `User-agent` / `Disallow` / `Crawl-delay` is not worth the dependency |
| Compression | `System.IO.Compression.GZipStream` — BCL | — |
| Images | WPF `BitmapImage` — already in use | ImageSharp/Magick: not needed if thumbnails are decode-time (§8.3) |
| Tests | xunit + `LoopbackFileServer` + HTML fixtures | — |

**Net new dependencies: one (AngleSharp).** That is the smallest set that does
not involve writing an HTML parser.

---

## 12. Implementation roadmap

Each step is independently reviewable, independently mergeable, and leaves the
build at 0 warnings with the suite green. Steps 1–5 add no UI; steps 1, 5 and 10
add no network.

| # | Step | Deliverable | Tests |
|---|---|---|---|
| **0** | **Spike: resolve the IA enumeration query** | ✅ **Done** — §2.1a records the confirmed contract, §2.1b the `@rohankar` dead end and the collection-query decision | — |
| 1 | Models + normalizer | `SourceListing`, `CatalogListing`, `ListingDownload`, `ListingImageRef`, `TitleNormalizer`, `GenreVocabulary`. Pure, no DB, no network | Normalizer unit tests incl. `"Oregon Trail, The"`, roman numerals, edition suffixes, diacritics |
| 2 | Schema v7 + repository | Migration appended to `Migrations`: listings, source rows, normalised lookups, joins, aliases, run bookkeeping, **FTS5 with triggers**. `ICatalogListingRepository` with bulk upsert in one transaction | Round-trip, cascade, lookup dedup, FTS ranking and prefix search, FTS trigger sync, bulk-upsert transaction |
| 3 | `ICatalogSource` seam + pipeline | Interface, DI registration as an open set, duplicate-key guard at construction, `CatalogImportService` driven by a **fake in-memory source** | Pipeline order, checkpointing, resume after simulated kill, duplicate-key throw |
| 4 | Matcher + merger | `IListingMatcher`, `IListingMerger`, `ListingAlias`, `FieldProvenance`, `MergeTrace` | Year window, ambiguous non-merge, null-never-overwrites, union of collections, provenance recorded, trace captured only on demand |
| 5 | **Internet Archive source** | `InternetArchiveCatalogSource`: scrape enumeration with cursor, `/metadata/{id}` fetch, `mobygames_*` mapping, per-file `sha1`, `d1`/`d2` mirrors, `access-restricted` detection. **Validates the architecture end to end before a second source exists** | Fixture tests from the real JSON captured in §2.1; `LoopbackFileServer` for transport |
| 6 | Background import + notification | `CatalogImportBackgroundService` registered last, `CatalogUpdated` event, shell banner (§9.1–9.2) | Never blocks startup, honours the stopping token, no concurrent runs, event raised only on change |
| 7 | Discover page | `DiscoverViewModel` + `DiscoverView`, `DataTemplate`, `NavigationSection.Discover`, nav item, paged query, FTS search box, facet filters, virtualization | `DialogSmokeTests` case seeded with rows in every display state |
| 8 | Image cache | `IListingImageCache`, `AppPaths.ListingImageDirectory`, content-addressed names, `DecodePixelWidth`, LRU sweep | Cache hit/miss, `.part` cleanup, size cap, eviction pins installed covers |
| 9 | Install integration + mirrors | `IListingInstallService`, `Game.ListingId`, `IsDownloadable` gate, `IMirrorSelector` with advance-on-failure | Listing → `InstallFromUrlRequest` mapping; restricted item refuses cleanly; mirror failover resumes the `.part` |
| 10 | MyAbandonware source | `IRobotsPolicy`, `ISourceRateLimiter`, AngleSharp, **typed extractors in code** (§3.5), batch health check. Gated on robots.txt actually permitting it | Golden-file HTML fixtures; a deliberately broken fixture **must** trip the abort |
| 11 | Polish | Settings (source toggles, collection query, refresh interval, cache cap), `docs/` update, attribution in the UI | — |

**Definition of done for every step:** 0 warnings (CS1591 is visible — every
public member needs XML docs), suite green, and for any test asserting an absence
or a safeguard, the fault deliberately injected and the failure observed, per the
discipline in §11 of the handoff.

**Suggested first slice to review:** steps 0–4. That is a working
Internet-Archive-only import with no UI, no images and no scraping — the point at
which the design is proven or not, at the lowest cost.

---

## 13. Open questions

1. ~~The IA enumeration query~~ — **resolved**, §2.1a.
2. ~~How many items does `@rohankar` hold?~~ — **resolved**: none reachable
   (§2.1b). Retargeted at collection queries; `softwarelibrary_msdos_games`
   holds 8 898 items, which confirms the performance target.
3. **Which IA collections should ship as the default query?**
   `softwarelibrary_msdos_games` (8 898) is the obvious first. The full
   `softwarelibrary` is 237 657 and far too broad to import wholesale. The
   default is a setting, so this is tunable rather than blocking.
4. **Should the Discover page be a section, or a mode of the Library page?** A
   section — the library is "what I have", discovery is "what exists", and
   conflating them makes every filter ambiguous.
5. **Should imported listings be relay-synced?** **No**, explicitly. The relay's
   conflict rules require every synced field to be monotonic or single-writer
   (§8 of the handoff); merged multi-source metadata is neither.

---

## 14. As built — deviations from this plan, and why

Everything above is the plan. This section is the record of what changed while
building it, and it takes precedence.

### 14.1 Found by running against the live API, not by fixtures

| Finding | Consequence |
|---|---|
| **The Internet Archive stamps every metadata response with a `created` timestamp** regenerated per request | The content hash covered the raw payload, so every unchanged item looked changed and the incremental path never engaged. The payload is provenance, not content, and is now excluded from the hash |
| **Registering a source made it available by default** | A fresh install would have begun crawling a third-party service unprompted, and every pipeline test enumerated 8 898 live items — the suite went from 10 s to hanging. Availability now requires the user to switch discovery on |
| **A failed run satisfied the refresh interval** | One transient outage during a nightly refresh silently cost a day of updates, and looked identical to success. A run that recorded an error is due again |
| **Most Archive items carry no `mobygames_genre`** | Genre facets were empty. Subjects are now mined as a fallback, but strictly — an unrecognised subject is a tag, not a genre |
| **Archive titles carry `(DOS) (Dosbox in Browser) (VGA,SB)`** | The same title was unmatchable between sources. A parenthesised group is dropped from the match key when every word in it is a technical annotation — never wholesale, because `Command & Conquer (Red Alert)` is a different game |

### 14.2 Found by tests

- **The health check ran only between batches**, so a source holding fewer items
  than one batch was never judged and a wholly broken parser reported a clean
  run. It now also runs after the final partial batch.
- **Ambiguous matches counted against the parse rate.** A remake the matcher
  declines to place says nothing about whether the parser works, and a catalogue
  full of them would have aborted a healthy pass. Fetch failures and placement
  failures are now counted separately.
- **`CompanyNormalizer.Clean` stripped a trailing full stop**, turning
  `Accolade, Inc.` into something no source had said. Only stray commas and
  semicolons are removed.

### 14.3 Deliberate departures from the plan

| Plan | Built | Reason |
|---|---|---|
| FTS5 **external-content** table | A **standard** FTS5 table, written by the repository inside the listing's transaction | External content requires the content table to carry the indexed columns, which would have meant denormalised `Developer`/`Publisher`/`Genres` columns on `CatalogListing` — the opposite of the normalisation this schema exists to get right. The repository is the only write path, so there is exactly one caller to keep honest |
| Triggers keep the index in sync | The repository does, in the same transaction | A trigger would have had to aggregate across three join tables. Harder to reason about, impossible to test in isolation, and no safer given a single writer |
| `LIKE` fallback if FTS5 is unavailable | No fallback | `Microsoft.Data.Sqlite` bundles SQLite with FTS5 enabled, so availability is a build-time fact rather than a runtime variable. A fallback path that cannot occur is a path that cannot be tested |
| `Game.ListingId` in a later migration | In v7 | Nothing had shipped v7 yet, so a second migration would have been ceremony |
| MyAbandonware supplies download mirrors | **Metadata only** | Its `robots.txt` disallows `/download/*` for every crawler. See §14.4 |
| Enumerate MyAbandonware by browsing | By its **sitemap** | Advertised in the site's own `robots.txt`: one compressed file instead of hundreds of page fetches, and the gentler thing to do |
| Selector map in overridable JSON | **Typed extractors in code** | Reversed at the user's direction during review, and the live site vindicated it: the page publishes `schema.org/VideoGame` JSON-LD carrying every field this source contributes, which is a far more stable anchor than any CSS selector |

### 14.4 The MyAbandonware constraint

Checking `robots.txt` before writing the source turned out to decide its shape.
The published rules disallow, for every crawler:

```
/download/*   /manual/*   /search/*
/game/rate/*  /game/comment/*  /game/playcomment/*
/game/vote/*  /game/playstat/*  /favorites/*
```

`/game/{slug}` and `/browse/*` are permitted, and a sitemap is advertised.

**So the source imports metadata and never collects a download address.** A game
only MyAbandonware describes is listed and not installable; a game it shares with
the Internet Archive is installable through the Archive and better described
because of MyAbandonware. That is the multi-source merge earning its keep.

`IRobotsPolicy` enforces this at runtime rather than by convention, and the test
fixture contains a `/download/` link precisely so the suite fails if the parser
ever starts following one.

### 14.5 Known limitations

1. **The memory-cost of `ImportMode.Remerge` grows with the catalogue.** It walks
   every listing; at 8 898 items that is seconds, and it has not been measured at
   ten times that.
2. **`FieldProvenance` is written; `MergeTrace` is computed but only logged.** The
   trace is not persisted — turning on `CaptureMergeTrace` writes it to the log
   rather than a table. Persisting it is a schema change nobody has needed yet.
3. **No end-to-end run against MyAbandonware.** The parser is tested against a
   page captured from the live site, and the robots policy against the site's
   real rules, but no full import has been performed. The Internet Archive path
   has been run end to end against the live API.
4. **Screenshots are stored but not displayed.** The Discover tile shows a cover;
   the details page does not yet show the screenshot set.
5. **`ListingAlias` has no user interface.** The table and repository methods
   exist and are tested, so a manual "these are the same game" decision is
   expressible in data but not yet from the page.
6. **The ambiguous-match queue is a log line.** When the matcher declines to place
   an observation it is counted and logged; there is no view listing them for
   review.

---

## 15. Downloads, saves and sourcing — the second round

Four capabilities added after the catalogue itself. Three are new subsystems;
one is a refusal.

### 15.1 Pluggable download transports

`IDownloadService` now owns the *rules* and delegates the *transfer*.

```
DownloadService            validate → name → [transport] → checksum → rename
   ├── Aria2DownloadTransport    priority 0, Http|Torrent, opt-in
   └── HttpDownloadTransport     priority 100, Http, always available
```

Validating the address, resolving a file name, verifying the checksum and
renaming into place stay in one implementation. A second engine cannot get any
of them subtly wrong, and a third would not have to reimplement them.

The extraction was done as a pure move: all 26 existing download tests passed
unmodified through it, which is the evidence that behaviour did not change.

**aria2 is opt-in.** Letting a launcher start an external process is worth
deciding explicitly rather than inheriting because a binary is on the path.
Missing or disabled, it reports itself unavailable and the built-in engine runs.

**Driven by CLI, not the RPC daemon.** RPC reports richer progress but means
owning a background process, a port and a secret for a launcher that downloads a
file occasionally. Progress is read from the size of the file on disk, so it
cannot be broken by a change to aria2's console format and correctness never
depends on parsing it.

**Magnet is accepted only now that something can move it**, and only for an
address already classified as a torrent. `file://` and `ftp://` are still
refused — a downloader that accepts `file://` turns a pasted string into an
arbitrary local file copy.

### 15.2 Archive.org torrents

Items now also offer their own `{identifier}_archive.torrent`. The Archive
generates one for most items and asks that large transfers use it, because peers
carry the load instead of its servers.

Ranked **last**, whatever the source said. It needs an engine that may not be
installed, so a direct address is always what an install reaches for first and
the torrent is a bonus rather than a dependency. Items flagged
`noarchivetorrent` are respected.

### 15.3 Ludusavi save-path resolution

`ISavePathResolver` answers "where does this game keep its saves" from the
Ludusavi community manifest — a data dependency, not hardcoded paths, because
the knowledge is large, changes constantly, and is already curated.

| Decision | Reasoning |
|---|---|
| Minimal deserialisation types, unmatched properties ignored | The parser walks past `launch` and `installDir` without building objects. Measured: 16 MB indexes in **~4 s**, ~20 MB resident |
| Loaded lazily on first lookup | Nothing about a 16 MB download and parse belongs on the startup path |
| Config-only and wrong-platform entries dropped while indexing | What survives is what a save feature wants, and it is a fraction of the file |
| `Expand` returns **null** rather than a half-expanded path | A literal `<base>/saves` is a directory nobody has; acting on it means searching nonsense |
| Steam id preferred over title | It identifies a game exactly; a title has to be matched and can be matched wrongly |

Validated against the real published manifest: Stardew Valley, Terraria,
Half-Life and Doom resolve to correct absolute paths, and `steam:220` resolves to
Half-Life 2 by id alone.

**There is no save-sync relay pipeline to integrate into** — cloud saves remain
deferred (§5 of the handoff). The resolver is built as the standalone service
that pipeline will need, and wired into the integration point that does exist:
the achievement editor's save-file picker now opens at the game's known save
directory.

### 15.4 Sourcing adapters, and one deliberate refusal

`ISourcingAdapter` answers "given this page, what can be downloaded" — a
different question from `ICatalogSource`'s "what games exist", with different
failure modes. Hence a separate open set rather than more methods on the
existing one.

**`MyAbandonwareSourcingAdapter` refuses, and that is its whole job.** The site
disallows `/download/*` for every crawler, so there is no download it can
honestly produce. It is written as a real adapter rather than left as a gap so
the decision is stated once, checked against the live rules rather than assumed,
and covered by a test that fails if the behaviour ever changes — a missing
adapter would be indistinguishable from an oversight.

**`DownloadSourceResolver` does the useful part.** When a listing carries no
address of its own it asks the adapters, and failing that looks for the same game
described elsewhere in the catalogue. A game MyAbandonware describes and the
Archive also holds is installable through the Archive and better described
because of MyAbandonware. It follows the importer's own ±1 year rule, so *Prince
of Persia* 1989 never borrows a download from the 2008 remake.

### 15.5 Explicitly not implemented

The original request also asked for browser-header impersonation, extraction of
direct links from crawler-blocked paths, and handling of Cloudflare/Turnstile
challenges. Those are not built, and the code is arranged so their absence is
visible rather than accidental:

- `IRobotsPolicy` is consulted before every request, not once at startup.
- The MyAbandonware parser's test fixture contains a `/download/` link
  specifically so the suite fails if extraction is ever added there.
- The adapter distinguishes `DisallowedByRobots` from `Unreachable`, so a
  permanent refusal is never retried as though it were a transient failure.

The capability that request wanted — installing a game discovered through a
metadata-only source — is delivered by §15.4 instead.

### 15.6 Known limitations of this round

1. **aria2 has not been exercised against a real download.** Availability
   detection, argument construction and the fallback are covered by tests; no
   file has been fetched through it, because the binary is not installed here.
2. **Torrent progress is coarse.** Reported from the growing file for HTTP; a
   multi-file torrent reports only on completion.
3. **Registry save locations are reported, not read.** A caller is told the key
   exists so it can decide; nothing here exports it.
4. **`SavePathQuery.SteamAppId` is never populated by the launcher** — it has no
   Steam integration. The parameter exists for a caller that does.
5. **The Ludusavi manifest download is slow** (~3 minutes on a domestic
   connection for 16 MB). It is cached for a fortnight and fetched lazily, so
   this is a one-off background cost rather than a recurring one.

## 16. Navigation — consolidating eight sidebar rows into five

Adding Discover and Downloads took the sidebar to eight rows. A sidebar that
grows a row per page stops being navigation and becomes a list, so the rows were
reduced to five destinations, each answering a different question:

| Section | Question | Pages |
|---|---|---|
| Library | what do I have | Overview, Installed games, Collections, Achievements |
| Search catalog | what exists | Discover |
| Downloads | what is transferring | Queue |
| Friends | who am I playing with | Friends |
| Settings | how is this configured | Settings |

Settings keeps its place at the foot, apart from the content sections.

### 16.1 Sub-navigation is data, not a second enum

`NavigationSection` names only the five. What is inside one is a
`SubNavigationItem` — key, label, and a delegate that shows the page — built by
`MainWindowViewModel.BuildSubSections`. A second enum plus a switch to map it
would have put the list of tabs and the code that opens them in two places that
had to be kept in step.

Sections with one entry still return it; the strip hides itself unless there is
more than one. A single tab that cannot be switched away from is decoration.

### 16.2 Pages are kept alive

`INavigationService.NavigateToKeptAliveAsync<T>` resolves a page once and
remembers it. The page view models stay registered transient, so an ordinary
navigation still starts clean; the shell opts into reuse at the call site rather
than changing the registration for every caller.

This is what makes switching tabs cheap enough to be worth doing: Discover keeps
its search text, facet filters and scroll position when the user glances at the
download queue and comes back. `OnNavigatedToAsync` still runs on each visit, so
data that should be fresh is refreshed while view state is not thrown away.

Which tab was last open is remembered per section, so returning to Library lands
on Achievements if that is where the user left it.

### 16.3 Sideways movement does not stack

Neither a section change nor a tab change pushes anything onto the back stack,
and both clear it. Back exists for drilling into a game and coming out again.

The alternative was tried and is worse: if tab changes stacked, pressing Back
would show the previous tab's page while the strip still highlighted the tab the
user had chosen. The two would disagree, and nothing on screen would explain
why.
