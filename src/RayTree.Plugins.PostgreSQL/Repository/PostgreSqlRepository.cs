using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RayTree.Core.Plugins.Repository;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Schema;
using RayTree.Plugins.PostgreSQL.Repository.Schema;

namespace RayTree.Plugins.PostgreSQL.Repository;

public class PostgreSqlRepository<TEntity> : IRepository<TEntity>
    where TEntity : class
{
    private readonly PostgreSqlRepositoryOptions _options;
    private readonly IReadOnlyList<EntityColumnMapper.PropertyColumn> _keyColumns;
    private readonly string _insertSql;
    private readonly string _whereClause;
    private readonly Dictionary<string, PropertyInfo> _columnToProperty;
    private readonly ILogger<PostgreSqlRepository<TEntity>> _logger;

    public PostgreSqlRepository(PostgreSqlRepositoryOptions options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<PostgreSqlRepository<TEntity>>();
        if (string.IsNullOrWhiteSpace(options.TableName))
            options.TableName = EntityColumnMapper.GetTableName(typeof(TEntity));

        _options = options;

        var keyProperties = EntityColumnMapper.GetKeyProperties(typeof(TEntity));
        var allEntityColumns = EntityColumnMapper.GetColumns(typeof(TEntity));
        var byPropertyName = allEntityColumns.ToDictionary(c => c.Property.Name);
        _keyColumns = keyProperties.Select(p => byPropertyName[p.Name]).ToList();

        _insertSql = BuildInsertSql();
        _whereClause = BuildWhereClause();
        _columnToProperty = allEntityColumns.ToDictionary(c => c.ColumnName, c => c.Property);
    }

    private string BuildInsertSql()
    {
        var cols = string.Join(", ", _keyColumns.Select(c => c.ColumnName));
        var parms = string.Join(", ", Enumerable.Range(0, _keyColumns.Count).Select(i => $"@K{i}"));
        return $"INSERT INTO {_options.TableName} ({cols}) VALUES ({parms})";
    }

    private string BuildWhereClause()
        => string.Join(" AND ", _keyColumns.Select((c, i) => $"{c.ColumnName} = @K{i}"));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var keySourceColumns = _keyColumns
            .Select(c => new SourceTableColumn { Name = c.ColumnName, Type = c.ColumnType, IsNullable = false })
            .ToList();
        var sourceSchema = SourceTableDdlGenerator.CreateDefault(typeof(TEntity).Name, keySourceColumns, _options.TableName);

        // Create table without indexes first so that migration can add missing key columns
        // before any index referencing those columns is attempted.
        var tableDdl = SourceTableDdlGenerator.GenerateCreateTable(sourceSchema, ifNotExists: true, includeIndexes: false);
        await ExecuteDdlDirectly(_options.ConnectionString, tableDdl, cancellationToken);

        if (_options.AutoMigrate)
            await MigrateSchemaAsync(cancellationToken);

        // Create indexes after migration so all key columns are guaranteed to exist.
        foreach (var index in sourceSchema.Indexes)
        {
            var indexDdl = SourceTableDdlGenerator.GenerateCreateIndex(sourceSchema.TableName, index);
            await ExecuteDdlDirectly(_options.ConnectionString, indexDdl, cancellationToken);
        }
    }

    private static readonly HashSet<string> s_InfraColumns =
        new(["id", "created_at", "updated_at", "version"], StringComparer.OrdinalIgnoreCase);

    private Task MigrateSchemaAsync(CancellationToken cancellationToken)
    {
        var desired = _keyColumns
            .Select(c => new ColumnMigrationSpec(c.ColumnName, c.ColumnType, false))
            .ToList();
        return SchemaMigrator.ApplyDiffAsync(
            _options.ConnectionString,
            _options.TableName,
            desired,
            col => SourceTableDdlGenerator.GenerateAddColumn(
                _options.TableName,
                new SourceTableColumn { Name = col.Name, Type = col.Type, IsNullable = false }),
            name => !s_InfraColumns.Contains(name),
            _logger,
            cancellationToken);
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
        ArgumentNullException.ThrowIfNull(entity);

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(_insertSql, conn);
        AddKeyParameters(cmd, entity);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            $"UPDATE {_options.TableName} SET updated_at = NOW() WHERE {_whereClause}", conn);
        AddKeyParameters(cmd, entity);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {_options.TableName} WHERE {_whereClause}", conn);
        AddKeyParameters(cmd, entity);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TEntity?> GetByIdAsync(object[] keyValues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyValues);

        if (keyValues.Length != _keyColumns.Count)
            throw new ArgumentException(
                $"Expected {_keyColumns.Count} key value(s) for {typeof(TEntity).Name}, got {keyValues.Length}.",
                nameof(keyValues));

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            $"SELECT * FROM {_options.TableName} WHERE {_whereClause}", conn);
        for (var i = 0; i < keyValues.Length; i++)
            cmd.Parameters.AddWithValue($"K{i}", keyValues[i] ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapEntity(reader) : null;
    }

    private void AddKeyParameters(NpgsqlCommand cmd, TEntity entity)
    {
        for (var i = 0; i < _keyColumns.Count; i++)
            cmd.Parameters.AddWithValue($"K{i}", _keyColumns[i].Property.GetValue(entity) ?? DBNull.Value);
    }

    protected virtual TEntity MapEntity(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var entity = Activator.CreateInstance<TEntity>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (!_columnToProperty.TryGetValue(reader.GetName(i), out var prop) || reader.IsDBNull(i))
                continue;

            var value = reader.GetValue(i);
            var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            prop.SetValue(entity, EntityColumnMapper.ConvertFromDb(value, targetType));
        }

        return entity;
    }
}
