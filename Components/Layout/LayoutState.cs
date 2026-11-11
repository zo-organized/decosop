using Microsoft.JSInterop;

namespace DecoSOP.Components.Layout;

/// <summary>
/// Per-browser layout choices: whether the left menu is pinned open, and which list (if any)
/// is showing in the list column. Kept in cookies so they are known when the page is first
/// drawn — no flash of the wrong layout — and survive a reload.
/// </summary>
public sealed class LayoutState
{
    private const string SidebarCookie = "decosop-sidebar";
    private const string ListCookie = "decosop-list";

    private readonly IJSRuntime _js;

    public LayoutState(IHttpContextAccessor http, IJSRuntime js)
    {
        _js = js;
        var cookies = http.HttpContext?.Request.Cookies;
        SidebarPinned = cookies?[SidebarCookie] != "unpinned";
        OpenList = cookies?[ListCookie] is { Length: > 0 and <= 20 } key ? key : null;
    }

    public bool SidebarPinned { get; private set; }

    /// <summary>Key of the list shown in the list column (see UserListService), or null when closed.</summary>
    public string? OpenList { get; private set; }

    public event Action? Changed;

    public async Task SetSidebarPinnedAsync(bool pinned)
    {
        SidebarPinned = pinned;
        Changed?.Invoke();
        await _js.InvokeVoidAsync("decoLayout.setCookie", SidebarCookie, pinned ? "" : "unpinned");
    }

    /// <summary>Show a list in the list column; showing the open one again closes it.</summary>
    public Task ToggleListAsync(string key) => SetOpenListAsync(OpenList == key ? null : key);

    public async Task SetOpenListAsync(string? key)
    {
        OpenList = key;
        Changed?.Invoke();
        await _js.InvokeVoidAsync("decoLayout.setCookie", ListCookie, key ?? "");
    }
}
