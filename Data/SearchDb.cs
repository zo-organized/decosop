using Microsoft.Data.Sqlite;

namespace DecoSOP.Data;

/// <summary>
/// Owns the search index's own SQLite file, kept deliberately separate from decosop.db.
///
/// Four reasons it lives apart: the Settings page exports decosop.db and the index is pure
/// derived data that would bloat every download; deleting this file is the entire recovery
/// procedure because it rebuilds from scratch; a long index build would otherwise contend with
/// the main database's single writer, undoing the concurrency hardening there; and FTS5 virtual
/// tables don't map onto EF Core's model, so keeping them here avoids colliding with
/// EnsureCreated().
/// </summary>
public sealed class SearchDb
{
    private readonly string _connectionString;
    private readonly ILogger<SearchDb> _logger;

    public string DatabasePath { get; }

    public SearchDb(string databasePath, ILogger<SearchDb> logger)
    {
        DatabasePath = databasePath;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Modules whose files are indexed. The ordinal is baked into rowids, so never renumber.</summary>
    public const string ModuleSop = "Sop";
    public const string ModuleDoc = "Doc";

    /// <summary>
    /// A stable, collision-free rowid for a (module, file) pair, computed rather than stored.
    /// Interleaving on the module ordinal means a file's index row can be replaced or removed
    /// without first looking up what rowid it was given.
    /// </summary>
    public static long RowIdFor(string module, int fileId)
        => (long)fileId * 2 + (module == ModuleDoc ? 1 : 0);

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var pragma = conn.CreateCommand();
        // Same reasoning as the main database: wait for a lock rather than failing outright.
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return conn;
    }

    /// <summary>
    /// Bumped whenever the schema or the tokenizer changes. An index built under an older
    /// version is discarded and rebuilt rather than migrated — it is derived data, so throwing
    /// it away is always correct and always cheaper than writing a migration.
    /// </summary>
    private const int SchemaVersion = 2;

    /// <summary>Create the schema if this is a fresh (or outdated) index file.</summary>
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        await using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            await wal.ExecuteNonQueryAsync(ct);
        }

        int existingVersion;
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version";
            existingVersion = Convert.ToInt32(await read.ExecuteScalarAsync(ct));
        }

        if (existingVersion != 0 && existingVersion != SchemaVersion)
        {
            _logger.LogInformation(
                "Search index was built by schema v{Old}; discarding and rebuilding at v{New}",
                existingVersion, SchemaVersion);
            await using var drop = conn.CreateCommand();
            drop.CommandText = """
                DROP TABLE IF EXISTS SearchIndex;
                DROP TABLE IF EXISTS IndexState;
                """;
            await drop.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            -- What we tried for each file and how it went. Survives restarts so a half-finished
            -- build resumes instead of starting over.
            CREATE TABLE IF NOT EXISTS IndexState (
                Module      TEXT    NOT NULL,
                FileId      INTEGER NOT NULL,
                StoredPath  TEXT    NOT NULL DEFAULT '',
                SourceMtime TEXT    NOT NULL DEFAULT '',
                FileSize    INTEGER NOT NULL DEFAULT 0,
                Extractor   TEXT,
                Status      TEXT    NOT NULL DEFAULT 'pending',
                CharCount   INTEGER NOT NULL DEFAULT 0,
                Attempts    INTEGER NOT NULL DEFAULT 0,
                Error       TEXT,
                IndexedAt   TEXT,
                PRIMARY KEY (Module, FileId)
            );

            CREATE INDEX IF NOT EXISTS IX_IndexState_Status ON IndexState(Status, Attempts);

            -- The index itself. Module/FileId/Ext ride along UNINDEXED so a hit carries
            -- everything needed to build a link and apply filters without a second lookup.
            -- Keep the UNINDEXED columns first: bm25() takes one weight per column in order,
            -- and BM25_WEIGHTS below depends on that layout.
            -- No stemmer here on purpose. Porter stems the query as well as the content, so a
            -- prefix search goes dead as soon as you type past the stem: "sterilization" indexes
            -- as "steril", making "steril*" match but "sterili*" and "steriliz*" match nothing,
            -- and results vanish mid-word. Same for "insur*" vs "insura*". Morphology is handled
            -- on the query side instead (see SearchQueryParser.Broaden), which only ever widens
            -- a search and so can never produce that dead zone.
            CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex USING fts5(
                Module UNINDEXED,
                FileId UNINDEXED,
                Ext UNINDEXED,
                Title,
                CategoryPath,
                FileName,
                Body,
                tokenize = 'unicode61 remove_diacritics 2',
                prefix = '2 3 4'
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        await using (var stamp = conn.CreateCommand())
        {
            // PRAGMA user_version takes no parameters.
            stamp.CommandText = $"PRAGMA user_version = {SchemaVersion}";
            await stamp.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Search index ready at {Path}", DatabasePath);
    }

    /// <summary>Drop everything and recreate an empty index, for the Settings "Rebuild" action.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await using (var conn = await OpenAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DROP TABLE IF EXISTS SearchIndex;
                DROP TABLE IF EXISTS IndexState;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await EnsureCreatedAsync(ct);

        await using var vacuumConn = await OpenAsync(ct);
        await using var vacuum = vacuumConn.CreateCommand();
        vacuum.CommandText = "VACUUM";
        vacuum.CommandTimeout = 300;
        await vacuum.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Search index reset");
    }
}
