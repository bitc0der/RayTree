using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RayTree.Plugins.PostgreSQL.Outbox;

public static class EntityColumnMapper
{
    public sealed record PropertyColumn(
        PropertyInfo Property,
        string ColumnName,
        string ColumnType,
        bool IsNullable);

    public static IReadOnlyList<PropertyColumn> GetColumns(Type entityType)
    {
        var result = new List<PropertyColumn>();
        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;
            if (prop.IsDefined(typeof(NotMappedAttribute), inherit: true))
                continue;

            var underlyingType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            result.Add(new PropertyColumn(
                prop,
                ResolveColumnName(prop),
                ResolveColumnType(prop, underlyingType),
                ResolveNullability(prop)));
        }

        return result;
    }

    public static string GetTableName(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<TableAttribute>();
        return attr?.Name ?? ToSnakeCase(entityType.Name);
    }

    public static string ToSnakeCase(string name)
        => Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    public static string ToPostgresType(Type type) => type switch
    {
        _ when type == typeof(short) || type == typeof(byte) || type == typeof(sbyte) => "SMALLINT",
        _ when type == typeof(int) => "INTEGER",
        _ when type == typeof(long) => "BIGINT",
        _ when type == typeof(float) => "REAL",
        _ when type == typeof(double) => "DOUBLE PRECISION",
        _ when type == typeof(decimal) => "NUMERIC",
        _ when type == typeof(bool) => "BOOLEAN",
        _ when type == typeof(Guid) => "UUID",
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => "TIMESTAMPTZ",
        _ => "TEXT"
    };

    private static string ResolveColumnName(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<ColumnAttribute>();
        var suffix = attr?.Name is { Length: > 0 } name ? name : ToSnakeCase(prop.Name);
        return "state_" + suffix;
    }

    private static string ResolveColumnType(PropertyInfo prop, Type underlyingType)
    {
        var attr = prop.GetCustomAttribute<ColumnAttribute>();
        if (attr?.TypeName is { Length: > 0 } typeName)
            return typeName;

        if (underlyingType == typeof(string))
        {
            var maxLength = prop.GetCustomAttribute<MaxLengthAttribute>()?.Length
                            ?? prop.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength;
            if (maxLength > 0)
                return $"VARCHAR({maxLength})";
        }

        return ToPostgresType(underlyingType);
    }

    private static bool ResolveNullability(PropertyInfo prop)
    {
        if (prop.IsDefined(typeof(RequiredAttribute), inherit: true))
            return false;
        return !prop.PropertyType.IsValueType || Nullable.GetUnderlyingType(prop.PropertyType) != null;
    }
}
