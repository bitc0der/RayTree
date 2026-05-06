using RayTree.Models;
using RayTree.Tracking;
using System.IO.Pipelines;

namespace RayTree.Plugins;

/// <summary>
/// Serializes and deserializes <see cref="EntityChange"/> instances for transport or storage.
/// Implementations must support both the non-generic base type and the generic
/// <see cref="EntityChange{TEntity}"/> that carries typed entity state.
/// </summary>
public interface IChangeSerializer
{
    /// <summary>Gets the human-readable name of this serializer (e.g. "Json", "Protobuf").</summary>
    string Name { get; }

    /// <summary>Serializes a non-generic <see cref="EntityChange"/> to <paramref name="destination"/>.</summary>
    Task SerializeAsync(EntityChange change, PipeWriter destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes an <see cref="EntityChange{TEntity}"/>, including the typed <see cref="EntityChange{TEntity}.State"/>,
    /// to <paramref name="destination"/>.
    /// </summary>
    Task SerializeAsync<TEntity>(EntityChange<TEntity> change, PipeWriter destination, CancellationToken cancellationToken = default);

    /// <summary>Deserializes a non-generic <see cref="EntityChange"/> from <paramref name="source"/>.</summary>
    Task<EntityChange> DeserializeAsync(PipeReader source, string entityType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deserializes an <see cref="EntityChange{TEntity}"/> from <paramref name="source"/>,
    /// restoring the typed <see cref="EntityChange{TEntity}.State"/> property.
    /// </summary>
    Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(PipeReader source, CancellationToken cancellationToken = default);
}
