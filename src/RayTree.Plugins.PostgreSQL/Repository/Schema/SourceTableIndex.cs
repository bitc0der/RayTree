namespace RayTree.Plugins.PostgreSQL.Repository.Schema;

public class SourceTableIndex
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public bool IsUnique { get; set; }
    public string? Where { get; set; }
}
