using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
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

            var columnAttr = prop.GetCustomAttribute<ColumnAttribute>(inherit: true);
            var underlyingType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            result.Add(new PropertyColumn(
                prop,
                ResolveColumnName(prop, columnAttr),
                ResolveColumnType(prop, columnAttr, underlyingType),
                ResolveNullability(prop)));
        }

        return result;
    }

    public static string GetTableName(Type entityType)
        => entityType.GetCustomAttribute<TableAttribute>()?.Name ?? ToSnakeCase(entityType.Name);

    public static IReadOnlyList<PropertyInfo> GetKeyProperties(Type entityType)
    {
        var props = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var keyed = props
            .Where(p => p.CanRead && p.CanWrite && p.IsDefined(typeof(KeyAttribute), inherit: true))
            .OrderBy(p =>
            {
                var order = p.GetCustomAttribute<ColumnAttribute>(inherit: true)?.Order ?? -1;
                return order >= 0 ? order : int.MaxValue;
            })
            .ThenBy(p => Array.IndexOf(props, p))
            .ToList();

        if (keyed.Count > 0)
            return keyed;

        var idProp = props.FirstOrDefault(p => p.Name == "Id" && p.CanRead && p.CanWrite);
        if (idProp != null)
            return [idProp];

        throw new InvalidOperationException(
            $"Entity type '{entityType.Name}' has no [Key]-annotated property and no 'Id' convention property. " +
            $"Annotate a property with [Key] or add a property named 'Id'.");
    }

    public static string ToSnakeCase(string name)
        => Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    public static string ToPostgresType(Type type)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            var elementType = Nullable.GetUnderlyingType(type.GetElementType()!) ?? type.GetElementType()!;
            return ToPostgresType(elementType) + "[]";
        }

        return type switch
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

    public static object ConvertFromDb(object value, Type targetType)
        => targetType.IsAssignableFrom(value.GetType()) ? value : Convert.ChangeType(value, targetType);

    // PropertyInfo.SetValue is live reflection on every call; compiling one delegate per
    // property up front and caching it turns per-row, per-column reflection into a cheap
    // delegate invocation once a property has been seen.
    private static readonly ConcurrentDictionary<PropertyInfo, Action<object, object?>> _setterCache = new();

    public static void SetValue(PropertyInfo property, object target, object? value)
        => _setterCache.GetOrAdd(property, CompileSetter)(target, value);

    private static Action<object, object?> CompileSetter(PropertyInfo property)
    {
        var targetParam = Expression.Parameter(typeof(object), "target");
        var valueParam = Expression.Parameter(typeof(object), "value");
        var call = Expression.Call(
            Expression.Convert(targetParam, property.DeclaringType!),
            property.SetMethod!,
            Expression.Convert(valueParam, property.PropertyType));
        return Expression.Lambda<Action<object, object?>>(call, targetParam, valueParam).Compile();
    }

    private static string ResolveColumnName(PropertyInfo prop, ColumnAttribute? columnAttr)
    {
        var suffix = columnAttr?.Name is { Length: > 0 } name ? name : ToSnakeCase(prop.Name);
        return "state_" + suffix;
    }

    private static string ResolveColumnType(PropertyInfo prop, ColumnAttribute? columnAttr, Type underlyingType)
    {
        if (columnAttr?.TypeName is { Length: > 0 } typeName)
            return typeName;

        if (underlyingType == typeof(string))
        {
            var maxLength = prop.GetCustomAttribute<MaxLengthAttribute>(inherit: true)?.Length
                            ?? prop.GetCustomAttribute<StringLengthAttribute>(inherit: true)?.MaximumLength;
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
