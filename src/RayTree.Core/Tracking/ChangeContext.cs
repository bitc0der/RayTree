using RayTree.Core.Models;

namespace RayTree.Core.Tracking;

public static class ChangeContext
{
    private static readonly AsyncLocal<Dictionary<object, List<EntityChange>>> Changes = new();

    public static void Set(object key, List<EntityChange> changes)
    {
        Changes.Value ??= new Dictionary<object, List<EntityChange>>();
        Changes.Value[key] = changes;
    }

    public static IReadOnlyList<EntityChange> Get(object key)
    {
        return Changes.Value?.TryGetValue(key, out var changes) == true ? changes : Array.Empty<EntityChange>();
    }

    public static void Clear(object key)
    {
        Changes.Value?.Remove(key);
    }
}
