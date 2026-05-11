using RayTree.Plugins.PostgreSQL.Repository;

namespace RayTree.Plugins.PostgreSQL.Tests;

public class DefaultSourceTableNameTests
{
    [Test]
    public void Constructor_WithNoTableName_DerivesSnakeCaseFromEntityType()
    {
        var options = new PostgreSqlRepositoryOptions { ConnectionString = "Host=localhost" };
        _ = new PostgreSqlRepository<TestEntity>(options);
        Assert.That(options.TableName, Is.EqualTo("test_entity"));
    }

    [Test]
    public void Constructor_WithExplicitTableName_KeepsIt()
    {
        var options = new PostgreSqlRepositoryOptions
        {
            ConnectionString = "Host=localhost",
            TableName = "my_custom_table"
        };
        _ = new PostgreSqlRepository<TestEntity>(options);
        Assert.That(options.TableName, Is.EqualTo("my_custom_table"));
    }
}
