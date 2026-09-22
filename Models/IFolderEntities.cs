namespace DecoSOP.Models;

/// <summary>Shared shape of a category row across the SOP and Document modules (scalars only).</summary>
public interface ICategoryNode
{
    int Id { get; set; }
    string Name { get; set; }
    int SortOrder { get; set; }
    int? ParentId { get; set; }
}

/// <summary>Shared shape of a file row across the SOP and Document modules (scalars only).</summary>
public interface IFileNode
{
    int Id { get; set; }
    string Title { get; set; }
    string FileName { get; set; }
    string StoredFileName { get; set; }
    string ContentType { get; set; }
    long FileSize { get; set; }
    int CategoryId { get; set; }
    int SortOrder { get; set; }
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}

/// <summary>Category row including its typed navigations, so FolderFileService can serve both modules.</summary>
public interface ICategoryNode<TCat, TFile> : ICategoryNode
    where TCat : ICategoryNode<TCat, TFile>
    where TFile : IFileNode
{
    TCat? Parent { get; set; }
    List<TCat> Children { get; set; }
    List<TFile> Documents { get; set; }
}

/// <summary>File row including its typed category navigation.</summary>
public interface IFileNode<TCat> : IFileNode
{
    TCat Category { get; set; }
}
