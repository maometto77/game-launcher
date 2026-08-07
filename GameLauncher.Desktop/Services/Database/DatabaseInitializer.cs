using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Creates and migrates the local SQLite schema.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are an ordered list of scripts. The database records how many have
/// been applied in <c>PRAGMA user_version</c>, and startup runs whatever is
/// outstanding inside a transaction. Adding a schema change means appending a
/// script; existing entries are never edited, because an installed database has
/// already run them.
/// </para>
/// <para>
/// <c>user_version</c> is used rather than a bookkeeping table because SQLite
/// provides it for exactly this purpose and it costs no extra query.
/// </para>
/// </remarks>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    /// <summary>
    /// The ordered migration scripts. Index + 1 is the schema version each
    /// produces.
    /// </summary>
    private static readonly string[] Migrations =
    [
        // v1 — initial schema
        """
        CREATE TABLE IF NOT EXISTS Collection (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            Name      TEXT    NOT NULL,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            DateAdded TEXT    NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Collection_Name
            ON Collection (Name COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS Game (
            Id               INTEGER PRIMARY KEY AUTOINCREMENT,
            Title            TEXT    NOT NULL,
            CoverArtPath     TEXT    NULL,
            HeroArtPath      TEXT    NULL,
            ExecutablePath   TEXT    NOT NULL,
            InstallDir       TEXT    NULL,
            InstallSizeBytes INTEGER NOT NULL DEFAULT 0,
            PlaytimeSeconds  INTEGER NOT NULL DEFAULT 0,
            LastPlayedAt     TEXT    NULL,
            DateAdded        TEXT    NOT NULL,
            Tags             TEXT    NOT NULL DEFAULT '[]',
            CollectionId     INTEGER NULL,
            Notes            TEXT    NULL,
            SourceUrl        TEXT    NULL,

            -- Deleting a collection un-files its games rather than deleting them.
            CONSTRAINT FK_Game_Collection FOREIGN KEY (CollectionId)
                REFERENCES Collection (Id) ON DELETE SET NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Game_CollectionId ON Game (CollectionId);
        CREATE INDEX IF NOT EXISTS IX_Game_Title        ON Game (Title COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS IX_Game_LastPlayedAt ON Game (LastPlayedAt DESC);

        CREATE TABLE IF NOT EXISTS AchievementDefinition (
            Id                INTEGER PRIMARY KEY AUTOINCREMENT,
            GameId            INTEGER NULL,
            Title             TEXT    NOT NULL,
            Description       TEXT    NOT NULL DEFAULT '',
            IconPath          TEXT    NULL,
            Kind              INTEGER NOT NULL,
            TriggerConfigJson TEXT    NOT NULL DEFAULT '{}',

            -- Library-wide achievements have no owning game, hence the nullable FK.
            CONSTRAINT FK_AchievementDefinition_Game FOREIGN KEY (GameId)
                REFERENCES Game (Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_AchievementDefinition_GameId
            ON AchievementDefinition (GameId);

        CREATE TABLE IF NOT EXISTS AchievementUnlock (
            -- One row per definition: an achievement cannot be earned twice, and
            -- making the FK the primary key enforces that without extra logic.
            DefinitionId INTEGER NOT NULL PRIMARY KEY,
            UnlockedAt   TEXT    NOT NULL,

            CONSTRAINT FK_AchievementUnlock_Definition FOREIGN KEY (DefinitionId)
                REFERENCES AchievementDefinition (Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS FriendCache (
            FriendCode    TEXT NOT NULL PRIMARY KEY,
            DisplayName   TEXT NOT NULL,
            LastKnownGame TEXT NULL,
            LastSeenAt    TEXT NOT NULL,
            AvatarPath    TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS PlaySession (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            GameId          INTEGER NOT NULL,
            StartedAt       TEXT    NOT NULL,
            EndedAt         TEXT    NULL,
            DurationSeconds INTEGER NULL,

            CONSTRAINT FK_PlaySession_Game FOREIGN KEY (GameId)
                REFERENCES Game (Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_PlaySession_GameId    ON PlaySession (GameId);
        CREATE INDEX IF NOT EXISTS IX_PlaySession_StartedAt ON PlaySession (StartedAt DESC);
        """,

        // v2 — stable identity, achievement progress and stats.
        //
        // Landed ahead of the achievement engine on purpose. Columns are cheap to
        // add later; *identity* is not. Once unlocks exist, retrofitting a stable
        // key onto definitions means guessing which local row corresponds to which
        // remote achievement, and a wrong guess silently attributes somebody's
        // unlock to the wrong achievement. Introducing it now, while the only rows
        // are sample data, costs nothing.
        """
        ---------------------------------------------------------------------
        -- Stable keys for anything that may later cross a machine boundary.
        --
        -- GlobalKey is a random 128-bit value rendered as 32 hex characters,
        -- generated by SQLite itself so that existing rows are backfilled in the
        -- same statement that adds the column. It is the identity a relay, an
        -- export file or a shared achievement set keys on; the INTEGER primary
        -- keys stay local and are never transmitted.
        ---------------------------------------------------------------------
        ALTER TABLE Game ADD COLUMN GlobalKey TEXT NOT NULL DEFAULT '';
        ALTER TABLE Game ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '';

        UPDATE Game SET GlobalKey = lower(hex(randomblob(16))) WHERE GlobalKey = '';
        UPDATE Game SET UpdatedAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE UpdatedAt = '';

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Game_GlobalKey ON Game (GlobalKey);

        ---------------------------------------------------------------------
        -- Achievement definitions become a catalog rather than a private list.
        --
        -- ApiName is the human-readable stable handle Steam-style catalogs use
        -- (ACH_WIN_100_GAMES); GlobalKey is the machine identity. Both exist
        -- because a shared definition set is authored against ApiName, while
        -- synchronisation needs something guaranteed unique.
        ---------------------------------------------------------------------
        ALTER TABLE AchievementDefinition ADD COLUMN ApiName        TEXT    NOT NULL DEFAULT '';
        ALTER TABLE AchievementDefinition ADD COLUMN GlobalKey      TEXT    NOT NULL DEFAULT '';
        ALTER TABLE AchievementDefinition ADD COLUMN IsHidden       INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE AchievementDefinition ADD COLUMN SortOrder      INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE AchievementDefinition ADD COLUMN ProgressTarget REAL    NULL;
        ALTER TABLE AchievementDefinition ADD COLUMN StatApiName    TEXT    NULL;
        ALTER TABLE AchievementDefinition ADD COLUMN UpdatedAt      TEXT    NOT NULL DEFAULT '';

        UPDATE AchievementDefinition SET GlobalKey = lower(hex(randomblob(16))) WHERE GlobalKey = '';
        UPDATE AchievementDefinition SET ApiName   = 'ACH_' || Id                WHERE ApiName   = '';
        UPDATE AchievementDefinition SET UpdatedAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE UpdatedAt = '';

        CREATE UNIQUE INDEX IF NOT EXISTS UX_AchievementDefinition_GlobalKey
            ON AchievementDefinition (GlobalKey);

        -- COALESCE(GameId, 0) rather than a bare GameId: SQLite treats NULLs as
        -- distinct in a unique index, so library-wide achievements (GameId IS
        -- NULL) would otherwise be free to collide on ApiName.
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AchievementDefinition_Game_ApiName
            ON AchievementDefinition (COALESCE(GameId, 0), ApiName COLLATE NOCASE);

        ---------------------------------------------------------------------
        -- Unlocks gain a synchronisation watermark.
        --
        -- Null means "never pushed to a relay". Kept separate from UnlockedAt so
        -- that re-syncing never rewrites when the achievement was actually earned.
        ---------------------------------------------------------------------
        ALTER TABLE AchievementUnlock ADD COLUMN SyncedAt TEXT NULL;

        ---------------------------------------------------------------------
        -- Progress towards an achievement, distinct from having earned it.
        --
        -- A separate table from AchievementUnlock because the two have opposite
        -- lifecycles: progress is mutable and rewritten constantly, an unlock is
        -- written once and is permanent. Merging them would put a hot,
        -- frequently-updated column next to an immutable audit record.
        ---------------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS AchievementProgress (
            DefinitionId INTEGER NOT NULL PRIMARY KEY,
            CurrentValue REAL    NOT NULL DEFAULT 0,
            UpdatedAt    TEXT    NOT NULL,

            CONSTRAINT FK_AchievementProgress_Definition FOREIGN KEY (DefinitionId)
                REFERENCES AchievementDefinition (Id) ON DELETE CASCADE
        );

        ---------------------------------------------------------------------
        -- Stats: named counters a game accumulates, which achievements can be
        -- defined against ("play 100 matches"). Definition and value are split
        -- so a shared catalog can ship definitions without carrying anybody's
        -- personal numbers.
        ---------------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS GameStatDefinition (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            GameId          INTEGER NULL,
            ApiName         TEXT    NOT NULL,
            DisplayName     TEXT    NOT NULL DEFAULT '',
            StatType        INTEGER NOT NULL DEFAULT 0,
            DefaultValue    REAL    NOT NULL DEFAULT 0,
            IsIncrementOnly INTEGER NOT NULL DEFAULT 1,
            GlobalKey       TEXT    NOT NULL,
            UpdatedAt       TEXT    NOT NULL,

            CONSTRAINT FK_GameStatDefinition_Game FOREIGN KEY (GameId)
                REFERENCES Game (Id) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_GameStatDefinition_GlobalKey
            ON GameStatDefinition (GlobalKey);

        CREATE UNIQUE INDEX IF NOT EXISTS UX_GameStatDefinition_Game_ApiName
            ON GameStatDefinition (COALESCE(GameId, 0), ApiName COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS GameStatValue (
            StatId    INTEGER NOT NULL PRIMARY KEY,
            Value     REAL    NOT NULL DEFAULT 0,
            UpdatedAt TEXT    NOT NULL,
            SyncedAt  TEXT    NULL,

            CONSTRAINT FK_GameStatValue_Definition FOREIGN KEY (StatId)
                REFERENCES GameStatDefinition (Id) ON DELETE CASCADE
        );
        """,

        // v3 — shared catalog identity.
        //
        // Replaces local game identity as the anchor for achievements and stats.
        // GlobalKey identifies *an installation's row*: two people who own the
        // same game generate different keys, so it can never express "the same
        // title" across users, which is precisely what global achievements,
        // shared stats and "your friend is playing this too" all require.
        //
        // CatalogEntry is that shared identity. Entries start provisional with a
        // locally-minted id and are promoted to a server-assigned one on first
        // contact with a relay; ON UPDATE CASCADE means the promotion repoints
        // every reference automatically.
        """
        CREATE TABLE IF NOT EXISTS CatalogEntry (
            -- Server-assigned once promoted. Provisional ids are prefixed
            -- 'local:' so the two can never be confused on sight or in a log.
            CatalogId        TEXT    NOT NULL PRIMARY KEY,

            -- Which relay assigned this id. Guards against two relays handing out
            -- colliding ids to the same client.
            Source           TEXT    NOT NULL DEFAULT 'local',

            IsProvisional    INTEGER NOT NULL DEFAULT 1,
            CanonicalTitle   TEXT    NOT NULL,

            -- Deterministic signature used to ask a relay "do you already know
            -- this game?" before creating a duplicate entry.
            MatchFingerprint TEXT    NOT NULL DEFAULT '',

            CreatedAt        TEXT    NOT NULL,
            UpdatedAt        TEXT    NOT NULL,
            SyncedAt         TEXT    NULL
        );

        CREATE INDEX IF NOT EXISTS IX_CatalogEntry_Fingerprint  ON CatalogEntry (MatchFingerprint);
        CREATE INDEX IF NOT EXISTS IX_CatalogEntry_Provisional  ON CatalogEntry (IsProvisional);

        -- SQLite permits ADD COLUMN with a REFERENCES clause only when the
        -- default is NULL, which is the case for all three.
        ALTER TABLE Game ADD COLUMN CatalogId TEXT NULL
            REFERENCES CatalogEntry (CatalogId) ON UPDATE CASCADE ON DELETE SET NULL;

        ALTER TABLE AchievementDefinition ADD COLUMN CatalogId TEXT NULL
            REFERENCES CatalogEntry (CatalogId) ON UPDATE CASCADE ON DELETE CASCADE;

        ALTER TABLE GameStatDefinition ADD COLUMN CatalogId TEXT NULL
            REFERENCES CatalogEntry (CatalogId) ON UPDATE CASCADE ON DELETE CASCADE;

        -- One provisional entry per existing game. GlobalKey is reused as the
        -- provisional suffix purely because it is already unique per row.
        INSERT INTO CatalogEntry
            (CatalogId, Source, IsProvisional, CanonicalTitle, MatchFingerprint, CreatedAt, UpdatedAt)
        SELECT 'local:' || GlobalKey,
               'local',
               1,
               Title,
               '',
               strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
               strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        FROM   Game;

        UPDATE Game SET CatalogId = 'local:' || GlobalKey;

        UPDATE AchievementDefinition
        SET    CatalogId = (SELECT g.CatalogId FROM Game g WHERE g.Id = AchievementDefinition.GameId)
        WHERE  GameId IS NOT NULL;

        -- The legacy ownership link is neutralised rather than dropped.
        --
        -- SQLite refuses DROP COLUMN on a column named in a foreign key, and
        -- rebuilding the table would mean dropping it — which, with foreign keys
        -- enabled, performs an implicit DELETE FROM and would cascade every
        -- AchievementUnlock row out of existence. Setting the column to NULL
        -- makes the cascade unreachable and costs one vestigial column, which a
        -- later maintenance migration can remove outside a transaction.
        --
        -- This is also the behaviour change that matters: achievements now
        -- survive uninstalling a game, exactly as they do on Steam.
        UPDATE AchievementDefinition SET GameId = NULL;
        UPDATE GameStatDefinition    SET GameId = NULL;

        -- Uniqueness now applies within a catalog entry rather than within a
        -- local install.
        DROP INDEX IF EXISTS UX_AchievementDefinition_Game_ApiName;
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AchievementDefinition_Catalog_ApiName
            ON AchievementDefinition (COALESCE(CatalogId, ''), ApiName COLLATE NOCASE);

        DROP INDEX IF EXISTS UX_GameStatDefinition_Game_ApiName;
        CREATE UNIQUE INDEX IF NOT EXISTS UX_GameStatDefinition_Catalog_ApiName
            ON GameStatDefinition (COALESCE(CatalogId, ''), ApiName COLLATE NOCASE);

        CREATE INDEX IF NOT EXISTS IX_Game_CatalogId                  ON Game (CatalogId);
        CREATE INDEX IF NOT EXISTS IX_AchievementDefinition_CatalogId ON AchievementDefinition (CatalogId);
        CREATE INDEX IF NOT EXISTS IX_GameStatDefinition_CatalogId    ON GameStatDefinition (CatalogId);
        """,

        // v4 — catalog aliases and merge redirects.
        //
        // Implements the agreed policy: catalog creation is open, one title may
        // legitimately have many fingerprints, and an assigned CatalogId is
        // immutable. Merging two titles therefore happens by moving *references*
        // and leaving a redirect behind, never by rewriting an identity a client
        // may already have synchronised.
        """
        ---------------------------------------------------------------------
        -- Many fingerprints resolve to one title.
        --
        -- A re-release, a different publisher's build, the launcher executable
        -- versus the game's own, all produce different fingerprints for what is
        -- really one title. Keeping them in their own table means an operator can
        -- unify them later without rewriting anybody's catalog id.
        ---------------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS CatalogAlias (
            Fingerprint TEXT NOT NULL PRIMARY KEY,
            CatalogId   TEXT NOT NULL,
            Source      TEXT NOT NULL DEFAULT 'local',
            CreatedAt   TEXT NOT NULL,

            CONSTRAINT FK_CatalogAlias_Entry FOREIGN KEY (CatalogId)
                REFERENCES CatalogEntry (CatalogId) ON UPDATE CASCADE ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_CatalogAlias_CatalogId ON CatalogAlias (CatalogId);

        ---------------------------------------------------------------------
        -- Merge redirect.
        --
        -- When an operator merges two titles, the absorbed entry is kept and
        -- points at the survivor rather than being deleted. A client that still
        -- holds the old id keeps working, and lookups follow the chain to the
        -- canonical entry.
        ---------------------------------------------------------------------
        ALTER TABLE CatalogEntry ADD COLUMN SupersededByCatalogId TEXT NULL
            REFERENCES CatalogEntry (CatalogId) ON UPDATE CASCADE ON DELETE SET NULL;

        CREATE INDEX IF NOT EXISTS IX_CatalogEntry_Superseded
            ON CatalogEntry (SupersededByCatalogId);

        -- Existing entries contribute their originating fingerprint as the first
        -- alias. CatalogEntry.MatchFingerprint stays as provenance only; from here
        -- on, fingerprint lookups go through CatalogAlias so there is exactly one
        -- path and nothing to drift.
        INSERT OR IGNORE INTO CatalogAlias (Fingerprint, CatalogId, Source, CreatedAt)
        SELECT MatchFingerprint, CatalogId, Source, CreatedAt
        FROM   CatalogEntry
        WHERE  MatchFingerprint <> '';

        DROP INDEX IF EXISTS IX_CatalogEntry_Fingerprint;
        """,

        // v5 — play sessions become the unit of playtime synchronisation.
        //
        // Cumulative totals cannot be merged: syncing Game.PlaytimeSeconds
        // between two machines either double-counts it or discards one side, and
        // no conflict rule recovers the difference because the information needed
        // is not in a total. Individual sessions can be merged, because each one
        // is a distinct fact that either has or has not been seen before.
        //
        // Three columns make that deterministic and idempotent. CatalogId is
        // deliberately absent: it is reached by joining through Game, so a session
        // recorded while the game still had a provisional catalog id follows the
        // promotion automatically. Copying the id onto the row would freeze the
        // stale one.
        """
        -- Globally unique identity for the session, independent of the local
        -- auto-increment key. Two devices both recording their fifth session must
        -- not collide, and re-pushing a session must be recognisable as the same
        -- fact rather than inserted twice.
        ALTER TABLE PlaySession ADD COLUMN SessionKey TEXT NOT NULL DEFAULT '';

        -- Which device recorded it. Without this a merge cannot tell two genuinely
        -- concurrent sessions apart from one session reported twice.
        ALTER TABLE PlaySession ADD COLUMN DeviceId TEXT NULL;

        -- The outbound queue predicate, matching AchievementUnlock.
        ALTER TABLE PlaySession ADD COLUMN SyncedAt TEXT NULL;

        UPDATE PlaySession SET SessionKey = lower(hex(randomblob(16))) WHERE SessionKey = '';

        CREATE UNIQUE INDEX IF NOT EXISTS UX_PlaySession_SessionKey ON PlaySession (SessionKey);
        CREATE INDEX IF NOT EXISTS IX_PlaySession_Synced ON PlaySession (SyncedAt);
        """,

        // v6 — achievement providers are addressed by name rather than by enum.
        //
        // The evaluation engine dispatches on ProviderKey. Dispatching on the Kind
        // enum would mean every new provider required editing the enum — a change
        // to the core model, and exactly what "extensible through
        // IAchievementProvider" is supposed to avoid. A string key makes a new
        // provider a registration and nothing more.
        //
        // Kind is kept as the coarse display category the UI groups by, and stays
        // accurate for the three built-in providers.
        """
        ALTER TABLE AchievementDefinition ADD COLUMN ProviderKey TEXT NOT NULL DEFAULT '';

        -- Backfilled from the enum's stored values: 0 Meta, 1 SaveFile, 2 Memory.
        UPDATE AchievementDefinition SET ProviderKey = 'meta'      WHERE ProviderKey = '' AND Kind = 0;
        UPDATE AchievementDefinition SET ProviderKey = 'save-file' WHERE ProviderKey = '' AND Kind = 1;
        UPDATE AchievementDefinition SET ProviderKey = 'memory'    WHERE ProviderKey = '' AND Kind = 2;

        -- Anything unrecognised is parked on the manual provider, which never
        -- unlocks on its own, rather than being silently evaluated by the wrong one.
        UPDATE AchievementDefinition SET ProviderKey = 'manual' WHERE ProviderKey = '';

        CREATE INDEX IF NOT EXISTS IX_AchievementDefinition_Provider
            ON AchievementDefinition (ProviderKey);
        """,

        // v7 — the discovery catalogue.
        //
        // This is a new subsystem, not an extension of CatalogEntry. That table is
        // the shared identity of a title the user has *installed*: its rows are
        // minted from a fingerprint of an executable, its primary key is rewritten
        // by promotion and demotion, and a relay is authoritative for it. A
        // discovery listing has no executable to fingerprint, is never
        // synchronised, and its identifier is never rewritten. Hanging thousands
        // of scraped rows off CatalogEntry would drag them through every relay
        // migration and put third-party metadata into the sync path.
        //
        // The two meet in exactly one place: Game.ListingId, written once when a
        // listing is installed. Identity continues to be minted by the existing
        // import path from the executable that is then on disk.
        //
        // Genre, developer, publisher and platform are normalised rather than
        // stored inline. Unlike Game.Tags — which is JSON because tags are read
        // and written as a set alongside one game — these are filtered and
        // faceted across the whole catalogue, which JSON answers only with a full
        // scan and a per-row parse. Normalising also means a misspelling is fixed
        // in one row instead of four hundred.
        """
        -- ---------------------------------------------------------------
        -- Lookup entities. NormalizedName is what deduplicates them, so that
        -- "MicroProse", "MicroProse Software, Inc." and "MICROPROSE" become one
        -- row that can be renamed once and corrected everywhere.
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS Genre (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            Name           TEXT    NOT NULL,
            NormalizedName TEXT    NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Genre_Normalized ON Genre (NormalizedName);

        CREATE TABLE IF NOT EXISTS Developer (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            Name           TEXT    NOT NULL,
            NormalizedName TEXT    NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Developer_Normalized ON Developer (NormalizedName);

        CREATE TABLE IF NOT EXISTS Publisher (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            Name           TEXT    NOT NULL,
            NormalizedName TEXT    NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Publisher_Normalized ON Publisher (NormalizedName);

        CREATE TABLE IF NOT EXISTS Platform (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            Name           TEXT    NOT NULL,
            NormalizedName TEXT    NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Platform_Normalized ON Platform (NormalizedName);

        -- ---------------------------------------------------------------
        -- The merged listing. Every column here is derived from ListingSource
        -- rows and can be rebuilt from them without touching the network.
        --
        -- RowId exists because a listing is addressed by a text identifier and
        -- several joins are cheaper against an integer. It is never displayed.
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS CatalogListing (
            RowId              INTEGER PRIMARY KEY AUTOINCREMENT,
            ListingId          TEXT    NOT NULL,
            Title              TEXT    NOT NULL,
            SortTitle          TEXT    NOT NULL DEFAULT '',
            Year               INTEGER NULL,
            DeveloperId        INTEGER NULL,
            PublisherId        INTEGER NULL,
            Description        TEXT    NULL,
            SystemRequirements TEXT    NULL,
            MatchKey           TEXT    NOT NULL DEFAULT '',
            CoverImageUrl      TEXT    NULL,
            CoverImagePath     TEXT    NULL,
            PrimarySourceKey   TEXT    NOT NULL DEFAULT '',

            -- Which source won each scalar field, as JSON. One column rather than
            -- a table because it is read by a human diagnosing a merge rule and
            -- never joined or filtered.
            FieldProvenance    TEXT    NULL,

            -- Corrections the user made by hand, applied after the merge so that
            -- re-importing cannot discard them and the merge stays pure.
            UserOverride       TEXT    NULL,

            IsDownloadable     INTEGER NOT NULL DEFAULT 1,
            IsHidden           INTEGER NOT NULL DEFAULT 0,
            ContentHash        TEXT    NOT NULL DEFAULT '',
            CreatedAt          TEXT    NOT NULL,
            UpdatedAt          TEXT    NOT NULL,

            CONSTRAINT FK_CatalogListing_Developer FOREIGN KEY (DeveloperId)
                REFERENCES Developer (Id) ON DELETE SET NULL,
            CONSTRAINT FK_CatalogListing_Publisher FOREIGN KEY (PublisherId)
                REFERENCES Publisher (Id) ON DELETE SET NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_CatalogListing_ListingId ON CatalogListing (ListingId);
        CREATE INDEX IF NOT EXISTS IX_CatalogListing_MatchKey  ON CatalogListing (MatchKey);
        CREATE INDEX IF NOT EXISTS IX_CatalogListing_SortTitle ON CatalogListing (SortTitle COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS IX_CatalogListing_Year      ON CatalogListing (Year);
        CREATE INDEX IF NOT EXISTS IX_CatalogListing_Developer ON CatalogListing (DeveloperId);
        CREATE INDEX IF NOT EXISTS IX_CatalogListing_Publisher ON CatalogListing (PublisherId);
        CREATE INDEX IF NOT EXISTS IX_CatalogListing_Created   ON CatalogListing (CreatedAt DESC);

        -- ---------------------------------------------------------------
        -- Many-to-many facets.
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ListingGenre (
            ListingId TEXT    NOT NULL,
            GenreId   INTEGER NOT NULL,

            PRIMARY KEY (ListingId, GenreId),

            CONSTRAINT FK_ListingGenre_Listing FOREIGN KEY (ListingId)
                REFERENCES CatalogListing (ListingId) ON DELETE CASCADE,
            CONSTRAINT FK_ListingGenre_Genre FOREIGN KEY (GenreId)
                REFERENCES Genre (Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_ListingGenre_Genre ON ListingGenre (GenreId);

        CREATE TABLE IF NOT EXISTS ListingPlatform (
            ListingId  TEXT    NOT NULL,
            PlatformId INTEGER NOT NULL,

            PRIMARY KEY (ListingId, PlatformId),

            CONSTRAINT FK_ListingPlatform_Listing FOREIGN KEY (ListingId)
                REFERENCES CatalogListing (ListingId) ON DELETE CASCADE,
            CONSTRAINT FK_ListingPlatform_Platform FOREIGN KEY (PlatformId)
                REFERENCES Platform (Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_ListingPlatform_Platform ON ListingPlatform (PlatformId);

        -- Tags stop short of a lookup entity on purpose. They are free-form and
        -- open-vocabulary, so a Tag table would accumulate thousands of
        -- single-use rows; they are displayed rather than faceted, so nothing
        -- would query it.
        CREATE TABLE IF NOT EXISTS ListingTag (
            ListingId TEXT NOT NULL,
            Tag       TEXT NOT NULL,

            PRIMARY KEY (ListingId, Tag),

            CONSTRAINT FK_ListingTag_Listing FOREIGN KEY (ListingId)
                REFERENCES CatalogListing (ListingId) ON DELETE CASCADE
        );

        -- ---------------------------------------------------------------
        -- The evidence behind the merged row. This is the authoritative input;
        -- CatalogListing is derived. Keeping the raw payload is what allows a
        -- normalisation or merge-rule fix to be applied to the whole catalogue
        -- offline, and it is the only way to diagnose a parser that produced a
        -- wrong answer rather than no answer.
        --
        -- Keyed by (SourceKey, SourceItemId): one row per item per source. The
        -- listing it contributes to can change when a re-match moves it.
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ListingSource (
            SourceKey         TEXT NOT NULL,
            SourceItemId      TEXT NOT NULL,
            ListingId         TEXT NOT NULL,
            SourceUrl         TEXT NOT NULL DEFAULT '',
            NormalizedJson    TEXT NOT NULL DEFAULT '',
            RawPayload        BLOB NULL,
            SourceUpdatedAt   TEXT NULL,
            FetchedAt         TEXT NOT NULL,
            SourceContentHash TEXT NOT NULL DEFAULT '',
            Rank              INTEGER NOT NULL DEFAULT 0,
            LastError         TEXT NULL,

            PRIMARY KEY (SourceKey, SourceItemId),

            CONSTRAINT FK_ListingSource_Listing FOREIGN KEY (ListingId)
                REFERENCES CatalogListing (ListingId) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_ListingSource_Listing ON ListingSource (ListingId);
        CREATE INDEX IF NOT EXISTS IX_ListingSource_Fetched ON ListingSource (SourceKey, FetchedAt);

        -- ---------------------------------------------------------------
        -- Mirrors and images. Several rows per listing is the normal case: the
        -- same file is served from more than one host, and sources are unioned
        -- rather than allowed to replace one another.
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ListingDownload (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            ListingId  TEXT    NOT NULL,
            SourceKey  TEXT    NOT NULL DEFAULT '',
            Url        TEXT    NOT NULL,
            FileName   TEXT    NULL,
            SizeBytes  INTEGER NULL,
            Md5        TEXT    NULL,
            Sha1       TEXT    NULL,
            Format     TEXT    NULL,
            Kind       INTEGER NOT NULL DEFAULT 0,
            MirrorRank INTEGER NOT NULL DEFAULT 0,

            CONSTRAINT FK_ListingDownload_Listing FOREIGN KEY (ListingId)
                REFERENCES CatalogListing (ListingId) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_ListingDownload_Url ON ListingDownload (ListingId, Url);
        CREATE INDEX IF NOT EXISTS IX_ListingDownload_Listing    ON ListingDownload (ListingId, MirrorRank);

        CREATE TABLE IF NOT EXISTS ListingImage (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            ListingId TEXT    NOT NULL,
            SourceKey TEXT    NOT NULL DEFAULT '',
            Kind      INTEGER NOT NULL DEFAULT 0,
            RemoteUrl TEXT    NOT NULL,
            LocalPath TEXT    NULL,
            Width     INTEGER NOT NULL DEFAULT 0,
            Height    INTEGER NOT NULL DEFAULT 0,
            SortOrder INTEGER NOT NULL DEFAULT 0,

            CONSTRAINT FK_ListingImage_Listing FOREIGN KEY (ListingId)
                REFERENCES CatalogListing (ListingId) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_ListingImage_Url  ON ListingImage (ListingId, RemoteUrl);
        CREATE INDEX IF NOT EXISTS IX_ListingImage_Listing     ON ListingImage (ListingId, Kind, SortOrder);

        -- ---------------------------------------------------------------
        -- Many match keys resolve to one listing, in the same spirit as
        -- CatalogAlias: one title legitimately spells its name several ways, and
        -- a manual "these are the same game" decision has to survive re-import.
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ListingAlias (
            MatchKey  TEXT NOT NULL PRIMARY KEY,
            ListingId TEXT NOT NULL,
            Source    TEXT NOT NULL DEFAULT '',
            CreatedAt TEXT NOT NULL,

            CONSTRAINT FK_ListingAlias_Listing FOREIGN KEY (ListingId)
                REFERENCES CatalogListing (ListingId) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_ListingAlias_Listing ON ListingAlias (ListingId);

        -- ---------------------------------------------------------------
        -- Import bookkeeping. Cursor is what lets a pass killed halfway resume
        -- instead of starting over, and the item counters are what reveal a
        -- parser that has silently started returning nothing.
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS CatalogImportRun (
            RunId         INTEGER PRIMARY KEY AUTOINCREMENT,
            SourceKey     TEXT    NOT NULL,
            Mode          INTEGER NOT NULL DEFAULT 0,
            StartedAt     TEXT    NOT NULL,
            CompletedAt   TEXT    NULL,
            Cursor        TEXT    NULL,
            ItemsSeen     INTEGER NOT NULL DEFAULT 0,
            ItemsChanged  INTEGER NOT NULL DEFAULT 0,
            ItemsFailed   INTEGER NOT NULL DEFAULT 0,
            ListingsAdded INTEGER NOT NULL DEFAULT 0,
            LastError     TEXT    NULL
        );

        CREATE INDEX IF NOT EXISTS IX_CatalogImportRun_Source ON CatalogImportRun (SourceKey, StartedAt DESC);

        -- ---------------------------------------------------------------
        -- Full-text search, present from the first version of this schema.
        --
        -- LIKE '%term%' cannot use an index and cannot rank, so it degrades
        -- linearly and offers no relevance ordering. Adding FTS later would mean
        -- a second migration plus a backfill of every row, so it goes in now.
        --
        -- Developer, publisher and genre are flattened into the index on
        -- purpose: search wants one document per listing, and joining at query
        -- time would defeat the index. The normalised tables stay authoritative;
        -- these columns are a projection the repository rewrites whenever it
        -- writes a listing, inside the same transaction.
        --
        -- remove_diacritics 2 is why typing "Pokemon" finds "Pokémon", matching
        -- what TitleNormalizer does for matching.
        -- ---------------------------------------------------------------
        CREATE VIRTUAL TABLE IF NOT EXISTS CatalogListingSearch USING fts5 (
            ListingId UNINDEXED,
            Title,
            Developer,
            Publisher,
            Genres,
            Description,
            tokenize = 'unicode61 remove_diacritics 2'
        );

        -- ---------------------------------------------------------------
        -- The single link between discovery and the library, written once when a
        -- listing is installed. Deliberately nullable and deliberately not
        -- involved in catalog identity: a game added by any other route simply
        -- has no listing.
        -- ---------------------------------------------------------------
        ALTER TABLE Game ADD COLUMN ListingId TEXT NULL REFERENCES CatalogListing (ListingId) ON DELETE SET NULL;

        CREATE INDEX IF NOT EXISTS IX_Game_ListingId ON Game (ListingId);
        """
    ];

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies connections to migrate.</param>
    /// <param name="logger">Logger for migration diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DatabaseInitializer(IDbConnectionFactory connectionFactory, ILogger<DatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the schema version this build expects.</summary>
    public static int TargetVersion => Migrations.Length;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        DapperConfiguration.Initialize();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var currentVersion = await GetSchemaVersionAsync(connection).ConfigureAwait(false);

        if (currentVersion > TargetVersion)
        {
            // Refusing here is deliberate. Running an older build against a newer
            // schema would appear to work while quietly ignoring columns it does
            // not know about, and writes would then lose data the newer build had
            // stored.
            throw new InvalidOperationException(
                $"The library database is at schema version {currentVersion}, but this build understands " +
                $"version {TargetVersion}. Update GameLauncher to open this library.");
        }

        if (currentVersion == TargetVersion)
        {
            _logger.LogDebug("Database schema is current at version {Version}.", currentVersion);
            return;
        }

        _logger.LogInformation(
            "Migrating database schema from version {From} to {To}.", currentVersion, TargetVersion);

        for (var version = currentVersion; version < TargetVersion; version++)
        {
            await ApplyMigrationAsync(connection, version, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Database schema is now at version {Version}.", TargetVersion);
    }

    /// <summary>
    /// Applies a single migration and advances the recorded version atomically.
    /// </summary>
    /// <param name="connection">Open connection to migrate.</param>
    /// <param name="zeroBasedIndex">Index into <see cref="Migrations"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    private async Task ApplyMigrationAsync(
        DbConnection connection,
        int zeroBasedIndex,
        CancellationToken cancellationToken)
    {
        var targetVersion = zeroBasedIndex + 1;

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    Migrations[zeroBasedIndex],
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            // PRAGMA does not accept a bound parameter, and the value is an int
            // from a private array rather than anything caller-supplied.
            await connection.ExecuteAsync(
                new CommandDefinition(
                    $"PRAGMA user_version = {targetVersion};",
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Applied schema migration {Version}.", targetVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema migration {Version} failed; rolling back.", targetVersion);
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads the schema version recorded in the database file.</summary>
    /// <param name="connection">Open connection to query.</param>
    /// <returns>The applied schema version; zero for a database that has never been migrated.</returns>
    private static async Task<int> GetSchemaVersionAsync(DbConnection connection) =>
        await connection.ExecuteScalarAsync<int>("PRAGMA user_version;").ConfigureAwait(false);
}
