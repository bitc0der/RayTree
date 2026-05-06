namespace RayTree.Plugins.PostgreSQL.Repository.Schema;

public class SourceTableColumn
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsPrimaryKey { get; set; }
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public string? Default { get; set; }
    public int? MaxLength { get; set; }
}
