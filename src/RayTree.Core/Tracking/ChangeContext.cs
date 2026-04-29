using System.Collections.Concurrent;
using RayTree.Models;

namespace RayTree.Tracking;

public static class ChangeContext
{
    private static readonly AsyncLocal<Dictionary<object, List<EntityChange>>> _changes = new();

    public static void Set(object key, List<EntityChange> changes)
    {
        _changes.Value ??= new Dictionary<object, List<EntityChange>>();
        _changes.Value[key] = changes;
    }

    public static IReadOnlyList<EntityChange> Get(object key)
    {
        return _changes.Value?.TryGetValue(key, out var changes) == true ? changes : Array.Empty<EntityChange>();
    }

    public static void Clear(object key)
    {
        _changes.Value?.Remove(key);
    }
}
