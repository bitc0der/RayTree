namespace RayTree.Plugins.PostgreSQL.Repository;

public class PostgreSqlRepositoryOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public bool AutoMigrate { get; set; }
}
