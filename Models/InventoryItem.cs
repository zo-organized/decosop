namespace DecoSOP.Models;

/// <summary>
/// Unified inventory row. One wide table holds both durable assets/equipment and consumable
/// supplies, distinguished by <see cref="Kind"/>; kind-specific columns are nullable and the UI
/// shows/hides them based on the kind.
/// </summary>
public class InventoryItem
{
    public int Id { get; set; }
    public InventoryKind Kind { get; set; } = InventoryKind.Asset;

    // --- Shared (both kinds) ---
    public int CategoryId { get; set; }
    public InventoryCategory Category { get; set; } = null!;
    public int? LocationId { get; set; }
    public InventoryLocation? Location { get; set; }
    public string Title { get; set; } = string.Empty;   // "Item Name"
    public string? Description { get; set; }
    public string? Barcode { get; set; }                // SKU / scanned code
    public string? Lot { get; set; }                    // Lot # (useful to both kinds)
    public int SortOrder { get; set; }
    public string? LastUpdatedBy { get; set; }          // staff name
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // --- Asset-only (nullable) ---
    public string? Identifier { get; set; }             // serial # / ItemID
    public string? ItemType { get; set; }               // e.g. "Air Abrasion Unit"
    public string? Brand { get; set; }                  // Brand / Make
    public string? Model { get; set; }
    public string Status { get; set; } = "Active";      // Active / In Repair / Retired ...
    public DateTime? StatusStartDate { get; set; }
    public DateTime? StatusEndDate { get; set; }
    public decimal? CurrentValue { get; set; }
    public decimal? StartingValue { get; set; }
    public DateTime? DateOfValue { get; set; }          // Value Updated Date
    public string? Manufacturer { get; set; }
    public string? Seller { get; set; }
    public decimal? Price { get; set; }
    public DateTime? PurchaseDate { get; set; }
    public string? WarrantyInfo { get; set; }
    public DateTime? WarrantyExpiration { get; set; }
    public bool SdsOnFile { get; set; }                 // "SDS Y/N" — Safety Data Sheet on file
    public string? Use { get; set; }

    // --- Consumable-only (nullable) ---
    public decimal? QuantityOnHand { get; set; }
    public string? Unit { get; set; }                   // "box", "each", "ml"
    public decimal? ReorderPoint { get; set; }          // low-stock threshold
    public DateTime? ExpirationDate { get; set; }

    public List<InventoryActivity> Activities { get; set; } = [];
}
