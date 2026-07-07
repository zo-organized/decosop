namespace DecoSOP.Models;

/// <summary>A physical location an item lives in — an operatory, sterilization area, storage, front office, etc.</summary>
public class InventoryLocation
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
