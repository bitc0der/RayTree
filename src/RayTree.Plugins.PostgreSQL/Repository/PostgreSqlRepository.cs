using Npgsql;

namespace RayTree.Plugins.PostgreSQL;

public class PostgreSqlRepository<TEntity> : IRepository<TEntity> where TEntity : class
{
    private readonly PostgreSqlRepositoryOptions _options;

    public PostgreSqlRepository(PostgreSqlRepositoryOptions options)
    {
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Create source table only - triggers are created by the outbox
        var sourceSchema = SourceTableDdlGenerator.CreateDefault(typeof(TEntity).Name, _options.TableName);
        var sourceDdl = SourceTableDdlGenerator.GenerateCreateTable(sourceSchema, ifNotExists: true);
        await ExecuteDdlDirectly(_options.ConnectionString, sourceDdl, cancellationToken);
    }

    private static async Task ExecuteDdlDirectly(string connectionString, string ddl, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"INSERT INTO {_options.TableName} DEFAULT VALUES RETURNING id";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteScalarAsync(cancellationToken);
    }

    public async Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"UPDATE {_options.TableName} SET updated_at = NOW() WHERE id = @Id";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", GetEntityId(entity));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"DELETE FROM {_options.TableName} WHERE id = @Id";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", GetEntityId(entity));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TEntity?> GetByIdAsync(object id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"SELECT * FROM {_options.TableName} WHERE id = @Id";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return MapEntity(reader);
        }

        return null;
    }

    protected virtual object GetEntityId(TEntity entity)
    {
        var prop = typeof(TEntity).GetProperty("Id")
            ?? throw new InvalidOperationException($"Entity type {typeof(TEntity).Name} has no Id property");

        return prop.GetValue(entity)
            ?? throw new InvalidOperationException($"Entity Id is null");
    }

    protected virtual TEntity MapEntity(NpgsqlDataReader reader)
    {
        var entity = Activator.CreateInstance<TEntity>();

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var propName = reader.GetName(i);
            var prop = typeof(TEntity).GetProperty(propName);
            if (prop != null && prop.CanWrite && !reader.IsDBNull(i))
            {
                var value = reader.GetValue(i);
                var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                prop.SetValue(entity, Convert.ChangeType(value, targetType));
            }
        }

        return entity;
    }
}
