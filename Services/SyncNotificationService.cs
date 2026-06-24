namespace DecoSOP.Services;

public enum SyncScope { Sop, Doc }

/// <summary>
/// In-process pub/sub that bridges the singleton folder-sync background service to
/// per-circuit Blazor components, so they can invalidate their scoped cache and
/// re-render after a reconcile makes changes.
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
