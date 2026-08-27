using System.Net;
using System.Text;
using DecoSOP.Data;
using Microsoft.Data.Sqlite;

namespace DecoSOP.Services.Search;

/// <summary>One file's searchable content, as written into the index.</summary>
public sealed record SearchDocument(
    string Module, int FileId, string Title, string CategoryPath,
    string FileName, string Body);

/// <summary>A single search result.</summary>
public sealed record SearchHit(
    string Module, int FileId, string Title, string CategoryPath,
    string FileName, string Extension, string SnippetHtml, double Rank)
{
    /// <summary>Where clicking this result should go.</summary>
    public string Url => Module == SearchDb.ModuleDoc ? $"/documents/{FileId}" : $"/sops/{FileId}";
}

public sealed record SearchResults(
    IReadOnlyList<SearchHit> Hits, int TotalCount, bool IsBroadened,
    IReadOnlyDictionary<string, int> CountsByModule,
    IReadOnlyDictionary<string, int> CountsByExtension);

/// <summary>How complete the index currently is, for the Settings panel.</summary>
public sealed record IndexStats(
    int Indexed, int Empty, int Failed, int Skipped, int Pending,
    long TotalChars, DateTime? LastIndexedUtc)
{
    public int Total => Indexed + Empty + Failed + Skipped + Pending;
    public int Done => Indexed + Empty + Failed + Skipped;
    public double PercentComplete => Total == 0 ? 100 : Done * 100.0 / Total;
}

/// <summary>Reads from and writes to the full-text index.</summary>
public sealed class SearchIndexService
{
    private readonly SearchDb _db;
    private readonly ILogger<SearchIndexService> _logger;

    /// <summary>
    /// One weight per FTS5 column, in declaration order. The first three are UNINDEXED and
    /// contribute nothing. A title match should outrank a passing mention buried in a
    /// spreadsheet, so Title is weighted an order of magnitude above Body.
    ///
    /// FolderPath was originally 4.0 and is now 2.0: a folder name matching a query word was
    /// beating documents that were genuinely about it — searching "fee schedule" surfaced
    /// everything under the Scheduling folder, and "collections" everything under a folder
    /// whose name happened to contain it.
    /// </summary>
    private const string Bm25Weights = "0.0, 0.0, 0.0, 10.0, 2.0, 3.0, 1.0";

    /// <summary>
    /// Superseded copies are pushed below their live equivalents. This library keeps retired
    /// documents in place, marked by folder or filename ("zzz ARCHIVE", "not in use", "old-"),
    /// and without this the archived copy of a procedure routinely outranked the live one —
    /// a search for "root canal setup" returned three archived copies at ranks 1-3 with the
    /// identical live documents directly beneath them.
    ///
    /// A penalty rather than a filter: bm25 is negative and lower is better, so adding a
    /// constant demotes these without hiding them. Someone deliberately looking for an
    /// archived version still finds it.
    /// </summary>
    private const string SupersededPenalty = """
        (CASE WHEN CategoryPath LIKE '%archive%' OR CategoryPath LIKE '%zzz%'
                OR CategoryPath LIKE '%not in use%' OR Title LIKE '%not in use%'
                OR Title LIKE 'zzz%' OR Title LIKE 'old-%'
              THEN 4.0 ELSE 0.0 END)
        """;

    /// <summary>Zero-based index of Body among the FTS5 columns, for snippet().</summary>
    private const int BodyColumn = 6;

    // Non-printing sentinels stand in for the highlight markers while the snippet is still
    // untrusted text. They are stripped by HtmlEncode's caller only after encoding, so document
    // content can never inject markup into the results page.
    private const string MarkOpen = "\u0002";
    private const string MarkClose = "\u0003";

    public SearchIndexService(SearchDb db, ILogger<SearchIndexService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ---------- writing ----------

    /// <summary>Replace a file's indexed content, and record how the extraction went.</summary>
    public async Task UpsertAsync(SearchDocument doc, string extractor, string status,
        string? error, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rowId = SearchDb.RowIdFor(doc.Module, doc.FileId);

        await using (var del = conn.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM SearchIndex WHERE rowid = @rowid";
            del.Parameters.AddWithValue("@rowid", rowId);
            await del.ExecuteNonQueryAsync(ct);
        }

        // Only content-bearing files earn an index row; a failed or empty file still gets its
        // state recorded so it isn't retried forever.
        if (!string.IsNullOrEmpty(doc.Body) || !string.IsNullOrWhiteSpace(doc.Title))
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = (SqliteTransaction)tx;
            ins.CommandText = """
                INSERT INTO SearchIndex (rowid, Module, FileId, Ext, Title, CategoryPath, FileName, Body)
                VALUES (@rowid, @module, @fileId, @ext, @title, @path, @file, @body)
                """;
            ins.Parameters.AddWithValue("@rowid", rowId);
            ins.Parameters.AddWithValue("@module", doc.Module);
            ins.Parameters.AddWithValue("@fileId", doc.FileId);
            ins.Parameters.AddWithValue("@ext", Path.GetExtension(doc.FileName).TrimStart('.').ToLowerInvariant());
            ins.Parameters.AddWithValue("@title", doc.Title);
            ins.Parameters.AddWithValue("@path", doc.CategoryPath);
            ins.Parameters.AddWithValue("@file", doc.FileName);
            ins.Parameters.AddWithValue("@body", doc.Body);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Record the outcome of an extraction attempt against a file.</summary>
    public async Task SetStateAsync(string module, int fileId, string storedPath,
        DateTime sourceMtimeUtc, long fileSize, string? extractor, string status,
        int charCount, string? error, bool incrementAttempts, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO IndexState
                (Module, FileId, StoredPath, SourceMtime, FileSize, Extractor, Status, CharCount, Attempts, Error, IndexedAt)
            VALUES
                (@module, @fileId, @path, @mtime, @size, @extractor, @status, @chars, @attempts, @error, @now)
            ON CONFLICT(Module, FileId) DO UPDATE SET
                StoredPath  = excluded.StoredPath,
                SourceMtime = excluded.SourceMtime,
                FileSize    = excluded.FileSize,
                Extractor   = excluded.Extractor,
                Status      = excluded.Status,
                CharCount   = excluded.CharCount,
                Attempts    = CASE WHEN @incrementAttempts THEN IndexState.Attempts + 1 ELSE 0 END,
                Error       = excluded.Error,
                IndexedAt   = excluded.IndexedAt
            """;
        cmd.Parameters.AddWithValue("@module", module);
        cmd.Parameters.AddWithValue("@fileId", fileId);
        cmd.Parameters.AddWithValue("@path", storedPath);
        cmd.Parameters.AddWithValue("@mtime", sourceMtimeUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@size", fileSize);
        cmd.Parameters.AddWithValue("@extractor", (object?)extractor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@chars", charCount);
        cmd.Parameters.AddWithValue("@attempts", incrementAttempts ? 1 : 0);
        cmd.Parameters.AddWithValue("@incrementAttempts", incrementAttempts);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Record files as queued before any work starts, so progress has a real denominator.
    /// Without this nothing ever holds Status='pending', Total always equals Done, and the
    /// completion figure reads 100% throughout a build — including on a first run where it is
    /// the one number anyone cares about.
    /// </summary>
    public async Task MarkPendingAsync(
        IReadOnlyList<(string Module, int FileId, string StoredPath)> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO IndexState (Module, FileId, StoredPath, Status)
            VALUES (@module, @fileId, @path, 'pending')
            ON CONFLICT(Module, FileId) DO UPDATE SET Status = 'pending'
            """;
        var pModule = cmd.Parameters.Add("@module", SqliteType.Text);
        var pFileId = cmd.Parameters.Add("@fileId", SqliteType.Integer);
        var pPath = cmd.Parameters.Add("@path", SqliteType.Text);

        foreach (var (module, fileId, path) in files)
        {
            ct.ThrowIfCancellationRequested();
            pModule.Value = module;
            pFileId.Value = fileId;
            pPath.Value = path;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Drop a file from the index entirely — it was deleted from the library.</summary>
    public async Task RemoveAsync(string module, int fileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM SearchIndex WHERE rowid = @rowid;
            DELETE FROM IndexState WHERE Module = @module AND FileId = @fileId;
            """;
        cmd.Parameters.AddWithValue("@rowid", SearchDb.RowIdFor(module, fileId));
        cmd.Parameters.AddWithValue("@module", module);
        cmd.Parameters.AddWithValue("@fileId", fileId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every file the index currently knows about, with the fingerprint used for staleness.</summary>
    public async Task<Dictionary<(string Module, int FileId), (string Mtime, long Size, string Status, int Attempts)>>
        GetAllStateAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<(string, int), (string, long, string, int)>();

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Module, FileId, SourceMtime, FileSize, Status, Attempts FROM IndexState";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[(reader.GetString(0), reader.GetInt32(1))] =
                (reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt32(5));
        }
        return map;
    }

    // ---------- reading ----------

    /// <summary>
    /// Run a search. Tries all-terms first and falls back to any-term when that finds nothing,
    /// so a query with one unusual word returns something useful instead of an empty page.
    /// </summary>
    public async Task<SearchResults> SearchAsync(string rawQuery, string? moduleFilter = null,
        string? extensionFilter = null, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        var parsed = SearchQueryParser.Parse(rawQuery);
        if (parsed.IsEmpty)
            return new SearchResults([], 0, false, new Dictionary<string, int>(), new Dictionary<string, int>());

        var results = await RunAsync(parsed.MatchAll, moduleFilter, extensionFilter, skip, take, false, ct);
        if (results.TotalCount == 0 && parsed.Terms.Count > 1)
            results = await RunAsync(parsed.MatchAny, moduleFilter, extensionFilter, skip, take, true, ct);

        return results;
    }

    private async Task<SearchResults> RunAsync(string match, string? moduleFilter,
        string? extensionFilter, int skip, int take, bool broadened, CancellationToken ct)
    {
        var filter = new StringBuilder();
        if (!string.IsNullOrEmpty(moduleFilter)) filter.Append(" AND Module = @module");
        if (!string.IsNullOrEmpty(extensionFilter)) filter.Append(" AND Ext = @ext");

        void Bind(SqliteCommand cmd)
        {
            cmd.Parameters.AddWithValue("@match", match);
            if (!string.IsNullOrEmpty(moduleFilter)) cmd.Parameters.AddWithValue("@module", moduleFilter);
            if (!string.IsNullOrEmpty(extensionFilter)) cmd.Parameters.AddWithValue("@ext", extensionFilter);
        }

        await using var conn = await _db.OpenAsync(ct);

        var hits = new List<SearchHit>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT Module, FileId, Ext, Title, CategoryPath, FileName,
                       snippet(SearchIndex, {BodyColumn}, char(2), char(3), '…', 18) AS Snip,
                       bm25(SearchIndex, {Bm25Weights}) + {SupersededPenalty} AS Rank
                FROM SearchIndex
                WHERE SearchIndex MATCH @match{filter}
                ORDER BY Rank
                LIMIT @take OFFSET @skip
                """;
            Bind(cmd);
            cmd.Parameters.AddWithValue("@take", take);
            cmd.Parameters.AddWithValue("@skip", skip);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                hits.Add(new SearchHit(
                    Module: reader.GetString(0),
                    FileId: reader.GetInt32(1),
                    Extension: reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Title: reader.GetString(3),
                    CategoryPath: reader.GetString(4),
                    FileName: reader.GetString(5),
                    SnippetHtml: ToSafeHighlightedHtml(reader.IsDBNull(6) ? "" : reader.GetString(6)),
                    Rank: reader.GetDouble(7)));
            }
        }

        var total = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT count(*) FROM SearchIndex WHERE SearchIndex MATCH @match{filter}";
            Bind(cmd);
            total = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        var byModule = await FacetAsync(conn, "Module", match, moduleFilter, extensionFilter, ct);
        var byExt = await FacetAsync(conn, "Ext", match, moduleFilter, null, ct);

        return new SearchResults(hits, total, broadened, byModule, byExt);
    }

    private static async Task<Dictionary<string, int>> FacetAsync(SqliteConnection conn, string column,
        string match, string? moduleFilter, string? extensionFilter, CancellationToken ct)
    {
        var filter = new StringBuilder();
        // A facet never constrains on its own column, or every bucket but the selected one reads zero.
        if (!string.IsNullOrEmpty(moduleFilter) && column != "Module") filter.Append(" AND Module = @module");
        if (!string.IsNullOrEmpty(extensionFilter) && column != "Ext") filter.Append(" AND Ext = @ext");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {column}, count(*) FROM SearchIndex
            WHERE SearchIndex MATCH @match{filter}
            GROUP BY {column} ORDER BY count(*) DESC
            """;
        cmd.Parameters.AddWithValue("@match", match);
        if (!string.IsNullOrEmpty(moduleFilter) && column != "Module") cmd.Parameters.AddWithValue("@module", moduleFilter);
        if (!string.IsNullOrEmpty(extensionFilter) && column != "Ext") cmd.Parameters.AddWithValue("@ext", extensionFilter);

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(0)) map[reader.GetString(0)] = reader.GetInt32(1);
        return map;
    }

    /// <summary>
    /// Encode a snippet for display. The text comes straight out of user documents, so it is
    /// HTML-encoded in full first; only then do the non-printing sentinels FTS5 inserted become
    /// real mark tags. Encoding after inserting tags would let document content inject markup.
    /// </summary>
    private static string ToSafeHighlightedHtml(string snippet)
        => WebUtility.HtmlEncode(snippet)
            .Replace(MarkOpen, "<mark>")
            .Replace(MarkClose, "</mark>");

    public async Task<IndexStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              sum(CASE WHEN Status = 'ok'      THEN 1 ELSE 0 END),
              sum(CASE WHEN Status = 'empty'   THEN 1 ELSE 0 END),
              sum(CASE WHEN Status = 'failed'  THEN 1 ELSE 0 END),
              sum(CASE WHEN Status = 'skipped' THEN 1 ELSE 0 END),
              sum(CASE WHEN Status = 'pending' THEN 1 ELSE 0 END),
              sum(CharCount),
              max(IndexedAt)
            FROM IndexState
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new IndexStats(0, 0, 0, 0, 0, 0, null);

        int Get(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
        DateTime? last = reader.IsDBNull(6) ? null
            : DateTime.TryParse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

        return new IndexStats(Get(0), Get(1), Get(2), Get(3), Get(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt64(5), last);
    }

    /// <summary>Files that failed, for the Settings panel's troubleshooting list.</summary>
    public async Task<IReadOnlyList<(string Module, int FileId, string Path, string Error)>>
        GetFailuresAsync(int limit = 100, CancellationToken ct = default)
    {
        var list = new List<(string, int, string, string)>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Module, FileId, StoredPath, COALESCE(Error, '')
            FROM IndexState WHERE Status = 'failed'
            ORDER BY StoredPath LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        return list;
    }
}
