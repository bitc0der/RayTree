using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using RayTree.Outbox;

namespace RayTree.Plugins;

public static class AttributeBasedDdlGenerator
{
    private static readonly Dictionary<Type, string> DefaultTypeMappings = new()
    {
        { typeof(bool), "BOOLEAN" },
        { typeof(byte), "SMALLINT" },
        { typeof(sbyte), "SMALLINT" },
        { typeof(short), "SMALLINT" },
        { typeof(ushort), "SMALLINT" },
        { typeof(int), "INTEGER" },
        { typeof(uint), "INTEGER" },
        { typeof(long), "BIGINT" },
        { typeof(ulong), "BIGINT" },
        { typeof(float), "REAL" },
        { typeof(double), "DOUBLE PRECISION" },
        { typeof(decimal), "DECIMAL(18,2)" },
        { typeof(string), "VARCHAR(255)" },
        { typeof(char), "CHAR(1)" },
        { typeof(Guid), "UUID" },
        { typeof(DateTime), "TIMESTAMPTZ" },
        { typeof(DateTimeOffset), "TIMESTAMPTZ" },
        { typeof(TimeSpan), "INTERVAL" },
        { typeof(DateOnly), "DATE" },
        { typeof(TimeOnly), "TIME" },
        { typeof(byte[]), "BYTEA" }
    };

    public static SourceTableSchema CreateFromType(Type entityType)
    {
        var tableName = GetTableName(entityType);
        var columns = GetColumns(entityType);
        var indexes = GetIndexes(entityType, tableName, columns);

        return new SourceTableSchema
        {
            EntityTypeName = entityType.Name,
            TableName = tableName,
            Columns = columns,
            Indexes = indexes
        };
    }

    public static SourceTableSchema CreateFromType<T>() => CreateFromType(typeof(T));

    public static string GenerateCreateTableFromType(Type entityType, bool ifNotExists = true, bool includeOutbox = true, bool includeTriggers = true)
    {
        var schema = CreateFromType(entityType);
        var outboxSchema = OutboxTableSchema.Create(entityType.Name);

        if (!includeOutbox)
        {
            return SourceTableDdlGenerator.GenerateCreateTable(schema, ifNotExists);
        }

        var generator = new CombinedDdlGenerator();
        return generator.GenerateInitialize(schema, outboxSchema, includeTriggers);
    }

    public static string GenerateCreateTableFromType<T>(bool ifNotExists = true, bool includeOutbox = true, bool includeTriggers = true)
        => GenerateCreateTableFromType(typeof(T), ifNotExists, includeOutbox, includeTriggers);

    public static string MapTypeToPostgres(PropertyInfo property)
    {
        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        var columnAttr = property.GetCustomAttribute<ColumnAttribute>();
        if (columnAttr?.TypeName != null)
            return columnAttr.TypeName;

        if (propertyType == typeof(string))
        {
            var maxLength = property.GetCustomAttribute<MaxLengthAttribute>();
            if (maxLength != null)
                return $"VARCHAR({maxLength.Length})";

            var stringLength = property.GetCustomAttribute<StringLengthAttribute>();
            if (stringLength != null)
                return $"VARCHAR({stringLength.MaximumLength})";

            return "TEXT";
        }

        if (propertyType == typeof(decimal))
        {
            var precisionAttr = GetEfPrecisionAttribute(property);
            if (precisionAttr != null)
            {
                var precision = (int)precisionAttr.GetType().GetProperty("Precision")!.GetValue(precisionAttr)!;
                var scale = (int)precisionAttr.GetType().GetProperty("Scale")!.GetValue(precisionAttr)!;
                return $"DECIMAL({precision},{scale})";
            }
        }

        if (DefaultTypeMappings.TryGetValue(propertyType, out var mapping))
            return mapping;

        if (propertyType.IsEnum)
            return "INTEGER";

        return "TEXT";
    }

    private static string GetTableName(Type entityType)
    {
        var tableAttr = entityType.GetCustomAttribute<TableAttribute>();
        if (tableAttr != null)
        {
            var name = string.IsNullOrEmpty(tableAttr.Schema)
                ? tableAttr.Name
                : $"{tableAttr.Schema}.{tableAttr.Name}";
            return name;
        }

        return $"{entityType.Name.ToLowerInvariant()}_source";
    }

    private static List<SourceTableColumn> GetColumns(Type entityType)
    {
        var columns = new List<SourceTableColumn>();
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

        foreach (var prop in properties)
        {
            var notMapped = prop.GetCustomAttribute<NotMappedAttribute>();
            if (notMapped != null)
                continue;

            var columnAttr = prop.GetCustomAttribute<ColumnAttribute>();
            var columnName = columnAttr?.Name ?? prop.Name.ToLowerInvariant();

            var isKey = prop.GetCustomAttribute<KeyAttribute>() != null;
            var isRequired = prop.GetCustomAttribute<RequiredAttribute>() != null;
            var nullableType = Nullable.GetUnderlyingType(prop.PropertyType);
            var isNullable = !isRequired && nullableType != null && !isKey;

            var dbGenerated = prop.GetCustomAttribute<DatabaseGeneratedAttribute>();
            var isIdentity = dbGenerated?.DatabaseGeneratedOption == DatabaseGeneratedOption.Identity;

            var propertyType = nullableType ?? prop.PropertyType;
            var columnType = MapTypeToPostgres(prop);

            string? defaultValue = null;
            if (prop.PropertyType == typeof(DateTime) || prop.PropertyType == typeof(DateTime?))
            {
                if (columnName == "created_at" || columnName == "updated_at")
                    defaultValue = "NOW()";
            }

            if (isKey && (propertyType == typeof(int) || propertyType == typeof(long)))
            {
                columnType = propertyType == typeof(int) ? "SERIAL" : "BIGSERIAL";
                isIdentity = false;
            }

            columns.Add(new SourceTableColumn
            {
                Name = columnName,
                Type = columnType,
                IsPrimaryKey = isKey,
                IsNullable = isNullable,
                IsIdentity = isIdentity,
                Default = defaultValue
            });
        }

        var keyProps = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<KeyAttribute>() != null)
            .ToList();

        if (keyProps.Count == 0)
        {
            columns.Insert(0, new SourceTableColumn
            {
                Name = "id",
                Type = "BIGSERIAL",
                IsPrimaryKey = true,
                IsNullable = false,
                IsIdentity = true
            });
        }

        return columns;
    }

    private static List<SourceTableIndex> GetIndexes(Type entityType, string tableName, List<SourceTableColumn> columns)
    {
        var indexes = new List<SourceTableIndex>();

        var indexAttrType = Type.GetType("Microsoft.EntityFrameworkCore.IndexAttribute, Microsoft.EntityFrameworkCore");
        if (indexAttrType != null)
        {
            var indexAttrs = entityType.GetCustomAttributes(indexAttrType);
            foreach (var indexAttrObj in indexAttrs)
            {
                var propertyNamesProp = indexAttrType.GetProperty("PropertyNames");
                var isUniqueProp = indexAttrType.GetProperty("IsUnique");
                var nameProp = indexAttrType.GetProperty("Name");

                if (propertyNamesProp == null) continue;

                var propertyNames = (string[])propertyNamesProp.GetValue(indexAttrObj)!;
                var isUnique = (bool)(isUniqueProp?.GetValue(indexAttrObj) ?? false);

                var indexName = (string?)nameProp?.GetValue(indexAttrObj)
                    ?? $"idx_{tableName.Replace(".", "_")}_{string.Join("_", propertyNames.Select(p => p.ToLowerInvariant()))}";

                var columnNames = propertyNames
                    .Select(p =>
                    {
                        var prop = entityType.GetProperty(p);
                        var colAttr = prop?.GetCustomAttribute<ColumnAttribute>();
                        return colAttr?.Name ?? p.ToLowerInvariant();
                    })
                    .ToList();

                indexes.Add(new SourceTableIndex
                {
                    Name = indexName,
                    Columns = columnNames,
                    IsUnique = isUnique
                });
            }
        }

        var createdAtCol = columns.FirstOrDefault(c => c.Name == "created_at");
        if (createdAtCol != null)
        {
            indexes.Add(new SourceTableIndex
            {
                Name = $"idx_{tableName.Replace(".", "_")}_created",
                Columns = ["created_at"]
            });
        }

        return indexes;
    }

    private static object? GetEfPrecisionAttribute(PropertyInfo property)
    {
        var precisionAttrType = Type.GetType("Microsoft.EntityFrameworkCore.Metadata.Builders.PrecisionAttribute, Microsoft.EntityFrameworkCore");
        if (precisionAttrType == null)
        {
            precisionAttrType = Type.GetType("Microsoft.EntityFrameworkCore.PrecisionAttribute, Microsoft.EntityFrameworkCore");
        }

        if (precisionAttrType != null)
        {
            return property.GetCustomAttribute(precisionAttrType);
        }

        return null;
    }
}
