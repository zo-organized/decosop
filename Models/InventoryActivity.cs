namespace DecoSOP.Models;

/// <summary>An audit / stock-movement record: who did what to an item and when.</summary>
public class InventoryActivity
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public InventoryItem Item { get; set; } = null!;
    public string StaffName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;  // Created / Edited / Received / Used / Count / CheckedOut
    public decimal? QtyDelta { get; set; }               // +/- for stock moves
    public decimal? QtyAfter { get; set; }               // resulting QuantityOnHand snapshot
    public string? Note { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
