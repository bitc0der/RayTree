using Npgsql;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Repository;
using Testcontainers.PostgreSql;

namespace RayTree.Plugins.PostgreSQL.Tests;

[NonParallelizable]

public class PostgreSqlRepositoryIntegrationTests : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private EntityChangeTracker _tracker = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _postgres.StartAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<TestUser>(e => e
            .UseRepository(new PostgreSqlRepository<TestUser>(new()
            {
                ConnectionString = _postgres.GetConnectionString(),
                TableName = "test_users"
            }))
            .UseOutbox(new PostgreSqlOutbox<TestUser>(new()
            {
                ConnectionString = _postgres.GetConnectionString(),
                OutboxTableName = "test_users_outbox"
            }))
            .UseQueue(new InMemoryQueue())
            .UseSerializer(new RayTree.Plugins.Serializers.Json.JsonSerializerPlugin())
            .UseCompressor(new RayTree.Plugins.Compressors.Gzip.GzipCompressorPlugin()));

        _tracker = builder.Build();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Test]
    public async Task InsertAsync_StoresEntity()
    {
        var repo = _tracker.Publisher.GetRepository(typeof(TestUser)) as PostgreSqlRepository<TestUser>;
        var user = new TestUser { Id = 1 };

        await repo!.InsertAsync(user);

        var stored = await repo.GetByIdAsync([1]);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.Id, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateAsync_UpdatesTimestamp()
    {
        var repo = _tracker.Publisher.GetRepository(typeof(TestUser)) as PostgreSqlRepository<TestUser>;
        var user = new TestUser { Id = 1 };
        await repo!.InsertAsync(user);

        await repo.UpdateAsync(user);

        var stored = await repo.GetByIdAsync([1]);
        Assert.That(stored, Is.Not.Null);
    }

    [Test]
    public async Task DeleteAsync_RemovesEntity()
    {
        var repo = _tracker.Publisher.GetRepository(typeof(TestUser)) as PostgreSqlRepository<TestUser>;
        var user = new TestUser { Id = 1 };
        await repo!.InsertAsync(user);

        await repo.DeleteAsync(user);

        var stored = await repo.GetByIdAsync([1]);
        Assert.That(stored, Is.Null);
    }

    [Test]
    public async Task GetByIdAsync_WithNonExistentId_ReturnsNull()
    {
        var repo = _tracker.Publisher.GetRepository(typeof(TestUser)) as PostgreSqlRepository<TestUser>;
        var result = await repo!.GetByIdAsync([999]);
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
