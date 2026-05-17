namespace RayTree.Plugins.Deduplication.Redis;

/// <summary>Configuration options for <see cref="RedisDeduplicationStore"/>.</summary>
public sealed class RedisDeduplicationOptions
{
    /// <summary>
    /// Key namespace prefix inserted between the <c>raytree:dedup:</c> root and the correlation ID.
    /// Use a distinct value per deployment when multiple RayTree instances share one Redis server.
    /// Defaults to <c>"default"</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "default";

    /// <summary>
    /// How long each processed correlation ID is retained in Redis.
    /// Must be at least as long as the broker's maximum redelivery window to prevent duplicate processing.
    /// Defaults to 24 hours.
    /// </summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Redis logical database index. <c>-1</c> selects the default database from the connection multiplexer.
    /// </summary>
    public int Database { get; set; } = -1;
}
