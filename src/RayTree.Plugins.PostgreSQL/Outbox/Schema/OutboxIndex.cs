namespace RayTree.Plugins.PostgreSQL.Outbox.Schema;

public class OutboxIndex
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public string? Where { get; set; }
}
