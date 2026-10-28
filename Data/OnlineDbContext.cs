using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Data;

/// <summary>
/// The ONLINE binder's database, kept in its own file (decosop-online.db) rather than in
/// decosop.db. Two reasons: the schema is created with EnsureCreated() and there are no
/// migrations, so new tables would never appear in an existing decosop.db — a fresh file gets
/// them; and unlike everything in decosop.db this is primary data that exists nowhere else, so it
/// is exported and preserved on its own terms.
/// </summary>
public class OnlineDbContext : DbContext
{
    public OnlineDbContext(DbContextOptions<OnlineDbContext> options) : base(options) { }

    public DbSet<OnlineSection> Sections => Set<OnlineSection>();
    public DbSet<OnlinePage> Pages => Set<OnlinePage>();
    public DbSet<OnlinePageVersion> PageVersions => Set<OnlinePageVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OnlineSection>(e =>
        {
            e.HasIndex(s => new { s.Module, s.ParentId });
            e.HasOne<OnlineSection>()
             .WithMany()
             .HasForeignKey(s => s.ParentId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OnlinePage>(e =>
        {
            e.HasIndex(p => new { p.Module, p.SectionId });
            e.HasOne<OnlineSection>()
             .WithMany()
             .HasForeignKey(p => p.SectionId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OnlinePageVersion>(e =>
        {
            e.HasIndex(v => new { v.PageId, v.Version });
            e.HasOne<OnlinePage>()
             .WithMany()
             .HasForeignKey(v => v.PageId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
