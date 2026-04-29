using Npgsql;
using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.Plugins.PostgreSQL;

public class PostgreSqlOutboxOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string OutboxTableName { get; set; } = string.Empty;
    public bool UseNotificationChannel { get; set; }
    public string? NotificationChannel { get; set; }
    public TimeSpan? FallbackPollingInterval { get; set; }
}

public class PostgreSqlOutbox : IOutbox
{
    private readonly PostgreSqlOutboxOptions _options;

    public PostgreSqlOutbox(PostgreSqlOutboxOptions options)
    {
        _options = options;
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
}
