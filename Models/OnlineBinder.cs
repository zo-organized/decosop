namespace DecoSOP.Models;

/// <summary>
/// Which side of the ONLINE binder a section or page belongs to. Mirrors the LOCAL SOPs/Docs
/// split so the sidebar's SOPS | DOCS toggle means the same thing in both modes.
/// </summary>
public static class OnlineModule
{
    public const string Sop = "sop";
    public const string Doc = "doc";

    public static bool IsValid(string? module) => module is Sop or Doc;

    /// <summary>URL segment used by the ONLINE routes, matching the LOCAL routes (/sops, /documents).</summary>
    public static string RouteSegment(string module) => module == Doc ? "documents" : "sops";
}

/// <summary>A folder in the ONLINE binder. Unlike LOCAL categories, these exist only in the database.</summary>
public class OnlineSection
{
    public int Id { get; set; }
    public string Module { get; set; } = OnlineModule.Sop;
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A page written in the in-app editor. <see cref="Html"/> is always stored sanitized.</summary>
public class OnlinePage
{
    public int Id { get; set; }
    public string Module { get; set; } = OnlineModule.Sop;

    /// <summary>The containing section, or null for a page at the top level of its module.</summary>
    public int? SectionId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    /// <summary>Incremented on every save; an editor that opened an older version gets a conflict warning.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the page is deleted. Deleted pages are hidden but kept, with their history.</summary>
    public DateTime? DeletedAt { get; set; }
}

/// <summary>A page's content as it was before a save, so a bad edit can be undone.</summary>
public class OnlinePageVersion
{
    public int Id { get; set; }
    public int PageId { get; set; }
    public int Version { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; }
}
