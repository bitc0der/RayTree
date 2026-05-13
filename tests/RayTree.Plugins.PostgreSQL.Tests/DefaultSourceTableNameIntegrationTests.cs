using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RayTree.Plugins.PostgreSQL.Repository;

namespace RayTree.Plugins.PostgreSQL.Tests;

[NonParallelizable]
public class DefaultSourceTableNameIntegrationTests : IAsyncDisposable
{
    private readonly IContainer _postgres = PostgresContainerFactory.Create();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Test]
    public async Task InitializeAsync_WithNoTableName_CreatesTableWithDerivedName()
    {
        var repo = new PostgreSqlRepository<TestEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
            // TableName intentionally omitted
        }, NullLoggerFactory.Instance);

        await repo.InitializeAsync();

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_name = 'test_entity'
            """, conn);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.That(count, Is.EqualTo(1));
    }
}
