using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RayTree.Core.Models;
using RayTree.Core.Tracking;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Repository;
using RayTree.Plugins.PostgreSQL.Schema;

namespace RayTree.Plugins.PostgreSQL.Tests;

// All four entities share the same physical outbox table via [Table] so the
// schema-evolution tests can register different shapes against one storage target.
[Table("schema_evolution")]
public class SlimEntity
{
    public int Id { get; set; }
}

[Table("schema_evolution")]
public class ExpandedEntity
{
    public int Id { get; set; }
    public string? Description { get; set; }
}

[Table("schema_evolution")]
public class RequiredFieldEntity
{
    public int Id { get; set; }
    [Required] public string RequiredField { get; set; } = string.Empty;
}

[Table("schema_evolution")]
public class TypeChangedEntity
{
    public long Id { get; set; }        // was int in ExpandedEntity
    public string? Description { get; set; }
}

/// <summary>
/// Captures Warning/Information log calls for assertion.
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
public class SchemaEvolutionOutboxTests : IAsyncDisposable
{
    private readonly IContainer _postgres = PostgresContainerFactory.Create();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    private const string OutboxTable = "schema_evolution_outbox";

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
    public async Task AddsNewNullableColumn_ToExistingOutboxTable()
    {
        await DropTableIfExists();

        var slimOutbox = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slimOutbox.InitializeAsync();

        await slimOutbox.WriteAsync(new EntityChange<SlimEntity>
        {
            EntityType = typeof(SlimEntity).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            State = new SlimEntity { Id = 1 }
        });

        var expandedOutbox = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await expandedOutbox.InitializeAsync();

        Assert.That(await ColumnExists("state_description"), Is.True);
    }

    [Test]
    public async Task WritesNewColumnData_AfterSchemaEvolution()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        var expanded = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
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
    public async Task WarnsAboutOrphanColumn_DoesNotDropIt()
    {
        await DropTableIfExists();

        var expanded = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        var factory = new CapturingLoggerFactory();

        // Re-initialise with slim entity — Description becomes orphan
        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
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
    public async Task WarnsAboutTypeMismatch_AppliesNoDdl()
    {
        await DropTableIfExists();

        var expanded = new PostgreSqlOutbox<ExpandedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        var factory = new CapturingLoggerFactory();

        // Re-initialise with TypeChangedEntity — state_id is BIGINT vs INTEGER
        var typeChanged = new PostgreSqlOutbox<TypeChangedEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, factory);
        await typeChanged.InitializeAsync();

        Assert.That(
            factory.Entries.Any(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("state_id")),
            Is.True,
            "Expected Warning log for type mismatch on state_id");
    }

    [Test]
    public async Task RequiredColumnOnNonEmptyTable_ThrowsInvalidOperationException()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        await slim.WriteAsync(new EntityChange<SlimEntity>
        {
            EntityType = typeof(SlimEntity).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            State = new SlimEntity { Id = 1 }
        });

        var requiredOutbox = new PostgreSqlOutbox<RequiredFieldEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await requiredOutbox.InitializeAsync());

        Assert.That(ex!.Message, Does.Contain("state_required_field"));
        Assert.That(ex.Message, Does.Contain(OutboxTable));
        Assert.That(ex.Message, Does.Contain("DEFAULT"));
    }

    [Test]
    public async Task RequiredColumnOnEmptyTable_Succeeds()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        var requiredOutbox = new PostgreSqlOutbox<RequiredFieldEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);

        Assert.DoesNotThrowAsync(async () => await requiredOutbox.InitializeAsync());
        Assert.That(await ColumnExists("state_required_field"), Is.True);
    }

    // ── Index migration ──────────────────────────────────────────────────────

    [Test]
    public async Task DroppedIndex_GetsRecreated()
    {
        await DropTableIfExists();

        var outbox = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await outbox.InitializeAsync();

        // Drop one of the three standard outbox indexes.
        const string indexName = "idx_slimentity_outbox_unpublished";
        await ExecuteSqlAsync($"DROP INDEX IF EXISTS {indexName}");
        Assert.That((await GetIndexesAsync()).ContainsKey(indexName), Is.False, "Precondition: index gone");

        // Re-initialise against the existing table — IndexMigrator should restore it.
        await outbox.InitializeAsync();

        var index = (await GetIndexesAsync())[indexName];
        Assert.That(index, Is.Not.Null, "Index must be recreated");
        Assert.That(index.Columns, Is.EqualTo(new[] { "published", "timestamp" }).AsCollection);
        Assert.That(index.Where, Is.Not.Null.And.Not.Empty, "Partial index must have a WHERE clause");
    }

    [Test]
    public async Task TamperedWhereClause_RecreatesIndex()
    {
        await DropTableIfExists();

        var outbox = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await outbox.InitializeAsync();

        // Replace the partial index with a full index (no WHERE clause) — simulates a tampered definition.
        const string indexName = "idx_slimentity_outbox_unpublished";
        await ExecuteSqlAsync($"DROP INDEX IF EXISTS {indexName}");
        await ExecuteSqlAsync($"CREATE INDEX {indexName} ON {OutboxTable} (published, timestamp)");

        var before = (await GetIndexesAsync())[indexName];
        Assert.That(before.Where, Is.Null, "Precondition: tampered index has no WHERE clause");

        // Re-init: mismatch detected → DROP + CREATE with the correct partial predicate.
        await outbox.InitializeAsync();

        var after = (await GetIndexesAsync())[indexName];
        Assert.That(after.Where, Is.Not.Null.And.Not.Empty, "Recreated index must have the WHERE clause restored");
        Assert.That(after.Where, Does.Contain("published").IgnoreCase.And.Contain("false").IgnoreCase,
            "Recreated index must filter on published = FALSE");
    }

    [Test]
    public async Task OrphanIndex_LogsWarning_DoesNotDrop()
    {
        await DropTableIfExists();

        var outbox = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await outbox.InitializeAsync();

        // Add a custom index that is not part of the entity schema.
        const string orphanIndex = "idx_custom_outbox_orphan";
        await ExecuteSqlAsync($"CREATE INDEX {orphanIndex} ON {OutboxTable} (entity_id)");

        var factory = new CapturingLoggerFactory();
        var outbox2 = new PostgreSqlOutbox<SlimEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, factory);
        await outbox2.InitializeAsync();

        Assert.That((await GetIndexesAsync()).ContainsKey(orphanIndex), Is.True,
            "Orphan index must not be dropped");
        Assert.That(
            factory.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(orphanIndex)),
            Is.True,
            "Expected Warning log for orphan index");
    }

    private Task<IReadOnlyDictionary<string, SchemaInspector.ExistingIndex>> GetIndexesAsync()
        => SchemaInspector.GetIndexesAsync(_postgres.GetConnectionString(), OutboxTable);

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

// Source-table entities — all share one physical table via [Table] so schema-evolution
// tests can register different shapes against one storage target.
[Table("schema_evolution_source")]
public class SlimSourceEntity
{
    [Key] public int OrderId { get; set; }
}

[Table("schema_evolution_source")]
public class ExpandedSourceEntity
{
    [Key] public int OrderId { get; set; }
    [Key] public int LineId { get; set; }
}

[Table("schema_evolution_source")]
public class RequiredSourceEntity
{
    [Key] public int OrderId { get; set; }
    [Key] public int LineId { get; set; }
}

[NonParallelizable]
public class SchemaEvolutionRepositoryTests : IAsyncDisposable
{
    private readonly IContainer _postgres = PostgresContainerFactory.Create();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    private const string SourceTable = "schema_evolution_source";

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
    public async Task AddsNewKeyColumn_ToExistingSourceTable()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        var expanded = new PostgreSqlRepository<ExpandedSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        Assert.That(await ColumnExists("state_line_id"), Is.True);
    }

    [Test]
    public async Task RequiredKeyColumnOnNonEmptyTable_ThrowsInvalidOperationException()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        await slim.InsertAsync(new SlimSourceEntity { OrderId = 1 });

        var expanded = new PostgreSqlRepository<RequiredSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await expanded.InitializeAsync());

        Assert.That(ex!.Message, Does.Contain(SourceTable));
        Assert.That(ex.Message, Does.Contain("DEFAULT"));
    }

    // ── Index migration ──────────────────────────────────────────────────────

    [Test]
    public async Task TamperedKeyIndex_GetsRecreated()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        // Replace the UNIQUE key index with a plain (non-unique) one — simulates a tampered definition.
        const string indexName = "idx_slimsourceentity_source_key";
        await ExecuteSqlAsync($"DROP INDEX IF EXISTS {indexName}");
        await ExecuteSqlAsync($"CREATE INDEX {indexName} ON {SourceTable} (state_order_id)");

        var before = (await GetIndexesAsync())[indexName];
        Assert.That(before.IsUnique, Is.False, "Precondition: tampered index is not unique");

        // Re-init: mismatch detected → DROP + CREATE with the correct UNIQUE definition.
        await slim.InitializeAsync();

        var after = (await GetIndexesAsync())[indexName];
        Assert.That(after.IsUnique, Is.True, "Recreated index must be UNIQUE");
        Assert.That(after.Columns, Is.EqualTo(new[] { "state_order_id" }).AsCollection);
    }

    [Test]
    public async Task CompositeKeyEvolution_CreatesNewKeyIndex()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        var expanded = new PostgreSqlRepository<ExpandedSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await expanded.InitializeAsync();

        // The expanded entity's unique key index must cover both key columns.
        const string newIndex = "idx_expandedsourceentity_source_key";
        var indexes = await GetIndexesAsync();
        Assert.That(indexes.ContainsKey(newIndex), Is.True, "New composite key index must be created");
        Assert.That(indexes[newIndex].IsUnique, Is.True);
        Assert.That(indexes[newIndex].Columns,
            Is.EqualTo(new[] { "state_order_id", "state_line_id" }).AsCollection);
    }

    [Test]
    public async Task OrphanIndex_LogsWarning_DoesNotDrop()
    {
        await DropTableIfExists();

        var slim = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, NullLoggerFactory.Instance);
        await slim.InitializeAsync();

        // Add a custom index that is not part of the entity schema.
        const string orphanIndex = "idx_custom_source_orphan";
        await ExecuteSqlAsync($"CREATE INDEX {orphanIndex} ON {SourceTable} (state_order_id)");

        var factory = new CapturingLoggerFactory();
        var slim2 = new PostgreSqlRepository<SlimSourceEntity>(new PostgreSqlRepositoryOptions
        {
            ConnectionString = _postgres.GetConnectionString()
        }, factory);
        await slim2.InitializeAsync();

        Assert.That((await GetIndexesAsync()).ContainsKey(orphanIndex), Is.True,
            "Orphan index must not be dropped");
        Assert.That(
            factory.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(orphanIndex)),
            Is.True,
            "Expected Warning log for orphan index");
    }

    private Task<IReadOnlyDictionary<string, SchemaInspector.ExistingIndex>> GetIndexesAsync()
        => SchemaInspector.GetIndexesAsync(_postgres.GetConnectionString(), SourceTable);

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
