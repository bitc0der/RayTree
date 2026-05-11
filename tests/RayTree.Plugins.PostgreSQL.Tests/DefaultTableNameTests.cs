using RayTree.Plugins.PostgreSQL.Outbox;

namespace RayTree.Plugins.PostgreSQL.Tests;

public class DefaultTableNameTests
{
    [Test]
    public void Constructor_WithNoTableName_DerivesSnakeCasePlusOutbox()
    {
        var options = new PostgreSqlOutboxOptions { ConnectionString = "Host=localhost" };
        _ = new PostgreSqlOutbox<TestEntity>(options);
        Assert.That(options.OutboxTableName, Is.EqualTo("test_entity_outbox"));
    }

    [Test]
    public void Constructor_WithExplicitTableName_KeepsIt()
    {
        var options = new PostgreSqlOutboxOptions
        {
            ConnectionString = "Host=localhost",
            OutboxTableName = "my_custom_table"
        };
        _ = new PostgreSqlOutbox<TestEntity>(options);
        Assert.That(options.OutboxTableName, Is.EqualTo("my_custom_table"));
    }
}
