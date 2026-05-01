using System.Text.Json;
using Npgsql;
using RayTree.Distribution;
using RayTree.Models;
using RayTree.Outbox;
using RayTree.Tracking;

namespace RayTree.Plugins.PostgreSQL;

public class PostgreSqlOutbox : IOutbox
{
    private readonly PostgreSqlOutboxOptions _options;

    public PostgreSqlOutbox(PostgreSqlOutboxOptions options)
    {
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 1. Create outbox table using DdlExecutor to handle multiple statements
        var outboxSchema = OutboxTableSchema.Create("Unknown", _options.OutboxTableName);
        var outboxDdl = OutboxSchemaGenerator.GenerateCreateTable(outboxSchema, includeIndexes: true);
        await ExecuteDdlDirectly(_options.ConnectionString, outboxDdl, cancellationToken);

        // 2. Create notification trigger if enabled
        if (_options.UseNotificationChannel && !string.IsNullOrEmpty(_options.NotificationChannel))
        {
            var functionName = $"notify_{_options.OutboxTableName}_change";
            var triggerFunctionDdl = NotificationBasedPublisher.GenerateNotifyTriggerFunction(
                _options.OutboxTableName, _options.NotificationChannel);
            await ExecuteDdlDirectly(_options.ConnectionString, triggerFunctionDdl, cancellationToken);

            var triggerName = $"{_options.OutboxTableName}_notify_trigger";
            var dropTriggerDdl = NotificationBasedPublisher.GenerateDropTrigger(triggerName, _options.OutboxTableName);
            await ExecuteDdlDirectly(_options.ConnectionString, dropTriggerDdl, cancellationToken);

            var triggerDdl = NotificationBasedPublisher.GenerateNotifyTrigger(triggerName, _options.OutboxTableName, functionName);
            await ExecuteDdlDirectly(_options.ConnectionString, triggerDdl, cancellationToken);
        }
    }

    private static async Task ExecuteDdlDirectly(string connectionString, string ddl, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task WriteAsync(EntityChange change, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {_options.OutboxTableName}
                (entity_id, change_type, timestamp, version, correlation_id, entity_type, data)
            VALUES
                (@EntityId, @ChangeType, @Timestamp, @Version, @CorrelationId, @EntityType, @Data)
            RETURNING id
            """, conn)
        {
            Parameters =
            {
                new("EntityId", change.EntityId),
                new("ChangeType", change.ChangeType.ToString()),
                new("Timestamp", change.Timestamp),
                new("Version", change.Version),
                new("CorrelationId", change.CorrelationId),
                new("EntityType", change.EntityType),
                new("Data", (object)DBNull.Value)
            }
        };

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        change.Id = result != null ? Convert.ToInt64(result) : 0;
    }

    public async Task WriteAsync<TEntity>(EntityChange<TEntity> change, CancellationToken cancellationToken = default)
    {
        if (change.State == null)
        {
            await WriteAsync((EntityChange)change, cancellationToken);
            return;
        }

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var properties = typeof(TEntity).GetProperties();
        var columnNames = new List<string> { "entity_id", "change_type", "timestamp", "version", "correlation_id", "entity_type" };
        var paramNames = new List<string> { "@EntityId", "@ChangeType", "@Timestamp", "@Version", "@CorrelationId", "@EntityType" };

        foreach (var prop in properties)
        {
            columnNames.Add(prop.Name.ToLowerInvariant());
            paramNames.Add($"@{prop.Name}");
        }

        var sql = $"""
            INSERT INTO {_options.OutboxTableName}
                ({string.Join(", ", columnNames)})
            VALUES
                ({string.Join(", ", paramNames)})
            RETURNING id
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.Add(new("EntityId", change.EntityId));
        cmd.Parameters.Add(new("ChangeType", change.ChangeType.ToString()));
        cmd.Parameters.Add(new("Timestamp", change.Timestamp));
        cmd.Parameters.Add(new("Version", change.Version));
        cmd.Parameters.Add(new("CorrelationId", change.CorrelationId));
        cmd.Parameters.Add(new("EntityType", change.EntityType));

        foreach (var prop in properties)
        {
            var value = prop.GetValue(change.State);
            cmd.Parameters.Add(new($"@{prop.Name}", value ?? DBNull.Value));
        }

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        change.Id = result != null ? Convert.ToInt64(result) : 0;
    }

    public async Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var changes = new List<EntityChange>();

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"""
            SELECT id, entity_id, change_type, timestamp, version, correlation_id, entity_type
            FROM {_options.OutboxTableName}
            WHERE published = FALSE
            ORDER BY timestamp
            LIMIT @BatchSize
            """, conn)
        {
            Parameters = { new("BatchSize", batchSize) }
        };

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(new EntityChange
            {
                Id = reader.GetInt64(0),
                EntityId = reader.GetString(1),
                ChangeType = Enum.Parse<ChangeType>(reader.GetString(2)),
                Timestamp = reader.GetDateTime(3),
                Version = reader.GetInt32(4),
                CorrelationId = reader.GetGuid(5),
                EntityType = reader.GetString(6)
            });
        }

        return changes;
    }

    public async Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(
        string entityType,
        ChangeType? changeType = null,
        DateTime? since = null,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<EntityChange>();

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT id, entity_id, change_type, timestamp, version, correlation_id, entity_type
            FROM {_options.OutboxTableName}
            WHERE published = FALSE AND entity_type = @EntityType
            """;

        if (changeType.HasValue)
            sql += " AND change_type = @ChangeType";

        if (since.HasValue)
            sql += " AND timestamp >= @Since";

        sql += " ORDER BY timestamp LIMIT @BatchSize";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new("EntityType", entityType));
        cmd.Parameters.Add(new("BatchSize", batchSize));

        if (changeType.HasValue)
            cmd.Parameters.Add(new("ChangeType", changeType.Value.ToString()));

        if (since.HasValue)
            cmd.Parameters.Add(new("Since", since.Value));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(new EntityChange
            {
                Id = reader.GetInt64(0),
                EntityId = reader.GetString(1),
                ChangeType = Enum.Parse<ChangeType>(reader.GetString(2)),
                Timestamp = reader.GetDateTime(3),
                Version = reader.GetInt32(4),
                CorrelationId = reader.GetGuid(5),
                EntityType = reader.GetString(6)
            });
        }

        return changes;
    }

    public async Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"""
            UPDATE {_options.OutboxTableName}
            SET published = TRUE
            WHERE id = @Id
            """, conn)
        {
            Parameters = { new("Id", id) }
        };

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"""
            DELETE FROM {_options.OutboxTableName}
            WHERE published = TRUE AND timestamp < @Cutoff
            """, conn)
        {
            Parameters = { new("Cutoff", DateTime.UtcNow - retentionPeriod) }
        };

        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EntityChange?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"""
            SELECT id, entity_id, change_type, timestamp, version, correlation_id, entity_type
            FROM {_options.OutboxTableName}
            WHERE id = @Id
            """, conn)
        {
            Parameters = { new("Id", id) }
        };

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return new EntityChange
            {
                Id = reader.GetInt64(0),
                EntityId = reader.GetString(1),
                ChangeType = Enum.Parse<ChangeType>(reader.GetString(2)),
                Timestamp = reader.GetDateTime(3),
                Version = reader.GetInt32(4),
                CorrelationId = reader.GetGuid(5),
                EntityType = reader.GetString(6)
            };
        }

        return null;
    }

    public async Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(int batchSize, CancellationToken cancellationToken = default)
    {
        var changes = new List<EntityChange<TEntity>>();

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var properties = typeof(TEntity).GetProperties();
        var columns = string.Join(", ", GetColumnList(properties));

        await using var cmd = new NpgsqlCommand($"""
            SELECT id, entity_id, change_type, timestamp, version, correlation_id, entity_type, {columns}
            FROM {_options.OutboxTableName}
            WHERE published = FALSE
            ORDER BY timestamp
            LIMIT @BatchSize
            """, conn)
        {
            Parameters = { new("BatchSize", batchSize) }
        };

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(CreateEntityChange<TEntity>(reader, properties, 7));
        }

        return changes;
    }

    public async Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(
        ChangeType? changeType = null,
        DateTime? since = null,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<EntityChange<TEntity>>();

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var properties = typeof(TEntity).GetProperties();
        var columns = string.Join(", ", GetColumnList(properties));

        var sql = $"""
            SELECT id, entity_id, change_type, timestamp, version, correlation_id, entity_type, {columns}
            FROM {_options.OutboxTableName}
            WHERE published = FALSE AND entity_type = @EntityType
            """;

        if (changeType.HasValue)
            sql += " AND change_type = @ChangeType";

        if (since.HasValue)
            sql += " AND timestamp >= @Since";

        sql += " ORDER BY timestamp LIMIT @BatchSize";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new("EntityType", typeof(TEntity).FullName!));
        cmd.Parameters.Add(new("BatchSize", batchSize));

        if (changeType.HasValue)
            cmd.Parameters.Add(new("ChangeType", changeType.Value.ToString()));

        if (since.HasValue)
            cmd.Parameters.Add(new("Since", since.Value));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(CreateEntityChange<TEntity>(reader, properties, 7));
        }

        return changes;
    }

    public async Task<EntityChange<TEntity>?> GetByIdAsync<TEntity>(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var properties = typeof(TEntity).GetProperties();
        var columns = string.Join(", ", GetColumnList(properties));

        await using var cmd = new NpgsqlCommand($"""
            SELECT id, entity_id, change_type, timestamp, version, correlation_id, entity_type, {columns}
            FROM {_options.OutboxTableName}
            WHERE id = @Id
            """, conn)
        {
            Parameters = { new("Id", id) }
        };

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return CreateEntityChange<TEntity>(reader, properties, 7);
        }

        return null;
    }

    private static string[] GetColumnList(System.Reflection.PropertyInfo[] properties)
    {
        return properties.Select(p => p.Name.ToLowerInvariant()).ToArray();
    }

    private static EntityChange<TEntity> CreateEntityChange<TEntity>(
        NpgsqlDataReader reader,
        System.Reflection.PropertyInfo[] properties,
        int stateStartIndex)
    {
        var change = new EntityChange<TEntity>
        {
            Id = reader.GetInt64(0),
            EntityId = reader.GetString(1),
            ChangeType = Enum.Parse<ChangeType>(reader.GetString(2)),
            Timestamp = reader.GetDateTime(3),
            Version = reader.GetInt32(4),
            CorrelationId = reader.GetGuid(5),
            EntityType = reader.GetString(6),
            State = (TEntity)Activator.CreateInstance(typeof(TEntity))!
        };

        for (int i = 0; i < properties.Length; i++)
        {
            var value = reader.IsDBNull(stateStartIndex + i) ? null : reader.GetValue(stateStartIndex + i);
            if (value != null)
            {
                properties[i].SetValue(change.State, Convert.ChangeType(value, properties[i].PropertyType));
            }
        }

        return change;
    }
}
