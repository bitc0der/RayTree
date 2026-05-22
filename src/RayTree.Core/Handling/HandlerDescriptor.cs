namespace RayTree.Core.Handling;

/// <summary>
/// Resolves a human-readable name for a delegate's declaring scope, stripping the
/// compiler-generated closure/display-class names (e.g. <c>&lt;&gt;c__DisplayClass3_0</c>)
/// that lambdas normally surface so log output shows the user's outer type instead.
/// </summary>
internal static class HandlerDescriptor
{
    internal static string Describe(Delegate handler)
    {
        var t = handler.Method.DeclaringType;
        while (t is not null && (t.Name.StartsWith("<>", StringComparison.Ordinal) || t.Name.Contains("DisplayClass")))
            t = t.DeclaringType;
        return t?.Name ?? "<delegate>";
    }
}
