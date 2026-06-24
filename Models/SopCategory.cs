namespace DecoSOP.Models;

public class SopCategory : ICategoryNode
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int? ParentId { get; set; }
    public SopCategory? Parent { get; set; }
    public List<SopCategory> Children { get; set; } = [];
    public List<SopFile> Documents { get; set; } = [];
}
