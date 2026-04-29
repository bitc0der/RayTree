using RayTree.Plugins;

namespace RayTree.Models;

public class EntityConfiguration
{
    public Type EntityType { get; set; } = typeof(object);
    public string EntityTypeName { get; set; } = string.Empty;
    public string? SourceTable { get; set; }
    public string? OutboxTable { get; set; }

    public string? RepositoryKey { get; set; }
    public string? OutboxKey { get; set; }
    public string? QueuePublisherKey { get; set; }
    public string? SerializerKey { get; set; }
    public string? CompressorKey { get; set; }
}
