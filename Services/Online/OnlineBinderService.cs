using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services.Online;

/// <summary>A page as listed in trees and tables of contents (no content).</summary>
public sealed record OnlinePageInfo(int Id, string Title, int? SectionId, int SortOrder, DateTime UpdatedAt);

/// <summary>A section with its subsections and pages, stitched in memory for display.</summary>
public sealed class OnlineSectionNode
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int? ParentId { get; init; }
    public OnlineSectionNode? Parent { get; set; }
    public List<OnlineSectionNode> Children { get; } = [];
    public List<OnlinePageInfo> Pages { get; } = [];

    public int TotalPages => Pages.Count + Children.Sum(c => c.TotalPages);
}

/// <summary>One module's whole binder: top-level sections plus pages that sit at the top level.</summary>
public sealed record OnlineTree(IReadOnlyList<OnlineSectionNode> Sections, IReadOnlyList<OnlinePageInfo> RootPages)
{
    public int TotalPages => RootPages.Count + Sections.Sum(s => s.TotalPages);

    public OnlineSectionNode? FindSection(int id) => Find(Sections, id);

    /// <summary>Section that holds a page, or null if the page is top-level or unknown.</summary>
    public int? SectionOfPage(int pageId)
        => All(Sections).SelectMany(s => s.Pages).FirstOrDefault(p => p.Id == pageId)?.SectionId;

    /// <summary>Every section as (Id, "A / B / C"), in tree order — for the location picker.</summary>
    public IEnumerable<(int Id, string Path)> Locations()
    {
        foreach (var s in All(Sections)) yield return (s.Id, PathOf(s));
    }

    public static string PathOf(OnlineSectionNode node)
    {
        var parts = new List<string>();
        for (var cur = node; cur is not null; cur = cur.Parent) parts.Insert(0, cur.Name);
        return string.Join(" / ", parts);
    }

    private static OnlineSectionNode? Find(IEnumerable<OnlineSectionNode> nodes, int id)
        => All(nodes).FirstOrDefault(n => n.Id == id);

    private static IEnumerable<OnlineSectionNode> All(IEnumerable<OnlineSectionNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in All(n.Children)) yield return c;
        }
    }
}

public enum OnlineSaveStatus { Saved, Conflict, NotFound }

/// <summary>Outcome of saving a page. On Conflict, <see cref="Current"/> is what someone else saved.</summary>
public sealed record OnlineSaveResult(OnlineSaveStatus Status, OnlinePage? Current);

/// <summary>
/// Reads and writes the ONLINE binder. A singleton over a context factory: every operation gets
/// its own short-lived DbContext, so the sidebar and a page can load at the same time without the
/// single-context serialisation the LOCAL services need (see DataCacheService).
/// </summary>
public sealed class OnlineBinderService(
    IDbContextFactory<OnlineDbContext> dbFactory,
    SyncNotificationService notify)
{
    /// <summary>Older versions kept per page. Enough to undo any realistic run of bad edits.</summary>
    private const int MaxVersionsPerPage = 50;

    // ---------- reading ----------

    public async Task<OnlineTree> GetTreeAsync(string module)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var sections = await db.Sections.AsNoTracking()
            .Where(s => s.Module == module)
            .OrderBy(s => s.Name)
            .ToListAsync();
        var pages = await db.Pages.AsNoTracking()
            .Where(p => p.Module == module && p.DeletedAt == null)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .Select(p => new OnlinePageInfo(p.Id, p.Title, p.SectionId, p.SortOrder, p.UpdatedAt))
            .ToListAsync();

        var nodes = sections.ToDictionary(s => s.Id,
            s => new OnlineSectionNode { Id = s.Id, Name = s.Name, ParentId = s.ParentId });
        var roots = new List<OnlineSectionNode>();
        foreach (var node in nodes.Values.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (node.ParentId is int pid && nodes.TryGetValue(pid, out var parent))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            else roots.Add(node);
        }

        var rootPages = new List<OnlinePageInfo>();
        foreach (var page in pages)
        {
            if (page.SectionId is int sid && nodes.TryGetValue(sid, out var section)) section.Pages.Add(page);
            else rootPages.Add(page);
        }

        return new OnlineTree(roots, rootPages);
    }

    public async Task<OnlinePage?> GetPageAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null);
    }

    public async Task<List<OnlinePageVersion>> GetVersionsAsync(int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // History lists metadata only; content is fetched when a version is actually restored.
        return await db.PageVersions.AsNoTracking()
            .Where(v => v.PageId == pageId)
            .OrderByDescending(v => v.Version)
            .Select(v => new OnlinePageVersion { Id = v.Id, PageId = v.PageId, Version = v.Version, Title = v.Title, SavedAt = v.SavedAt })
            .ToListAsync();
    }

    // ---------- sections ----------

    public async Task<OnlineSection> CreateSectionAsync(string module, string name, int? parentId)
    {
        RequireModule(module);
        name = ValidateName(name, "Folder name");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (parentId is int pid && !await db.Sections.AnyAsync(s => s.Id == pid && s.Module == module))
            throw new InvalidOperationException("The parent folder no longer exists.");
        await EnsureUniqueSectionNameAsync(db, module, parentId, name, exceptId: null);

        var maxSort = await db.Sections.Where(s => s.Module == module && s.ParentId == parentId)
            .MaxAsync(s => (int?)s.SortOrder) ?? -1;
        var section = new OnlineSection { Module = module, Name = name, ParentId = parentId, SortOrder = maxSort + 1 };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        await NotifyAsync(module);
        return section;
    }

    public async Task RenameSectionAsync(int id, string newName)
    {
        newName = ValidateName(newName, "Folder name");
        await using var db = await dbFactory.CreateDbContextAsync();
        var section = await db.Sections.FindAsync(id) ?? throw new InvalidOperationException("That folder no longer exists.");
        if (section.Name == newName) return;
        await EnsureUniqueSectionNameAsync(db, section.Module, section.ParentId, newName, exceptId: id);
        section.Name = newName;
        await db.SaveChangesAsync();
        await NotifyAsync(section.Module);
    }

    /// <summary>
    /// Delete an empty folder. Refuses while it still holds pages or subfolders, so deleting a
    /// folder can never take content with it.
    /// </summary>
    public async Task DeleteSectionAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var section = await db.Sections.FindAsync(id);
        if (section is null) return;

        if (await db.Sections.AnyAsync(s => s.ParentId == id))
            throw new InvalidOperationException("This folder still has subfolders. Move or delete them first.");
        if (await db.Pages.AnyAsync(p => p.SectionId == id && p.DeletedAt == null))
            throw new InvalidOperationException("This folder still has pages. Move or delete them first.");

        // Previously deleted pages still point here; lift them to the top level so they (and
        // their history) survive the folder.
        await db.Pages.Where(p => p.SectionId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.SectionId, (int?)null));

        db.Sections.Remove(section);
        await db.SaveChangesAsync();
        await NotifyAsync(section.Module);
    }

    // ---------- pages ----------

    public async Task<OnlinePage> CreatePageAsync(string module, int? sectionId, string title, string html)
    {
        RequireModule(module);
        title = ValidateName(title, "Title");
        var clean = CleanHtml(html);

        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireSectionAsync(db, module, sectionId);

        var maxSort = await db.Pages.Where(p => p.Module == module && p.SectionId == sectionId)
            .MaxAsync(p => (int?)p.SortOrder) ?? -1;
        var now = DateTime.UtcNow;
        var page = new OnlinePage
        {
            Module = module, SectionId = sectionId, Title = title, Html = clean,
            SortOrder = maxSort + 1, Version = 1, CreatedAt = now, UpdatedAt = now
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        await NotifyAsync(module);
        return page;
    }

    /// <summary>
    /// Save an edit. <paramref name="expectedVersion"/> is the version the editor opened; if
    /// someone else has saved since, nothing is written and the result carries their version —
    /// unless <paramref name="overwrite"/> is set, after the user has chosen to replace it.
    /// The previous content is always kept as a version first.
    /// </summary>
    public async Task<OnlineSaveResult> SavePageAsync(int id, int? sectionId, string title, string html,
        int expectedVersion, bool overwrite = false)
    {
        title = ValidateName(title, "Title");
        var clean = CleanHtml(html);

        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null);
        if (page is null) return new OnlineSaveResult(OnlineSaveStatus.NotFound, null);
        if (page.Version != expectedVersion && !overwrite)
            return new OnlineSaveResult(OnlineSaveStatus.Conflict, page);

        await RequireSectionAsync(db, page.Module, sectionId);

        var contentChanged = page.Title != title || page.Html != clean;
        var moved = page.SectionId != sectionId;
        if (!contentChanged && !moved)
            return new OnlineSaveResult(OnlineSaveStatus.Saved, page);

        if (contentChanged)
        {
            db.PageVersions.Add(new OnlinePageVersion
            {
                PageId = page.Id, Version = page.Version, Title = page.Title, Html = page.Html, SavedAt = page.UpdatedAt
            });
        }

        if (moved)
        {
            page.SortOrder = (await db.Pages.Where(p => p.Module == page.Module && p.SectionId == sectionId)
                .MaxAsync(p => (int?)p.SortOrder) ?? -1) + 1;
            page.SectionId = sectionId;
        }

        page.Title = title;
        page.Html = clean;
        page.Version++;
        page.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await PruneVersionsAsync(db, page.Id);
        await NotifyAsync(page.Module);
        return new OnlineSaveResult(OnlineSaveStatus.Saved, page);
    }

    /// <summary>Put an earlier version back. The content being replaced becomes a version itself.</summary>
    public async Task<OnlineSaveResult> RestoreVersionAsync(int pageId, int versionId)
    {
        OnlinePageVersion? version;
        OnlinePage? page;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            version = await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.PageId == pageId);
            page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId && p.DeletedAt == null);
        }
        if (version is null || page is null) return new OnlineSaveResult(OnlineSaveStatus.NotFound, null);

        return await SavePageAsync(pageId, page.SectionId, version.Title, version.Html, page.Version, overwrite: true);
    }

    /// <summary>Hide a page. It is kept, with its history, rather than destroyed.</summary>
    public async Task DeletePageAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null);
        if (page is null) return;
        page.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await NotifyAsync(page.Module);
    }

    // ---------- helpers ----------

    private static void RequireModule(string module)
    {
        if (!OnlineModule.IsValid(module)) throw new ArgumentException($"Unknown module '{module}'.");
    }

    private static string ValidateName(string? name, string field)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"{field} is required.");
        name = name.Trim();
        if (name.Length > 200) throw new ArgumentException($"{field} must be 200 characters or fewer.");
        return name;
    }

    private static string CleanHtml(string? html)
    {
        if (html is not null && html.Length > OnlineHtml.MaxHtmlChars)
            throw new ArgumentException("This page is too large to save. Try using fewer or smaller pasted images.");
        return OnlineHtml.Sanitize(html);
    }

    private static async Task RequireSectionAsync(OnlineDbContext db, string module, int? sectionId)
    {
        if (sectionId is int sid && !await db.Sections.AnyAsync(s => s.Id == sid && s.Module == module))
            throw new InvalidOperationException("The chosen folder no longer exists. Pick another location.");
    }

    private static async Task EnsureUniqueSectionNameAsync(OnlineDbContext db, string module, int? parentId,
        string name, int? exceptId)
    {
        var siblings = await db.Sections
            .Where(s => s.Module == module && s.ParentId == parentId && s.Id != exceptId)
            .Select(s => s.Name).ToListAsync();
        if (siblings.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A folder named \"{name}\" already exists here.");
    }

    private static async Task PruneVersionsAsync(OnlineDbContext db, int pageId)
    {
        var stale = await db.PageVersions.Where(v => v.PageId == pageId)
            .OrderByDescending(v => v.Version).Skip(MaxVersionsPerPage)
            .Select(v => v.Id).ToListAsync();
        if (stale.Count > 0)
            await db.PageVersions.Where(v => stale.Contains(v.Id)).ExecuteDeleteAsync();
    }

    /// <summary>Tell every open sidebar, and the search indexer, that the binder changed.</summary>
    private Task NotifyAsync(string module)
        => notify.NotifyAsync(module == OnlineModule.Doc ? SyncScope.OnlineDoc : SyncScope.OnlineSop);
}
