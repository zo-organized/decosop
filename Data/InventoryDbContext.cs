using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Data;

/// <summary>
/// The inventory module's database — hosted in Azure SQL (separate from the local SQLite
/// AppDbContext that backs SOPs/Docs/preferences). The model is provider-neutral: explicit
/// string lengths, decimal precision, UTC datetimes set in code, and portable cascades.
/// </summary>
public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

    public DbSet<InventoryCategory> InventoryCategories => Set<InventoryCategory>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryLocation> InventoryLocations => Set<InventoryLocation>();
    public DbSet<InventoryStaff> InventoryStaff => Set<InventoryStaff>();
    public DbSet<InventoryActivity> InventoryActivities => Set<InventoryActivity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryCategory>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.HasIndex(c => new { c.ParentId, c.Name }).IsUnique();
            e.HasMany(c => c.Children)
             .WithOne(c => c.Parent)
             .HasForeignKey(c => c.ParentId)
             .OnDelete(DeleteBehavior.Restrict);
            // Restrict (not Cascade): removing a category must never silently delete its inventory,
            // and it keeps a single cascade path (avoids SQL Server multiple-cascade-path errors).
            e.HasMany(c => c.Items)
             .WithOne(i => i.Category)
             .HasForeignKey(i => i.CategoryId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventoryLocation>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<InventoryStaff>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<InventoryItem>(e =>
        {
            e.Property(i => i.Title).HasMaxLength(200);
            e.Property(i => i.Description).HasMaxLength(1000);
            e.Property(i => i.Barcode).HasMaxLength(100);
            e.Property(i => i.Lot).HasMaxLength(100);
            e.Property(i => i.LastUpdatedBy).HasMaxLength(120);
            e.Property(i => i.Identifier).HasMaxLength(100);
            e.Property(i => i.ItemType).HasMaxLength(100);
            e.Property(i => i.Brand).HasMaxLength(100);
            e.Property(i => i.Model).HasMaxLength(100);
            e.Property(i => i.Status).HasMaxLength(40);
            e.Property(i => i.Manufacturer).HasMaxLength(100);
            e.Property(i => i.Seller).HasMaxLength(100);
            e.Property(i => i.WarrantyInfo).HasMaxLength(1000);
            e.Property(i => i.Use).HasMaxLength(1000);
            e.Property(i => i.Unit).HasMaxLength(100);
            e.Property(i => i.CurrentValue).HasPrecision(18, 2);
            e.Property(i => i.StartingValue).HasPrecision(18, 2);
            e.Property(i => i.Price).HasPrecision(18, 2);
            e.Property(i => i.QuantityOnHand).HasPrecision(18, 3);
            e.Property(i => i.ReorderPoint).HasPrecision(18, 3);

            e.HasIndex(i => i.Barcode);
            e.HasIndex(i => new { i.Kind, i.CategoryId });
            e.HasOne(i => i.Location)
             .WithMany()
             .HasForeignKey(i => i.LocationId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(i => i.Activities)
             .WithOne(a => a.Item)
             .HasForeignKey(a => a.ItemId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InventoryActivity>(e =>
        {
            e.Property(a => a.StaffName).HasMaxLength(120);
            e.Property(a => a.Action).HasMaxLength(40);
            e.Property(a => a.Note).HasMaxLength(1000);
            e.Property(a => a.QtyDelta).HasPrecision(18, 3);
            e.Property(a => a.QtyAfter).HasPrecision(18, 3);
            e.HasIndex(a => a.ItemId);
        });
    }
}
