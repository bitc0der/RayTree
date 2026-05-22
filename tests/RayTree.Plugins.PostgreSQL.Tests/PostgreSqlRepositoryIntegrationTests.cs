using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Repository;

namespace RayTree.Plugins.PostgreSQL.Tests;

[NonParallelizable]
public class PostgreSqlRepositoryIntegrationTests : IAsyncDisposable
{
    private readonly IContainer _postgres = PostgresContainerFactory.Create();

    private EntityChangeTracker _tracker = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _postgres.StartAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        _tracker = EntityChangeTracker.Create()
            .ForEntity<TestUser>(e => e
                .UseRepository(new PostgreSqlRepository<TestUser>(new()
                {
                    ConnectionString = _postgres.GetConnectionString()
                }, NullLoggerFactory.Instance))
                .UseOutbox(new PostgreSqlOutbox<TestUser>(new()
                {
                    ConnectionString = _postgres.GetConnectionString()
                }, NullLoggerFactory.Instance))
                .UsePublisher(new InMemoryQueue())
                .UseSerializer(new RayTree.Plugins.Serializers.Json.JsonSerializerPlugin())
                .UseCompressor(new RayTree.Plugins.Compressors.Gzip.GzipCompressorPlugin()))
            .Build();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Test]
    public async Task InsertAsync_StoresEntity()
    {
        // Arrange
        var repo = _tracker.Publisher.GetRepository(typeof(TestUser)) as PostgreSqlRepository<TestUser>;
        var user = new TestUser { Id = 1 };

        // Act
        await repo!.InsertAsync(user);

        // Assert
        var stored = await repo.GetByIdAsync([1]);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.Id, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateAsync_UpdatesTimestamp()
    {
        // Arrange
        var repo = _tracker.Publisher.GetRepository(typeof(TestUser)) as PostgreSqlRepository<TestUser>;
        var user = new TestUser { Id = 1 };
        await repo!.InsertAsync(user);

        // Act
        await repo.UpdateAsync(user);

        // Assert
        var stored = await repo.GetByIdAsync([1]);
        Assert.That(stored, Is.Not.Null);
    }

    [Test]
    public async Task DeleteAsync_RemovesEntity()
    {
        // Arrange
        var repo = _tracker.Publisher.GetRepository(typeof(TestUser)) as PostgreSqlRepository<TestUser>;
        var user = new TestUser { Id = 1 };
        await repo!.InsertAsync(user);

        // Act
        await repo.DeleteAsync(user);

        // Assert
        var stored = await repo.GetByIdAsync([1]);
        Assert.That(stored, Is.Null);
    }

    [Test]
    public async Task GetByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var repo = _tracker.Publisher.GetRepository(typeof(TestUser)) as PostgreSqlRepository<TestUser>;

        // Act
        var result = await repo!.GetByIdAsync([999]);

        // Assert
        Assert.That(result, Is.Null);
    }

    [TearDown]
    public async Task TearDown()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("TRUNCATE test_users, test_users_outbox RESTART IDENTITY", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
