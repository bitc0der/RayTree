namespace RayTree.Core.Handling;

/// <summary>
/// Identifies a named handler within the Isolated dispatch mode.
/// Used as the dictionary key for isolated consumers and handler registrations in
/// <see cref="ChangeSubscriber"/>.
/// </summary>
/// <param name="EntityType">The CLR type of the entity being handled.</param>
/// <param name="HandlerName">The stable handler name assigned at registration time.</param>
public readonly record struct EntityHandlerKey(Type EntityType, string HandlerName);
