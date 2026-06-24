using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.Extensions.Options;

namespace DecoSOP.Services;

/// <summary>
/// Singleton background service that keeps the SOP and Document modules in sync with
/// their configured folders. Reconciles on startup, on debounced filesystem events,
/// and on a periodic fallback timer. Notifies connected circuits when anything changes.
/// </summary>
public class FolderSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FolderSyncOptions _opts;
    private readonly SyncNotificationService _notify;
    private readonly ILogger<FolderSyncBackgroundService> _logger;

    private readonly SemaphoreSlim _sopGate = new(1, 1);
    private readonly SemaphoreSlim _docGate = new(1, 1);
    private FileSystemWatcher? _sopWatcher;
    private FileSystemWatcher? _docWatcher;
    private Timer? _sopDebounce;
    private Timer? _docDebounce;

    public FolderSyncBackgroundService(IServiceScopeFactory scopeFactory,
        IOptions<FolderSyncOptions> opts, SyncNotificationService notify,
        ILogger<FolderSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _notify = notify;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled) return;

        var sopRoot = _opts.Sop.Root;
        var docRoot = _opts.Doc.Root;

        // Initial reconcile so a cold start reflects current disk state.
        if (HasRoot(sopRoot)) await RunSopAsync(stoppingToken);
        if (HasRoot(docRoot)) await RunDocAsync(stoppingToken);

        // Debounced watchers.
        if (HasRoot(sopRoot))
        {
            _sopDebounce = new Timer(_ => _ = RunSopAsync(stoppingToken), null, Timeout.Infinite, Timeout.Infinite);
            _sopWatcher = TryCreateWatcher(sopRoot!, () => _sopDebounce!.Change(_opts.DebounceMs, Timeout.Infinite));
        }
        if (HasRoot(docRoot))
        {
            _docDebounce = new Timer(_ => _ = RunDocAsync(stoppingToken), null, Timeout.Infinite, Timeout.Infinite);
            _docWatcher = TryCreateWatcher(docRoot!, () => _docDebounce!.Change(_opts.DebounceMs, Timeout.Infinite));
        }

        // Periodic fallback (covers missed events, batched OneDrive syncs, downtime).
        var period = TimeSpan.FromSeconds(Math.Max(30, _opts.PollIntervalSeconds));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (HasRoot(sopRoot)) await RunSopAsync(stoppingToken);
                if (HasRoot(docRoot)) await RunDocAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool HasRoot(string? root) => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);

    private FileSystemWatcher? TryCreateWatcher(string root, Action onChange)
    {
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler h = (_, _) => onChange();
            RenamedEventHandler rh = (_, _) => onChange();
            w.Created += h; w.Changed += h; w.Deleted += h; w.Renamed += rh;
            w.Error += (_, e) => _logger.LogWarning(e.GetException(), "Folder watcher error on {Root}", root);
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not watch folder {Root}; relying on periodic sync", root);
            return null;
        }
    }

    private Task RunSopAsync(CancellationToken ct) =>
        RunAsync(_sopGate, _opts.Sop.Root, SyncScope.Sop, ct,
            (db, root, c) => FolderReconciler.ReconcileAsync<SopCategory, SopFile>(db, root, nameof(SopCategory), nameof(SopFile), c));

    private Task RunDocAsync(CancellationToken ct) =>
        RunAsync(_docGate, _opts.Doc.Root, SyncScope.Doc, ct,
            (db, root, c) => FolderReconciler.ReconcileAsync<DocumentCategory, OfficeDocument>(db, root, nameof(DocumentCategory), nameof(OfficeDocument), c));

    private async Task RunAsync(SemaphoreSlim gate, string? root, SyncScope scope, CancellationToken ct,
        Func<AppDbContext, string, CancellationToken, Task<FolderReconciler.Result>> reconcile)
    {
        if (!HasRoot(root)) return;
        await gate.WaitAsync(ct);   // serialize runs per module (single SQLite writer)
        try
        {
            using var scopeSvc = _scopeFactory.CreateScope();
            var db = scopeSvc.ServiceProvider.GetRequiredService<AppDbContext>();
            var result = await reconcile(db, root!, ct);
            if (result.Changed)
            {
                _logger.LogInformation("Folder sync ({Scope}): +{Added} ~{Updated} -{Removed}",
                    scope, result.Added, result.Updated, result.Removed);
                await _notify.NotifyAsync(scope);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Folder sync failed for {Scope}", scope);
        }
        finally
        {
            gate.Release();
        }
    }

    public override void Dispose()
    {
        _sopWatcher?.Dispose();
        _docWatcher?.Dispose();
        _sopDebounce?.Dispose();
        _docDebounce?.Dispose();
        base.Dispose();
    }
}
