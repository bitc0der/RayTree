namespace RayTree.Plugins.PostgreSQL.Outbox.Schema;

public class EntityPropertyColumn
{
    public string PropertyName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string ColumnType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
}
