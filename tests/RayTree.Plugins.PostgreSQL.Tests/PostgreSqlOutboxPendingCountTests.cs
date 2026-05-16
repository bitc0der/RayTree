using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RayTree.Core.Models;
using RayTree.Core.Tracking;
using RayTree.Plugins.PostgreSQL.Outbox;

namespace RayTree.Plugins.PostgreSQL.Tests;

[NonParallelizable]
public class PostgreSqlOutboxPendingCountTests : IAsyncDisposable
{
    private const string TableName = "pending_count_outbox";
    private readonly IContainer _postgres = PostgresContainerFactory.Create();
    private PostgreSqlOutbox<TestEntity> _outbox = null!;

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _postgres.StartAsync();

    [SetUp]
    public async Task SetUp()
    {
        _outbox = new PostgreSqlOutbox<TestEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName  = TableName
        }, NullLoggerFactory.Instance);
        await _outbox.InitializeAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE TABLE {TableName}";
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Test]
    public async Task GetPendingCountAsync_OnEmptyTable_ReturnsZero()
    {
        Assert.That(await _outbox.GetPendingCountAsync(typeof(TestEntity)), Is.EqualTo(0));
    }

    [Test]
    public async Task GetPendingCountAsync_CountsUnpublishedOnly()
    {
        var published = new EntityChange<TestEntity>
        {
            EntityType = typeof(TestEntity).FullName!,
            EntityId   = "1",
            ChangeType = ChangeType.Insert,
            State      = new TestEntity { Id = 1 }
        };
        await _outbox.WriteAsync(published);
        await _outbox.MarkPublishedAsync(published.Id);

        for (var i = 0; i < 3; i++)
        {
            await _outbox.WriteAsync(new EntityChange<TestEntity>
            {
                EntityType = typeof(TestEntity).FullName!,
                EntityId   = i.ToString(),
                ChangeType = ChangeType.Insert,
                State      = new TestEntity { Id = i }
            });
        }

        Assert.That(await _outbox.GetPendingCountAsync(typeof(TestEntity)), Is.EqualTo(3));
    }

    [Test]
    public void GetPendingCountAsync_WithWrongType_Throws()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _outbox.GetPendingCountAsync(typeof(string)));
    }
}
