# GameLauncher — Project Handoff

**This is the authoritative document for continuing development.** It assumes no
access to any prior conversation. Read this plus the codebase and you should be
able to continue without guessing.

**Status: version 1.0 complete, plus the discovery catalogue.** The solution
builds with **0 warnings, 0 errors**; **343 tests pass**; the client database is
at schema **v7** and the relay at **v1**.

> **Schema v7 and the `Discover` section are newer than most of this document.**
> The discovery catalogue is a separate subsystem from `CatalogEntry` — that
> type remains exactly what §6 describes, the identity of an *installed* title.
> A "listing" is a game the launcher knows exists and mostly has not got.
> [`docs/catalog-import-design.md`](catalog-import-design.md) is authoritative
> for it, and its §14 records what changed during implementation.

Companion documents, all current:

- [`README.md`](../README.md) — what the project is, prerequisites, build, run, reset
- [`docs/deployment.md`](deployment.md) — hosting the relay: proxies, tunnels, backups, security
- [`docs/catalog-identity.md`](catalog-identity.md) — catalog identity, merging, relay migration
- [`docs/relay-architecture.md`](relay-architecture.md) — auth, sync, conflict resolution, portability
- [`docs/catalog-import-design.md`](catalog-import-design.md) — the discovery catalogue: sources, matching, merging, schema v7

**To just build and run it, skip to [Appendix A](#appendix-a--build-run-and-operate).**

---

## 1. Project overview

### Purpose

A Steam-style Windows game launcher for a personal, locally-managed game library,
plus a self-hosted relay that adds the social layer: friend codes, presence, and
synchronised achievements.

The launcher manages games the user already has on disk. It is not a store and
has no notion of ownership or licensing — a library entry is a pointer to an
executable plus the metadata and statistics the launcher has accumulated about
it.

### Goals as built

- A complete local launcher: library, artwork, launching, playtime, collections,
  achievements.
- A relay that is genuinely optional. Everything except friends works with no
  relay configured, and nothing blocks on the network.
- A Steam-style achievement platform: definitions belong to a shared catalog,
  progress belongs to the user, unlocks synchronise through the relay.

### Explicitly out of scope

These were ruled out at the start and should stay out:

- No scraping, parsing, or bespoke integration for any specific game
  repack/crack/warez distribution site.
- No torrent or magnet link handling.
- **No memory writing, process modification, or DLL injection.** Memory
  achievements are read-only inspection. Only `OpenProcess`, `ReadProcessMemory`
  and `CloseHandle` are imported anywhere in the project, and the handle is
  opened without write rights.

Cloud saves and workshop/mods are deferred, not forbidden (§5).

### Technology stack

| Component | Stack |
|---|---|
| Desktop | .NET 8, WPF (`net8.0-windows`), CommunityToolkit.Mvvm 8.4.2 |
| Hosting/DI | Microsoft.Extensions.Hosting 8.0.1 |
| Client data | SQLite (Microsoft.Data.Sqlite 8.0.11) + Dapper 2.1.79 — **no Entity Framework** |
| Real-time | Microsoft.AspNetCore.SignalR.Client 8.0.11 |
| Archives | SharpCompress 0.50.3 |
| Relay | ASP.NET Core 8 Minimal API + SignalR, SQLite + Dapper |
| Tests | xunit 2.5.3, Microsoft.AspNetCore.Mvc.Testing 8.0.11 |

Notifications use an in-app overlay and need no package (§8).

### Build environment

`global.json` pins the SDK to **8.0.423** with `rollForward: latestFeature`. The
development machine also has SDK 10 installed; without the pin, `dotnet new` and
NuGet default to .NET 10 artefacts. All framework packages are deliberately
pinned to the **8.0.x** line — NuGet otherwise resolves `Microsoft.Extensions.*`
and SignalR to 10.0.x, which would put .NET 10 libraries in a .NET 8 app and
skew against the ASP.NET Core 8 relay.

---

## 2. Architecture

### Solution structure

```
GameLauncher.sln
├── GameLauncher.Shared    net8.0           wire contracts only, zero dependencies
├── GameLauncher.Desktop   net8.0-windows   WPF client (WinExe)
├── GameLauncher.Relay     net8.0           ASP.NET Core service
└── GameLauncher.Tests     net8.0-windows   xunit, references all three
```

`Shared` is deliberately dependency-free so both sides can reference it without
dragging in either's implementation stack. It holds **DTO contracts only** — no
behaviour, no business logic.

Desktop and Relay never reference each other. They communicate solely through
`Shared` contracts.

### Major services

**Desktop — data**

| Interface | Responsibility |
|---|---|
| `IDbConnectionFactory` | Opens SQLite connections with pragmas applied |
| `IDatabaseInitializer` | Versioned migrations (§6) |
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

**Desktop — relay**

| Interface | Responsibility |
|---|---|
| `IRelayApiClient` | HTTP seam: relay-info, register, friends, catalog resolve, achievement sync |
| `IRelayHubClient` | SignalR seam: presence, friend requests, connection state |
| `IRelayIdentityService` | Which relay are we on; migrate when it changes |
| `IRelaySyncService` | Drains outbound queues |
| `IFriendsService` | Merged friend list (cache + live) |

**Desktop — achievements and notification**

| Interface | Responsibility |
|---|---|
| `IAchievementProvider` | **Decides only.** No persistence, no network |
| `IAchievementEngine` | Dispatches providers, persists, raises events, lists providers |
| `ISaveFileReader` | JSON/XML/INI/regex value extraction |
| `IProcessMemoryReader` | Read-only process memory |
| `AchievementWatcherService` | Decides *when* evaluation runs (hosted service) |
| `IAchievementNotificationService` | Queues earned achievements, announces one at a time (hosted service) |

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

### Startup and communication flow

```
Hosted services run in registration order, and the order matters:
  1. SettingsStartupService          load settings, apply theme (before any window)
  2. DatabaseStartupService          migrate schema, reconcile sessions, repair fingerprints
  3. AchievementNotificationService  subscribe to the engine (must precede step 4)
  4. AchievementWatcherService       subscribe to launch events, library-wide startup pass
  5. RelayCoordinatorService         identity → connect → sync (never blocks startup)

RelayCoordinatorService.StartAsync:
  friends.LoadFromCacheAsync()          ← cache first, before any network call
  └─ background: IRelayIdentityService.EstablishAsync()
       GET /relay-info → relayId
       relayId != ActiveRelayId ?  → migrate (§7)
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

This is the central constraint:

- **Local SQLite is authoritative** for library, installs, launching, sessions
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
  `Disabled` (no relay configured) from `Offline` (configured, unreachable).
  Showing "offline" for both would imply something is broken when nothing is.

---

## 3. How it was built, and what each stage decided

The project was built in stages. What follows is not a changelog — it is the
reasoning behind decisions that are not obvious from the code that depends on
them.

### Solution scaffold and shared contracts

Four projects, `global.json` SDK pin, `Directory.Build.props` with nullable,
implicit usings, and `GenerateDocumentationFile`. **CS1591 is left visible on
purpose**, as a compiler-enforced check that public APIs stay documented. It is
why there was no documentation debt to pay down at the end.

- **Friend code format `GL-XXXXX-XXXXX`, Crockford Base32.** The alphabet omits
  I, L, O and U, removing 1/I/L and 0/O ambiguity when a code is read aloud or
  copied by hand. Ten symbols = 50 bits.
- **`IPresenceClient` is a shared strongly-typed hub contract.** The relay
  implements `Hub<IPresenceClient>`; the client subscribes via `nameof` against
  the same interface. A renamed method becomes a build error rather than a
  handler that silently never fires.

### Desktop shell

Generic Host + DI, navigation with a back stack and load cancellation, Steam-style
dark theme across five resource dictionaries, rolling file logger, global
exception handling on all three channels.

- `NavigationService` cancels the previous navigation on each new one. Without
  it, navigating away from a slow page and back leaves two loads racing.
- `FileLoggerProvider` writes **synchronously and flushes**. A buffered async
  writer loses the final and most interesting entries precisely when the process
  is crashing.
- Dark title bar via `DwmSetWindowAttribute`, best-effort.

**Gotcha that will bite again:** WPF's XAML markup pass compiles through a
generated `*_wpftmp` project that imports `System.Windows.Shapes` (which has its
own `Path`) but **not** `System.IO`. File-system code compiles in the main pass
and fails in that one. Fixed by a project-level `<Using Include="System.IO" />`
in the Desktop csproj — **do not remove it**.

### Schema, repositories, sample data

- **Two tables beyond the original spec.** `Collection`, because
  `Game.CollectionId` is a foreign key with nothing to point at otherwise; and
  `PlaySession`, because "track start time, end time, duration" needs somewhere
  to live and `Game.PlaytimeSeconds` only holds the total.
- **Dapper type handlers** for `DateTimeOffset` (ISO-8601 round-trip) and
  `IReadOnlyList<string>` (JSON array).
- **Tags are written explicitly, not via the type handler.** Dapper resolves a
  *parameter* by the value's runtime type and expands an array into an `IN (...)`
  clause. Reads use the handler; writes serialise at the call site. This
  asymmetry is documented in `GameRepository` and is a trap for whoever edits
  those queries next.
- **Sample data is opt-in** (`--seed-sample-data`). It points at executables that
  do not exist; auto-seeding would leave the user hand-deleting rows.

### Library UI

Grid/list toggle, search over titles *and* tags, five sort orders, collection
filter, game details page.

- `GameItemViewModel` snapshots `ExecutableExists` at construction. Binding it
  directly would turn scrolling a large library into a storm of disk checks.
- Playtime is measured with `Stopwatch` (monotonic), not wall clock — a
  daylight-saving change or NTP correction mid-session cannot distort it.
- Uninstall refuses to recursively delete a drive root or system folder.
- `IUiDispatcher` exists because `Process.Exited` fires on a thread-pool thread
  and subscribers update `ObservableCollection`s, which throws.

### Add Game and Scan Folder

- **Icon extraction** tries `PrivateExtractIcons` at 256px first, falling back to
  `ExtractIconEx`. Both are pure Win32 (no `System.Drawing.Common`).
  `ExtractIconEx` alone returns the 32×32 system icon, visibly blurred on a
  150×225 cover tile. Every handle is released with `DestroyIcon`.
- **PE header parsing by hand** for architecture and GUI/console subsystem —
  never loads the image into this process. Subsystem is at `peOffset + 24 + 68`
  for both PE32 and PE32+ (PE32+ spends 8 extra bytes on a 64-bit image base but
  drops `BaseOfData`).
- **Title derivation** prefers ProductName → FileDescription → prettified file
  name; strips Unreal-style `-Win64-Shipping` suffixes; rejects engine
  placeholders (`Unity Player`, `DefaultCompany`).
- **Scan walks iteratively with a stack.** `SearchOption.AllDirectories` abandons
  the whole enumeration on the first unreadable folder, and a games drive
  reliably has one. Skips redist/anti-cheat folders and reparse points (junction
  loops). "launcher" is deliberately **not** filtered — for many games it is the
  entry point the user wants.
- **Launch validation runs immediately before `Process.Start`**, not at import
  time: a game can be moved or replaced by an updater in between.

### Install from URL

Resumable download (HTTP Range), cancel, checksum verification, SharpCompress
extraction, executable auto-detection, user confirmation before registering.

- Writes to `.part` and renames only after the checksum passes, so the final path
  never holds a partial or corrupt file. A failed checksum **deletes** the file —
  resuming corruption never converges.
- **Resume is judged by the response status, not by `Accept-Ranges`.** Some
  servers advertise ranges and ignore them. A 206 continues the file; a 200 means
  the whole thing is coming and the partial prefix must be discarded rather than
  appended to. There is a test that serves exactly that combination.
- The download `HttpClient` has **`Timeout = InfiniteTimeSpan`**. The default
  100 s covers the response body and would abort any large download.
- **Path traversal blocked in two places:** the `Content-Disposition` filename
  (attacker-controlled) and archive entry paths ("zip slip"). A leading separator
  is stripped and treated as relative — safe, and archives legitimately contain
  such entries; a drive-qualified path is rejected outright.
- Collapses a single top-level archive folder so `InstallDir` points at the game,
  not a wrapper.

**Note:** SharpCompress 0.50 renamed `ArchiveFactory.Open` → `OpenArchive(path,
ReaderOptions)`.

### Collections, settings, theming

Settings are written atomically (temp file + move) so losing power mid-save cannot
take the friend code with it. Two palettes; every hard-coded hex was moved into
the palette first, so a theme is a pure dictionary swap.

**Honest constraint:** `StaticResource` binds once, so a theme change applies on
**restart**. The settings page says so rather than appearing half-broken.

### Relay

Schema, registration, device registration, `PresenceHub`, sync endpoints. See §7.

### SignalR client, friends, offline sync

- **Reconnect needs two mechanisms.** SignalR's `WithAutomaticReconnect` only
  covers a connection that drops *after* succeeding once. It does nothing for a
  first connect that never succeeds — the common case when the launcher starts
  before the relay is up. A supervisor loop covers that. Both share one backoff
  policy: exponential, jittered, capped, **never returning null** (SignalR's
  default gives up after ~30 s, right for a web page, wrong for a launcher open
  across a router reboot).
- **Token refresh** is `AccessTokenProvider` reading settings per attempt rather
  than capturing once. That is the whole of what refresh means here — relay
  tokens do not expire.
- Losing the connection marks everyone offline rather than leaving stale "online"
  claims the launcher cannot justify.

**Bug found by tests:** `SignalRRelayHubClient` implemented only
`IAsyncDisposable`, which makes the DI container's synchronous `Dispose()` throw
— on every application exit. It now implements both. **Do not remove the
synchronous `Dispose`.**

### Achievements: engine and providers

Provider architecture, engine, four providers (meta, save-file, memory, manual),
watcher service, progress persistence, hidden achievements.

- **`ProviderKey` string dispatch** (migration v6) rather than the `Kind` enum.
  Dispatching on an enum would make every new provider a core-model edit.
- **The engine throws at construction if two providers share a key.** Two
  providers silently sharing one would mean definitions were evaluated by
  whichever won, which is far harder to diagnose than a startup error.
- **Idempotency has two layers:** already-unlocked definitions are never handed
  to a provider, and `UnlockAsync` is insert-only and returns true only on the
  transition — so events, toasts and counts all hang off that.
- **Providers:** Meta (skips `RunningPoll`), SaveFile (JSON dotted paths with
  array indices, XPath with **XXE blocked**, INI, regex; `FileShare.ReadWrite` so
  a running game's open save can still be read), Memory (`PROCESS_QUERY_INFORMATION
  | PROCESS_VM_READ` only), Manual (~30 lines, demonstrates the extension point).
- **`AchievementWatcherService`** — startup library pass, game start/exit, 1.5 s
  running poll, directory-level `FileSystemWatcher` with a 2 s settle delay and
  burst coalescing.

### Achievements: the interface

Achievements page, editor, and toast presenter. **This half added no schema
change at all** — everything it shows was already stored.

- **Concealment lives in the view model, not the template.** A template that
  declines to draw a hidden achievement's title still has the real text bound
  into the visual tree, where a tooltip, an automation client or a copy command
  can reach it. `DisplayTitle` / `DisplayDescription` / `DisplayIconPath`
  substitute at that boundary. Progress is suppressed too — "34 / 50" discloses
  both the goal and how close the player is.
- **The page cannot evaluate anything.** It depends on `IAchievementRepository`
  for rows and on `IAchievementEngine` only for `Providers` /
  `IsProviderAvailable`, which is metadata. There is no path from the page to an
  unlock.
- **The editor's Test Read is structurally inert.** `TestAsync` does not route
  through `RunAsync`; persistence and notification both live there.
- **Toast queueing is a service, not a view model.** Ordering and dwell are
  application logic. It is registered as a hosted service *before* the watcher,
  so it is subscribed before the startup pass — subscribing when the shell window
  is first built would silently drop anything that pass earns.

**Bug found by tests:** the notification service originally re-raised
`CurrentChanged` when a new unlock arrived while one was already on screen, to
refresh the "+N more" badge. That made every subscriber counting announcements
see the same one repeatedly — a test asserting three unlocks produced
`[ACH_ONE, ACH_ONE, ACH_ONE, ACH_TWO, ACH_THREE]`. The pump is now the sole
publisher and the event means what its name says.

### Download integration coverage

15 end-to-end tests against a real Kestrel server on a loopback port. See §11.

### Polish

There were no TODOs, no `NotImplementedException`, and no undocumented public
members to find. What it did find:

- **The Home page was a placeholder** — it carried the text "Recently played
  titles and library highlights" and showed neither, on the first screen the
  application opens to. It now shows a recently-played row and three library
  totals, built entirely from repository methods that already existed. No new
  services, no schema change.
- **Home opens a game rather than launching it.** Launching from the landing page
  would mean a second copy of the details page's error handling inside a view
  model whose job is to summarise.
- **`NavigationSection.Search` was removed.** Nothing mapped to it and the
  navigation switch would have thrown had anything reached it. `Settings` keeps
  its value of 6; renumbering an enum gains nothing.
- **Two dead members went from `AchievementItemViewModel`** — `GameTitle` became
  redundant when the page started grouping by title, and `ProgressTarget` only
  shadowed `Definition.ProgressTarget`.

### Post-1.0 fix: solid-archive extraction

Reported from real use: unpacking a 650 MB, 2192-entry 7z was unusably slow.

The cause was not 7z being slow. `ExtractCore` iterated `archive.Entries` and
called `entry.OpenEntryStream()` per entry. **A solid archive compresses every
file into one continuous stream**, so opening an entry directly makes the decoder
run from the start of that stream to reach it — one full decode per entry, which
is quadratic. Measured on that archive: a single late entry cost 875 ms by random
access, against 59 seconds for one forward pass over all 2192.

Now gated on `IArchive.IsSolid`:

- **Solid** (7z, solid RAR) → `archive.ExtractAllEntries()`, a forward-only
  reader that decodes the stream once. Full extraction of that archive: **69
  seconds** for 1.42 GB, all 2192 files.
- **Not solid** (zip) → random access as before, which is already optimal because
  each entry is compressed independently. SharpCompress actively refuses
  `ExtractAllEntries` here, which is how the first attempt at the fix was caught:
  it broke all six zip extraction tests.

Progress reporting was throttled to 200 ms in the same change. Unthrottled, 2192
entries meant 2192 posts to the interface thread; it is now 96.

### Final cleanup

- **`Microsoft.Toolkit.Uwp.Notifications` removed** and both Windows target
  frameworks lowered from `net8.0-windows10.0.17763.0` to **`net8.0-windows`**.
  The package was the only reason for pinning Windows 10 1809 and no code used
  it. Verified absent from the restored dependency graph, not just the csproj.
- **Repository tracking fixed.** An early commit captured build output before a
  `.gitignore` existed; 1008 such files were removed from the index without
  touching the working tree. A `.gitattributes` was added so the history does not
  carry CRLF into a Linux checkout of `Shared` or `Relay`.

---

## 4. Current implementation state

### Verified by running the software

- Launcher starts, migrates schema v0→v6, opens, navigates. Verified repeatedly,
  most recently on the lowered target framework with a clean log and exit code 0.
- The **Home** and **Achievements** pages open in the running application with no
  resource failure and no binding error.
- Library renders seeded games in grid and list; details page renders with
  achievements.
- Relay runs as a process; `/health` and `/relay-info` respond.
- **Registration → connection → sync, end to end against a real relay process:**
  the relay assigned a friend code, state went `Connecting → Connected`, and all
  8 provisional catalog entries were promoted on first connect.
- **Catalog resolution across users:** two different registered users resolving
  the same fingerprint received the *same* catalog id.
- **Achievement sync conflict rules over HTTP:** push → `accepted=1`; replay →
  `accepted=0`, unchanged; push earlier → time moves earlier; push later → does
  **not** move forward; pure fetch recovers history.
- **Relay identity:** id stable across relay restarts (same database), different
  for a different database.
- Settings persist; first run generates a valid friend code.
- Externalised configuration works — the relay database path was supplied via the
  `Relay__Database__ConnectionString` environment variable.

### Verified by automated tests only

- **The download path, thoroughly** — resume, redirects, checksums, cancellation,
  interruption recovery and extraction, all end to end over a real socket. No
  download from a host on the internet has been performed by this code.
- Add Game, Scan Folder, Install from URL, and the achievement editor **dialogs**:
  realised by the WPF smoke tests (construction + full layout pass), but nobody
  has clicked through the flows by hand.
- Collections page membership moves.
- Relay migration between two relays — six integration tests, never done by hand
  with two real relay processes.
- The achievement engine end to end. **No achievement has been earned by actually
  playing a game.**
- The toast overlay, including queueing and ordering.

### Never exercised

- **The memory provider against a real running game.** `ProcessMemoryReader` has
  never been pointed at a live process. Its failure paths are handled; its
  success path is unproven in reality. This is the single largest gap between
  what is tested and what is known to work.

### Known limitations

1. **Memory achievements are unproven in practice** (above).
2. **`PresenceTracker` is in-process.** Correct for one self-hosted instance;
   wrong the moment the relay runs on more than one node — it needs a shared
   store plus a SignalR backplane.
3. **No PostgreSQL implementation.** The schema and every query are portable;
   `RelayDatabaseProvider.Postgres` throws at startup by design. Adding it is a
   package reference plus one ~20-line factory.
4. **Playtime does not sync.** Only the schema for it exists (deliberate — §8).
5. **Achievement icons cannot be set in the editor.** `IconPath` is preserved
   across an edit and rendered when present, but nothing populates it.
6. **Stats are unwired.** `StatApiName` and `ProgressTarget` exist and the
   `GameStat*` tables exist, but no provider reads stats and the editor cannot
   author against one.
7. **Custom providers have no editor panel.** Their rule JSON round-trips
   untouched, but authoring one means writing the JSON by hand.
8. **The toast backlog badge is a snapshot.** "+N more" is counted when an
   announcement appears and is not refreshed while it is on screen (§8).
9. **A theme change applies on restart**, because `StaticResource` binds once.
10. **Sample catalog entries hold ids from a throwaway relay.** During end-to-end
    verification the seeded entries were promoted against a temporary relay that
    no longer exists. Harmless for sample data, and connecting to a real relay
    correctly treats them as foreign and re-resolves them.

---

## 5. Roadmap

**The planned roadmap is complete.** Everything below is optional, ordered by
value.

1. **Prove the memory provider against a real game.** The largest gap between
   tested and known-working. Needs a running process and a known offset.
2. **Achievement icon picker** — finishes the editor. Needs a file picker writing
   into `AppPaths.AchievementIconDirectory` so the icon survives the source file
   moving.
3. **Stat-driven achievements** — a provider reading `GameStatValue`, plus the
   repository those tables never got. The last piece of the achievement model
   that exists in the schema but not in code.
4. **PostgreSQL factory**, when a VPS is provisioned. One package and one
   ~20-line class; see [deployment.md](deployment.md).
5. **A light theme.** Now a pure palette swap, but its contrast is unverified.
6. **Remaining test gaps** (§11) — none blocking.

### Intentionally deferred

- **Cloud saves.** Would add `SaveSlot` keyed `(FriendCode, CatalogId, SlotName)`
  plus blob storage. Unblocked: it attaches to catalog identity.
- **Workshop/mods.** Same shape, same reason.
- **Playtime sync.** Schema ready; see §8 for why totals cannot be merged.
- **Rarity, global completion, leaderboards.** All are relay-side aggregates over
  existing tables; no client schema change needed.
- **Operator merge tooling.** Client and relay both support merge; the admin UI
  does not exist. Needs a duplicate-candidate view, a merge preview, and an audit
  log — merges are not reversible once aliases move.
- **Multi-device pairing.** Schema supports it fully (§8); no endpoint yet.

---

## 6. Database documentation

### Client database

Location: `%LOCALAPPDATA%\Don\gamelauncher.db`. WAL mode, foreign keys
**ON** — per-connection, because SQLite defaults them off, so
`SqliteConnectionFactory` sets it every time or the cascades silently do nothing.

> **Inspecting the file directly:** it runs in WAL mode. Copying only
> `gamelauncher.db` yields a stale snapshot missing recent commits. Copy the
> `-wal` and `-shm` sidecars alongside it. This wasted real debugging time once.

#### Tables

**`Game`** — one row per installed game.
`Id` (local PK), `GlobalKey` (installation-local identity, 32 hex),
`CatalogId` → `CatalogEntry` (shared identity), `Title`, `CoverArtPath`,
`HeroArtPath`, `ExecutablePath`, `InstallDir`, `InstallSizeBytes`,
`PlaytimeSeconds`, `LastPlayedAt`, `DateAdded`, `Tags` (JSON array),
`CollectionId` → `Collection`, `Notes`, `SourceUrl`, `UpdatedAt`.

**`Collection`** — exclusive grouping. `Id`, `Name` (unique NOCASE), `SortOrder`,
`DateAdded`. A game belongs to at most one; `Tags` is the overlapping mechanism.

**`PlaySession`** — one row per launch-to-exit. `Id`, `SessionKey` (**globally
unique**, assigned at launch), `GameId` → `Game` CASCADE, `DeviceId`, `StartedAt`,
`EndedAt`, `DurationSeconds`, `SyncedAt`.
A row with null `EndedAt` on startup is the residue of a crash; startup closes
those out crediting **zero** time, because the only honest answer to "how long
was this played" is that we do not know.

**`CatalogEntry`** — the shared identity of a *title*. `CatalogId` (PK), `Source`
(which relay assigned it, or `local`), `IsProvisional`, `CanonicalTitle`,
`MatchFingerprint` (provenance only), `CreatedAt`, `UpdatedAt`, `SyncedAt`,
`SupersededByCatalogId` → self.

**`CatalogAlias`** — many fingerprints → one title. `Fingerprint` (PK),
`CatalogId` → `CatalogEntry`, `Source`, `CreatedAt`. **This is the authoritative
fingerprint lookup**, not `CatalogEntry.MatchFingerprint`.

**`AchievementDefinition`** — the catalog of achievements. `Id`, `CatalogId` →
`CatalogEntry` CASCADE, `ApiName`, `GlobalKey`, `Title`, `Description`,
`IconPath`, `Kind` (display category), `ProviderKey` (**dispatch key**),
`TriggerConfigJson`, `IsHidden`, `SortOrder`, `ProgressTarget`, `StatApiName`,
`UpdatedAt`, `GameId` (**inert — see below**).

**`AchievementUnlock`** — insert-only history. `DefinitionId` (PK, →
`AchievementDefinition` CASCADE), `UnlockedAt`, `SyncedAt`. The row's *presence*
is the unlock; there is no boolean.

**`AchievementProgress`** — mutable progress, separate from unlocks.
`DefinitionId` (PK), `CurrentValue`, `UpdatedAt`.

**`GameStatDefinition` / `GameStatValue`** — named counters for progressive
achievements. Definition and value are split so a shared catalog can ship
definitions without personal numbers. **No repository yet.**

**`FriendCache`** — offline friend list. `FriendCode` (PK), `DisplayName`,
`LastKnownGame`, `LastSeenAt`, `AvatarPath`. Cache only, never truth.

#### Key constraints

| Object | Why it exists |
|---|---|
| `UX_Game_GlobalKey`, `CatalogEntry` PK | Identity uniqueness |
| `UX_AchievementDefinition_Catalog_ApiName` on `(COALESCE(CatalogId,''), ApiName NOCASE)` | **`COALESCE` is essential** — SQLite treats NULLs as distinct in a unique index, so library-wide achievements (`CatalogId IS NULL`) would otherwise collide freely |
| `UX_PlaySession_SessionKey` | Idempotent session merge |
| `IX` on `AchievementUnlock.SyncedAt`, `PlaySession.SyncedAt` | The outbound queues |
| `Game.CatalogId` FK `ON UPDATE CASCADE` | **Load-bearing.** Promotion and demotion rewrite the catalog primary key; the cascade carries every reference |

#### Migration history

| Version | Change | Reasoning |
|---|---|---|
| **v1** | Collection, Game, AchievementDefinition, AchievementUnlock, FriendCache, PlaySession | — |
| **v2** | `GlobalKey`/`UpdatedAt`; `ApiName`, `IsHidden`, `SortOrder`, `ProgressTarget`, `StatApiName`; `AchievementUnlock.SyncedAt`; new `AchievementProgress`, `GameStatDefinition`, `GameStatValue` | Identity is cheap to add now, impossible to retrofit once unlocks exist — a wrong guess would attribute someone's unlock to the wrong achievement |
| **v3** | `CatalogEntry`; `CatalogId` on Game/AchievementDefinition/GameStatDefinition; **`GameId` nulled** on the latter two | Achievements must belong to the *title*, not one installation. Behaviour change: **uninstalling a game no longer erases its achievements** |
| **v4** | `CatalogAlias`; `CatalogEntry.SupersededByCatalogId`; alias seeding | One title legitimately has several fingerprints; merges must not rewrite an assigned id |
| **v5** | `PlaySession.SessionKey`, `DeviceId`, `SyncedAt` | Sessions, not totals, are the mergeable unit |
| **v6** | `AchievementDefinition.ProviderKey` + backfill | Dispatching on the `Kind` enum would make every new provider a core-model edit |

**The schema is at v6 and the achievements interface added nothing to it.** The
UI is built on columns added in v2 and v6 precisely so it would not need a
migration. If you are looking for a v7 because the UI landed, there isn't one.

#### Non-obvious choices

- **The vestigial `GameId` columns.** SQLite refuses `DROP COLUMN` on a column
  named in a foreign key. Rebuilding the table would mean dropping it, and with
  foreign keys enabled `DROP TABLE` performs an implicit `DELETE FROM` — which
  would cascade **every `AchievementUnlock` row out of existence.** v3 sets the
  columns to `NULL` instead: the cascade becomes unreachable, the columns are
  inert, nothing reads them, and the model classes do not expose them.
  **Do not attempt to drop them inside a transaction.**
- **Client timestamps keep their local offset** (ISO-8601 round-trip). The client
  *displays* times. Contrast the relay, which normalises to UTC because it only
  ever orders them.
- **`MatchFingerprint` is provenance, not lookup.** Keeping it authoritative as
  well would give two sources of truth that could drift.

### Relay database

Schema **v1**, tracked in a `SchemaVersion` table — not `PRAGMA user_version`,
which has no PostgreSQL equivalent.

`AppUser`, `Device`, `Friendship`, `Presence`, `CatalogEntry`, `CatalogAlias`,
`UserAchievement`, `UserLibrary`, `RelayMetadata`, `SchemaVersion`.

**`AppUser`, not `User`** — `user` is reserved in PostgreSQL, and a table that
needs quoting everywhere is an invitation to get it wrong once.

**`UserAchievement` is keyed `(FriendCode, CatalogId, ApiName)`** — never a
definition row id. This is the single most load-bearing relay decision: a row id
belongs to whichever database produced it and a catalog merge may delete one; the
api name is the stable authored handle. It makes a merge a data-movement problem
rather than a history-loss problem.

---

## 7. Relay architecture

Full detail in [relay-architecture.md](relay-architecture.md). Summary:

### Responsibilities

Source of truth for identity and friend codes, devices, friendships and requests,
presence, catalog identity and aliases, and synchronised achievement history. It
knows nothing about any machine's filesystem or installed games.

### Authentication

- `POST /register { displayName }` → `{ friendCode, authToken, deviceId }`.
  Anonymous; no password, no email, no recovery. The token **is** the credential.
- `Authorization: Bearer glr_…`. SignalR also accepts `?access_token=`, but only
  on the hub path, so tokens stay out of access logs for ordinary requests.
- **Unsalted SHA-256.** Reasoned in §8.
- Database-backed rather than JWT, so revocation is immediate.

### Friend system

One directed `Friendship` row created by the requester; acceptance sets `Status`
rather than creating a second row. Rejection **deletes** — so the requester can
try again and the relay keeps no list of who declined whom.

Two security properties, both covered by tests:

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

`Hub<IPresenceClient>` with a custom `IUserIdProvider` returning the friend code,
so `Clients.User(code)` addresses the person and reaches every device they have
online. That is the whole of what multi-device delivery needs.

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
`ON UPDATE CASCADE` carries every reference. A collision — the relay returning an
id the client already holds — is the **normal** outcome of the catalog working,
not an error; it triggers a merge.

Catalog creation is **open**: a miss creates the entry rather than failing, so
users never wait for moderation. Accepted cost: duplicates until someone merges.

### Relay migration

Relays are identified by an id from `GET /relay-info`, stored in the relay's own
database. Address comparison would get both interesting cases wrong — a relay
moved to a VPS looks new; a different relay at the same URL looks the same.

On a detected change: demote foreign catalog entries to provisional, clear unlock
and session sync watermarks, clear the friend cache, select or create credentials
for the new relay. **Nothing local is deleted.** Credentials are kept per relay,
so switching back restores the original identity. Offline-safe (never migrates on
a failed probe) and idempotent.

### Portability rules

Relay migrations must stay PostgreSQL-compatible: no `PRAGMA`, no `randomblob()`,
no `strftime()`, no `AUTOINCREMENT`, no `DEFAULT` on boolean or timestamp columns
(PG rejects `DEFAULT 0` on a boolean), no two-argument `MIN` (PG spells it
`LEAST` — use `CASE`).

Timestamps are UTC ISO-8601 **text**. PostgreSQL would prefer `timestamptz`;
converting is one `ALTER TABLE … USING (col::timestamptz)` per column and no
application change, because Dapper already maps through `DateTimeOffset`.

---

## 8. Important design decisions

### CatalogId vs GlobalKey

**Decision.** `GlobalKey` identifies *an installation's row*; `CatalogId`
identifies *a title* across all users. Everything cross-user keys on `CatalogId`.

**Reasoning.** `GlobalKey` is minted locally, so two people who own the same game
generate unrelated values. It cannot express "the same title", which is exactly
what global achievements need.

**Alternatives considered.** Use `GlobalKey` as the global id — fails immediately
across users. Match on title string — breaks on re-releases, localisation and
punctuation. Integer AppIDs like Steam — requires a central authority we do not
have.

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

**Note:** `CatalogId` is deliberately *not* denormalised onto `PlaySession`. It is
reached by joining through `Game`, so a session recorded while the game still had
a provisional id automatically follows the promotion. A copied id would freeze
the stale one.

### Achievement architecture

**Decision.** Providers decide; the engine persists and notifies; the sync service
handles the network. Dispatch is by string `ProviderKey`.

**Reasoning.** Keeping decisions pure makes a provider testable with no database
and no network. A string key means adding a provider is a container registration
— dispatching on the `Kind` enum would make every new provider an edit to the
core model.

**Alternatives considered.** Enum dispatch (rejected — above); providers writing
their own unlocks (rejected — idempotency would then be every provider's problem,
and they would each get it slightly wrong).

**Future impact.** New providers need no engine, schema or interface change.
`Kind` remains only as a UI grouping category.

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

What follows automatically: the engine picks it up (it resolves
`IEnumerable<IAchievementProvider>`) and throws at construction if two providers
claim one key; it appears in the editor's provider picker, because that list comes
from `IAchievementEngine.Providers`; the editor's Test Read works against it; and
definitions naming it stop being reported as inert.

Two rules the engine enforces so a provider cannot corrupt anything:

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
saves and loads without a panel — its stored configuration is carried through
untouched — so authoring one currently means writing the JSON. Adding a panel is
a `Visibility` block in `AchievementEditorWindow.xaml` plus a case in
`TryBuildRule`/`LoadRule`.

### Announcements are queued in a service, not a view model

**Decision.** `IAchievementNotificationService` owns the queue and the dwell
timer; `AchievementToastHostViewModel` only renders whatever it says is current.

**Reasoning.** Ordering and timing are application logic, which this project's
rules keep out of view models. It also makes the behaviour testable without a WPF
dispatcher: the interesting guarantee — several achievements earned in one pass
appear one after another rather than on top of one another — is asserted against
the service directly.

**Alternatives considered.** A view model owning a `DispatcherTimer` (rejected —
untestable and against the project's own rules); toasting straight from the
engine (rejected — the engine would then decide presentation).

**One deliberate trade-off.** `CurrentChanged` fires only when the announcement on
screen actually changes, so the "+N more" badge is a snapshot from when that
announcement began and does not tick up if more are earned behind it. The first
version refreshed it, and that made every subscriber counting announcements see
the same one repeatedly. Correctness of the event won; the badge corrects itself
within one dwell.

**UI thread.** The engine already raises `AchievementUnlocked` through
`IUiDispatcher`, but the toast host marshals again anyway. `Invoke` runs inline
when the caller is already on the UI thread, so it costs nothing and leaves the
overlay correct independently of who raises the event.

### An in-app overlay rather than a Windows toast

**Decision.** Achievement unlocks are announced by an overlay drawn inside the
shell window. `Microsoft.Toolkit.Uwp.Notifications` was referenced for most of the
project's life and has been removed.

**Reasoning.** An unpackaged WPF app needs a Start Menu shortcut carrying an AUMID
before Windows will show it a toast at all — a per-machine install-time
side-effect for a cosmetic feature. The overlay needs no package, no registration
and no raised target framework, and it is closer to what Steam actually does.

**Consequence, and it is a real one.** Nothing appears when the launcher is
minimised or behind the game. For a launcher whose achievements are earned while a
game is in the foreground, that is the wrong half of the time. If that matters,
implement a Windows toast behind the same `IAchievementNotificationService`
consumer rather than replacing the queue — the service is UI-agnostic precisely
so a second presenter can be added without touching it.

**Target framework.** Removing the package let both Windows projects drop from
`net8.0-windows10.0.17763.0` to `net8.0-windows`. Every native call the launcher
makes — icon extraction, read-only process memory, the dark title bar — either
predates Windows 10 or is best-effort with a documented fallback.

### Device identity

**Decision.** One user, many devices, from the very first release. The friend code
identifies the person; the token identifies the machine.

**Reasoning.** Cheap now, effectively impossible to retrofit — a token issued as a
*user* credential cannot be split into per-device credentials without invalidating
everyone's existing one.

**Future impact.** Adding a machine is `POST /devices/pair` plus a short-lived
pairing code; revoking one is setting `RevokedAt`. Neither needs a schema change.

### Relay identity

**Decision.** A relay reports an id from `GET /relay-info`, generated once and
stored in its own database. Clients compare that, never the URL.

**Reasoning.** The id travels with the data. Moving the relay to a VPS, or
restoring it from backup, keeps the identity — clients carry on. Pointing at a
genuinely different relay is detected.

**Alternatives considered.** URL comparison (wrong in both directions);
certificate fingerprint (breaks on renewal); operator-configured name (changes
whenever someone edits a file).

### Token hashing — unsalted SHA-256

**Decision.** Store `SHA256(token)` with no salt and no slow KDF.

**Reasoning.** Salt and a slow KDF exist to make *guessing* expensive, and
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

| Path | Purpose |
|---|---|
| `README.md` | Entry point: what it is, prerequisites, build, run, reset |
| `docs/` | Architecture and deployment documents. **Keep current** — they are the design record |
| `global.json` | SDK pin (8.0.423) |
| `Directory.Build.props` | Solution-wide: nullable, implicit usings, XML docs |
| `.gitignore` / `.gitattributes` | Build output excluded; line endings normalised to LF |
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
  else changes — see §8.
- **New page:** ViewModel in `ViewModels/`, View in `Views/`, `DataTemplate` in
  `Resources/ViewTemplates.xaml`, case in `MainWindowViewModel.NavigateAsync`,
  nav item in `MainWindow.xaml`, registration in `ServiceRegistration`, smoke
  test in `DialogSmokeTests`. `AchievementsViewModel` / `AchievementsView` is the
  most recent worked example.
- **New dialog:** as above plus a `DialogRegistry.Register<TViewModel, TWindow>()`
  entry. View models must **never** name a `Window` type. If the dialog needs to
  be told what it is editing, use
  `IWindowService.ShowDialogFor<TViewModel>(vm => vm.Initialize(...))` rather than
  adding a constructor parameter.
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
and CS1591 is deliberately left as a visible warning. The build is at **0
warnings**; keep it there.

**Comments explain *why*, not *what*.** The existing code comments non-obvious
decisions, trade-offs and traps. Match that density — do not add narration.

### Dependency injection

Constructor injection throughout, with `?? throw new ArgumentNullException`. All
registration in `ServiceRegistration.AddGameLauncher`, grouped by area.

- Repositories and stateless services: **singleton** (each call opens its own
  connection).
- Page view models: **transient** (each navigation starts clean).
- Dialog view models and windows: **transient** (a closed WPF window cannot be
  reshown).
- `MainWindow` and the toast host: **singleton**. *Note: this is why tests that
  realise `MainWindow` need a fresh container per iteration.*

Anything registered as a singleton that implements `IAsyncDisposable` **must also
implement `IDisposable`** — the container disposes synchronously.

An object needing two roles (the notification service is both a notifier and a
hosted service) is registered concretely once and forwarded:

```csharp
services.AddSingleton<AchievementNotificationService>();
services.AddSingleton<IAchievementNotificationService>(p => p.GetRequiredService<AchievementNotificationService>());
services.AddHostedService(p => p.GetRequiredService<AchievementNotificationService>());
```

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
- Translate storage errors into domain errors — `CollectionRepository` turns a
  SQLite unique violation into `InvalidOperationException` with a user-facing
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
interpolation). `Debug` for flow, `Information` for state changes worth seeing,
`Warning` for recoverable problems, `Error` for failures.

**Never log secrets.** Friend codes are public and logged; auth tokens are not.

### Testing

- xunit. Test names are sentences:
  `Merge_preserves_an_unlock_only_the_absorbed_entry_had`.
- Integration tests use `TestAppHost`, which builds the **real** DI graph against
  a temp directory — so a service missing from the real composition root fails the
  tests too.
- WPF tests use `WpfTestHost` (one STA thread, one `Application`). **A `Window`
  can only be constructed on that thread**, so resolve it inside `_wpf.Invoke`;
  but `await` view-model loads *outside* it, because they use
  `ConfigureAwait(true)` and blocking the dispatcher deadlocks against their own
  continuation.
- Relay tests use `RelayTestFactory` (`WebApplicationFactory<Program>`,
  long-polling transport).
- **Validate that a new test can fail** — see §11.

### Database access

- Dapper only. Parameterised queries always.
- Client migrations may use SQLite-specific SQL. **Relay migrations may not.**
- Never edit an existing migration.

---

## 11. Testing status

**150 tests, all passing.** Single project: `GameLauncher.Tests`.

| Suite | Count | Covers |
|---|---|---|
| `Download.ArchiveExtractionTests` | 20 | Zip-slip, traversal, real zips, format detection, progress throttling |
| `Views.DialogSmokeTests` | 18 | Every window and page realised; both palettes |
| `Achievements.SaveFileReaderTests` | 16 | JSON/XML/INI/regex, XXE, locked files |
| `Download.DownloadIntegrationTests` | 15 | End-to-end HTTP: resume, redirects, checksums, cancellation |
| `Achievements.AchievementPresentationTests` | 11 | Concealment, progress, grouping, filtering, missing providers |
| `Download.DownloadServiceTests` | 11 | Filename sanitisation, traversal |
| `Catalog.CatalogIdentityTests` | 11 | Fingerprints, promotion, merge, repair |
| `Achievements.AchievementEditorTests` | 9 | Test Read inertness, provider validation, authoring |
| `Achievements.AchievementEngineTests` | 9 | Idempotency, extensibility, progress |
| `Relay.PresenceHubTests` | 7 | Auth, requests, presence isolation |
| `Friends.RelayMigrationTests` | 6 | Relay switching, data preservation |
| `Achievements.AchievementToastTests` | 6 | Announcement queueing, order, no duplicates |
| `Friends.BackoffPolicyTests` | 5 | Never gives up, no overflow, jitter |
| `Friends.OfflineSyncTests` | 4 | Offline queue, reconnect drain |
| `Achievements.OfflineUnlockFlowTests` | 2 | Full offline → restart → reconnect → sync |

### Integration tests worth knowing about

- **`OfflineUnlockFlowTests`** — the flagship. Unlocks offline, **restarts** (a
  genuinely new container over the same database), reconnects, syncs, and asserts
  timestamps, progress and idempotency.
- **`DownloadIntegrationTests`** — a real Kestrel server on a loopback port
  (`Infrastructure/LoopbackFileServer.cs`), not a stubbed message handler, so
  bytes genuinely move over a socket. It serves ranges, ignores ranges while still
  advertising them, drops the connection mid-body, redirects, and stalls between
  chunks. Kestrel rather than `HttpListener` because the latter needs a Windows
  URL ACL reservation a test run cannot assume it has; port 0 so a run never
  collides with anything.
- **`PresenceHubTests`** — real in-process relay via `WebApplicationFactory`,
  including proving presence does not leak to non-friends.
- **`RelayMigrationTests`** — switching relays preserves game, collection, tags,
  achievement definition, unlock and play session.
- **`DialogSmokeTests`** — catches `StaticResource` failures that compile fine and
  throw at load. **Item templates only instantiate when their control has items**,
  so these tests populate view models first; an earlier version passed with a
  deliberately broken resource because the list was empty. The achievement cases
  seed one row of every display state — unlocked, locked, progressing, hidden,
  orphaned provider — because each is a different branch of the template.

### The discipline: prove a test can fail

Two tests in this codebase once passed for the wrong reason. Since then, every
test asserting an absence or a security property has been validated by breaking
the code it covers and confirming it fails. Recorded results:

| Fault injected | Tests that failed |
|---|---|
| `DisplayTitle` returns the real title | 2 (concealment) |
| `TestAsync` routes through the persisting path | 4 (unlocks, progress *and* notifications) |
| Editor substitutes an installed provider for a missing key | 1 |
| Re-raise `CurrentChanged` on enqueue | 1 — **this one found a real bug**, not a confirmation |
| Trust `Accept-Ranges` instead of the response status | 1 |
| Never send the `Range` header | 5 |
| Disable the zip-slip guard | 6, including the end-to-end one asserting nothing was written above the destination |

Do this for any new test asserting an absence.

### Two deliberate testing choices

- **`AchievementToastTests` builds its engine explicitly** with a substituted
  dispatcher rather than resolving it. The container's `UiDispatcher` binds to the
  WPF test host's thread whenever an `Application` exists in the process, so a
  test outside that collection would otherwise marshal onto — and block on — a
  dispatcher owned by a different collection.
- **One assertion is deliberately loose.** The backlog count published with each
  announcement is not asserted exactly, because whether an unlock is queued before
  or after the pump picks up the previous one is a real race. Both `[2,1,0]` and
  `[0,1,0]` are correct; only "empty by the last one" is invariant.

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

## 12. Known technical debt

| Item | Notes |
|---|---|
| Vestigial `GameId` columns | On `AchievementDefinition`/`GameStatDefinition`. Removing needs a table rebuild outside a transaction — see §6 for why that is dangerous |
| `GameStat*` tables unused | Schema exists, no repository, nothing writes them |
| `PresenceTracker` in-process | Blocks multi-instance relay |
| Custom providers have no editor panel | Their rule JSON round-trips, but authoring one means writing it by hand |
| Achievement icons cannot be chosen | `IconPath` renders and survives an edit; nothing sets it |
| Toasts are invisible when the launcher is not in the foreground | Inherent to the in-app overlay; see §8 |
| Sample data holds throwaway-relay catalog ids | See §4 limitation 10 |

---

## 13. Open questions

1. **Should a Windows toast be added after all?** The in-app overlay does not
   appear while a game is in the foreground, which is when achievements are
   earned. The alternative costs an AUMID and a Start Menu shortcut. The
   notification service is UI-agnostic so both could coexist. §8 has the full
   trade-off.
2. **Who may create catalog entries long-term.** Currently open creation.
   Promotion-on-N-independent-matches was considered and deferred — the aliases
   needed to compute it are already recorded, so it can be added without a schema
   change.
3. **Catalog entry lifecycle.** Achievements now outlive uninstalls, so catalog
   entries and definitions accumulate. No cleanup exists. Probably correct —
   discarding earned achievements to reclaim rows would be wrong — but the growth
   is unbounded.
4. **Should `Kind` survive?** It is now only a UI grouping category while
   `ProviderKey` does the dispatch. Either give it a `Custom` member for
   third-party providers, or drop it and let providers supply their own display
   name.
5. **A light theme.** Now a pure palette swap, but its contrast is unverified.
6. **Relay federation.** `CatalogEntry.Source` records the issuing relay, but
   nothing consumes it beyond migration detection. Two relays sharing a catalog is
   unexplored.
7. **Should the build enforce zero warnings?** The convention is documented and
   currently held, but `TreatWarningsAsErrors` is not set. Enforcing it mechanically
   would guarantee it and would also fail the build on an unrelated warning after
   an SDK bump.

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
  both live in `RunAsync`; keeping the paths separate is what makes "testing a
  rule cannot award it" structural rather than a step somebody has to remember to
  skip. Four tests fail if this changes.
- **Concealment stays in `AchievementItemViewModel`, not the template.** Binding
  the real title and hiding it in XAML leaves it reachable through tooltips,
  automation and copy.
- **An unrecognised `ProviderKey` is left alone, never rewritten.** Substituting
  an installed provider would silently change what an achievement means, and its
  unlock would survive under the new meaning.
- **One announcement pump.** `AchievementNotificationService` must keep a single
  pump and publish `CurrentChanged` only from it.
- **No memory writing.** Only `OpenProcess`, `ReadProcessMemory` and
  `CloseHandle` are imported anywhere in the project. Keep it that way.
- **The `IArchive.IsSolid` branch in `ArchiveExtractionService`.** Collapsing it
  back to a single `archive.Entries` loop looks like a simplification and makes
  extraction of any solid archive quadratic — the difference between 69 seconds
  and half an hour on a real game archive. The two branches share one `Write`
  local so the path validation still has exactly one implementation.
- **Relay migrations stay PostgreSQL-portable** (§7).
- **`global.json`** — removing it silently switches the build to SDK 10.
- **`<Using Include="System.IO" />` in the Desktop csproj** — the `_wpftmp` fix.
- **Migrations are append-only.**

### Backwards compatibility

- **Settings file.** `AppSettings.SchemaVersion` exists for this. The v1→v2
  migration (flat token → per-relay identities) is the model: carry data across,
  never discard. Silently deregistering a user is data loss.
- **Wire contracts.** New DTO fields must be optional; the relay may be older than
  the client or vice versa.
- **`ApiName` values.** Once an achievement has synced, changing its api name
  orphans everyone's unlock.
- **`CatalogId` once assigned is immutable.** Promotion of a *provisional* id is
  the sole exception, and only because no relay has ever seen it.

### Preferred approach for new work

1. **Read the relevant `docs/` file first.** They record reasoning, not just
   description.
2. **Interface first, then implementation**, so it can be substituted in tests.
3. **Offline path first.** Ask what happens with no relay before writing the
   online path.
4. **Idempotency is a requirement, not a nicety**, for anything synced.
5. **Verify at runtime, not just at compile time.** XAML resource failures, DI
   disposal problems and WAL snapshot issues all compile cleanly. Run the app; run
   the relay; realise the window.
6. **Prove a new test can fail** before trusting it.
7. **Keep the build at 0 warnings.**
8. **Update `docs/` in the same change** when a decision changes.

---

## Appendix A — Build, run, and operate

### Prerequisites

- .NET SDK **8.0.423** (pinned by `global.json`); newer 8.0.4xx patches are
  accepted via `rollForward: latestFeature`.
- **Windows** to build or run `Desktop` and `Tests`. `Shared` and `Relay` build
  anywhere. There is no Windows 10 build-time requirement — that was removed with
  the toast package.

If a build suddenly targets .NET 10, `global.json` is missing or you are outside
the solution directory. `dotnet --version` there should report `8.0.423`.

### Build and test

```bash
dotnet build "GameLauncher.sln"
```

```bash
dotnet test "GameLauncher.Tests/GameLauncher.Tests.csproj"
```

Expected: **0 warnings, 0 errors**; **150 tests passed**. Warnings are meaningful
here — CS1591 is deliberately visible, so a non-zero count means something
regressed.

### Run the launcher

```bash
dotnet run --project "GameLauncher.Desktop"
```

With the sample library (only seeds when the library is empty):

```bash
dotnet run --project "GameLauncher.Desktop" -- --seed-sample-data
```

Sample entries point at executables that do not exist, so launching them fails by
design. That is the only startup switch.

### Run the relay

```bash
dotnet run --project "GameLauncher.Relay" --launch-profile http
```

Listens on `http://localhost:5107` (the `https` profile adds
`https://localhost:7141`). Quick check:

```bash
curl http://localhost:5107/relay-info
```

Configuration is `appsettings.json` → section `Relay`, overridable by environment
variable with the double-underscore convention:

```bash
Relay__Database__ConnectionString="Data Source=/var/lib/gamelauncher/relay.db"
```

| Setting | Default | Notes |
|---|---|---|
| `Relay:Database:Provider` | `Sqlite` | `Postgres` throws — factory not implemented |
| `Relay:Database:ConnectionString` | `Data Source=gamelauncher-relay.db` | Supply via env var in production |
| `Relay:Presence:HeartbeatSeconds` | `60` | Refreshes last-seen only; not a liveness mechanism |
| `Relay:AllowedOrigins` | `[]` | CORS, for a future web client. The desktop app is unaffected |

Full hosting guidance — reverse proxy, tunnels, backups, security — is in
[deployment.md](deployment.md).

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
the hub connects, and the first sync promotes provisional catalog ids. There is no
manual registration step and no way to enter a friend code by hand.

### Where runtime state lives

Everything is under `%LOCALAPPDATA%\Don`. Nothing is written next to the
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

> **Destructive.** This deletes the library, playtime, achievements and the relay
> credentials. `settings.json` holds the only copy of the auth token, which is
> unrecoverable once gone — the relay stores a hash, not the token. Back the
> folder up first if the data matters.

Delete `%LOCALAPPDATA%\Don`. The next start recreates it and migrates a
fresh database from v0 to v6.

To reset only the relay, stop it and delete its `.db` file — but note this gives
the relay a **new relay id**, so every connected client will correctly treat it as
a different relay and re-resolve its catalog ids.

### Version control

The repository is initialised on branch `main` with no remote. Build output is
excluded by `.gitignore`; an early commit captured it before that file existed, so
one commit removes it from the index without touching the working tree.
`.gitattributes` normalises line endings to LF in the history.

`.claude/` is ignored, which includes `.claude/CLAUDE.md` — the project's
development rules. If those rules should travel with the repository, add
`!.claude/CLAUDE.md` to `.gitignore` and commit it.
