# Deploying the relay

The relay is the optional half of GameLauncher: friend codes, presence, shared
catalog identity and achievement synchronisation. The launcher works without it,
so nothing here is required to use the application.

This document covers running it somewhere other than your own desktop.

**Before deploying anything, read the security summary at the end.** The relay
has no password login and no account recovery by design, which changes what
exposing it to the internet means.

---

## What you are deploying

A single ASP.NET Core 8 process. It needs:

- .NET 8 runtime (or publish self-contained)
- A writable directory for its SQLite file
- One TCP port

It does **not** need: a database server, a message broker, a scheduler, or any
access to the machines its users run the launcher on. It knows nothing about
anybody's filesystem or installed games.

Resource use is small. A first-generation self-hosting target of an i5 7th-gen
U-series laptop is comfortable for a handful of users.

---

## Configuration

Everything lives under the `Relay` section of `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",

  "Relay": {
    "Database": {
      "Provider": "Sqlite",
      "ConnectionString": "Data Source=/var/lib/gamelauncher/relay.db"
    },
    "Presence": {
      "HeartbeatSeconds": 60
    },
    "AllowedOrigins": []
  }
}
```

| Setting | Default | Notes |
|---|---|---|
| `Relay:Database:Provider` | `Sqlite` | `Postgres` throws at startup — the factory is not implemented. See below. |
| `Relay:Database:ConnectionString` | `Data Source=gamelauncher-relay.db` | Relative paths resolve against the working directory. Use an absolute path in production. |
| `Relay:Presence:HeartbeatSeconds` | `60` | Refreshes last-seen only. Disconnects are detected by SignalR, so this is not a liveness mechanism and can be generous. |
| `Relay:AllowedOrigins` | `[]` | CORS, for a future web client. The desktop launcher is not a browser and is unaffected. |

Every value can be overridden by environment variable using the
double-underscore convention, which is how a server should supply them rather
than editing a file in the deployment directory:

```bash
Relay__Database__ConnectionString="Data Source=/var/lib/gamelauncher/relay.db"
```

Set the listening address with the standard ASP.NET Core variable:

```bash
ASPNETCORE_URLS="http://127.0.0.1:5107"
```

Binding to `127.0.0.1` and putting a reverse proxy in front is the recommended
shape for anything internet-facing — see below.

---

## Publishing

```bash
dotnet publish "GameLauncher.Relay" -c Release -o ./publish
```

Then run `./publish/GameLauncher.Relay`. To avoid installing the runtime on the
target machine, publish self-contained instead:

```bash
dotnet publish "GameLauncher.Relay" -c Release -r linux-x64 --self-contained -o ./publish
```

---

## Running as a service

### Linux (systemd)

`/etc/systemd/system/gamelauncher-relay.service`:

```ini
[Unit]
Description=GameLauncher Relay
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/gamelauncher-relay
ExecStart=/opt/gamelauncher-relay/GameLauncher.Relay
Restart=always
RestartSec=5
User=gamelauncher
Environment=ASPNETCORE_URLS=http://127.0.0.1:5107
Environment=Relay__Database__ConnectionString=Data Source=/var/lib/gamelauncher/relay.db

# The relay needs nothing outside its own state directory.
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
NoNewPrivileges=true
ReadWritePaths=/var/lib/gamelauncher

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now gamelauncher-relay
```

`Type=notify` requires the `Microsoft.Extensions.Hosting.Systemd` package. If you
would rather not add it, use `Type=simple`.

### Windows

```bash
sc.exe create GameLauncherRelay binPath= "C:\gamelauncher-relay\GameLauncher.Relay.exe" start= auto
```

Windows service hosting wants the `Microsoft.Extensions.Hosting.WindowsServices`
package and a `builder.Host.UseWindowsService()` call. Without them, run it from
Task Scheduler at startup instead — the relay has no interactive requirements.

---

## Reverse proxy

Terminating TLS at a proxy is the simplest way to get a certificate that the
launcher will trust without configuration.

### Caddy

Caddy obtains and renews a certificate automatically, which is why it is the
recommendation here.

```caddyfile
relay.example.com {
    reverse_proxy 127.0.0.1:5107
}
```

That is the whole configuration. Caddy handles WebSocket upgrades by default, so
SignalR works without extra directives.

### nginx

```nginx
server {
    listen 443 ssl;
    server_name relay.example.com;

    ssl_certificate     /etc/letsencrypt/live/relay.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/relay.example.com/privkey.pem;

    location / {
        proxy_pass         http://127.0.0.1:5107;
        proxy_http_version 1.1;

        # Required for SignalR. Without the upgrade headers the hub falls back to
        # long polling, which works but reconnects more often.
        proxy_set_header   Upgrade    $http_upgrade;
        proxy_set_header   Connection "upgrade";

        proxy_set_header   Host              $host;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;

        # A hub connection is long-lived; the default 60s read timeout would cut
        # it repeatedly.
        proxy_read_timeout 1h;
    }
}
```

The `X-Forwarded-*` headers are conventional rather than required: the relay
never generates absolute URLs, so it does not run
`UseForwardedHeaders` and does not read them. It also does not call
`UseHttpsRedirection` — enforcing HTTPS is the proxy's job, which is why the
recommendation is to bind Kestrel to loopback rather than leaving a plain-HTTP
port reachable.

---

## Cloudflare Tunnel

Useful when the machine has no public IP, sits behind CGNAT, or you would rather
not open a port at all. The tunnel makes an outbound connection, so nothing needs
forwarding on your router.

```bash
cloudflared tunnel create gamelauncher
cloudflared tunnel route dns gamelauncher relay.example.com
```

`~/.cloudflared/config.yml`:

```yaml
tunnel: gamelauncher
credentials-file: /home/you/.cloudflared/<tunnel-id>.json

ingress:
  - hostname: relay.example.com
    service: http://127.0.0.1:5107
  - service: http_status:404
```

```bash
cloudflared tunnel run gamelauncher
```

WebSockets are enabled by default on Cloudflare, so SignalR works. If you have
turned them off for the zone, the hub falls back to long polling — functional,
but it reconnects more often.

---

## LAN versus internet

These are genuinely different deployments, and the difference is not just the
address.

### LAN only

- Bind to the machine's LAN address: `ASPNETCORE_URLS=http://0.0.0.0:5107`.
- Plain HTTP is defensible on a network you control.
- Clients use `http://192.168.x.x:5107`.
- Give the machine a static address or a DHCP reservation. **This matters less
  than it looks**: the launcher identifies a relay by the id it reports, not by
  its address, so a relay that moves is still recognised. Changing the address
  breaks discovery, not identity.

### Internet-facing

- **Use HTTPS.** The auth token is a bearer credential sent on every request;
  over plain HTTP anyone on the path can take it and impersonate that device.
- Bind Kestrel to `127.0.0.1` and let the proxy hold the public port.
- Registration is open — anyone who can reach `/register` can create an account.
  There is no rate limiting in the relay. If that matters, put it in the proxy;
  Caddy and nginx both do request limiting.
- Consider whether you want it public at all. A tunnel, a VPN such as Tailscale
  or WireGuard, or an allowlist in the proxy all keep it reachable to the people
  you intend without publishing it.

---

## Backups

Everything the relay owns is in its SQLite file: users, devices, friendships,
catalog entries and achievement history.

```bash
sqlite3 /var/lib/gamelauncher/relay.db ".backup '/backups/relay-$(date +%F).db'"
```

Use `.backup` rather than copying the file. The relay may be mid-write, and a
plain copy can capture a torn state or miss the write-ahead log.

**What a lost relay database costs.** The relay id lives inside it. Restoring
from backup preserves that id and clients carry on unaffected. Starting from an
empty database creates a *new* id, and every launcher will correctly conclude it
has been pointed at a different relay: it re-resolves its catalog ids, clears its
friend cache, and registers afresh. Nothing local is lost — games, playtime,
achievements and collections all survive — but friendships and the shared
history on the server are gone, and users get a new friend code.

Client-side, the equivalent file is `%LOCALAPPDATA%\Don\settings.json`,
which holds each device's auth token. The relay stores only a hash, so a lost
token cannot be recovered — that device registers again as a new one.

---

## Moving the relay

Moving to a different host, or from a laptop to a VPS, needs no client
configuration beyond the new address:

1. Stop the relay.
2. Copy the SQLite file to the new machine.
3. Start it there and point clients at the new address.

Because the relay id travelled with the database, clients recognise it as the
same relay and keep their identity and friendships. This is exactly why identity
is a value in the database rather than a hostname — see
[catalog-identity.md](catalog-identity.md) for the migration flow, including what
happens when a client is genuinely pointed at a *different* relay.

---

## PostgreSQL

The relay is written to be portable: `AppUser` avoids the reserved word `user`,
there is no `AUTOINCREMENT`, no `PRAGMA`, no `DEFAULT` on boolean or timestamp
columns, and no two-argument `MIN`. Schema version is tracked in a table rather
than `PRAGMA user_version`.

What is missing is only the connection factory. To add it:

1. Reference `Npgsql`.
2. Implement `IRelayConnectionFactory` returning an `NpgsqlConnection`.
3. Register it when `RelayDatabaseProvider.Postgres` is configured — the enum
   member already exists and currently throws at startup.

No migration, query or repository needs to change.

Timestamps are stored as UTC ISO-8601 text. PostgreSQL would prefer
`timestamptz`; converting is one `ALTER TABLE … USING (col::timestamptz)` per
column and no application change, because Dapper already maps through
`DateTimeOffset`.

---

## Scaling beyond one instance

The relay currently assumes a single process. Two things break if you run more
than one:

- `PresenceTracker` counts live connections per user **in memory**. Two instances
  would each see only their own.
- SignalR needs a backplane to deliver a message to a client connected to another
  instance.

Both are contained: the tracker is a small interface behind which a shared store
(Redis, or a table) would sit, and SignalR's Redis backplane is a package and a
line of configuration. Neither is done, because a single self-hosted instance has
no need of it.

---

## Security summary

Worth understanding before exposing this to the internet.

- **No passwords, no email, no recovery.** Registration mints a random token
  which *is* the credential. Losing it means registering a new device; there is
  nothing to reset.
- **Tokens are stored as unsalted SHA-256.** This is deliberate and reasoned: a
  256-bit random token cannot be brute-forced however fast the hash is, so a slow
  KDF would cost CPU on every request and buy nothing, and omitting the salt is
  what makes the hash a usable lookup key. **This reasoning does not transfer to
  passwords** — if a password login is ever added it needs a salt and a slow KDF.
- **Tokens do not expire**, but authentication is database-backed rather than
  JWT, so setting `Device.RevokedAt` takes effect immediately.
- **Registration is open and unthrottled.** Rate limiting belongs in the proxy.
- **Presence reaches accepted friends only.** A pending request leaks nothing
  beyond a display name, and there is a test that deliberately breaks the fan-out
  to prove the leak test discriminates.
- **Friend code lookup does not confirm existence.** An unknown code and a
  malformed one return identical messages, so the endpoint cannot be used to
  enumerate which codes exist.
- **The relay never learns anything about your machine.** No paths, no installed
  games, no executable names. It stores catalog identities, which are opaque.
