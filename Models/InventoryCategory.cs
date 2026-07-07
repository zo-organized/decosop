namespace DecoSOP.Models;

/// <summary>Self-referencing category tree for inventory (Category ▸ Subcategory ▸ Sub-subcategory).</summary>
public class InventoryCategory : ICategoryNode
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int? ParentId { get; set; }
    public InventoryCategory? Parent { get; set; }
    public List<InventoryCategory> Children { get; set; } = [];
    public List<InventoryItem> Items { get; set; } = [];
}
