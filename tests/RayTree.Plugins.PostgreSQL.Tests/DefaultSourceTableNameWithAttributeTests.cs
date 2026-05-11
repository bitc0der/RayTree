using RayTree.Plugins.PostgreSQL.Repository;

namespace RayTree.Plugins.PostgreSQL.Tests;

public class DefaultSourceTableNameWithAttributeTests
{
    [Test]
    public void Constructor_WithTableAttribute_DerivesNameFromAttribute()
    {
        var options = new PostgreSqlRepositoryOptions { ConnectionString = "Host=localhost" };
        _ = new PostgreSqlRepository<AnnotatedEntity>(options);
        Assert.That(options.TableName, Is.EqualTo("annotated_entity"));
    }
}
