namespace RayTree.Plugins.PostgreSQL.Repository.Schema;

public class SourceTableSchema
{
    public string EntityTypeName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public List<SourceTableColumn> Columns { get; set; } = new();
    public List<SourceTableIndex> Indexes { get; set; } = new();
}
