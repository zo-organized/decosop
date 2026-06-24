namespace DecoSOP.Models;

public class DocumentCategory : ICategoryNode
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int? ParentId { get; set; }
    public DocumentCategory? Parent { get; set; }
    public List<DocumentCategory> Children { get; set; } = [];
    public List<OfficeDocument> Documents { get; set; } = [];
}
