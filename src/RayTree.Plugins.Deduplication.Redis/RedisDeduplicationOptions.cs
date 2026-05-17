namespace RayTree.Plugins.Deduplication.Redis;

public sealed class RedisDeduplicationOptions
{
    public string KeyPrefix { get; set; } = "default";
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Redis logical database index. -1 uses the default database from the connection multiplexer.
    /// </summary>
    public int Database { get; set; } = -1;
}
