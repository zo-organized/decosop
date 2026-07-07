using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DocumentCategory> DocumentCategories => Set<DocumentCategory>();
    public DbSet<OfficeDocument> OfficeDocuments => Set<OfficeDocument>();
    public DbSet<SopCategory> SopCategories => Set<SopCategory>();
    public DbSet<SopFile> SopFiles => Set<SopFile>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    // Inventory lives in Azure SQL via InventoryDbContext — not here.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserPreference>(e =>
        {
            e.HasIndex(p => new { p.ClientId, p.EntityType, p.EntityId }).IsUnique();
            e.HasIndex(p => new { p.ClientId, p.EntityType, p.IsFavorited });
        });

        modelBuilder.Entity<DocumentCategory>(e =>
        {
            e.HasIndex(c => new { c.ParentId, c.Name }).IsUnique();
            e.HasMany(c => c.Children)
             .WithOne(c => c.Parent)
             .HasForeignKey(c => c.ParentId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(c => c.Documents)
             .WithOne(d => d.Category)
             .HasForeignKey(d => d.CategoryId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OfficeDocument>(e =>
        {
            e.HasIndex(d => new { d.CategoryId, d.Title }).IsUnique();
        });

        modelBuilder.Entity<SopCategory>(e =>
        {
            e.HasIndex(c => new { c.ParentId, c.Name }).IsUnique();
            e.HasMany(c => c.Children)
             .WithOne(c => c.Parent)
             .HasForeignKey(c => c.ParentId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(c => c.Documents)
             .WithOne(d => d.Category)
             .HasForeignKey(d => d.CategoryId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SopFile>(e =>
        {
            e.HasIndex(d => new { d.CategoryId, d.Title }).IsUnique();
        });

        // Inventory entities are configured in InventoryDbContext (Azure SQL).
    }
}
