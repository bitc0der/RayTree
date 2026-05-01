namespace RayTree.Plugins.PostgreSQL;

public class PostgreSqlRepositoryOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
}
