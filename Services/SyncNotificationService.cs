namespace DecoSOP.Services;

/// <summary>What changed: a LOCAL module (folder reconcile) or an ONLINE binder module (an edit).</summary>
public enum SyncScope { Sop, Doc, OnlineSop, OnlineDoc }

/// <summary>
/// In-process pub/sub that bridges singleton writers — the folder-sync background service and
/// the ONLINE binder — to per-circuit Blazor components, so they can invalidate their scoped
/// cache and re-render, and to the search indexer, so it picks the change up.
/// </summary>
public class SyncNotificationService
{
    public event Func<SyncScope, Task>? OnReconciled;

    public async Task NotifyAsync(SyncScope scope)
    {
        var handler = OnReconciled;
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList().Cast<Func<SyncScope, Task>>())
        {
            // A disposed/dead circuit's handler must not break the others.
            try { await d(scope); } catch { }
        }
    }
}
