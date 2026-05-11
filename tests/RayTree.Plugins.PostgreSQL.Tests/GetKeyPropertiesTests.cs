using System.ComponentModel.DataAnnotations;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Repository;

namespace RayTree.Plugins.PostgreSQL.Tests;

// Composite key with no [Column(Order)] — order must follow declaration order.
public class CompositeKeyNoOrderEntity
{
    [Key] public int TenantId { get; set; }
    [Key] public int UserId { get; set; }
    public string? Name { get; set; }
}

public class GetKeyPropertiesTests
{
    [Test]
    public void GetKeyProperties_WithKeyAttribute_ReturnsThatProperty()
    {
        var keys = EntityColumnMapper.GetKeyProperties(typeof(KeyAnnotatedEntity));
        Assert.That(keys, Has.Count.EqualTo(1));
        Assert.That(keys[0].Name, Is.EqualTo("OrderId"));
    }

    [Test]
    public void GetKeyProperties_WithoutKeyAttribute_FallsBackToIdConvention()
    {
        var keys = EntityColumnMapper.GetKeyProperties(typeof(TestEntity));
        Assert.That(keys, Has.Count.EqualTo(1));
        Assert.That(keys[0].Name, Is.EqualTo("Id"));
    }

    [Test]
    public void GetKeyProperties_WithCompositeKey_ReturnsAllInColumnOrder()
    {
        var keys = EntityColumnMapper.GetKeyProperties(typeof(CompositeKeyEntity));
        Assert.That(keys, Has.Count.EqualTo(2));
        Assert.That(keys[0].Name, Is.EqualTo("OrderId"));
        Assert.That(keys[1].Name, Is.EqualTo("LineNumber"));
    }

    [Test]
    public void GetKeyProperties_WithNoKeyAndNoId_Throws()
    {
        Assert.That(
            () => EntityColumnMapper.GetKeyProperties(typeof(NoKeyEntity)),
            Throws.InvalidOperationException.With.Message.Contains("NoKeyEntity"));
    }

    [Test]
    public void Constructor_WithKeyAttribute_ResolvesKeyAtStartup()
    {
        var options = new PostgreSqlRepositoryOptions { ConnectionString = "Host=localhost" };
        Assert.That(() => new PostgreSqlRepository<KeyAnnotatedEntity>(options), Throws.Nothing);
    }

    [Test]
    public void Constructor_WithCompositeKey_ResolvesKeyAtStartup()
    {
        var options = new PostgreSqlRepositoryOptions { ConnectionString = "Host=localhost" };
        Assert.That(() => new PostgreSqlRepository<CompositeKeyEntity>(options), Throws.Nothing);
    }

    [Test]
    public void Constructor_WithNoKeyAndNoId_ThrowsAtStartup()
    {
        var options = new PostgreSqlRepositoryOptions { ConnectionString = "Host=localhost" };
        Assert.That(
            () => new PostgreSqlRepository<NoKeyEntity>(options),
            Throws.InvalidOperationException.With.Message.Contains("NoKeyEntity"));
    }

    [Test]
    public void GetByIdAsync_WrongKeyValueCount_Throws()
    {
        var options = new PostgreSqlRepositoryOptions { ConnectionString = "Host=localhost" };
        var repo = new PostgreSqlRepository<CompositeKeyEntity>(options);
        Assert.That(
            async () => await repo.GetByIdAsync([1]),
            Throws.ArgumentException.With.Message.Contains("Expected 2"));
    }

    [Test]
    public void GetKeyProperties_CompositeKeyWithoutColumnOrder_FallsBackToDeclarationOrder()
    {
        var keys = EntityColumnMapper.GetKeyProperties(typeof(CompositeKeyNoOrderEntity));
        Assert.That(keys, Has.Count.EqualTo(2));
        Assert.That(keys[0].Name, Is.EqualTo("TenantId"));
        Assert.That(keys[1].Name, Is.EqualTo("UserId"));
    }
}
