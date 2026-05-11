using DotNet.Testcontainers.Containers;
using Npgsql;
using RayTree.Plugins.PostgreSQL.Outbox;

namespace RayTree.Plugins.PostgreSQL.Tests;

[NonParallelizable]
public class DefaultTableNameIntegrationTests : IAsyncDisposable
{
    private readonly IContainer _postgres = PostgresContainerFactory.Create();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Test]
    public async Task InitializeAsync_WithNoTableName_CreatesTableWithDerivedName()
    {
        var outbox = new PostgreSqlOutbox<TestEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
            // OutboxTableName intentionally omitted
        });

        await outbox.InitializeAsync();

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_name = 'test_entity_outbox'
            """, conn);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.That(count, Is.EqualTo(1));
    }
}
