using System.Reflection;
using System.Text.RegularExpressions;

namespace RayTree.Plugins.PostgreSQL;

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
            var isNullable = !prop.PropertyType.IsValueType || Nullable.GetUnderlyingType(prop.PropertyType) != null;
            var underlyingType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            result.Add(new PropertyColumn(prop, "state_" + ToSnakeCase(prop.Name), ToPostgresType(underlyingType), isNullable));
        }
        return result;
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
}
