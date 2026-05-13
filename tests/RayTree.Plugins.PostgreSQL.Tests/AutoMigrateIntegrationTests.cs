using System.ComponentModel.DataAnnotations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RayTree.Core.Models;
using RayTree.Core.Tracking;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Repository;

namespace RayTree.Plugins.PostgreSQL.Tests;

// Entity with only Id — used to create the table before the "expanded" version is applied
public class SlimEntity
{
    public int Id { get; set; }
}

// Entity with an extra nullable property — represents a schema evolution of SlimEntity
public class ExpandedEntity
{
    public int Id { get; set; }
    public string? Description { get; set; }
}

// Entity with a [Required] (NOT NULL) property and no default
public class RequiredFieldEntity
{
    public int Id { get; set; }
    [Required] public string RequiredField { get; set; } = string.Empty;
}

// Entity whose Id type differs from ExpandedEntity (for type-mismatch test)
public class TypeChangedEntity
{
    public long Id { get; set; }        // was int in ExpandedEntity
    public string? Description { get; set; }
}

/// <summary>
/// Captures Warning/Information log calls for assertion. Implements ILoggerFactory directly
/// to avoid a dependency on Microsoft.Extensions.Logging (non-abstractions).
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class CapturingLogger(List<(LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add((logLevel, formatter(state, exception)));
    }
}

[NonParallelizable]
public class AutoMigrateOutboxTests : IAsyncDisposable
{
    private readonly IContainer _postgres = PostgresContainerFactory.Create();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    private const string OutboxTable = "auto_migrate_outbox_test";

    private async Task DropTableIfExists()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {OutboxTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ColumnExists(string column)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name = @Table AND column_name = @Column
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("Table", OutboxTable));
        cmd.Parameters.Add(new NpgsqlParameter("Column", column));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    [Test]
    public async Task AutoMigrate_AddsNewNullableColumn_ToExistingOutboxTable()
    {
        await DropTableIfExists();

        // Create table with slim entity (no Description column)
        var slimOutbox = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable
        }, NullLoggerFactory.Instance);
        await slimOutbox.InitializeAsync();

        // Insert a row so the table is non-empty
        await slimOutbox.WriteAsync(new EntityChange<SlimEntity>
        {
            EntityType = typeof(SlimEntity).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            State = new SlimEntity { Id = 1 }
        });

        // Re-initialise with expanded entity and AutoMigrate = true
        var expandedOutbox = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable,
            AutoMigrate = true
        }, NullLoggerFactory.Instance);
        await expandedOutbox.InitializeAsync();

        Assert.That(await ColumnExists("state_description"), Is.True);
    }

    [Test]
    public async Task AutoMigrate_WritesNewColumnData_AfterMigration()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(), OutboxTableName = OutboxTable
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        var expanded = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable,
            AutoMigrate = true
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        var change = new EntityChange<ExpandedEntity>
        {
            EntityType = typeof(ExpandedEntity).FullName!,
            EntityId = "42",
            ChangeType = ChangeType.Insert,
            State = new ExpandedEntity { Id = 42, Description = "hello" }
        };
        await expanded.WriteAsync(change);

        var stored = await expanded.GetByIdAsync<ExpandedEntity>(change.Id);
        Assert.That(stored!.State!.Description, Is.EqualTo("hello"));
    }

    [Test]
    public async Task AutoMigrate_WarnsAboutOrphanColumn_DoesNotDropIt()
    {
        await DropTableIfExists();

        // Create table with expanded entity (has Description)
        var expanded = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(), OutboxTableName = OutboxTable
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        var factory = new CapturingLoggerFactory();

        // Re-initialise with slim entity — Description becomes orphan
        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable,
            AutoMigrate = true
        }, factory);
        await slim.InitializeAsync();

        Assert.That(await ColumnExists("state_description"), Is.True,
            "Orphan column must not be dropped");
        Assert.That(
            factory.Entries.Any(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("state_description")),
            Is.True,
            "Expected Warning log for orphan column");
    }

    [Test]
    public async Task AutoMigrate_WarnsAboutTypeMismatch_AppliesNoDdl()
    {
        await DropTableIfExists();

        // Create table with ExpandedEntity (state_id = INTEGER, state_description = TEXT)
        var expanded = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(), OutboxTableName = OutboxTable
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        var factory = new CapturingLoggerFactory();

        // Re-initialise with TypeChangedEntity — state_id is BIGINT vs INTEGER
        var typeChanged = new PostgreSqlOutbox<TypeChangedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable,
            AutoMigrate = true
        }, factory);
        await typeChanged.InitializeAsync();

        Assert.That(
            factory.Entries.Any(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("state_id")),
            Is.True,
            "Expected Warning log for type mismatch on state_id");
    }

    [Test]
    public async Task AutoMigrate_RequiredColumnOnNonEmptyTable_ThrowsInvalidOperationException()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(), OutboxTableName = OutboxTable
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        // Insert a row to make the table non-empty
        await slim.WriteAsync(new EntityChange<SlimEntity>
        {
            EntityType = typeof(SlimEntity).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            State = new SlimEntity { Id = 1 }
        });

        var requiredOutbox = new PostgreSqlOutbox<RequiredFieldEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable,
            AutoMigrate = true
        }, NullLoggerFactory.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await requiredOutbox.InitializeAsync());

        Assert.That(ex!.Message, Does.Contain("state_required_field"));
        Assert.That(ex.Message, Does.Contain(OutboxTable));
        Assert.That(ex.Message, Does.Contain("DEFAULT"));
    }

    [Test]
    public async Task AutoMigrate_RequiredColumnOnEmptyTable_Succeeds()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(), OutboxTableName = OutboxTable
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();
        // Table is empty — no rows inserted

        var requiredOutbox = new PostgreSqlOutbox<RequiredFieldEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable,
            AutoMigrate = true
        }, NullLoggerFactory.Instance);

        Assert.DoesNotThrowAsync(async () => await requiredOutbox.InitializeAsync());
        Assert.That(await ColumnExists("state_required_field"), Is.True);
    }

    [Test]
    public async Task AutoMigrate_False_DoesNotAddColumn()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(), OutboxTableName = OutboxTable
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        // Re-init with expanded entity but AutoMigrate = false (default)
        var expanded = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable
            // AutoMigrate defaults to false
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        Assert.That(await ColumnExists("state_description"), Is.False);
    }
}

// Source-table entities
public class SlimSourceEntity
{
    [Key] public int OrderId { get; set; }
}

public class ExpandedSourceEntity
{
    [Key] public int OrderId { get; set; }
    [Key] public int LineId { get; set; }
}

public class RequiredSourceEntity
{
    [Key] public int OrderId { get; set; }
    [Key][Required] public int LineId { get; set; }
}

[NonParallelizable]
public class AutoMigrateRepositoryTests : IAsyncDisposable
{
    private readonly IContainer _postgres = PostgresContainerFactory.Create();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    private const string SourceTable = "auto_migrate_source_test";

    private async Task DropTableIfExists()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {SourceTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ColumnExists(string column)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name = @Table AND column_name = @Column
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("Table", SourceTable));
        cmd.Parameters.Add(new NpgsqlParameter("Column", column));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    [Test]
    public async Task AutoMigrate_AddsNewKeyColumn_ToExistingSourceTable()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString(), TableName = SourceTable
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        // Re-initialise with expanded entity (adds LineId key column) on empty table
        var expanded = new PostgreSqlRepository<ExpandedSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            TableName = SourceTable,
            AutoMigrate = true
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        Assert.That(await ColumnExists("state_line_id"), Is.True);
    }

    [Test]
    public async Task AutoMigrate_RequiredKeyColumnOnNonEmptyTable_ThrowsInvalidOperationException()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString(), TableName = SourceTable
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        // Insert a row so the table is non-empty
        await slim.InsertAsync(new SlimSourceEntity { OrderId = 1 });

        var expanded = new PostgreSqlRepository<RequiredSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            TableName = SourceTable,
            AutoMigrate = true
        }, NullLoggerFactory.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await expanded.InitializeAsync());

        Assert.That(ex!.Message, Does.Contain(SourceTable));
        Assert.That(ex.Message, Does.Contain("DEFAULT"));
    }

    [Test]
    public async Task AutoMigrate_False_DoesNotAddColumn()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString(), TableName = SourceTable
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        var expanded = new PostgreSqlRepository<ExpandedSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            TableName = SourceTable
            // AutoMigrate defaults to false
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        Assert.That(await ColumnExists("state_line_id"), Is.False);
    }
}
