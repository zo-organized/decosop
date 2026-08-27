using DecoSOP.Data;
using DecoSOP.Models;
using DecoSOP.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services.Search;

/// <summary>
/// Keeps the full-text index in step with the library, in the background.
///
/// Work is discovered by comparing each file's size and modified time against what the index
/// last saw, so a restart mid-build resumes rather than starting over, and a steady state costs
/// one cheap comparison per file. The folder reconciler already knows when files change, so this
/// listens to it rather than polling the disk itself.
///
/// Deliberately unhurried: one batch at a time with a pause between, because a first build walks
/// a couple of gigabytes of documents and must never make the app feel slow for someone browsing.
/// </summary>
public sealed class SearchIndexBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchDb _searchDb;
    private readonly SearchIndexService _index;
    private readonly TextExtractionService _extraction;
    private readonly SyncNotificationService _syncNotify;
    private readonly ILogger<SearchIndexBackgroundService> _logger;

    /// <summary>Files handed to the extractor at once. Large enough for LibreOffice to amortise startup.</summary>
    private const int BatchSize = 40;

    /// <summary>Breather between batches so indexing never monopolises the machine.</summary>
    private static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(250);

    /// <summary>How many times a failing file is retried before it's left alone.</summary>
    private const int MaxAttempts = 3;

    /// <summary>Fallback sweep, in case a change slips past the reconciler notification.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _rescanRequested;

    public bool IsIndexing { get; private set; }
    public int QueuedTotal { get; private set; }
    public int QueuedDone { get; private set; }
    public DateTime? LastRunUtc { get; private set; }

    public SearchIndexBackgroundService(
        IServiceScopeFactory scopeFactory, SearchDb searchDb, SearchIndexService index,
        TextExtractionService extraction, SyncNotificationService syncNotify,
        ILogger<SearchIndexBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _searchDb = searchDb;
        _index = index;
        _extraction = extraction;
        _syncNotify = syncNotify;
        _logger = logger;
    }

    /// <summary>Ask for a rescan as soon as the current pass finishes (a sync, or a rebuild).</summary>
    public void RequestRescan() => _rescanRequested = true;

    /// <summary>
    /// Discard the index and start again. Takes the same gate the indexing pass holds, so the
    /// tables can never be dropped out from under a pass that is midway through writing to them.
    /// </summary>
    public async Task RebuildAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await _searchDb.ResetAsync(ct);
            _logger.LogInformation("Search index rebuild requested; index cleared");
        }
        finally
        {
            _gate.Release();
        }
        RequestRescan();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _searchDb.EnsureCreatedAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the search index. Search will be unavailable.");
            return;
        }

        // A folder reconcile means files appeared, changed or vanished — exactly our trigger.
        _syncNotify.OnReconciled += OnReconciled;

        // Let the app finish starting before touching a couple of gigabytes of documents.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _rescanRequested = false;
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search index pass failed");
            }

            // Wake early if something asked for a rescan, otherwise sweep periodically.
            var waited = TimeSpan.Zero;
            while (!stoppingToken.IsCancellationRequested && !_rescanRequested && waited < SweepInterval)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { return; }
                waited += TimeSpan.FromSeconds(5);
            }
        }

        _syncNotify.OnReconciled -= OnReconciled;
    }

    private Task OnReconciled(SyncScope scope)
    {
        RequestRescan();
        return Task.CompletedTask;
    }

    /// <summary>Bring the index up to date with the library once.</summary>
    private async Task RunPassAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var work = await DiscoverWorkAsync(ct);
            if (work.ToIndex.Count == 0 && work.ToRemove.Count == 0)
            {
                LastRunUtc = DateTime.UtcNow;
                return;
            }

            _logger.LogInformation("Search index: {Index} file(s) to index, {Remove} to remove",
                work.ToIndex.Count, work.ToRemove.Count);

            IsIndexing = true;
            QueuedTotal = work.ToIndex.Count;
            QueuedDone = 0;

            // Claim the whole queue up front so the Settings panel and the search page banner
            // can show honest progress instead of a permanent 100%.
            await _index.MarkPendingAsync(
                work.ToIndex.Select(f => (f.Module, f.FileId, f.StoredPath)).ToList(), ct);

            foreach (var (module, fileId) in work.ToRemove)
            {
                ct.ThrowIfCancellationRequested();
                await _index.RemoveAsync(module, fileId, ct);
            }

            for (var i = 0; i < work.ToIndex.Count; i += BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = work.ToIndex.GetRange(i, Math.Min(BatchSize, work.ToIndex.Count - i));
                await IndexBatchAsync(batch, ct);
                QueuedDone += batch.Count;
                await Task.Delay(BatchPause, ct);
            }

            LastRunUtc = DateTime.UtcNow;
            _logger.LogInformation("Search index pass complete: {Count} file(s) processed", work.ToIndex.Count);
        }
        finally
        {
            IsIndexing = false;
            _gate.Release();
        }
    }

    private async Task IndexBatchAsync(List<PendingFile> batch, CancellationToken ct)
    {
        var results = await _extraction.ExtractManyAsync(batch.Select(f => f.FullPath).ToList(), ct);

        foreach (var file in batch)
        {
            ct.ThrowIfCancellationRequested();

            if (!results.TryGetValue(file.FullPath, out var result))
                result = ExtractionResult.Failed("dispatcher", "No result returned for this file");

            var status = result.Status switch
            {
                ExtractionStatus.Ok => "ok",
                ExtractionStatus.Empty => "empty",
                ExtractionStatus.Skipped => "skipped",
                _ => "failed",
            };

            // Title, folder and filename are always indexed, even when the body couldn't be read —
            // a scanned chart should still be findable by its name.
            await _index.UpsertAsync(
                new SearchDocument(file.Module, file.FileId, file.Title, file.CategoryPath,
                    file.FileName, result.Text),
                result.Extractor, status, result.Error, ct);

            await _index.SetStateAsync(file.Module, file.FileId, file.StoredPath,
                file.MtimeUtc, file.Size, result.Extractor, status, result.Text.Length,
                result.Error, incrementAttempts: result.Status == ExtractionStatus.Failed, ct);
        }
    }

    // ---------- work discovery ----------

    private sealed record PendingFile(
        string Module, int FileId, string Title, string CategoryPath, string FileName,
        string StoredPath, string FullPath, DateTime MtimeUtc, long Size);

    private sealed record WorkSet(List<PendingFile> ToIndex, List<(string Module, int FileId)> ToRemove);

    private async Task<WorkSet> DiscoverWorkAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var state = await _index.GetAllStateAsync(ct);
        var toIndex = new List<PendingFile>();
        var live = new HashSet<(string, int)>();

        await CollectAsync<SopCategory, SopFile>(db, SearchDb.ModuleSop,
            SopFileService.GetUploadDirectory(), state, toIndex, live, ct);
        await CollectAsync<DocumentCategory, OfficeDocument>(db, SearchDb.ModuleDoc,
            DocumentService.GetUploadDirectory(), state, toIndex, live, ct);

        // Anything the index still holds but the library no longer lists has been deleted.
        var toRemove = state.Keys.Where(k => !live.Contains(k)).ToList();

        return new WorkSet(toIndex, toRemove);
    }

    private static async Task CollectAsync<TCat, TFile>(
        AppDbContext db, string module, string uploadRoot,
        Dictionary<(string Module, int FileId), (string Mtime, long Size, string Status, int Attempts)> state,
        List<PendingFile> toIndex, HashSet<(string, int)> live, CancellationToken ct)
        where TCat : class, ICategoryNode
        where TFile : class, IFileNode
    {
        var categories = await db.Set<TCat>().AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.ParentId }).ToListAsync(ct);
        var byId = categories.ToDictionary(c => c.Id);

        string PathFor(int categoryId)
        {
            var parts = new List<string>();
            var current = byId.GetValueOrDefault(categoryId);
            var guard = 0;
            while (current is not null && guard++ < 32)
            {
                parts.Insert(0, current.Name);
                current = current.ParentId is int pid ? byId.GetValueOrDefault(pid) : null;
            }
            return string.Join(" / ", parts);
        }

        var files = await db.Set<TFile>().AsNoTracking()
            .Select(f => new { f.Id, f.Title, f.FileName, f.StoredFileName, f.CategoryId })
            .ToListAsync(ct);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(uploadRoot, f.StoredFileName);
            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
            }
            catch
            {
                // Could not even stat it — a share hiccup or a permissions blip. Treat as
                // transient: keep it live so whatever is already indexed survives this pass.
                live.Add((module, f.Id));
                continue;
            }

            if (!info.Exists)
            {
                // The row is still here but its file is gone. Normally the folder reconciler
                // deletes the row and this never arises, but with folder sync switched off — or
                // in the window between a deletion and the next reconcile — leaving it indexed
                // means search happily returns a result that opens onto nothing. Leaving it out
                // of `live` lets the caller drop it from the index.
                continue;
            }

            live.Add((module, f.Id));

            var mtime = info.LastWriteTimeUtc;
            var size = info.Length;

            if (state.TryGetValue((module, f.Id), out var known))
            {
                var unchanged = known.Mtime == mtime.ToString("O") && known.Size == size;
                // Leave a file alone if it is current, or if it already used up its retry
                // allowance and hasn't changed since — retrying a corrupt file forever helps
                // nobody. Editing the file resets that, because the fingerprint changes.
                // 'pending' means a previous pass claimed this file and was interrupted before
                // finishing it. Without this clause an unchanged file could sit pending forever,
                // never indexed and permanently inflating the outstanding count.
                var stillOwed = known.Status == "pending";
                if (unchanged && !stillOwed && (known.Status != "failed" || known.Attempts >= MaxAttempts)) continue;
            }

            toIndex.Add(new PendingFile(
                Module: module,
                FileId: f.Id,
                Title: f.Title,
                CategoryPath: PathFor(f.CategoryId),
                FileName: f.FileName,
                StoredPath: f.StoredFileName,
                FullPath: fullPath,
                MtimeUtc: mtime,
                Size: size));
        }
    }
}
