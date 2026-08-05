# GameLauncher — Project Handoff

**Purpose of this document:** the primary context source for continuing
development in a fresh session. It assumes no access to prior conversation
history. Read this plus the codebase and you should be able to continue without
guessing.

**State at handoff:** the planned roadmap is complete — stages 1–14. Solution
builds with **0 warnings, 0 errors**. **148 tests pass.** Client schema **v6**,
relay schema **v1**. What remains is optional work, listed in §5 and §12.

Companion documents, all current and worth reading before touching their areas:

- [`README.md`](../README.md) — what the project is, how to build, run and reset it
- [`docs/catalog-identity.md`](catalog-identity.md) — catalog identity, merge, relay migration
- [`docs/relay-architecture.md`](relay-architecture.md) — auth, sync, conflict resolution, portability
- [`docs/deployment.md`](deployment.md) — hosting the relay: proxies, tunnels, backups, security

**If you just want to build and run it, skip to [Appendix A](#appendix-a--build-run-and-operate).**

---

## 1. Project overview

### Purpose

A Steam-style Windows game launcher for a personal, locally-managed game
library, plus a self-hosted relay service that adds the social layer: friend
codes, presence, and synchronised achievements.

The launcher manages games the user already has on disk. It is not a store, and
it has no notion of ownership or licensing — a library entry is a pointer to an
executable plus the metadata and statistics the launcher has accumulated about
it.

### Current goals and scope

- A complete local launcher: library, artwork, launching, playtime, collections,
  achievements.
- A relay that is optional. Everything except friends works with no relay
  configured, and the launcher never blocks on the network.
- A Steam-style achievement platform: definitions belong to a shared catalog,
  progress belongs to the user, unlocks synchronise through the relay.

### Explicitly out of scope

These were ruled out at the start and should stay out:

- No scraping, parsing, or bespoke integration for any specific game
  repack/crack/warez distribution site.
- No torrent or magnet link handling.
- **No memory writing, process modification, or DLL injection.** Memory
  achievements are read-only inspection only. See §8.
- Cloud saves and workshop/mods are deferred, not forbidden (§5).

### Technology stack

| Component | Stack |
|---|---|
| Desktop | .NET 8, WPF, CommunityToolkit.Mvvm 8.4.2 |
| Hosting/DI | Microsoft.Extensions.Hosting 8.0.1 |
| Client data | SQLite (Microsoft.Data.Sqlite 8.0.11) + Dapper 2.1.79 — **no Entity Framework** |
| Real-time | Microsoft.AspNetCore.SignalR.Client 8.0.11 |
| Archives | SharpCompress 0.50.3 |
| Notifications | Microsoft.Toolkit.Uwp.Notifications 7.1.3 (not yet used — see §12) |
| Relay | ASP.NET Core 8 Minimal API + SignalR, SQLite + Dapper |
| Tests | xunit 2.5.3, Microsoft.AspNetCore.Mvc.Testing 8.0.11 |

### Target platforms

- **Desktop:** Windows 10 1809 (build 17763) or later, x64.
  TFM is `net8.0-windows10.0.17763.0` — the raised platform version is required
  by the Windows toast API. `SupportedOSPlatformVersion` keeps the runtime
  requirement at 1809.
- **Relay:** any .NET 8 host. Currently self-hosted on a laptop; designed to move
  to a VPS.

### Build environment note

`global.json` pins the SDK to **8.0.423** with `rollForward: latestFeature`. The
development machine also has SDK 10 installed; without the pin, `dotnet new` and
NuGet default to .NET 10 artefacts. All framework packages are deliberately
pinned to the **8.0.x** line — NuGet resolves `Microsoft.Extensions.*` and
SignalR to 10.0.x by default, which would put .NET 10 libraries in a .NET 8 app
and skew against the ASP.NET Core 8 relay.

---

## 2. Current architecture

### Solution structure

```
GameLauncher.sln
├── GameLauncher.Shared    net8.0            wire contracts only, zero dependencies
├── GameLauncher.Desktop   net8.0-windows…   WPF client (WinExe)
├── GameLauncher.Relay     net8.0            ASP.NET Core service
└── GameLauncher.Tests     net8.0-windows…   xunit, references all three
```

### Dependency relationships

```
Desktop ─┐
         ├──> Shared        (Shared depends on nothing)
Relay ───┘

Tests ──> Desktop, Relay, Shared
```

`Shared` is deliberately dependency-free so both sides can reference it without
dragging in either's implementation stack. Per project conventions it holds
**DTO contracts only** — no behaviour, no business logic.

Desktop and Relay never reference each other. They communicate solely through
`Shared` contracts.

### Major services and interfaces

**Desktop — data (`Services/Database`, `Services/Catalog`)**

| Interface | Responsibility |
|---|---|
| `IDbConnectionFactory` | Opens SQLite connections with pragmas applied |
| `IDatabaseInitializer` | Versioned migrations (see §6) |
| `IGameRepository` | Library CRUD, playtime accrual, collection assignment |
| `ICollectionRepository` | Collections and game counts |
| `IAchievementRepository` | Definitions, unlocks, progress, sync watermarks |
| `IPlaySessionRepository` | Session lifecycle, unsynced queue |
| `IFriendCacheRepository` | Offline friend list cache |
| `ICatalogRepository` / `ICatalogService` | Shared catalog identity, promotion, merge, demotion |
| `ISampleDataSeeder` | Opt-in dev data (`--seed-sample-data`) |

**Desktop — application logic**

| Interface | Responsibility |
|---|---|
| `ILibraryService` | Notes, metadata updates, uninstall, directory sizing |
| `IExecutableInspector` | Version resource + PE header inspection, launch validation |
| `IIconExtractionService` | Win32 icon extraction to PNG |
| `IGameScanService` | Recursive folder discovery |
| `IGameImportService` | Adds a game: metadata, icon, size, catalog entry, insert |
| `IGameLaunchService` | `Process.Start`, session tracking, playtime accrual |
| `IDownloadService` | Resumable HTTP download, checksum verification |
| `IArchiveExtractionService` | SharpCompress extraction with traversal guards |
| `IInstallFromUrlService` | Orchestrates download → verify → extract → detect |
| `ISettingsService` / `IThemeService` / `IIdentityGenerator` | Settings, palette, friend-code generation |

**Desktop — relay (`Services/Friends`)**

| Interface | Responsibility |
|---|---|
| `IRelayApiClient` | HTTP seam: relay-info, register, friends, catalog resolve, achievement sync |
| `IRelayHubClient` | SignalR seam: presence, friend requests, connection state |
| `IRelayIdentityService` | Which relay are we on; migrate when it changes |
| `IRelaySyncService` | Drains outbound queues |
| `IFriendsService` | Merged friend list (cache + live) |

**Desktop — achievements (`Services/Achievements`)**

| Interface | Responsibility |
|---|---|
| `IAchievementProvider` | **Decides only.** No persistence, no network |
| `IAchievementEngine` | Dispatches providers, persists, raises events, lists providers |
| `ISaveFileReader` | JSON/XML/INI/regex value extraction |
| `IProcessMemoryReader` | Read-only process memory |
| `AchievementWatcherService` | Decides *when* evaluation runs (hosted service) |
| `IAchievementNotificationService` | Queues earned achievements and announces them one at a time (hosted service) |

**Relay**

| Component | Responsibility |
|---|---|
| `IRelayConnectionFactory` | The single seam between relay and database engine |
| `RelayDatabaseInitializer` | Portable schema + relay instance identity |
| `ITokenService` | Token minting and hashing, friend code generation |
| `RelayAuthenticationHandler` | Bearer scheme, database-backed |
| `PresenceHub` | Strongly-typed `Hub<IPresenceClient>` |
| `PresenceTracker` | In-process connection counting per user |
| `RelayEndpoints` | All HTTP endpoints, one file |

### Client/server communication flow

```
Startup (hosted services run in registration order):
  1. SettingsStartupService   load settings, apply theme  (must precede any window)
  2. DatabaseStartupService   migrate schema, reconcile sessions, repair fingerprints
  3. AchievementNotificationService  subscribe to the engine (must precede step 4)
  4. AchievementWatcherService  subscribe to launch events, library-wide startup pass
  5. RelayCoordinatorService  identity → connect → sync   (never blocks startup)

RelayCoordinatorService.StartAsync:
  friends.LoadFromCacheAsync()          ← cache first, before any network call
  └─ background: IRelayIdentityService.EstablishAsync()
       GET /relay-info → relayId
       relayId != ActiveRelayId ?  → migrate (see §7)
       no credentials for relayId ? → POST /register
     then IRelayHubClient.StartAsync()  ← supervisor loop, returns immediately

On Connected:
  RelaySyncService.SynchronizeAsync()
     1. promote provisional catalog entries   POST /catalog/resolve
     2. push queued unlocks                   POST /sync/achievements
  then re-assert presence (the relay cleared it on disconnect)

While running:
  hub → PresenceChanged / FriendRequestReceived / FriendRequestResolved
      → FriendsService updates its map → UI event on the dispatcher
```

### Offline-first design

This is the central constraint. Concretely:

- **Local SQLite is authoritative** for library, installs, launching, sessions,
  and queued sync operations. The relay is authoritative only for identity,
  friendships, presence, catalog ids and synchronised history.
- **Nothing blocks on the network.** `IRelayHubClient.StartAsync` returns
  immediately and supervises in the background. Registration failure is logged
  and retried later. The window opens regardless.
- **Queues are not a data structure.** Each is an indexed predicate over data
  already stored — `CatalogEntry WHERE IsProvisional = 1`,
  `AchievementUnlock WHERE SyncedAt IS NULL`,
  `PlaySession WHERE SyncedAt IS NULL`. Nothing is lost if the process is killed
  mid-pass, and there is no queue file to corrupt or replay.
- **Every sync operation is idempotent**, which is what makes blind retry safe.
- **Offline is not an error state.** `RelayConnectionState` distinguishes
  `Disabled` (no relay configured — a blank setting) from `Offline` (configured,
  unreachable — a problem). Showing "offline" for both would imply something is
  broken when nothing is.

---

## 3. Completed stages

### Stage 1 — Solution scaffold + Shared DTOs

Four projects, `global.json` SDK pin, `Directory.Build.props` with nullable,
implicit usings, and `GenerateDocumentationFile` (CS1591 left **on** as a
compiler-enforced check that public APIs stay documented).

Shared contracts: registration, presence, friends, friend requests, errors,
`FriendCodeContract`, `IPresenceClient`, `PresenceHubContract`.

**Design decisions**

- **Friend code format `GL-XXXXX-XXXXX`, Crockford Base32.** The alphabet omits
  I, L, O and U, removing 1/I/L and 0/O ambiguity when a code is read aloud or
  copied by hand. Ten symbols = 50 bits.
- **`IPresenceClient` is a shared strongly-typed hub contract.** The relay
  implements `Hub<IPresenceClient>`; the client subscribes via `nameof` against
  the same interface. A renamed method becomes a build error rather than a
  handler that silently never fires.

### Stage 2 — Desktop shell

Generic Host + DI, navigation service with back stack and load cancellation,
Steam-style dark theme (5 resource dictionaries), rolling file logger, global
exception handling on all three channels (dispatcher, AppDomain, unobserved
tasks).

**Implementation details**

- `NavigationService` cancels the previous navigation on each new one. Without
  it, navigating away from a slow page and back would leave two loads racing.
- `FileLoggerProvider` writes **synchronously and flushes**. Deliberate: a
  buffered async writer loses the final and most interesting entries precisely
  when the process is crashing.
- Dark title bar via `DwmSetWindowAttribute`, best-effort.

**Gotcha that will bite again:** WPF's XAML markup pass compiles through a
generated `*_wpftmp` project that imports `System.Windows.Shapes` (which has its
own `Path`) but **not** `System.IO`. File-system code compiles in the main pass
and fails in that one. Fixed by a project-level `<Using Include="System.IO" />`
in the Desktop csproj — do not remove it.

### Stage 3–4 — SQLite schema, repositories, sample data

Six tables (v1), versioned migrations via `PRAGMA user_version`, six Dapper
repositories, WAL mode, enforced foreign keys.

**Design decisions**

- **Two tables beyond the original spec.** `Collection` — `Game.CollectionId` is
  a foreign key with nothing to point at otherwise. `PlaySession` — "track start
  time, end time, session duration" needs somewhere to live;
  `Game.PlaytimeSeconds` only holds the total.
- **Dapper type handlers** for `DateTimeOffset` (ISO-8601 round-trip) and
  `IReadOnlyList<string>` (JSON array).
- **Tags are written explicitly, not via the type handler.** Dapper resolves a
  *parameter* by the value's runtime type and expands an array into an
  `IN (...)` clause. Reads use the handler; writes serialise at the call site.
  This asymmetry is documented in `GameRepository` and is a trap for whoever
  edits those queries next.
- **Sample data is opt-in** (`--seed-sample-data`). It points at executables
  that do not exist; auto-seeding would leave the user hand-deleting rows.

### Stage 5 — Library UI

Grid/list toggle, search over titles *and* tags, five sort orders, collection
filter, game details page. Launch service, uninstall with protected-path guards,
dialog service, UI dispatcher were pulled forward here because they would
otherwise have meant dead buttons.

**Implementation details**

- `GameItemViewModel` snapshots `ExecutableExists` at construction. Binding it
  directly would turn scrolling a large library into a storm of disk checks.
- Playtime is measured with `Stopwatch` (monotonic), not wall clock — a
  daylight-saving change or NTP correction mid-session cannot distort it.
- Uninstall refuses to recursively delete a drive root or system folder.
- `IUiDispatcher` exists because `Process.Exited` fires on a thread-pool thread
  and subscribers update `ObservableCollection`s, which throws.

### Stage 6 — Add Game + Scan Folder

**Implementation details**

- **Icon extraction** tries `PrivateExtractIcons` at 256px first, falling back to
  `ExtractIconEx`. Both are pure Win32 (no `System.Drawing.Common`).
  `ExtractIconEx` alone returns the 32×32 system icon, visibly blurred on a
  150×225 cover tile. Every handle is released with `DestroyIcon`.
- **PE header parsing by hand** for architecture and GUI/console subsystem —
  never loads the image into this process. Subsystem is at
  `peOffset + 24 + 68` for both PE32 and PE32+ (PE32+ spends 8 extra bytes on a
  64-bit image base but drops `BaseOfData`).
- **Title derivation** prefers ProductName → FileDescription → prettified file
  name; strips Unreal-style `-Win64-Shipping` suffixes; rejects engine
  placeholders (`Unity Player`, `DefaultCompany`).
- **Scan** walks iteratively with a stack. `SearchOption.AllDirectories` abandons
  the whole enumeration on the first unreadable folder, and a games drive
  reliably has one. Skips redist/anti-cheat folders and reparse points (junction
  loops). "launcher" is deliberately **not** filtered — for many games it is the
  entry point the user wants.
- **Launch validation** runs immediately before `Process.Start`, not at import
  time: a game can be moved or replaced by an updater in between.

**Schema (v2)** — see §6. Landed identity ahead of the achievement engine
because columns are cheap to add later and *identity* is not.

### Stage 7 — Install from URL

Resumable download (HTTP Range), cancel, checksum verification (algorithm
inferred from digest length), SharpCompress extraction, executable
auto-detection reusing the folder scanner, user confirmation before registering.

**Implementation details**

- Writes to `.part` and renames only after the checksum passes, so the final path
  never holds a partial or corrupt file. A failed checksum **deletes** the file —
  resuming corruption never converges.
- Resume is attempted with a range request and judged by the response status, not
  by `Accept-Ranges`: some servers advertise it and ignore it.
- The download `HttpClient` has **`Timeout = InfiniteTimeSpan`**. The default
  100 s covers the response body and would abort any large download.
- **Path traversal blocked in two places:** the `Content-Disposition` filename
  (attacker-controlled) and archive entry paths ("zip slip"). A leading separator
  is stripped and treated as relative — safe, and archives legitimately contain
  such entries; a drive-qualified path is rejected outright.
- Collapses a single top-level archive folder so `InstallDir` points at the game,
  not a wrapper.

**Note:** SharpCompress 0.50 renamed `ArchiveFactory.Open` → `OpenArchive(path,
ReaderOptions)`. Discovered by reflection, not guesswork.

### Stage 8 — Collections, Settings, theming

Settings written atomically (temp file + move) so losing power mid-save cannot
take the friend code with it. Two palettes (Dark, Midnight); every hard-coded hex
was moved into the palette first, so a theme is a pure dictionary swap.

**Honest constraint:** `StaticResource` binds once, so a theme change applies on
**restart**. The settings page says so rather than appearing half-broken. A light
theme is now a pure palette swap but is not shipped because its contrast could
not be verified without looking at it.

### Stage 9 — Relay

Schema, registration, device registration, `PresenceHub`, sync endpoints.

See §7 for the full architecture. Key points: PostgreSQL-compatible schema
decisions, unsalted SHA-256 token hashing (reasoned in §8), per-device
credentials from the start.

### Stage 10 — SignalR client, friends, offline sync

**Implementation details**

- **Reconnect needs two mechanisms.** SignalR's `WithAutomaticReconnect` only
  covers a connection that drops *after* succeeding once. It does nothing for a
  first connect that never succeeds — the common case when the launcher starts
  before the relay is up. A supervisor loop covers that and resumes if the
  connection closes permanently. Both share one backoff policy: exponential,
  jittered, capped, **never returning null** (SignalR's default gives up after
  ~30 s, right for a web page, wrong for a launcher open across a router reboot).
- **Token refresh** is `AccessTokenProvider` reading settings per attempt rather
  than capturing once. That is the whole of what refresh means here — relay
  tokens do not expire.
- Losing the connection marks everyone offline rather than leaving stale
  "online" claims the launcher cannot justify.

**Bug found by tests:** `SignalRRelayHubClient` implemented only
`IAsyncDisposable`, which makes the DI container's synchronous `Dispose()` throw
— on every application exit. It now implements both. **Do not remove the
synchronous `Dispose`.**

**Schema (v5)** — `PlaySession` gains `SessionKey`, `DeviceId`, `SyncedAt`.

### Stage 10b — Relay identity and migration

Relays identify themselves via `GET /relay-info` with an id stored in their own
database. See §7 and §8.

**Schema (v4 client, relay v1 addition)** — `CatalogAlias`,
`CatalogEntry.SupersededByCatalogId`, `RelayMetadata`.

### Stage 11 — Achievements, end to end

Provider architecture, engine, four providers (meta, save-file, memory, manual),
watcher service, progress persistence, hidden achievements, plus the whole
interface: achievements page, editor, and toast presenter.

**Schema (v6)** — `AchievementDefinition.ProviderKey`. The UI half of the stage
added **no schema change at all**; everything it shows was already stored.

**Implementation details**

- **Concealment lives in the view model, not the template.** A template that
  simply declines to draw a hidden achievement's title still has the real text
  bound into the visual tree, where a tooltip, an automation client or a copy
  command can reach it. `AchievementItemViewModel.DisplayTitle` /
  `DisplayDescription` / `DisplayIconPath` substitute at that boundary, so the
  secret never arrives at the interface. Progress is suppressed too — "34 / 50"
  discloses both the goal and how close the player is.
- **The page cannot evaluate anything.** It depends on `IAchievementRepository`
  for rows and on `IAchievementEngine` only for
  `Providers` / `IsProviderAvailable`, which is metadata. There is no path from
  the page to an unlock.
- **The editor's Test Read is structurally inert.** `TestAsync` does not route
  through `RunAsync`; persistence and notification both live there, so testing
  reaches the provider and nothing else. Four tests fail if that changes.
- **Toast queueing is a service, not a view model** (`Services/Notifications`).
  Ordering and dwell are application logic, and the project's rules keep that out
  of view models. It is also registered as a hosted service *before* the watcher,
  so it is subscribed before the startup pass — subscribing when the shell window
  is first built would silently drop anything that pass earns.

**Bug found by tests (stage 11):** the notification service originally re-raised
`CurrentChanged` when a new unlock arrived while one was already on screen, to
refresh the "+N more" badge. That made every subscriber counting announcements
see the same one repeatedly — a test asserting three unlocks produced
`[ACH_ONE, ACH_ONE, ACH_ONE, ACH_TWO, ACH_THREE]`. The pump is now the sole
publisher and the event means what its name says. The badge is therefore a
snapshot from when the announcement began; see §8.

### Stage 12 — Polish

A sweep rather than a feature. There were no TODOs, no `NotImplementedException`,
and no undocumented public members to find — CS1591 had been kept visible from
stage 1 precisely so that could not accumulate. What it did find:

- **The Home page was a placeholder.** It carried the text "Recently played
  titles and library highlights" and showed neither, on the first screen the
  application opens to. It now shows a recently-played row and three library
  totals, built entirely from repository methods that already existed
  (`GetRecentlyPlayedAsync`, `CountAsync`, `GetTotalPlaytimeSecondsAsync`,
  `GetUnlockCountAsync`) — no new services and no schema change. It has both an
  empty-library state and a nothing-played-yet state, and each is realised by its
  own smoke test.
- **Home opens a game rather than launching it.** Launching from the landing page
  would mean a second copy of the details page's error handling — missing
  executable, refused process start — inside a view model whose job is to
  summarise. One extra click is worth not having that logic in two places.
- **`NavigationSection.Search` was removed.** Nothing mapped to it and the
  navigation switch would have thrown had anything reached it. Searching happens
  inside the library page, over titles and tags. `Settings` keeps its value of 6;
  the gap is deliberate, because renumbering an enum gains nothing.
- **Two dead members went from `AchievementItemViewModel`.** `GameTitle` became
  redundant when the achievements page started grouping by title — the heading
  carries it — and `ProgressTarget` only shadowed `Definition.ProgressTarget`
  without being read.

---

## 4. Current implementation state

### Works today (manually verified by running the software)

- Launcher starts, migrates schema v0→v6, opens, navigates. Verified repeatedly.
- **The Achievements page opens in the running application** — navigated to from
  the sidebar with a clean log: no resource failure, no binding error, exit code
  0.
- Library renders 8 seeded games in grid and list; details page renders with
  achievements.
- Relay runs as a process; `/health` and `/relay-info` respond.
- **Registration → connection → sync, end to end against a real relay process:**
  relay assigned a friend code, state went `Connecting → Connected`, and all 8
  provisional catalog entries were promoted on first connect.
- **Catalog resolution across users:** two different registered users resolving
  the same fingerprint received the *same* catalog id.
- **Achievement sync conflict rules over HTTP:** push → `accepted=1`; replay →
  `accepted=0`, unchanged; push earlier → time moves earlier; push later → does
  **not** move forward; pure fetch recovers history.
- **Relay identity:** id stable across relay restarts (same database), different
  for a different database.
- Settings persist; first run generates a valid friend code.
- Externalised configuration works — the relay database path was supplied via
  the `Relay__Database__ConnectionString` environment variable.

### Verified by automated tests only (not exercised by hand)

- Add Game, Scan Folder, and Install from URL **dialogs**: they are realised by
  the WPF smoke tests (construction + full layout pass), but no one has clicked
  through the flows. The underlying services are unit-tested.
- **The download path, thoroughly, but only against a loopback server.** Resume,
  redirects, checksums, cancellation, interruption recovery and extraction are
  all covered end to end over a real socket — no download from a real host on
  the internet has been performed by this code.
- Collections page membership moves.
- Relay migration between two relays — six integration tests, but never done by
  hand with two real relay processes.
- The achievement engine end to end. No achievement has been earned by actually
  playing a game.
- **The achievement editor and the toast overlay.** Both are realised by the WPF
  smoke tests — every rule panel, the missing-provider banner, and a toast
  mid-announcement — and the editor's behaviour is covered by unit tests, but
  nobody has typed a rule into the dialog by hand.
- Memory provider against a real running game — the `ProcessMemoryReader` has
  **never been pointed at a live process**. Its failure paths are handled but
  its success path is unproven in reality.

### Compile-verified only

- **Windows toast notifications.** `Microsoft.Toolkit.Uwp.Notifications` is
  referenced and the TFM was raised for it, but the shipped presenter is an
  in-app overlay and no code calls that package. See §13.

### Known bugs and limitations

1. **No download integration test against a live HTTP server.** Resume,
   redirects and real checksums are unit-tested at the helper level only. Needs a
   loopback test server.
2. **`PresenceTracker` is in-process.** Correct for one self-hosted instance;
   wrong the moment the relay runs on more than one node (needs a shared store
   plus a SignalR backplane).
3. **No PostgreSQL implementation.** The schema and every query are portable;
   `RelayDatabaseProvider.Postgres` throws at startup by design. Adding it is a
   package reference plus one ~20-line factory.
4. **Playtime does not sync.** Only the schema for it exists (deliberate — §8).
5. **Sample catalog entries hold ids from a throwaway relay.** During end-to-end
   verification the 8 seeded entries were promoted against a temporary relay that
   no longer exists. Harmless (sample data), but if you connect to a real relay
   they will be treated as foreign and re-resolved — which is correct behaviour.
6. **Achievement icons cannot be set in the editor.** `IconPath` is preserved
   across an edit and rendered when present, but nothing populates it — there is
   no icon picker and no import path.
7. **The toast backlog badge is a snapshot.** "+N more" is counted when an
   announcement appears and is not refreshed while it is on screen. Deliberate:
   see §8.
8. **Stats are still unwired.** `StatApiName` and `ProgressTarget` exist and the
   `GameStat*` tables exist, but no provider reads stats, and the editor cannot
   author against one.
9. **Not a git repository.** No version control has been initialised.

---

## 5. Remaining roadmap

**The planned roadmap is finished.** Everything below is optional.

### Recommended order

1. **Achievement icon picker**, which finishes the editor — the most visible
   unfinished edge in the product.
2. **Stat-driven achievements**: a provider that reads `GameStatValue`, plus the
   repository those tables never got. This is the last piece of the achievement
   model that exists in the schema but not in code.
3. **PostgreSQL factory**, when a VPS is actually provisioned. One package and
   one ~20-line class; see [deployment.md](deployment.md).
4. **Remaining test gaps** (§11) — none blocking, and two of them need hardware
   or an implementation that does not exist yet.
5. **A light theme.** Now a pure palette swap, but its contrast is unverified.

### Intentionally deferred

- **Cloud saves.** Would add `SaveSlot` keyed `(FriendCode, CatalogId, SlotName)`
  plus blob storage. Unblocked: it attaches to catalog identity.
- **Workshop/mods.** Same shape, same reason.
- **Playtime sync.** Schema ready; see §8 for why totals cannot be merged.
- **Rarity, global completion, leaderboards.** All are relay-side aggregates over
  existing tables; no client schema change needed.
- **Operator merge tooling.** The client and relay both support merge; the admin
  UI does not exist. Needs a duplicate-candidate view, a merge preview, and an
  audit log (merges are not reversible once aliases move).
- **Multi-device pairing.** Schema supports it fully (§8); no endpoint yet.

---

## 6. Database documentation

### Client database

Location: `%LOCALAPPDATA%\GameLauncher\gamelauncher.db`. WAL mode, foreign keys
**ON** (per-connection — SQLite defaults them off, so
`SqliteConnectionFactory` sets it every time or the cascades silently do
nothing).

> **Inspecting the file directly:** it runs in WAL mode. Copying only
> `gamelauncher.db` yields a stale snapshot missing recent commits. Copy
> `-wal` and `-shm` alongside it. This wasted real debugging time once.

#### Tables

**`Game`** — one row per installed game.
`Id` (local PK), `GlobalKey` (installation-local identity, 32 hex),
`CatalogId` → `CatalogEntry` (shared identity), `Title`, `CoverArtPath`,
`HeroArtPath`, `ExecutablePath`, `InstallDir`, `InstallSizeBytes`,
`PlaytimeSeconds` (running total), `LastPlayedAt`, `DateAdded`, `Tags` (JSON
array), `CollectionId` → `Collection`, `Notes`, `SourceUrl`, `UpdatedAt`.

**`Collection`** — exclusive grouping. `Id`, `Name` (unique NOCASE),
`SortOrder`, `DateAdded`. A game belongs to at most one; `Tags` is the
overlapping label mechanism.

**`PlaySession`** — one row per launch-to-exit. `Id`, `SessionKey` (**globally
unique**, assigned at launch), `GameId` → `Game` CASCADE, `DeviceId`,
`StartedAt`, `EndedAt`, `DurationSeconds`, `SyncedAt`.
A row with null `EndedAt` on startup is the residue of a crash; startup closes
those out crediting **zero** time.

**`CatalogEntry`** — the shared identity of a *title*.
`CatalogId` (PK), `Source` (which relay assigned it, or `local`),
`IsProvisional`, `CanonicalTitle`, `MatchFingerprint` (provenance only),
`CreatedAt`, `UpdatedAt`, `SyncedAt`, `SupersededByCatalogId` → self.

**`CatalogAlias`** — many fingerprints → one title.
`Fingerprint` (PK), `CatalogId` → `CatalogEntry`, `Source`, `CreatedAt`.
**This is the authoritative fingerprint lookup**, not
`CatalogEntry.MatchFingerprint`.

**`AchievementDefinition`** — the catalog of achievements.
`Id`, `CatalogId` → `CatalogEntry` CASCADE, `ApiName`, `GlobalKey`, `Title`,
`Description`, `IconPath`, `Kind` (display category), `ProviderKey` (**dispatch
key**), `TriggerConfigJson`, `IsHidden`, `SortOrder`, `ProgressTarget`,
`StatApiName`, `UpdatedAt`, `GameId` (**inert — see below**).

**`AchievementUnlock`** — insert-only history.
`DefinitionId` (PK, → `AchievementDefinition` CASCADE), `UnlockedAt`, `SyncedAt`.
The row's *presence* is the unlock; there is no boolean.

**`AchievementProgress`** — mutable progress, separate from unlocks.
`DefinitionId` (PK), `CurrentValue`, `UpdatedAt`.

**`GameStatDefinition` / `GameStatValue`** — named counters for progressive
achievements. Definition and value are split so a shared catalog can ship
definitions without personal numbers. **No repository yet** — tables exist,
nothing writes them.

**`FriendCache`** — offline friend list. `FriendCode` (PK), `DisplayName`,
`LastKnownGame`, `LastSeenAt`, `AvatarPath`. Cache only, never truth.

#### Key constraints and indexes

| Object | Why it exists |
|---|---|
| `UX_Game_GlobalKey`, `UX_CatalogEntry` PK | Identity uniqueness |
| `UX_AchievementDefinition_Catalog_ApiName` on `(COALESCE(CatalogId,''), ApiName NOCASE)` | **`COALESCE` is essential** — SQLite treats NULLs as distinct in a unique index, so library-wide achievements (`CatalogId IS NULL`) would otherwise collide freely |
| `UX_PlaySession_SessionKey` | Idempotent session merge |
| `IX_AchievementUnlock`/`PlaySession` on `SyncedAt` | The outbound queues |
| `Game.CatalogId` FK `ON UPDATE CASCADE` | **Load-bearing.** Promotion and demotion rewrite the catalog primary key; the cascade carries every reference |

#### Migration history

| Version | Change | Reasoning |
|---|---|---|
| **v1** | Initial: Collection, Game, AchievementDefinition, AchievementUnlock, FriendCache, PlaySession | — |
| **v2** | `GlobalKey`/`UpdatedAt` on Game and AchievementDefinition; `ApiName`, `IsHidden`, `SortOrder`, `ProgressTarget`, `StatApiName`; `AchievementUnlock.SyncedAt`; new `AchievementProgress`, `GameStatDefinition`, `GameStatValue` | Identity is cheap to add now, impossible to retrofit once unlocks exist — a wrong guess would attribute someone's unlock to the wrong achievement |
| **v3** | `CatalogEntry`; `CatalogId` on Game/AchievementDefinition/GameStatDefinition; **`GameId` nulled** on the latter two | Achievements must belong to the *title*, not one installation. Behaviour change: **uninstalling a game no longer erases its achievements** |
| **v4** | `CatalogAlias`; `CatalogEntry.SupersededByCatalogId`; alias seeding | One title legitimately has several fingerprints; merges must not rewrite an assigned id |
| **v5** | `PlaySession.SessionKey`, `DeviceId`, `SyncedAt` | Sessions, not totals, are the mergeable unit |
| **v6** | `AchievementDefinition.ProviderKey` + backfill | Dispatching on the `Kind` enum would make every new provider a core-model edit |

**The current schema version is 6, and the stage-11 interface added nothing to
it.** The achievements page, the editor and the toast presenter are all built on
columns that already existed — `IsHidden`, `ProgressTarget`, `SortOrder`,
`IconPath`, `ProviderKey` and the `AchievementProgress` table were added in v2
and v6 precisely so the UI would not need a migration. If you are looking for a
v7 because the UI landed, there isn't one.

#### Non-obvious choices

- **The vestigial `GameId` columns.** SQLite refuses `DROP COLUMN` on a column
  named in a foreign key. Rebuilding the table would mean dropping it, and with
  foreign keys enabled `DROP TABLE` performs an implicit `DELETE FROM` — which
  would cascade **every `AchievementUnlock` row out of existence.** v3 sets the
  columns to `NULL` instead: the cascade becomes unreachable, the columns are
  inert, nothing reads them. The model classes do not expose them.
  **Do not attempt to drop them inside a transaction.**
- **Client timestamps keep their local offset** (ISO-8601 round-trip). The client
  *displays* times. Contrast the relay (§7), which normalises to UTC because it
  only ever orders them.
- **`MatchFingerprint` is provenance, not lookup.** Keeping it authoritative as
  well would give two sources of truth that could drift.

### Relay database

Schema **v1**, tracked in a `SchemaVersion` table (not `PRAGMA user_version` —
that has no PostgreSQL equivalent).

`AppUser`, `Device`, `Friendship`, `Presence`, `CatalogEntry`, `CatalogAlias`,
`UserAchievement`, `UserLibrary`, `RelayMetadata`, `SchemaVersion`.

**`AppUser`, not `User`** — `user` is reserved in PostgreSQL, and a table that
needs quoting everywhere is an invitation to get it wrong once.

**`UserAchievement` is keyed `(FriendCode, CatalogId, ApiName)`** — never a
definition row id. This is the single most load-bearing relay decision: a row id
belongs to whichever database produced it and a catalog merge may delete one;
the api name is the stable authored handle. It makes a merge a data-movement
problem rather than a history-loss problem.

---

## 7. Relay architecture

Full detail in [`docs/relay-architecture.md`](relay-architecture.md). Summary:

### Responsibilities

Source of truth for: identity and friend codes, devices, friendships and
requests, presence, catalog identity and aliases, synchronised achievement
history. It knows nothing about any machine's filesystem or installed games.

### Authentication

- `POST /register { displayName }` → `{ friendCode, authToken, deviceId }`.
  Anonymous; no password, no email, no recovery. The token **is** the credential.
- `Authorization: Bearer glr_…`. SignalR also accepts `?access_token=` — but
  only on the hub path, so tokens stay out of access logs for ordinary requests.
- **Unsalted SHA-256.** Reasoned in §8.
- Database-backed rather than JWT, so revocation is immediate.

### Friend system

One directed `Friendship` row created by the requester; acceptance sets `Status`
rather than creating a second row. Rejection **deletes** — so the requester can
try again and the relay keeps no list of who declined whom.

Security properties, both covered by tests:
- An unknown friend code and a malformed one return **identical** messages, so
  the endpoint cannot be used to enumerate which codes exist.
- Only the addressee may answer; a user cannot accept a request they sent.

### Presence

Keyed on the **person**, not the device — a user with two machines online appears
once. `PresenceTracker` counts live connections per user with a compare-and-swap
loop, so two devices disconnecting at once cannot both conclude they were last.
Disconnect clears the current game as well as the flag.

Presence is broadcast **only to accepted friends**. A pending request leaks
nothing beyond a display name. There is a test that deliberately breaks the
fan-out to prove the leak test discriminates.

### SignalR design

`Hub<IPresenceClient>` (strongly typed) with a custom `IUserIdProvider` returning
the friend code, so `Clients.User(code)` addresses the person and reaches every
device they have online. That is the whole of what multi-device delivery needs.

### Sync and conflict resolution

| Data | Rule |
|---|---|
| Achievement unlock | **Earliest** `UnlockedAt` wins — monotonic; a replay can never move an earned-on date forward |
| Achievement progress | Highest wins |
| Increment-only stat | Highest wins |
| Gauge stat | Last write wins by `UpdatedAt` |
| Display name | Last write wins |
| Catalog identity | Server assigns, client adopts |
| Presence | Last write wins, ephemeral |

Every rule is idempotent and needs no vector clock — each is either monotonic or
genuinely single-writer. That is a deliberate constraint on *what gets synced*.

### Catalog identity

Provisional `local:<32 hex>` ids minted offline; promoted to server-assigned
`app_<32 hex>` on first contact. Promotion rewrites the primary key and
`ON UPDATE CASCADE` carries every reference. Collision (the relay returns an id
the client already holds) is the **normal** outcome of the catalog working, not
an error — it triggers a merge.

Catalog creation is **open**: a miss creates the entry rather than failing, so
users never wait for moderation. Accepted cost: duplicates until someone merges.

### Relay migration

Relays are identified by an id from `GET /relay-info`, stored in the relay's own
database. Address comparison would get both interesting cases wrong (a relay
moved to a VPS looks new; a different relay at the same URL looks the same).

On a detected change: demote foreign catalog entries to provisional, clear
unlock and session sync watermarks, clear the friend cache, select or create
credentials for the new relay. **Nothing local is deleted.** Credentials are kept
per relay, so switching back restores the original identity.

Offline-safe (never migrates on a failed probe) and idempotent.

### Future scaling

- Multi-instance needs a SignalR backplane and `PresenceTracker` behind a shared
  store. The interface is small precisely so that swap is contained.
- PostgreSQL: schema and queries already portable; one factory class needed.
- Timestamps are UTC ISO-8601 **text**. PG would prefer `timestamptz`; converting
  is one `ALTER TABLE … USING (col::timestamptz)` per column and no application
  change, because Dapper already maps through `DateTimeOffset`.

---

## 8. Important design decisions

### CatalogId vs GlobalKey

**Decision.** `GlobalKey` identifies *an installation's row*; `CatalogId`
identifies *a title* across all users. Everything cross-user — achievements,
stats, presence matching, sync — keys on `CatalogId`.

**Reasoning.** `GlobalKey` is minted locally, so two people who own the same game
generate unrelated values. It cannot express "the same title", which is exactly
what global achievements need.

**Alternatives considered.** (a) Use `GlobalKey` as the global id — fails
immediately across users. (b) Match on title string — breaks on re-releases,
localisation and punctuation. (c) Integer AppIDs like Steam — requires a central
authority we do not have.

**Future impact.** Rarity, leaderboards, cloud saves and workshop all attach to
`CatalogId` and need no schema change. `GlobalKey` survives for local export and
log correlation only.

### Offline-first

**Decision.** Local SQLite is authoritative for everything local. The relay is
optional. No operation blocks on the network.

**Reasoning.** A launcher that cannot start a game because a home server is down
is worse than one with no social features.

**Alternatives considered.** Online-required (rejected outright); optimistic
online with offline fallback (rejected — the fallback path is then the untested
one).

**Future impact.** Every new synced feature must supply an idempotent merge rule
and a `SyncedAt`-style watermark. If a feature cannot be made idempotent, it does
not belong in the sync path.

### PlaySession as the source of truth for playtime

**Decision.** Never synchronise `Game.PlaytimeSeconds`. Sessions sync; the total
is derived.

**Reasoning.** Totals cannot be merged. Two machines each reporting "40 hours"
either double-counts to 80 or discards one side — and no conflict rule recovers
the truth, because the information needed is not present in a total. A session
carrying a globally unique key is a distinct fact that either has or has not been
seen.

**Alternatives considered.** Last-writer-wins on the total (loses time);
max-wins (loses the smaller machine's play entirely); delta reporting (requires
trusting each client's arithmetic and breaks on a restore from backup).

**Future impact.** `PlaySession` already has `SessionKey`, `DeviceId` and
`SyncedAt`. Relay-side needs a `UserPlaySession` table keyed
`(FriendCode, SessionKey)`; the total becomes a `SUM`.

**Note:** `CatalogId` is deliberately *not* denormalised onto `PlaySession`. It
is reached by joining through `Game`, so a session recorded while the game still
had a provisional id automatically follows the promotion. A copied id would
freeze the stale one.

### Achievement architecture

**Decision.** Providers decide; the engine persists and notifies; the sync
service handles the network. Dispatch is by string `ProviderKey`.

**Reasoning.** Keeping decisions pure makes a provider testable with no database
and no network. A string key means adding a provider is a container registration
— dispatching on the `Kind` enum would make every new provider an edit to the
core model.

**Alternatives considered.** Enum dispatch (rejected — see above); providers
writing their own unlocks (rejected — idempotency would then be every provider's
problem, and they would each get it slightly wrong).

**Future impact.** New providers (Steam import, in-game HTTP API, log scraping)
need no engine, schema or interface change. `Kind` remains only as a UI grouping
category; a custom provider can use any `Kind` or a future `Custom` member.

**Idempotency has two layers:** already-unlocked definitions are never handed to
a provider, and `UnlockAsync` is insert-only and returns true only on the
transition — so events, toasts and counts all hang off that.

#### The provider extension model, concretely

Adding a provider is one class and one registration. Nothing else changes — not
the engine, not the schema, not the interface, not the editor.

1. Implement `IAchievementProvider`:
   - `Key` — the dispatch string stored in `AchievementDefinition.ProviderKey`.
     Declare it as a `public const string ProviderKey` so definitions and tests
     can name it without a magic string.
   - `DisplayName` — what the editor's picker shows.
   - `HandlesTrigger(trigger)` — return false for triggers that cannot possibly
     have changed anything. `ManualAchievementProvider` returns false for all of
     them; `MetaAchievementProvider` skips the running poll because playtime is
     only credited at exit.
   - `EvaluateAsync(definitions, context, ct)` — decide, and return one
     `AchievementEvaluation` per definition you reached a view on. Use
     `Unlock` / `NotYet` / `Unavailable`. **Do not persist and do not raise
     events**; the engine does both.
2. Register it: `services.AddSingleton<IAchievementProvider, MyProvider>();`

That is the whole cost. `ManualAchievementProvider` is ~30 lines and exists
partly to demonstrate it.

What follows automatically:
- The engine picks it up (it resolves `IEnumerable<IAchievementProvider>`) and
  throws at construction if two providers claim one key.
- It appears in the editor's provider picker, because that list comes from
  `IAchievementEngine.Providers`.
- The editor's Test Read works against it.
- Definitions naming it stop being reported as inert.

Two rules the engine enforces so that a provider cannot corrupt anything:
- **An unknown key is left alone, never guessed at.** Remove a provider and its
  definitions become inert — still listed, still holding their unlocks, still
  synchronising — rather than being evaluated by whatever else is installed.
- **A throwing provider does not stop the others.** It is logged and the pass
  continues; a memory read failing against a protected process is routine.

Rule configuration is JSON in `TriggerConfigJson`, so a new provider brings its
own config shape with no migration. Follow the existing pattern: a record with
`[JsonPropertyName]`, a `TryParse` returning null rather than throwing, and a
`Validate(out string? error)`. Returning null on malformed input is what stops
one hand-edited definition from breaking every other achievement.

The editor renders panels for the three built-in rule shapes. A custom provider
saves and loads without an editor panel — its stored configuration is carried
through untouched — so authoring one currently means writing the JSON. Adding a
panel is a `Visibility` block in `AchievementEditorWindow.xaml` plus a case in
`TryBuildRule`/`LoadRule`.

### Announcements are queued in a service, not a view model

**Decision.** `IAchievementNotificationService` owns the queue and the dwell
timer; `AchievementToastHostViewModel` only renders whatever it says is current.

**Reasoning.** Ordering and timing are application logic, and this project's
rules keep that out of view models. It also makes the behaviour testable without
a WPF dispatcher: the interesting guarantee — several achievements earned in one
pass appear one after another rather than on top of one another — is asserted
against the service directly.

**Alternatives considered.** A view model owning a `DispatcherTimer` (rejected —
untestable and against the project's own rules); toasting straight from the
engine (rejected — the engine would then decide presentation).

**One deliberate trade-off.** `CurrentChanged` fires only when the announcement
on screen actually changes, so the "+N more" badge is a snapshot from when that
announcement began and does not tick up if more are earned behind it. The first
version refreshed it, and that made every subscriber counting announcements see
the same one repeatedly. Correctness of the event won; the badge corrects itself
within one dwell.

**UI thread.** The engine already raises `AchievementUnlocked` through
`IUiDispatcher`, but the toast host marshals again anyway. `Invoke` runs inline
when the caller is already on the UI thread, so it costs nothing and leaves the
overlay correct independently of who raises the event.

### Device identity

**Decision.** One user, many devices, from the very first release. The friend
code identifies the person; the token identifies the machine.

**Reasoning.** Cheap now, effectively impossible to retrofit — a token issued as
a *user* credential cannot be split into per-device credentials without
invalidating everyone's existing one.

**Alternatives considered.** One token per user (blocks per-device revocation and
makes multi-device presence ambiguous).

**Future impact.** Adding a machine is `POST /devices/pair` plus a short-lived
pairing code; revoking one is setting `RevokedAt`. Neither needs a schema change,
because nothing else references a device.

### Relay identity

**Decision.** A relay reports an id from `GET /relay-info`, generated once and
stored in its own database. Clients compare that, never the URL.

**Reasoning.** The id travels with the data. Moving the relay to a VPS, or
restoring it from backup, keeps the identity — clients carry on. Pointing at a
genuinely different relay is detected.

**Alternatives considered.** URL comparison (wrong in both directions);
certificate fingerprint (breaks on renewal); operator-configured name (changes
whenever someone edits a file).

**Future impact.** Verified: the id is stable across relay restarts and differs
for a different database. Enables safe migration between self-hosted relays with
no data loss.

### Token hashing — unsalted SHA-256

**Decision.** Store `SHA256(token)` with no salt and no slow KDF.

**Reasoning.** Salt + bcrypt/Argon2 exists to make *guessing* expensive, and
guessing only threatens low-entropy secrets like human-chosen passwords. A
256-bit random token cannot be brute-forced however fast the hash is, so a slow
KDF would cost a CPU-bound operation on every request — on a machine with little
CPU to spare — and buy nothing. Omitting the salt is also what makes the hash a
usable **lookup key**: one indexed read instead of hashing against every row.

**Accepted trade-off.** Identical tokens hash identically, so a stolen database
reveals which rows share a token. Since tokens are unique random values, that
reveals nothing.

**This reasoning does not transfer to passwords.** If a password login is ever
added, it needs a salt and a slow KDF.

---

## 9. Files and folders

### Where things live

| Path | Purpose |
|---|---|
| `README.md` | Entry point: what it is, prerequisites, build, run, reset |
| `docs/` | Architecture and deployment documents. **Keep current** — they are the design record |
| `global.json` | SDK pin (8.0.423) |
| `Directory.Build.props` | Solution-wide: nullable, implicit usings, XML docs |
| `GameLauncher.Shared/Contracts/` | Wire DTOs. **DTO contracts only — no behaviour** |
| `GameLauncher.Shared/Hubs/` | `IPresenceClient`, `PresenceHubContract` |
| `GameLauncher.Desktop/Infrastructure/` | Hosting, DI, navigation, dispatcher, hosted services |
| `GameLauncher.Desktop/Models/` | Domain models and settings |
| `GameLauncher.Desktop/Services/<Area>/` | Application logic, one folder per area |
| `GameLauncher.Desktop/ViewModels/` | One per page/dialog; no business logic |
| `GameLauncher.Desktop/Views/` | XAML + minimal code-behind |
| `GameLauncher.Desktop/Resources/Theme/` | Palettes and control styles |
| `GameLauncher.Relay/Endpoints/` | All HTTP endpoints, one file |
| `GameLauncher.Tests/Infrastructure/` | `TestAppHost`, `WpfTestHost`, `RelayTestFactory`, `LoopbackFileServer`, `ImmediateDispatcher` |

### Files worth reading first

| File | Why |
|---|---|
| `Infrastructure/ServiceRegistration.cs` | The whole object graph in one place |
| `Services/Database/DatabaseInitializer.cs` | Every migration with its reasoning |
| `Services/Catalog/CatalogRepository.cs` | Promotion, merge, demotion — the trickiest code |
| `Services/Achievements/AchievementEngine.cs` | Dispatch and idempotency |
| `Services/Notifications/AchievementNotificationService.cs` | The announcement queue and its single-pump invariant |
| `ViewModels/AchievementItemViewModel.cs` | Where hidden achievements are actually concealed |
| `Relay/Program.cs` | Relay composition |
| `Relay/Hubs/PresenceHub.cs` | Presence and friend request logic |

### Where new features go

- **New achievement provider:** implement `IAchievementProvider` in
  `Services/Achievements/Providers/`, register in `ServiceRegistration`. Nothing
  else changes — see the extension model in §8.
- **New page:** ViewModel in `ViewModels/`, View in `Views/`, `DataTemplate` in
  `Resources/ViewTemplates.xaml`, case in `MainWindowViewModel.NavigateAsync`,
  nav item in `MainWindow.xaml`, registration in `ServiceRegistration`, smoke
  test in `DialogSmokeTests`. `AchievementsViewModel` / `AchievementsView` is the
  most recent worked example.
- **New dialog:** as above plus a `DialogRegistry.Register<TViewModel, TWindow>()`
  entry. View models must **never** name a `Window` type. If the dialog needs to
  be told what it is editing, use
  `IWindowService.ShowDialogFor<TViewModel>(vm => vm.Initialize(...))` rather
  than adding a constructor parameter — the callback reaches the view model the
  window already built from the container.
- **New relay endpoint:** a `Map…` method in `RelayEndpoints`, contracts in
  `Shared/Contracts`.
- **Schema change:** append to the `Migrations` array. **Never edit an existing
  entry** — installed databases have already run it.

---

## 10. Coding conventions

### Naming and style

File-scoped namespaces. Nullable enabled. `LangVersion 12.0`. Interfaces
`IThing`; implementations `Thing`. Async methods end `Async` and take a
`CancellationToken` where cancellation is meaningful.

**XML documentation on every public member.** `GenerateDocumentationFile` is on
and CS1591 is deliberately left as a visible warning — it is a compiler-enforced
check that the public surface stays documented. The build is currently at **0
warnings**; keep it there.

**Comments explain *why*, not *what*.** The existing code comments non-obvious
decisions, trade-offs and traps. Match that density — do not add narration.

### Dependency injection

Constructor injection throughout, with `?? throw new ArgumentNullException`.
All registration in `ServiceRegistration.AddGameLauncher`, grouped by area.

- Repositories and stateless services: **singleton** (each call opens its own
  connection).
- Page view models: **transient** (each navigation starts clean).
- Dialog view models and windows: **transient** (a closed WPF window cannot be
  reshown).
- `MainWindow`: **singleton** (one shell). *Note: this is why tests that realise
  it need a fresh container per iteration.*

Anything registered as a singleton that implements `IAsyncDisposable` **must also
implement `IDisposable`** — the container disposes synchronously.

### MVVM

- No business logic in view models. They orchestrate services and expose state.
- CommunityToolkit source generators: `[ObservableProperty]`, `[RelayCommand]`.
  Classes must be `partial`.
- View models never reference `Window` types — use `IWindowService` /
  `IDialogService`.
- Data loading happens in `OnNavigatedToAsync`, never in constructors.
- Cross-thread events go through `IUiDispatcher`.

### Repository and service patterns

- Repositories: one per aggregate, Dapper, each method opens and disposes its own
  connection. Multi-statement operations use an explicit transaction.
- Services hold application logic; anything more than a single persistence call
  belongs in a service, not a view model.
- Translate storage errors into domain errors — e.g. `CollectionRepository` turns
  a SQLite unique violation into `InvalidOperationException` with a user-facing
  message.

### Error handling

- Expected conditions return results; exceptional ones throw. A missing save file
  is a `SaveFileReadResult.Failure`, not an exception.
- `RelayApiException` carries `IsTransient` so callers know whether to retry.
- Catch narrowly: `catch (Exception ex) when (ex is IOException or
  UnauthorizedAccessException)`.
- Background work never lets exceptions escape — a `Process.Exited` callback
  throwing would take the process down.

### Logging

`ILogger<T>`, structured message templates (`{Name}` placeholders, never string
interpolation). Levels: `Debug` for flow, `Information` for state changes worth
seeing, `Warning` for recoverable problems, `Error` for failures.

**Never log secrets.** Friend codes are public and logged; auth tokens are not.

### Testing

- xunit. Test names are sentences: `Merge_preserves_an_unlock_only_the_absorbed_entry_had`.
- Integration tests use `TestAppHost`, which builds the **real** DI graph against
  a temp directory — so a service missing from the real composition root fails
  the tests too.
- WPF tests use `WpfTestHost` (one STA thread, one `Application`).
- Relay tests use `RelayTestFactory` (`WebApplicationFactory<Program>`,
  long-polling transport).
- **Validate that a new test can fail.** Two tests in this codebase initially
  passed for the wrong reason; both were caught by deliberately breaking the code
  and confirming the test failed. Do this for any test asserting an absence or a
  security property.

### Database access

- Dapper only. Parameterised queries always.
- Client migrations may use SQLite-specific SQL. **Relay migrations may not** —
  see §7 portability rules.
- Never edit an existing migration.

---

## 11. Testing status

**148 tests, all passing.** Single project: `GameLauncher.Tests`.

| Suite | Count | Covers |
|---|---|---|
| `Views.DialogSmokeTests` | 18 | Every window and page realised; both palettes |
| `Download.ArchiveExtractionTests` | 18 | Zip-slip, traversal, real zips, format detection |
| `Download.DownloadIntegrationTests` | 15 | End-to-end HTTP: resume, redirects, checksums, cancellation |
| `Achievements.SaveFileReaderTests` | 16 | JSON/XML/INI/regex, XXE, locked files |
| `Catalog.CatalogIdentityTests` | 11 | Fingerprints, promotion, merge, repair |
| `Download.DownloadServiceTests` | 11 | Filename sanitisation, traversal |
| `Achievements.AchievementPresentationTests` | 11 | Concealment, progress, grouping, filtering, missing providers |
| `Achievements.AchievementEditorTests` | 9 | Test Read inertness, provider validation, authoring |
| `Achievements.AchievementEngineTests` | 9 | Idempotency, extensibility, progress |
| `Relay.PresenceHubTests` | 7 | Auth, requests, presence isolation |
| `Achievements.AchievementToastTests` | 6 | Announcement queueing, order, no duplicates |
| `Friends.RelayMigrationTests` | 6 | Relay switching, data preservation |
| `Friends.BackoffPolicyTests` | 5 | Never gives up, no overflow, jitter |
| `Friends.OfflineSyncTests` | 4 | Offline queue, reconnect drain |
| `Achievements.OfflineUnlockFlowTests` | 2 | Full offline → restart → reconnect → sync |

### Integration tests worth knowing about

- **`OfflineUnlockFlowTests`** — the flagship. Unlocks offline, **restarts**
  (a genuinely new container over the same database), reconnects, syncs, and
  asserts timestamps, progress and idempotency.
- **`PresenceHubTests`** — real in-process relay via `WebApplicationFactory`.
  Includes proving presence does not leak to non-friends.
- **`RelayMigrationTests`** — switching relays preserves game, collection, tags,
  achievement definition, unlock and play session.
- **`DownloadIntegrationTests`** — a real Kestrel server on a loopback port
  (`Infrastructure/LoopbackFileServer.cs`), not a stubbed message handler, so
  bytes genuinely move over a socket. It serves ranges, ignores ranges while
  still advertising them, drops the connection mid-body, redirects, and stalls
  between chunks — which is what lets the tests cover resume, restart-on-200,
  interruption recovery, redirect-preserving-Range, cancellation and checksum
  failure. Kestrel rather than `HttpListener` because `HttpListener` needs a
  Windows URL ACL reservation a test run cannot assume it has; port 0 so a run
  never collides with anything.
- **`DialogSmokeTests`** — catches `StaticResource` failures that compile fine
  and throw at load. **Item templates only instantiate when their control has
  items**, so these tests populate view models first; an earlier version passed
  with a deliberately broken resource because the list was empty. The achievement
  cases seed one row of every display state — unlocked, locked, progressing,
  hidden, orphaned provider — because each is a different branch of the template.

### The stage-11 tests, and what each is really guarding

Every one of these was validated by breaking the code it covers and confirming
it failed:

- **Concealment** (`AchievementPresentationTests`) — asserted against the view
  model, not the rendered view, because that is where the guarantee lives. Making
  `DisplayTitle` return the real title fails 2 tests.
- **Test Read inertness** (`AchievementEditorTests`, `AchievementToastTests`,
  `AchievementEngineTests`) — routing `TestAsync` through the persisting path
  fails 4 tests, covering unlocks, progress *and* notifications separately.
- **No duplicate announcements** (`AchievementToastTests`) — this one found a
  real bug rather than confirming an assumption; see stage 11 in §3.
- **Missing providers do not corrupt** (`AchievementEditorTests`) — making the
  editor substitute an installed provider for a missing key fails 1 test that
  checks the key, the rule JSON and the unlock all survive.

`AchievementToastTests` builds its engine explicitly with a substituted
dispatcher rather than resolving it. The container's `UiDispatcher` binds to the
WPF test host's thread whenever an `Application` exists in the process, so a test
outside that collection would otherwise marshal onto — and block on — a
dispatcher owned by a different collection.

One assertion in that suite is deliberately loose: the backlog count published
with each announcement is not asserted exactly, because whether an unlock is
queued before or after the pump picks up the previous one is a real race. Both
`[2,1,0]` and `[0,1,0]` are correct; only "empty by the last one" is invariant.

The download suite was validated the same way. Trusting `Accept-Ranges` instead
of the response status fails 1 test; never sending the `Range` header fails 5;
disabling the zip-slip guard fails 6, including the end-to-end one that asserts
nothing was written above the destination folder.

### Gaps

1. **Memory provider never run against a real process.**
2. **No UI interaction tests** — windows are realised but never driven. The
   editor's behaviour is covered through its view model instead.
3. **No relay-migration test with two real relay processes** (covered at
   integration level with stubs).
4. **No PostgreSQL tests** (no implementation).
5. **No concurrency tests** for two devices syncing simultaneously.
6. **No HTTPS or proxy coverage** in the download tests — the loopback server is
   plain HTTP. Certificate handling and corporate proxies are untested.

---

## 12. Current TODO list

### Immediate next task

**None — the roadmap is complete.** Pick from the list below, or from §13's open
questions if you would rather settle a decision than add a feature.

### Short-term

1. **Achievement icon picker** — `IconPath` round-trips and renders, but the
   editor cannot set it. Needs a file picker writing into
   `AppPaths.AchievementIconDirectory` so the icon survives the source file
   moving.
3. **Stat-driven achievements** — `GameStatDefinition` / `GameStatValue` exist
   with no repository and no provider. Needs both, plus a `StatApiName` field in
   the editor.
4. **Windows toast, optionally.** The shipped presenter is an in-app overlay:
   no dependency, no registration, and closer to what Steam does. If a real
   Windows toast is wanted, put it behind `IAchievementNotificationService`'s
   consumer rather than replacing the queue — and note that an unpackaged app
   needs a Start Menu shortcut carrying an AUMID before
   `Microsoft.Toolkit.Uwp.Notifications` will work at all.

### Long-term

- PostgreSQL connection factory (when a VPS exists).
- Multi-device pairing endpoint.
- Playtime sync (relay `UserPlaySession` table).
- Rarity and global completion percentages.
- Operator merge tooling with audit log.
- Cloud saves; workshop/mods.

### Known technical debt

| Item | Notes |
|---|---|
| Vestigial `GameId` columns | On `AchievementDefinition`/`GameStatDefinition`. Removing needs a table rebuild outside a transaction |
| `GameStat*` tables unused | Schema exists, no repository, nothing writes them |
| `PresenceTracker` in-process | Blocks multi-instance relay |
| Sample data holds throwaway-relay catalog ids | See §4 limitation 5 |
| Custom providers have no editor panel | Their rule JSON round-trips untouched, but authoring one means writing it by hand |
| Achievement icons cannot be chosen | `IconPath` renders and survives an edit; nothing sets it |
| No git repository | Not initialised |

---

## 13. Open architectural questions

1. **Whether a real Windows toast is ever wanted.** The shipped presenter is an
   in-app overlay, chosen because it needs no AUMID, no Start Menu shortcut and
   no package identity — and because it matches what Steam does. The package for
   the alternative is still referenced and is the reason the TFM is raised to
   1809. Either wire it up behind the same notification service or drop the
   reference and lower the TFM; leaving both is the current fudge.
2. **Who may create catalog entries long-term.** Currently open creation.
   Promotion-on-N-independent-matches was considered and deferred — the aliases
   needed to compute it are already recorded, so it can be added without a schema
   change.
3. **Catalog entry lifecycle.** Achievements now outlive uninstalls, so catalog
   entries and definitions accumulate. No cleanup exists. Probably correct
   (discarding earned achievements to reclaim rows would be wrong), but the
   growth is unbounded.
4. **Should `Kind` survive?** It is now only a UI grouping category while
   `ProviderKey` does the dispatch. Either give it a `Custom` member for
   third-party providers or drop it and let providers supply their own display
   name.
5. **Light theme.** Now a pure palette swap, but contrast is unverified.
6. **Relay federation.** `CatalogEntry.Source` records the issuing relay, but
   nothing consumes it beyond migration detection. Two relays sharing a catalog
   is unexplored.
7. **Achievement icons.** `IconPath` exists; nothing populates it and there is no
   import path.
8. **Stat-driven achievements.** `StatApiName` and `ProgressTarget` exist and the
   tables exist, but no provider reads stats. The wiring is unbuilt.

---

## 14. Instructions for future development

### Must not be changed without serious reconsideration

- **`UserAchievement` keyed on `(FriendCode, CatalogId, ApiName)`.** Changing to
  a row id makes catalog merges lose history.
- **`ON UPDATE CASCADE` on catalog foreign keys.** Promotion, demotion and merge
  all depend on it. Removing it silently orphans games and achievements.
- **Unlocks are insert-only, earliest-wins.** Every idempotency guarantee in the
  system rests on this.
- **Providers do not persist.** The moment one writes its own unlock, idempotency
  becomes every provider's problem.
- **`TestAsync` must not route through `RunAsync`.** Persistence and notification
  both live in `RunAsync`; keeping the two paths separate is what makes "testing
  a rule cannot award it" structural rather than a step somebody has to remember
  to skip. Four tests fail if this is changed.
- **Concealment stays in `AchievementItemViewModel`, not in the template.**
  Binding the real title and hiding it in XAML leaves it reachable through
  tooltips, automation and copy. `DisplayTitle`/`DisplayDescription`/
  `DisplayIconPath` exist so the value never reaches the view.
- **An unrecognised `ProviderKey` is left alone, never rewritten.** The engine
  skips it and the editor preserves it. Substituting an installed provider would
  silently change what an achievement means, and its unlock would survive under
  the new meaning.
- **One announcement pump.** `AchievementNotificationService` must keep a single
  pump and publish `CurrentChanged` only from it. Re-raising on enqueue makes
  every subscriber that counts announcements double-count.
- **No memory writing.** `ProcessMemoryReader` requests read rights only; the
  handle cannot write even if the code tried. Only `OpenProcess`,
  `ReadProcessMemory` and `CloseHandle` are imported anywhere in the project.
  Keep it that way.
- **Relay migrations stay PostgreSQL-portable.** No `PRAGMA`, no `randomblob()`,
  no `strftime()`, no `AUTOINCREMENT`, no `DEFAULT` on boolean/timestamp columns
  (PG rejects `DEFAULT 0` on a boolean), no two-argument `MIN` (PG spells it
  `LEAST` — use `CASE`).
- **`global.json`** — removing it silently switches the build to SDK 10.
- **`<Using Include="System.IO" />` in the Desktop csproj** — the `_wpftmp` fix.
- **Migrations are append-only.**

### Backwards compatibility

- **Settings file.** `AppSettings.SchemaVersion` exists for this. The v1→v2
  migration (flat token → per-relay identities) is the model: carry data across,
  never discard. Silently deregistering a user is data loss.
- **Wire contracts.** New DTO fields must be optional; the relay may be older
  than the client or vice versa.
- **`ApiName` values.** Once an achievement has synced, changing its api name
  orphans everyone's unlock.
- **`CatalogId` once assigned is immutable.** Promotion of a *provisional* id is
  the sole exception, and only because no relay has ever seen it.

### Preferred approach for new features

1. **Read the relevant `docs/` file first.** They record reasoning, not just
   description.
2. **Interface first, then implementation**, so it can be substituted in tests.
3. **Offline path first.** Ask what happens with no relay before writing the
   online path.
4. **Idempotency is a requirement, not a nicety**, for anything synced.
5. **Verify at runtime, not just at compile time.** XAML resource failures,
   DI disposal problems and WAL snapshot issues all compile cleanly. Run the app;
   run the relay; realise the window.
6. **Prove a new test can fail** before trusting it.
7. **Keep the build at 0 warnings.**
8. **Update `docs/` in the same change** when a decision changes.

---

## Appendix A — Build, run, and operate

### Prerequisites

- .NET SDK **8.0.423** (pinned by `global.json`). Newer 8.0.4xx patches are
  accepted via `rollForward: latestFeature`.
- Windows 10 1809+ to build or run the Desktop and Tests projects (they use a
  Windows TFM). `Shared` and `Relay` build anywhere.

The development machine also has SDK 10 installed. `global.json` handles this —
`dotnet --version` in the solution directory reports `8.0.423`. If a build
suddenly targets .NET 10, `global.json` is missing or you are outside the
solution directory.

### Build and test

```bash
dotnet build "GameLauncher.sln"
```

```bash
dotnet test "GameLauncher.Tests/GameLauncher.Tests.csproj"
```

Expected: **0 warnings, 0 errors**; **148 tests passed**. Both were verified at
the time this document was written. Warnings are meaningful here — CS1591
(missing XML doc) is deliberately left visible, so a non-zero warning count means
something regressed.

### Run the desktop launcher

```bash
dotnet run --project "GameLauncher.Desktop"
```

With the sample library (only seeds when the library is empty):

```bash
dotnet run --project "GameLauncher.Desktop" -- --seed-sample-data
```

Sample entries point at executables that do not exist, so launching them will
fail by design. That is the only startup switch; `StartupOptions.Parse` ignores
anything else.

### Run the relay

```bash
dotnet run --project "GameLauncher.Relay" --launch-profile http
```

Listens on `http://localhost:5107` (the `https` profile adds
`https://localhost:7141`). The SQLite file is created on first run relative to
the working directory.

Quick check:

```bash
curl http://localhost:5107/relay-info
```

Configuration is `appsettings.json` → section `Relay`. Every value can be
overridden by environment variable using the double-underscore convention, which
is how a VPS should supply them:

```bash
Relay__Database__ConnectionString="Data Source=/var/lib/gamelauncher/relay.db"
```

| Setting | Default | Notes |
|---|---|---|
| `Relay:Database:Provider` | `Sqlite` | `Postgres` throws — factory not implemented |
| `Relay:Database:ConnectionString` | `Data Source=gamelauncher-relay.db` | Supply via env var in production |
| `Relay:Presence:HeartbeatSeconds` | `60` | Refreshes last-seen only; not a liveness mechanism |
| `Relay:AllowedOrigins` | `[]` | CORS, for a future web client. The desktop app is not a browser and is unaffected |

### Relay surface

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | — | Liveness |
| GET | `/relay-info` | — | Relay id + schema version — **the identity probe** |
| POST | `/register` | — | Create user + device, returns friend code and token |
| GET | `/friends` | Bearer | Friend list with presence |
| POST | `/catalog/resolve` | Bearer | Fingerprint → catalog id (creates on miss) |
| POST | `/sync/achievements` | Bearer | Push unlocks / fetch history |
| — | `/hubs/presence` | Bearer or `?access_token=` | SignalR hub |

Friend requests are sent and answered **over the hub**, not over HTTP — see
`PresenceHubContract.Methods`.

### Connecting the launcher to a relay

Settings page → set the relay address. Everything else is automatic:
`/relay-info` is probed, credentials are selected or created for that relay id,
the hub connects, and the first sync promotes provisional catalog ids.

There is no manual registration step and no way to enter a friend code by hand —
the relay issues it.

### Where runtime state lives

Everything is under `%LOCALAPPDATA%\GameLauncher`. Nothing is written next to the
executable, so the app runs from Program Files without elevation.

| Path | Contents |
|---|---|
| `gamelauncher.db` (+ `-wal`, `-shm`) | The library database |
| `settings.json` | Settings **and relay credentials** |
| `logs\` | Rolling log files |
| `artwork\`, `achievements\`, `avatars\` | Extracted and cached images |
| `downloads\` | In-progress downloads (`.part` files) |
| `games\` | Default install target |

Tests never touch this — `TestAppHost` redirects `AppPaths` to a temp folder.

### Resetting to a first-run state

> **Destructive.** This deletes the library, playtime, achievements and the
> relay credentials. `settings.json` holds the only copy of the auth token,
> which is unrecoverable once gone — the relay stores a hash, not the token.
> Back the folder up first if the data matters.

Delete `%LOCALAPPDATA%\GameLauncher`. The next start recreates it and migrates a
fresh database from v0 to v6.

To reset only the relay, stop it and delete its `.db` file — but note this gives
the relay a **new relay id**, so every connected client will correctly treat it
as a different relay and re-resolve its catalog ids.

### Inspecting the database

It runs in WAL mode. Read it in place, or copy `gamelauncher.db` **together with
its `-wal` and `-shm` sidecars** — copying the `.db` alone yields a stale
snapshot that silently omits recent commits. This has already caused one
false-alarm bug report during development.
