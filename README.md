# GameLauncher

A Steam-style launcher for a personal, locally-managed Windows game library,
plus an optional self-hosted relay that adds friend codes, presence and
synchronised achievements.

The launcher manages games you already have on disk. It is not a store: a
library entry is a pointer to an executable, plus the artwork, playtime,
collections and achievements the launcher has accumulated about it.

**The relay is optional.** Everything except friends works with no relay
configured, and nothing in the launcher blocks on the network.

---

## What it does

- **Library** — cover-art grid or detail list, search across titles and tags,
  five sort orders, collections, per-game notes.
- **Adding games** — pick an executable, scan a folder, or install from a direct
  download URL. Metadata, architecture and icons are read from the executable
  itself.
- **Launching** — starts the game, tracks the session, accumulates playtime, and
  notices when the executable has moved.
- **Achievements** — a Steam-style platform. Definitions belong to a shared
  catalog, progress belongs to you, and unlocks synchronise through the relay.
  Four built-in providers: library metrics, save-file rules, read-only process
  memory, and manual. Adding another is one class and one registration. Unlocks
  are announced by an in-app overlay, queued so several earned at once appear in
  turn rather than on top of one another.
- **Friends** — friend codes, requests and presence over SignalR, with a local
  cache so the page still works with the relay down.
- **Discover** — an optional catalogue of games that exist, imported from the
  Internet Archive's software libraries and, if you switch it on, described
  further by MyAbandonware. Full-text search, genre and platform filters, and
  one-click install through the same download path as everything else, with the
  checksum the source published. **Off by default:** nothing is fetched from any
  source until you turn it on in Settings.
- **Custom sourcing feeds** — describe your own feed in a YAML or JSON file in
  `%LOCALAPPDATA%\GameLauncher\adapters\` and the launcher takes download
  addresses from it: a home server, a preservation project's export, an RSS feed
  of releases, or a JSON file on this machine with no server at all. Checksums
  and torrents come through the same path as everything else. When mapping is
  not enough, a manifest can pipe the payload through a program you nominate.
  [Full contract](docs/sourcing-adapters.md).

### Deliberately out of scope

- No scraping, parsing or bespoke integration for any game repack, crack or
  warez distribution site. The two supported sources are an archive with a
  documented public API and a metadata site whose own `robots.txt` is honoured;
  everywhere else, you supply a URL that already points at a file.
- **No scripting engine.** A custom feed's `transform` runs a program you already
  have, as a child process with a pipe on each end. Nothing is embedded, so no
  manifest ever executes code inside the launcher.
- No bundled torrent client. Torrents and magnet links work only if you have
  aria2c installed and switch it on; the launcher shells out to it and never
  ships one. With aria2 off, every download is plain HTTP.
- **Nothing is imported or fetched from a path a site's `robots.txt` disallows.**
  MyAbandonware disallows its download paths, so that source contributes titles,
  genres and screenshots only — never anything to download. Custom feeds pass
  through the same check: a manifest is your instruction to this launcher, not a
  dispensation from the site's.
- **No memory writing, process modification or DLL injection.** Memory
  achievements are read-only inspection: only `OpenProcess`,
  `ReadProcessMemory` and `CloseHandle` are imported anywhere in the project, and
  the process handle is opened without write rights.

---

## Layout

```
GameLauncher.sln
├── GameLauncher.Shared    net8.0           wire contracts only, no dependencies
├── GameLauncher.Desktop   net8.0-windows   WPF client
├── GameLauncher.Relay     net8.0           ASP.NET Core service
└── GameLauncher.Tests     net8.0-windows   xunit
```

Desktop and Relay never reference each other; they share only the DTOs in
`GameLauncher.Shared`.

| Area | Choice |
|---|---|
| UI | WPF, MVVM, CommunityToolkit.Mvvm |
| Hosting | `Microsoft.Extensions.Hosting` generic host |
| Data | SQLite + Dapper — **no Entity Framework** |
| Real-time | SignalR |
| Relay data | SQLite by default; the schema and every query are PostgreSQL-portable |

---

## Prerequisites

- **.NET SDK 8.0.423** or a later 8.0.4xx patch. The version is pinned by
  `global.json`.
- **Windows** to build or run `Desktop` and `Tests` — they target
  `net8.0-windows` for WPF. `Shared` and `Relay` build anywhere .NET 8 runs.

There is no Windows 10 build-time requirement. Every native call the launcher
makes — icon extraction, read-only process memory, the dark title bar — either
predates Windows 10 or degrades gracefully, so the runtime floor is whatever
.NET 8 itself supports.

> If you also have the .NET 10 SDK installed, `global.json` is what keeps the
> build on .NET 8. Running `dotnet --version` inside the solution directory
> should report `8.0.423`. If it reports something else, you are either outside
> the solution directory or `global.json` has gone missing.

---

## Build and test

```bash
dotnet build "GameLauncher.sln"
```

```bash
dotnet test "GameLauncher.Tests/GameLauncher.Tests.csproj"
```

Expect **0 warnings, 0 errors** and **150 passing tests**. Warnings are
meaningful here: CS1591 (missing XML documentation) is deliberately left visible,
so a non-zero warning count means something regressed.

The test suite includes a real Kestrel server on a loopback port for the download
path and an in-process relay for the hub tests. Nothing reaches the network.

---

## Run the launcher

```bash
dotnet run --project "GameLauncher.Desktop"
```

To try it with a sample library (only seeds when the library is empty):

```bash
dotnet run --project "GameLauncher.Desktop" -- --seed-sample-data
```

Sample entries point at executables that do not exist, so launching them will
fail by design. That is the only command-line switch.

### Where your data lives

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

> **Back up `settings.json` before deleting anything.** It holds the only copy of
> your relay auth token, which is unrecoverable — the relay stores a hash, not the
> token.

To reset to a first-run state, delete `%LOCALAPPDATA%\GameLauncher`. The next
start recreates it and migrates a fresh database.

> Reading `gamelauncher.db` with an external tool: it runs in WAL mode. Copy the
> `-wal` and `-shm` sidecars with it, or you will get a stale snapshot that
> silently omits recent commits.

---

## Run the relay

The relay is optional. Skip this entirely if you only want a local library.

```bash
dotnet run --project "GameLauncher.Relay" --launch-profile http
```

It listens on `http://localhost:5107` (the `https` profile adds
`https://localhost:7141`) and creates its SQLite file on first run.

Check it responds:

```bash
curl http://localhost:5107/relay-info
```

Then, in the launcher: **Settings → relay address**. Everything else is
automatic — the relay is probed for its identity, credentials are created for it,
the hub connects, and the first sync promotes your local catalog ids. There is no
manual registration step and no way to type in a friend code; the relay issues
one.

For anything beyond your own machine, see **[docs/deployment.md](docs/deployment.md)**.

---

## Documentation

| Document | Covers |
|---|---|
| [docs/project-handoff.md](docs/project-handoff.md) | The full picture: architecture, schema, decisions and their reasoning, conventions, testing, open questions. **Start here to work on the code.** |
| [docs/catalog-identity.md](docs/catalog-identity.md) | Shared catalog identity, merging duplicates, moving between relays |
| [docs/catalog-import-design.md](docs/catalog-import-design.md) | The discovery catalogue: sources, matching, merging, and what running it against the live APIs changed |
| [docs/relay-architecture.md](docs/relay-architecture.md) | Authentication, sync conflict resolution, portability |
| [docs/deployment.md](docs/deployment.md) | Hosting the relay: reverse proxy, tunnels, LAN vs internet, backups |

---

## A note on conventions

If you are going to change the code, two things are worth knowing up front
because they are easy to undo by accident:

- **Migrations are append-only.** Never edit an existing entry in
  `DatabaseInitializer.Migrations` — installed databases have already run it.
- **Relay migrations must stay PostgreSQL-portable.** No `PRAGMA`, no
  `AUTOINCREMENT`, no `DEFAULT` on boolean or timestamp columns, no
  two-argument `MIN`.

The handoff document has the rest, including a list of invariants that should not
be changed without reading why they exist.
