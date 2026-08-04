# Relay architecture

The relay is a standalone ASP.NET Core 8 service. It is the source of truth for
everything that is shared between people — identity, friendships, presence, the
game catalog, and synchronised achievements. It knows nothing about any
particular machine's filesystem, installed games, or launcher.

Initial deployment target is a self-hosted machine (a low-power laptop is
sufficient). The design assumes a move to a VPS later.

---

## Division of authority

| Concern | Authority | Why |
|---|---|---|
| Local library, install paths, executables | **Desktop** | Machine-specific and meaningless to anyone else |
| Launching, play sessions | **Desktop** | The relay must never be required to start a game |
| Queued sync operations | **Desktop** | The queue survives the relay being unreachable |
| Identity, friend codes, devices | **Relay** | Must be unique across everyone |
| Friendships and requests | **Relay** | Two-party state |
| Presence | **Relay** | Ephemeral, fanned out to friends |
| Catalog identity and aliases | **Relay** | The whole point is that it is shared |
| Achievement and stat history | **Relay**, mirrored locally | Must survive a reinstall |

**The launcher is offline-first and stays fully usable with no relay
configured.** Everything above that is relay-authoritative degrades to a local
provisional form: catalog ids are minted `local:…`, presence is simply absent,
and achievement unlocks queue up in `AchievementUnlock` with `SyncedAt IS NULL`.
Nothing blocks on the network.

---

## Authentication

### Shape

```
POST /register { displayName }
   → { friendCode: "GL-XXXXX-XXXXX", authToken: "glr_…", deviceId: "…" }
```

Registration is anonymous. There is no password, no email, and no account
recovery: the token *is* the credential. Every subsequent request carries it:

```
Authorization: Bearer glr_<43 chars>
```

SignalR cannot set headers on the WebSocket handshake, so the hub also accepts
`?access_token=` — the standard SignalR convention, and already declared in
`PresenceHubContract.AccessTokenQueryParameter`.

### Token handling

Tokens are 32 cryptographically random bytes, base64url-encoded, prefixed
`glr_`. The prefix is deliberate: it makes a leaked token recognisable in a log
file or a paste, and lets a future scanner find them.

**The relay stores only a SHA-256 hash of the token, and no salt.**

That is a considered choice, not an oversight. Salting plus a deliberately slow
KDF (bcrypt, Argon2) exists to make *guessing* expensive, and guessing is only a
threat when the secret has low entropy — which is what a human-chosen password
is. A 256-bit random token cannot be brute-forced or dictionary-attacked
regardless of how fast the hash is, so a slow KDF would buy nothing and cost a
CPU-bound operation on every request on a machine that has very little CPU to
spare.

Omitting the salt is what makes the hash a usable lookup key: the relay hashes
the presented token and finds the device by that hash in one indexed read. With
a per-row salt it would have to scan every device row and hash against each.

The trade-off this accepts: identical tokens produce identical hashes, so a
stolen database reveals which rows share a token. Since tokens are unique random
values, that tells an attacker nothing.

### Why not JWT

A JWT would let the relay validate without a database read, at the cost of not
being able to revoke one before it expires. Revocation matters more here: this
is a small self-hosted service where "I want that device off my account" should
take effect immediately, and the device lookup is a single indexed read against
a table with as many rows as the user has machines.

---

## Devices, and how multi-device works later

Identity is modelled as **one user, many devices** from the start, even though
registration currently creates exactly one device.

```
User    FriendCode (PK), DisplayName, CreatedAt, UpdatedAt
Device  DeviceId (PK), FriendCode (FK), TokenHash (UNIQUE), Label,
        CreatedAt, LastSeenAt, RevokedAt
```

The friend code identifies the *person*; the token identifies the *device*. That
split is cheap now and effectively impossible to retrofit later — a token issued
as a user credential cannot be split into per-device credentials without
invalidating everybody's existing one.

What it buys, without any schema change:

- **Adding a second machine.** An authenticated device calls a future
  `POST /devices/pair` and receives a short-lived pairing code; the new machine
  exchanges it for its own token. The friend code, friendships and history are
  unchanged, because none of them reference a device.
- **Revoking one machine.** Set `RevokedAt`. Other devices are unaffected.
- **Presence across devices.** A user is online if *any* non-revoked device holds
  a live connection. `Presence` is keyed on friend code, not device, so friends
  see one status rather than one per machine.

### Playtime is deliberately not synced yet

Worth stating because it constrains a future design. `Game.PlaytimeSeconds` is a
running total. Syncing totals between two devices either double-counts them or
loses one side, and no conflict rule fixes that — the information needed to merge
correctly is not in a total.

When playtime does sync, it must sync as **individual sessions tagged with a
device id**, with the total derived server-side. `PlaySession` already exists
locally for exactly this reason; it only needs a device column added.

---

## Sync conflict resolution

Every rule below is chosen so that **merging is commutative and never loses what
a user earned**. Where a rule could go either way, it goes the way that keeps the
user's history.

| Data | Rule | Reasoning |
|---|---|---|
| Achievement unlock | **Earliest `UnlockedAt` wins** | An unlock is monotonic — once earned, always earned. Taking the earliest means a later re-sync can never move an earned-on date forward. Insert-only, so it is idempotent. |
| Achievement progress | **Highest value wins** | Progress must never appear to go backwards because of housekeeping or an out-of-order message. |
| Increment-only stat | **Highest value wins** | Same reasoning; these are lifetime totals. |
| Gauge stat | **Last write wins, by `UpdatedAt`** | A current level or rank is genuinely a latest-value-wins quantity. `GameStatDefinition.IsIncrementOnly` distinguishes the two. |
| Display name | **Last write wins** | Trivially resolvable; the user is the only writer. |
| Catalog identity | **Server assigns, client adopts** | See below. There is no conflict to resolve — the relay is authoritative. |
| Presence | **Last write wins, not persisted long** | Ephemeral by nature. |
| Friendship state | **Server-authoritative** | Two-party state; the relay is the only place both parties meet. |

Two properties fall out of this that are worth naming:

- **Every rule is idempotent.** Replaying a sync batch changes nothing. That is
  what makes "retry on failure" safe, which matters when the transport is a
  home broadband connection.
- **No rule needs a vector clock or a causality graph.** Each is either
  monotonic (max/min) or genuinely last-writer-wins on a single-writer field.
  That is a deliberate constraint on what gets synced, not an accident.

### The outbound queue

The client never diffs against the server. Each syncable table carries a
nullable `SyncedAt`, and the queue is the indexed predicate
`WHERE SyncedAt IS NULL`. Push, then stamp. If the push succeeded but the
response was lost, the next attempt re-sends — which is harmless, because every
rule is idempotent.

---

## Promoting provisional catalog ids

The client mints `local:<32 hex>` so it works offline. Promotion is the only
moment a catalog identity is rewritten, and it happens only to provisional ids.

```
client                                   relay
  │ entries WHERE IsProvisional = 1
  │
  │ POST /catalog/resolve
  │   { fingerprint, title, company }
  │                                        ├─ CatalogAlias lookup by fingerprint
  │                                        │    hit  → existing CatalogId
  │                                        │    miss → create entry + alias  (open creation)
  │                                        └─ follow SupersededBy to canonical
  │ ← { catalogId, canonicalTitle }
  │
  │ CatalogService.ApplyAssignedIdentityAsync
  │   ├─ PromoteAsync: UPDATE CatalogEntry SET CatalogId = assigned
  │   │      ON UPDATE CASCADE carries Game, AchievementDefinition,
  │   │      GameStatDefinition and CatalogAlias with it
  │   └─ on collision (the assigned id already exists locally)
  │         MergeIntoAsync: move references, carry unlocks forward,
  │         leave the absorbed entry as a redirect
```

The relay always returns the **canonical** id, following its own
`SupersededByCatalogId` chain, so a client never adopts an id that has already
been merged away.

Because the relay may return an id the client already holds — two local entries
turning out to be one title — the collision path is not an error case. It is the
normal outcome of the catalog doing its job, and it is covered by tests.

Full detail in [catalog-identity.md](catalog-identity.md).

---

## Database portability

SQLite now; PostgreSQL on a VPS later. Three decisions keep one schema valid on
both:

**1. No auto-increment identity columns.** This is where the dialects actually
diverge — `INTEGER PRIMARY KEY AUTOINCREMENT` against
`GENERATED BY DEFAULT AS IDENTITY`. Every relay table is keyed on a value the
application generates: a friend code, a catalog id, a device id, or a composite
of natural keys. Nothing needs a database-assigned number, so nothing depends on
how the database assigns one.

**2. Only column types both understand.** `TEXT`, `INTEGER`, `BIGINT`,
`BOOLEAN`, `DOUBLE PRECISION`. No `SERIAL`, no `AUTOINCREMENT`, no
`TIMESTAMPTZ`.

**3. Timestamps as UTC ISO-8601 text.** Deliberately different from the desktop
client, which stores local offsets because it displays them. The relay only ever
compares and orders timestamps, and a UTC-normalised ISO-8601 string sorts
lexicographically in exactly the same order as chronologically — so `MIN()`,
`MAX()` and `ORDER BY` are correct on both engines with no conversion.

The honest cost: PostgreSQL would rather these were `timestamptz`, which indexes
better and validates on write. Converting is a single
`ALTER TABLE … USING (col::timestamptz)` per column when it becomes worth doing,
and nothing in the application layer changes because Dapper already maps through
`DateTimeOffset`.

**DML is portable throughout.** `ON CONFLICT … DO UPDATE` is standard in both.
No `PRAGMA`, no `randomblob()`, no `strftime()` — the v3 client migration used
those, which is exactly the kind of thing that does not travel.

### What moving to PostgreSQL actually costs

The schema and every query are already portable. What is needed:

1. Add the `Npgsql` package.
2. Add one `IRelayConnectionFactory` implementation (~20 lines).
3. Change `Relay:Database:Provider` in configuration.

No PostgreSQL implementation ships today, because there is no PostgreSQL here to
test it against and an untested provider that fails at 3am is worse than a
documented gap.

---

## Configuration

Everything environment-specific is externalised, so the same binaries run on a
laptop and on a VPS:

```
Relay:Database:Provider          Sqlite | Postgres
Relay:Database:ConnectionString  
Relay:Cors:AllowedOrigins        
Relay:Presence:HeartbeatSeconds  
```

Standard ASP.NET Core configuration precedence applies, so any of these can be
overridden by an environment variable (`Relay__Database__ConnectionString`)
without touching a file — which is how secrets should reach a VPS.

---

## Deliberately not built

Neither is blocked by anything above.

- **Cloud saves.** Would add a `SaveSlot` table keyed on
  `(FriendCode, CatalogId, SlotName)` plus blob storage. Nothing in the current
  schema constrains it; the catalog identity it would key on already exists.
- **Workshop / mods.** Would key on `CatalogId` in the same way.

The reason both are unblocked is that they attach to the **catalog identity**
rather than to a local game row — the same decision that made global achievements
possible.
