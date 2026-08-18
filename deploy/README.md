# Deployment

Two things ship: the desktop client, and the relay it optionally talks to. They
are independent — **the launcher is fully functional with no relay at all**, and
everything except friends works offline.

---

## Desktop client

### Publish

```bash
dotnet publish GameLauncher.Desktop -c Release -p:PublishProfile=win-x64
```

Produces one file — `bin/publish/win-x64/Don.exe`, about 77 MB — that runs on a
Windows machine with no .NET installed.

> **Never publish into a directory that contains the source.** WPF compiles its
> XAML markup pass through a generated temporary project, and if the output
> directory is an ancestor of the source tree, that pass overwrites the real
> assembly with a stub: a 7 KB `GameLauncher.Desktop.dll` that builds cleanly
> and will not start. Publish to a sibling folder.

### What is on, and what is deliberately off

| Setting | | Why |
|---|---|---|
| `SelfContained` | on | Installed by people who want to play a game, not to be sent to find a framework. |
| `PublishSingleFile` | on | One file to copy, one file to sign. |
| `EnableCompressionInSingleFile` | on | Roughly halves the size for a one-off decompression on first run. |
| `PublishReadyToRun` | on | ~15 MB for a noticeably faster cold start, which is the first thing anyone judges a launcher on. |
| `PublishTrimmed` | **off** | See below. |

**Trimming is off and should stay off.** WPF is not trim-compatible and
Microsoft does not support it: XAML resolves types by name at run time and the
linker cannot see those references. A trimmed build links, publishes, and then
throws when some style, template or converter is first realised — after
shipping, on a screen no test opened. The size saving is not worth a class of
failure that only appears in the field. This is the "where safe" in "trimming
where safe".

### Bundling aria2c

Optional. Without it, torrents are unavailable and downloads use the built-in
HTTP engine; nothing else changes.

Drop `aria2c.exe` into `GameLauncher.Desktop/tools/win-x64/` and publish. It
lands in `tools/` beside the executable, and the launcher finds it with no path
configured. Full instructions and the reasoning are in
[`GameLauncher.Desktop/tools/README.md`](../GameLauncher.Desktop/tools/README.md).

Nothing downloads it for you, at build time or at run time. A launcher that
silently fetched an executable and ran it would be doing the most abusable thing
a desktop application can do, and that the binary is well known does not change
what the mechanism is.

Where the launcher looks, in order: the configured path → beside the executable
→ `tools/` → `%LOCALAPPDATA%\Don\tools\` → `PATH`. Each candidate is run with
`--version` before it is accepted, because a file existing is not the same as a
file working.

---

## Giving it to someone else

Three steps: build a release, put it on the VPS, send the link.

### 1. Build

```powershell
.\deploy\installer\build-release.ps1
```

Publishes, then stages and packages
`deploy/installer/output/Don-<version>-win-x64.zip` — the executable, the install
and uninstall scripts, and a README written for whoever receives it. Alongside it
go a `.sha256` and a `release.json` carrying the version, size and hash.

It checks the scripts before it builds anything. A PowerShell script is parsed in
full before its first line runs, so one syntax error means it does nothing at
all — discovered, in an uninstaller, by someone who now cannot remove the
application. The check exists because exactly that happened: `"$InstallRoot:"` in
a message parses as a drive-qualified variable reference and took the whole
uninstaller down with it.

Inno Setup is used **if it happens to be installed**, producing a conventional
`Don-Setup-<version>.exe` as well. It is not required, and nothing is lost
without it: the payload is one self-contained executable and two scripts, so
there is nothing a setup program must do that a copy cannot.

### 2. Publish

```powershell
.\deploy\installer\publish-release.ps1 -Server root@relay.example.com
```

Uploads over SSH to the `dist` directory that `compose.yml` mounts into Caddy,
then verifies twice: the VPS re-checks the SHA-256 against the file it received,
and the archive is fetched over HTTPS. The first proves the bytes survived the
wire, the second proves the mount, the route and the certificate work — which is
the half that is wrong the first time.

### 3. Send

```
https://relay.example.com/download/
```

A directory listing, served publicly. Anyone with the link can download it, which
is the point: a friend should not need an account to install the thing that
creates their account. **Nothing secret belongs in `dist/`.**

### What they do

Extract, double-click `Install.cmd`.

It installs to `%LOCALAPPDATA%\Programs\Don` — per-user, so Windows never asks
for an administrator password — and registers in Add/Remove Programs like
anything else. `-AllUsers` installs to Program Files instead and does need
elevation.

Windows will warn first. The build is not code-signed, so SmartScreen shows
"Windows protected your PC" and they have to choose *More info → Run anyway*. A
certificate is the only thing that removes that, and it is bought per year. The
archive's README says so, so nobody has to guess whether the warning is normal.

### Sharing a catalogue with them

Once they have the launcher, the VPS can also serve the catalogue it reads.

```bash
# on the VPS
cp -r "Quake (1996)" deploy/feed/games/
deploy/feed/build-feed.py --name "The shelf"
```

They set **Settings → Discovery → Shared catalogue feed** to
`https://your-domain/feed/catalog.json` and see the same shelf you do.

This is the one source that is not somebody else's website. Because you host the
files, it is the only one that can state a SHA-256 it actually computed — so
installs from it are verified, not merely hoped for. Full format and the rules a
reader is protected by: [`feed/README.md`](feed/README.md).

### Uninstalling

Add/Remove Programs, or `Uninstall-Don.ps1` in the install folder.

It removes exactly the files listed in `install-manifest.txt`, which the
installer writes. That is why an uninstall can never take anything with it that
the install did not put there — including when Don was installed into a folder
that already had other things in it.

The library is **kept unless you ask**, and the prompt defaults to no.
`settings.json` holds the only copy of your relay token — the relay stores a hash
and cannot reissue it — so a silent delete would be unrecoverable. `-RemoveLibrary`
deletes it; `-Silent` on its own keeps it.

---

## Relay

ASP.NET Core, SQLite by default. Optional: the launcher works without it.

### One command on a fresh VPS

```bash
sudo ./deploy/deploy-vps.sh relay.example.com you@example.com
```

Installs Docker if missing, writes `.env`, builds the image, brings the stack up
behind Caddy, and then verifies `https://<domain>/health` through the public
name — which is the only check that proves DNS, the certificate, the proxy and
the relay all work together.

Point the DNS record at the VPS **before** running it. The script checks, and
refuses if the name does not resolve: a failed certificate request counts
against the Let's Encrypt rate limit, and five of them locks the name out for an
hour.

### By hand

```bash
cd deploy
cp .env.example .env      # edit RELAY_DOMAIN and RELAY_ACME_EMAIL
docker compose up -d
```

Caddy is used rather than Nginx because the entire TLS story is two lines and a
certificate it obtains and renews itself; Nginx would add certbot, a renewal
timer and a reload hook for a single-service deployment.

The relay's port is **not** published to the host. It is reachable only from the
proxy on the internal network, so nothing can bypass TLS by hitting 8080
directly.

### Without Docker

[`gamelauncher-relay.service`](gamelauncher-relay.service) runs it under systemd,
bound to loopback, with the filesystem hardening a service reachable from the
internet should have. It still needs a proxy in front for TLS. Instructions are
in the file's header.

### Configuration

Everything binds from the `Relay` section, and environment variables override
it — which is how a connection string should reach a VPS, never in a committed
file:

| Variable | Default | |
|---|---|---|
| `Relay__Database__Provider` | `Sqlite` | `Postgres` throws at startup; see below. |
| `Relay__Database__ConnectionString` | `Data Source=/data/gamelauncher-relay.db` | On the volume, not in the image layer. |
| `Relay__Presence__HeartbeatSeconds` | `60` | |
| `Relay__AllowedOrigins__0` | unset | Browser clients only. The desktop client is not a browser. |

**PostgreSQL is not wired up.** The schema and every query are already portable
and need no changes; what is missing is the Npgsql package and one
`IRelayConnectionFactory`. Selecting it throws at startup rather than starting
and failing on the first request.

### Backups

Everything is in one SQLite file on the `relay-data` volume:

```bash
docker compose exec relay sh -c 'sqlite3 /data/gamelauncher-relay.db ".backup /data/backup.db"'
docker compose cp relay:/data/backup.db ./relay-backup-$(date +%F).db
```

Use `.backup` rather than copying the file. The database runs in WAL mode, and a
plain copy without the `-wal` sidecar is a stale snapshot that silently omits
recent commits.

---

## What the relay does and does not do

It exists, it is deployable, and it is smaller than the task description
assumes. Endpoints today:

| | |
|---|---|
| `GET /health` | Liveness, used by the container health check. |
| `GET /relay-info` | Identifies the relay to a client before it registers. |
| `POST /register` | **Authentication.** Issues the device token everything else uses. |
| `GET /friends` | Friend codes, requests, presence. |
| `POST /catalog/resolve` | Shared catalog identity for a title. |
| `POST /sync/achievements` | Achievement unlock synchronisation. |
| `/hubs/presence` | SignalR, for live presence. |

Two things the deployment brief asks for **do not exist and are not stubbed
here**:

- **Cloud save synchronisation.** There are no save endpoints. Building them is
  not a deployment task — it needs blob storage, a conflict-resolution policy
  for two machines that both played offline, and a decision about client-side
  encryption, because save files are the one thing a user cannot re-download.
  Deploying an endpoint that pretended to do this would be worse than not having
  one.
- **A manifest feed index.** Sourcing feeds stay client-side: a manifest in
  `%LOCALAPPDATA%\Don\adapters\` is read by the launcher itself, with no server
  involved. See [`docs/sourcing-adapters.md`](../docs/sourcing-adapters.md).

  A shared *catalogue* does now exist — see [`feed/README.md`](feed/README.md) —
  but note where it lives. It is a static JSON file Caddy serves, not an endpoint
  the relay implements, and the launcher reads it directly. That is deliberate:
  a catalogue is a document, and a document needs a web server rather than an
  API. The relay stays a service for the things that genuinely need one, which
  are the things that involve more than one person's account.

Both are real features worth building. They are named here so that "the relay is
deployed" is not mistaken for "cloud saves work".
