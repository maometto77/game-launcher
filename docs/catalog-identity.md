# Shared catalog identity

How GameLauncher identifies a *game title* as opposed to *one person's copy of
it*, and how that identity reaches the relay.

Status: schema and client-side lifecycle implemented (schema v3). Relay
endpoints are **not** built yet; the client mints provisional identities and
queues them for registration.

---

## The problem

Before v3 the closest thing to a global identifier was `Game.GlobalKey` — a
random 128-bit value minted locally when a game is added.

That is fine for what it is, but it cannot identify a title across users. Two
people who add the same game generate two unrelated keys. Anything that has to
mean the same thing to more than one person is therefore impossible to build on
it:

- **Global achievements.** One authored achievement set applying to everyone who
  owns the title.
- **Achievement rarity.** "4% of players earned this" requires knowing that many
  users' unlocks refer to one achievement.
- **Shared stats.** Aggregating a counter across players.
- **Presence.** "Your friend is playing a game you also own" needs both sides to
  agree on what "the same game" is.
- **Cloud sync.** Restoring a library onto a new machine must reattach unlocks
  to the right title, and a fresh install mints fresh `GlobalKey`s.

## The model

```
CatalogEntry ──< Game                 (one title, many installations)
     │
     ├──────< AchievementDefinition   (achievements belong to the title)
     │              │
     │              ├──< AchievementUnlock     (what this user earned)
     │              └──< AchievementProgress   (how far this user is)
     │
     └──────< GameStatDefinition
                    └──< GameStatValue
```

`CatalogEntry.CatalogId` is the shared identity. Every cross-user concept hangs
off it. Local integer primary keys and `Game.GlobalKey` never leave the machine.

### Identity states

| State | `CatalogId` | `IsProvisional` | `Source` |
|---|---|---|---|
| Minted locally, no relay yet | `local:<32 hex>` | `1` | `local` |
| Assigned by a relay | whatever the relay issued | `0` | relay host |

The `local:` prefix is deliberate: a provisional identity is recognisable on
sight, in a log line, or in a database browser.

**Why provisional identities at all?** The launcher has to work fully offline
and with no relay configured. Requiring a server round trip before a game can be
added would make the core feature depend on network availability. Instead
identity is minted locally and reconciled later.

### `Source`

Catalog ids are only unique within the authority that issued them. A user who
moves between two self-hosted relays would otherwise silently merge two
unrelated titles that happened to receive the same id. `Source` records which
relay assigned it.

### Match fingerprint

The signature a relay uses to answer "do you already know this game?".

Computed from **publisher-supplied metadata only** — product name, company,
executable file name, normalised to letters and digits — and deliberately *not*
from install path, file size, or modification time. Two people who installed the
same game to different drives, or who are on different patch levels, must
produce the same fingerprint or the catalog fragments into one entry per user.

Local title is used only as a fallback when the binary carries no product name,
because titles are user-editable and therefore not stable.

---

## Data flow

### 1. Adding a game (offline, today)

```
Add Game / Scan Folder
   └─ ExecutableInspector reads product name, company, PE headers
        └─ CatalogService.ComputeFingerprint(title, executable)
             └─ CatalogRepository.FindByFingerprintAsync
                  ├─ hit  → reuse that CatalogEntry
                  └─ miss → create provisional entry (local:…)
                       └─ Game.CatalogId = entry.CatalogId
```

A second copy of an already-known game reuses the existing entry, so any
achievements already authored against that title apply to it immediately.

### 2. Registration (when a relay exists)

```
CatalogService.GetPendingRegistrationsAsync()   → entries where IsProvisional = 1
   └─ POST /catalog/resolve  { fingerprint, title, company }      [NOT BUILT]
        └─ relay matches or creates, returns { catalogId, canonicalTitle }
             └─ CatalogService.ApplyAssignedIdentityAsync(...)
```

### 3. Promotion

`ApplyAssignedIdentityAsync` rewrites the primary key:

```sql
UPDATE CatalogEntry SET CatalogId = @assigned WHERE CatalogId = @provisional;
```

Every `Game`, `AchievementDefinition` and `GameStatDefinition` reference follows
automatically via `ON UPDATE CASCADE`. Nothing else is touched, and — critically
— no row is ever deleted and recreated, so `AchievementUnlock` survives intact.

If the assigned id already exists locally (two local entries turn out to be one
title), promotion returns `false` and the caller merges instead: references are
repointed, duplicate api names are dropped in favour of the target's copies
(which may already carry unlocks), and the absorbed entry is deleted.

Both paths are covered by tests in `CatalogIdentityTests`, including an explicit
assertion that unlocks survive promotion.

### 4. Sync (future)

Outbound queues are indexed predicates rather than server diffs:

- Catalog registration — `CatalogEntry WHERE IsProvisional = 1`
- Unlocks — `AchievementUnlock WHERE SyncedAt IS NULL`
- Stats — `GameStatValue WHERE SyncedAt IS NULL`

`SyncedAt` is kept separate from `UnlockedAt` so re-syncing can never rewrite
when something was actually earned. `UpdatedAt` on `Game`, `CatalogEntry` and
`AchievementDefinition` exists for last-writer-wins conflict resolution: playtime
accrues on whichever machine the game was played on, so a merge has to be able to
tell which side is newer.

---

## Schema changes made (v3)

| Change | Reason |
|---|---|
| New `CatalogEntry` table | The shared identity itself |
| `Game.CatalogId` FK, `ON UPDATE CASCADE` / `ON DELETE SET NULL` | Installation points at title; promotion cascades |
| `AchievementDefinition.CatalogId` FK, `ON UPDATE CASCADE` / `ON DELETE CASCADE` | Achievements belong to the title |
| `GameStatDefinition.CatalogId` FK | Same, for stats |
| Unique index on `(COALESCE(CatalogId,''), ApiName)` | Api names unique per title; `COALESCE` because SQLite treats NULLs as distinct |
| `AchievementDefinition.GameId` / `GameStatDefinition.GameId` set to `NULL` | Neutralises the old cascade — see below |
| Index on `CatalogEntry.MatchFingerprint`, `IsProvisional` | Lookup and the registration queue |

### Behaviour change worth knowing

**Uninstalling a game no longer erases its achievements.** Definitions used to
cascade from `Game`; they now hang off `CatalogEntry`. This matches Steam and is
the point of the redesign, but it means catalog entries and achievements outlive
the installs that created them. Orphan cleanup is not implemented — entries are
small, and discarding earned achievements to reclaim a few rows would be the
wrong trade.

### The vestigial `GameId` columns

SQLite refuses `DROP COLUMN` on a column named in a foreign key. Rebuilding the
table would mean dropping it, and with foreign keys enabled `DROP TABLE`
performs an implicit `DELETE FROM` — which would cascade every
`AchievementUnlock` row out of existence.

So v3 sets both columns to `NULL` instead. The cascade becomes unreachable, the
columns are inert, and nothing reads or writes them. A later maintenance
migration can remove them properly outside a transaction. The model classes do
not expose them.

---

## Settled policy

Agreed and implemented client-side (schema v4):

1. **Open creation.** Any client may cause a catalog entry to exist. A user never
   waits for moderation to add a game.
2. **Aliases from the start.** One title, many fingerprints.
3. **Operator merge**, without touching anybody's achievements, stats, ownership
   or history.
4. **An assigned `CatalogId` is immutable.** Merging happens through aliases and
   reference updates, never by replacing an identity a client already holds.
5. **Fully functional offline** on provisional ids, promoted transparently.

### The one place an identity is rewritten

Point 4 has exactly one carve-out, and it is deliberate: promoting a
**provisional** id (`local:…`) to an assigned one rewrites the key, relying on
`ON UPDATE CASCADE`. That is legitimate because a provisional id was never
*assigned* — no relay has ever seen it, so nothing outside this machine can be
holding it. From the moment an id is assigned it is immutable.

Unifying two **assigned** ids is a different operation entirely
(`MergeIntoAsync`), which moves references and leaves the absorbed entry behind
as a redirect.

---

## Merge workflow

### What a merge must not do

Two failure modes drove the implementation, both silent:

- **Losing an unlock.** Where both entries define the same api name, deleting the
  duplicate cascades its `AchievementUnlock` away. If the *absorbed* entry was
  the one the user had unlocked, the achievement vanishes. The survivor now
  inherits the **earlier** unlock time and the **higher** progress before the
  duplicate is removed. (Earlier, not later: housekeeping must not move an
  earned-on date forward. Higher, not newer: progress must never appear to go
  backwards.)
- **Orphaning a client.** Deleting the absorbed entry breaks any client still
  holding its id. The row is kept with `SupersededByCatalogId` set instead.

### Sequence

```
operator decides  app-2001  is the same title as  app-1042
                     │
  1. carry history forward   for each duplicate api name:
                             survivor gets earlier unlock, higher progress
  2. delete duplicates       (history already preserved)
  3. repoint references      Game, AchievementDefinition, GameStatDefinition
  4. repoint aliases         every fingerprint of 2001 now resolves to 1042
  5. leave a redirect        2001.SupersededByCatalogId = 1042   ← kept, not deleted
```

All five steps run in one transaction. Neither id is rewritten.

### Resolution afterwards

`ResolveCanonicalAsync` walks the redirect chain, bounded at 16 hops so a cycle
introduced by a faulty relay fails loudly rather than hanging. Clients holding
`app-2001` keep working indefinitely; they simply resolve to `app-1042`.

### Repairing entries with no fingerprint

The v3 backfill ran in SQL, which cannot hash normalised metadata, so it created
entries with an empty fingerprint — unmatchable, meaning a re-add would create a
second entry for one title. `RepairMissingFingerprintsAsync` fixes those in code
where the executable can actually be inspected. It runs at every startup, is
idempotent, and declines to steal a fingerprint already bound elsewhere (that is
a merge decision, not a repair).

---

## Moving between relays

A catalog id is only meaningful inside the relay that issued it. Pointing the
launcher at a different relay therefore invalidates every assigned id it holds —
and silently reusing one would attach this user's achievements to whatever
unrelated title happens to occupy that id on the new relay.

### Relays are identified by an id they report, not by their address

`GET /relay-info` returns `{ relayId, name, schemaVersion }`. The id is generated
on first start and stored in the relay's own database, so it travels with the
data.

Comparing **addresses** would get both interesting cases wrong:

| Situation | By address | By relay id |
|---|---|---|
| Relay moved laptop → VPS (same database) | looks like a new relay, needlessly migrates | same relay, nothing happens ✓ |
| Different relay reachable at the same URL | looks like the same relay, silently reuses ids ✗ | detected ✓ |
| Relay reachable at two URLs | looks like two relays | one relay ✓ |

Because the id lives in the database, restoring a relay from backup also keeps
its identity — clients carry on as though nothing happened.

### What happens on a switch

Detected during startup, before the connection is opened and before anything is
pushed:

```
GET /relay-info  →  relayId differs from settings.ActiveRelayId
   │
   1. Demote foreign catalog entries
   │     assigned id → fresh local:… id, IsProvisional = 1
   │     ON UPDATE CASCADE carries Game, AchievementDefinition,
   │     GameStatDefinition and CatalogAlias across
   │
   2. Clear sync watermarks   AchievementUnlock.SyncedAt = NULL
   │                          PlaySession.SyncedAt      = NULL
   │
   3. Clear the friend cache  (friendships are per relay)
   │
   4. Select or create credentials for the new relay
   │
   5. ActiveRelayId = new relay
```

Demotion is the exact mirror of promotion, using the same key-rewrite and the
same cascade. **Nothing local is deleted.** Games, achievements, unlocks, play
sessions, collections, tags and notes are all untouched; only the identity
changes, and the fingerprint follows it so the new relay can resolve it on the
next sync pass.

Watermarks are cleared because the new relay has seen none of this history — a
`SyncedAt` recorded against the old one would silently withhold everything the
user has earned. Re-pushing is safe: unlock merge is earliest-wins and
idempotent, and play sessions carry globally unique keys, so nothing is
double-counted.

### Credentials are kept per relay

A friend code issued by one relay means nothing on another, so switching cannot
overwrite it — that would lose the friendships built up on the first relay.
Settings hold a `RelayIdentity` per relay, and switching **back** restores the
original identity rather than registering afresh.

Round-tripping therefore works: A → B → A leaves the user with their original
friend code on A, and their catalog entries re-resolve to A's ids by fingerprint
because A's alias table still maps them.

### Offline-safe and idempotent

- **Offline**: an unreachable relay leaves everything untouched. Migration never
  runs on a guess, because the launcher cannot distinguish "different relay" from
  "no answer", and demoting on the latter would churn identities on every network
  hiccup.
- **Idempotent**: demoted entries have source `local`, so a second pass finds
  nothing. Establishing identity runs on every launch and every reconnect and is
  a no-op once the active relay matches.

Covered by `RelayMigrationTests`, including that a switch preserves the game,
its collection, its achievement definition, its unlock and its play session.

### Deliberately manual

Nothing automatically re-points the launcher at a different relay. The user
changes the address in Settings; the launcher then detects and handles it. There
is no discovery, no failover to a secondary relay, and no merging of two relays'
catalogs — each of those would mean guessing at an intent the user has not
expressed.

---

## Relay data model

Not built yet. Documented so the relay work starts from a decision.

```sql
-- Identity. Rows are never deleted and CatalogId is never rewritten.
CatalogEntry(
    CatalogId             TEXT PRIMARY KEY,
    CanonicalTitle        TEXT NOT NULL,
    SupersededByCatalogId TEXT NULL REFERENCES CatalogEntry(CatalogId),
    CreatedAt, UpdatedAt  TEXT NOT NULL)

-- Many fingerprints resolve to one title. Open creation writes here.
CatalogAlias(
    Fingerprint TEXT PRIMARY KEY,
    CatalogId   TEXT NOT NULL REFERENCES CatalogEntry(CatalogId),
    CreatedAt   TEXT NOT NULL)

-- Authored achievement definitions, shared by everyone who owns the title.
CatalogAchievement(
    CatalogId TEXT NOT NULL REFERENCES CatalogEntry(CatalogId),
    ApiName   TEXT NOT NULL,
    Title, Description, IconUrl, IsHidden, ProgressTarget, StatApiName,
    PRIMARY KEY (CatalogId, ApiName))

-- Per-user history. Keyed on api name, NOT on any definition row id, so a
-- merge that removes a duplicate definition cannot orphan a user's unlock.
UserAchievement(
    FriendCode TEXT NOT NULL REFERENCES User(FriendCode),
    CatalogId  TEXT NOT NULL REFERENCES CatalogEntry(CatalogId),
    ApiName    TEXT NOT NULL,
    UnlockedAt TEXT NOT NULL,
    PRIMARY KEY (FriendCode, CatalogId, ApiName))

UserStat(
    FriendCode TEXT NOT NULL,
    CatalogId  TEXT NOT NULL,
    ApiName    TEXT NOT NULL,
    Value      REAL NOT NULL,
    UpdatedAt  TEXT NOT NULL,
    PRIMARY KEY (FriendCode, CatalogId, ApiName))

-- Ownership, for "your friend plays this too" and rarity denominators.
UserLibrary(
    FriendCode TEXT NOT NULL,
    CatalogId  TEXT NOT NULL,
    AddedAt    TEXT NOT NULL,
    PRIMARY KEY (FriendCode, CatalogId))
```

**The load-bearing decision** is that `UserAchievement` is keyed on
`(FriendCode, CatalogId, ApiName)` rather than on a definition row id. Api name
is the stable, human-authored handle; a row id is an implementation detail that a
merge may delete. Keying on the name means a merge is a data-movement problem —
`UPDATE … SET CatalogId = @survivor` — and never a history-loss problem.

Relay-side merge is then the same shape as the client's, minus the local
concerns:

```sql
UPDATE OR IGNORE UserAchievement SET CatalogId = @target WHERE CatalogId = @source;
UPDATE OR IGNORE UserStat        SET CatalogId = @target WHERE CatalogId = @source;
UPDATE OR IGNORE UserLibrary     SET CatalogId = @target WHERE CatalogId = @source;
UPDATE CatalogAlias SET CatalogId = @target WHERE CatalogId = @source;
UPDATE CatalogEntry SET SupersededByCatalogId = @target WHERE CatalogId = @source;
```

`OR IGNORE` handles a user who has the same achievement under both entries: the
existing row wins and the duplicate is discarded. That is safe **only** because
both rows assert the same fact — "this user earned this achievement" — so
discarding either leaves the truth intact. If a later change makes those rows
carry data that differs meaningfully, this needs to become an explicit
earlier-wins merge, as the client already does.

### Endpoints the client is waiting for

| Endpoint | Purpose |
|---|---|
| `POST /catalog/resolve` | `{ fingerprint, title, company }` → `{ catalogId, canonicalTitle }`; creates on miss |
| `GET /catalog/{id}` | Resolve an id, following redirects |
| `GET /catalog/{id}/achievements` | The shared definition set |
| `POST /sync/achievements` | Push unlocks where `SyncedAt IS NULL` |

### Operator tooling — not built

Deliberately out of scope for now. It needs, at minimum: a duplicate-candidate
view (entries whose titles are similar but whose fingerprints differ), a merge
action with a preview of affected user rows, and an audit log — merges are not
reversible once aliases have moved, so a record of who merged what matters.

### Shared contracts

`PresenceDto` should gain a nullable `CurrentGameCatalogId` alongside
`CurrentGameTitle`. The title stays for display — a friend whose game has no
catalog entry yet must still show as "Playing something" — while the catalog id
enables "you own this too". Nullable, because presence must keep working for
unregistered games.

### Consequence of open creation

Thirty users adding the same game with slightly different metadata produce up to
thirty entries until someone merges them. That is accepted: the alternative is
making people wait for moderation before their library works. `CatalogAlias`
plus non-destructive merge is what keeps the cleanup cheap and safe. If pollution
becomes a real problem, promotion-on-N-independent-matches can be added later
without a schema change, because the aliases needed to compute "N independent
matches" are already being recorded.
