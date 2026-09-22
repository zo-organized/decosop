namespace DecoSOP.Services;

public enum CategoryType { Sop, Document }
public enum ItemKind { Category, Document }

/// <summary>
/// Scoped state service coordinating the right-click context menu
/// between trigger sites (sidebar, card views) and the menu component.
/// </summary>
public class ContextMenuState
{
    public bool IsVisible { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public int ItemId { get; private set; }
    public string ItemName { get; private set; } = "";
    public bool IsFavorited { get; private set; }
    public bool IsPinned { get; private set; }
    public string? Color { get; private set; }
    public CategoryType Type { get; private set; }
    public ItemKind Kind { get; private set; }

    // Backward-compat aliases
    public int CategoryId => ItemId;
    public string CategoryName => ItemName;

    /// <summary>Fires when menu visibility changes (show/hide).</summary>
    public event Action? OnChange;

    /// <summary>Fires after a category is modified (renamed, deleted, favorited). Subscribers refresh their data.</summary>
    public event Func<Task>? OnCategoryModified;

    public void Show(double x, double y, int id, string name,
                     bool isFavorited, bool isPinned, string? color,
                     CategoryType type, ItemKind kind = ItemKind.Category)
    {
        X = x;
        Y = y;
        ItemId = id;
        ItemName = name;
        IsFavorited = isFavorited;
        IsPinned = isPinned;
        Color = color;
        Type = type;
        Kind = kind;
        IsVisible = true;
        OnChange?.Invoke();
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        OnChange?.Invoke();
    }

    public async Task NotifyCategoryModified()
    {
        if (OnCategoryModified is not null)
            await OnCategoryModified.Invoke();
    }
}
