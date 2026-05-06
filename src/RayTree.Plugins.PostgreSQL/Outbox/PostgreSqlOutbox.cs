using Npgsql;
using RayTree.Core.Models;
using RayTree.Core.Outbox;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;
using RayTree.Distribution;
using RayTree.Plugins;

namespace RayTree.Plugins.PostgreSQL;

public class PostgreSqlOutbox<TEntity> : IOutbox where TEntity : class
{
    private readonly PostgreSqlOutboxOptions _options;
    private readonly IReadOnlyList<EntityColumnMapper.PropertyColumn> _propertyColumns;
    private readonly string _insertSql;
    private readonly string _selectColumns;

    public PostgreSqlOutbox(PostgreSqlOutboxOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutboxTableName))
            options.OutboxTableName = EntityColumnMapper.ToSnakeCase(typeof(TEntity).Name) + "_outbox";

        _options = options;
        _propertyColumns = EntityColumnMapper.GetColumns(typeof(TEntity));
        _insertSql = BuildInsertSql();
        _selectColumns = BuildSelectColumns();
    }

    private string BuildInsertSql()
    {
        var extraCols = _propertyColumns.Count > 0
            ? ", " + string.Join(", ", _propertyColumns.Select(c => c.ColumnName))
            : "";
        var extraParams = _propertyColumns.Count > 0
            ? ", " + string.Join(", ", _propertyColumns.Select(c => "@" + c.ColumnName))
            : "";
        return $"""
            INSERT INTO {_options.OutboxTableName}
                (entity_id, change_type, timestamp, version, correlation_id, entity_type{extraCols})
            VALUES
                (@EntityId, @ChangeType, @Timestamp, @Version, @CorrelationId, @EntityType{extraParams})
            RETURNING id
            """;
    }

    private string BuildSelectColumns()
    {
        var extraCols = _propertyColumns.Count > 0
            ? ", " + string.Join(", ", _propertyColumns.Select(c => c.ColumnName))
            : "";
        return $"id, entity_id, change_type, timestamp, version, correlation_id, entity_type{extraCols}";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var outboxSchema = OutboxTableSchema.Create(typeof(TEntity).Name, _options.OutboxTableName);
        foreach (var col in _propertyColumns)
            outboxSchema.AddEntityPropertyColumn(col.Property.Name, col.ColumnName, col.ColumnType, col.IsNullable);

        var outboxDdl = OutboxSchemaGenerator.GenerateCreateTable(outboxSchema, includeIndexes: true);
        await ExecuteDdlDirectly(_options.ConnectionString, outboxDdl, cancellationToken);

        if (_options.UseNotificationChannel && !string.IsNullOrEmpty(_options.NotificationChannel))
        {
            var functionName = $"notify_{_options.OutboxTableName}_change";
            var triggerFunctionDdl = NotificationBasedPublisher.GenerateNotifyTriggerFunction(
                functionName, _options.NotificationChannel);
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

    public async Task WriteAsync<T>(EntityChange<T> change, CancellationToken cancellationToken = default)
        where T : class
    {
        if (typeof(T) != typeof(TEntity))
            throw new InvalidOperationException($"This outbox handles {typeof(TEntity).Name}, not {typeof(T).Name}");

        var typedChange = (EntityChange<TEntity>)(object)change;

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(_insertSql, conn);
        cmd.Parameters.AddWithValue("EntityId", change.EntityId);
        cmd.Parameters.AddWithValue("ChangeType", change.ChangeType.ToString());
        cmd.Parameters.AddWithValue("Timestamp", change.Timestamp);
        cmd.Parameters.AddWithValue("Version", change.Version);
        cmd.Parameters.AddWithValue("CorrelationId", change.CorrelationId);
        cmd.Parameters.AddWithValue("EntityType", change.EntityType);

        foreach (var col in _propertyColumns)
        {
            var value = typedChange.State != null ? col.Property.GetValue(typedChange.State) : null;
            cmd.Parameters.AddWithValue(col.ColumnName, value ?? DBNull.Value);
        }

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        change.Id = result != null ? Convert.ToInt64(result) : 0;
    }

    public async Task<IReadOnlyList<EntityChange<T>>> GetUnpublishedAsync<T>(
        int batchSize,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (typeof(T) != typeof(TEntity))
            throw new InvalidOperationException($"This outbox handles {typeof(TEntity).Name}, not {typeof(T).Name}");

        var changes = new List<EntityChange<T>>();

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"""
            SELECT {_selectColumns}
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
            changes.Add((EntityChange<T>)(object)ReadEntityChange(reader));

        return changes;
    }

    public async Task<IReadOnlyList<EntityChange<T>>> GetUnpublishedAsync<T>(
        ChangeType? changeType = null,
        DateTime? since = null,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (typeof(T) != typeof(TEntity))
            throw new InvalidOperationException($"This outbox handles {typeof(TEntity).Name}, not {typeof(T).Name}");

        var changes = new List<EntityChange<T>>();

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT {_selectColumns}
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
            changes.Add((EntityChange<T>)(object)ReadEntityChange(reader));

        return changes;
    }

    public Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default)
        => ExecuteNonQueryAsync($"""
            UPDATE {_options.OutboxTableName}
            SET published = TRUE
            WHERE id = @Id
            """, new NpgsqlParameter("Id", id), cancellationToken);

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

    public async Task<EntityChange<T>?> GetByIdAsync<T>(long id, CancellationToken cancellationToken = default)
        where T : class
    {
        if (typeof(T) != typeof(TEntity))
            throw new InvalidOperationException($"This outbox handles {typeof(TEntity).Name}, not {typeof(T).Name}");

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"""
            SELECT {_selectColumns}
            FROM {_options.OutboxTableName}
            WHERE id = @Id
            """, conn)
        {
            Parameters = { new("Id", id) }
        };

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (EntityChange<T>)(object)ReadEntityChange(reader)
            : null;
    }

    private EntityChange<TEntity> ReadEntityChange(NpgsqlDataReader reader)
    {
        var change = new EntityChange<TEntity>
        {
            Id = reader.GetInt64(0),
            EntityId = reader.GetString(1),
            ChangeType = Enum.Parse<ChangeType>(reader.GetString(2)),
            Timestamp = reader.GetDateTime(3),
            Version = reader.GetInt32(4),
            CorrelationId = reader.GetGuid(5),
            EntityType = reader.GetString(6)
        };

        if (_propertyColumns.Count > 0)
        {
            var entity = Activator.CreateInstance<TEntity>();
            for (var i = 0; i < _propertyColumns.Count; i++)
            {
                var col = _propertyColumns[i];
                if (!reader.IsDBNull(7 + i))
                {
                    var value = reader.GetValue(7 + i);
                    var targetType = Nullable.GetUnderlyingType(col.Property.PropertyType) ?? col.Property.PropertyType;
                    col.Property.SetValue(entity, Convert.ChangeType(value, targetType));
                }
            }
            change.State = entity;
        }

        return change;
    }

    private async Task ExecuteNonQueryAsync(string sql, NpgsqlParameter parameter, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(parameter);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
