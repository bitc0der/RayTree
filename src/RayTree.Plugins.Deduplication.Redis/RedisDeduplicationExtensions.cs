using RayTree.Core.Handling;
using RayTree.Core.Tracking;
using RayTree.Plugins.Deduplication.Redis;
using StackExchange.Redis;

namespace RayTree;

public static class RedisDeduplicationExtensions
{
    public static IChangeSubscriberBuilder UseRedisDeduplication(
        this IChangeSubscriberBuilder builder,
        IConnectionMultiplexer multiplexer)
        => builder.UseDeduplicationStore(new RedisDeduplicationStore(multiplexer, new RedisDeduplicationOptions()));

    public static IChangeSubscriberBuilder UseRedisDeduplication(
        this IChangeSubscriberBuilder builder,
        IConnectionMultiplexer multiplexer,
        Action<RedisDeduplicationOptions> configure)
    {
        var options = new RedisDeduplicationOptions();
        configure(options);
        return builder.UseDeduplicationStore(new RedisDeduplicationStore(multiplexer, options));
    }

    public static IChangeTrackingBuilder UseRedisDeduplication(
        this IChangeTrackingBuilder builder,
        IConnectionMultiplexer multiplexer)
        => builder.UseDeduplicationStore(new RedisDeduplicationStore(multiplexer, new RedisDeduplicationOptions()));

    public static IChangeTrackingBuilder UseRedisDeduplication(
        this IChangeTrackingBuilder builder,
        IConnectionMultiplexer multiplexer,
        Action<RedisDeduplicationOptions> configure)
    {
        var options = new RedisDeduplicationOptions();
        configure(options);
        return builder.UseDeduplicationStore(new RedisDeduplicationStore(multiplexer, options));
    }
}
