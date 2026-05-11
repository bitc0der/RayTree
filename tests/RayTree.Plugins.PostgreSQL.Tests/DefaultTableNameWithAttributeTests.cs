using RayTree.Plugins.PostgreSQL.Outbox;

namespace RayTree.Plugins.PostgreSQL.Tests;

public class DefaultTableNameWithAttributeTests
{
    [Test]
    public void Constructor_WithTableAttribute_DerivesOutboxNameFromAttribute()
    {
        var options = new PostgreSqlOutboxOptions { ConnectionString = "Host=localhost" };
        _ = new PostgreSqlOutbox<AnnotatedEntity>(options);
        Assert.That(options.OutboxTableName, Is.EqualTo("annotated_entity_outbox"));
    }
}
