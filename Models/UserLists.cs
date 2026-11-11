namespace DecoSOP.Models;

/// <summary>
/// A person in the office. There is no login: people pick their name in the header, and their
/// favorites, pins, folder colours and lists follow them to whichever PC they use. Nothing is
/// private — anyone can pick any name.
/// </summary>
public class AppUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A named list of documents and pages ("To Review", "Training", …) belonging to one owner —
/// a user, or a PC when nobody has picked a name. Favorites is not stored here: it is the
/// long-standing star on <see cref="UserPreference"/>, shown alongside these as a list.
/// </summary>
public class UserList
{
    public int Id { get; set; }

    /// <summary>Same key as <see cref="UserPreference.ClientId"/>: "user:5", or a PC's address.</summary>
    public string OwnerKey { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>True for "To Review", which every owner has and cannot rename or delete.</summary>
    public bool IsBuiltIn { get; set; }

    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<UserListItem> Items { get; set; } = [];
}

public class UserListItem
{
    public int Id { get; set; }
    public int ListId { get; set; }
    public UserList List { get; set; } = null!;

    /// <summary>One of <see cref="ListEntity"/>: what <see cref="EntityId"/> points at.</summary>
    public string EntityType { get; set; } = "";
    public int EntityId { get; set; }

    public DateTime AddedAt { get; set; }
}

/// <summary>
/// The kinds of thing a list (or a favorite) can point at. The LOCAL names match the entity
/// type names <see cref="UserPreference"/> has always used, so favorites need no conversion.
/// </summary>
public static class ListEntity
{
    public const string SopFile = nameof(Models.SopFile);
    public const string OfficeDocument = nameof(Models.OfficeDocument);
    public const string SopCategory = nameof(Models.SopCategory);
    public const string DocumentCategory = nameof(Models.DocumentCategory);
    public const string OnlinePage = nameof(Models.OnlinePage);
}
