namespace DecoSOP.Models;

/// <summary>A staff member name for "pick-your-name" accountability — no passwords, just a dropdown selection.</summary>
public class InventoryStaff
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
