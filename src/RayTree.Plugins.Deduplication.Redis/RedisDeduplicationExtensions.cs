using RayTree.Core.Handling;
using RayTree.Core.Tracking;
using RayTree.Plugins.Deduplication.Redis;
using StackExchange.Redis;

namespace RayTree.Plugins.Deduplication.Redis;

public static class RedisDeduplicationExtensions
{
    /// <summary>
    /// Registers a <see cref="RedisDeduplicationStore"/> with default options as the deduplication store
    /// for the subscriber being configured.
    /// </summary>
    /// <param name="builder">The subscriber builder.</param>
    /// <param name="multiplexer">An open StackExchange.Redis connection multiplexer.</param>
    public static IChangeSubscriberBuilder UseRedisDeduplication(
        this IChangeSubscriberBuilder builder,
        IConnectionMultiplexer multiplexer)
        => builder.UseDeduplicationStore(
            new RedisDeduplicationStore(multiplexer: multiplexer, options: new RedisDeduplicationOptions()));

    /// <summary>
    /// Registers a <see cref="RedisDeduplicationStore"/> with custom options as the deduplication store
    /// for the subscriber being configured.
    /// </summary>
    /// <param name="builder">The subscriber builder.</param>
    /// <param name="multiplexer">An open StackExchange.Redis connection multiplexer.</param>
    /// <param name="configure">Callback to configure <see cref="RedisDeduplicationOptions"/>.</param>
    public static IChangeSubscriberBuilder UseRedisDeduplication(
        this IChangeSubscriberBuilder builder,
        IConnectionMultiplexer multiplexer,
        Action<RedisDeduplicationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RedisDeduplicationOptions();
        configure(options);
        return builder.UseDeduplicationStore(
            new RedisDeduplicationStore(multiplexer: multiplexer, options: options));
    }

    /// <summary>
    /// Registers a <see cref="RedisDeduplicationStore"/> with default options as the deduplication store
    /// for the tracker being configured.
    /// </summary>
    /// <param name="builder">The change tracking builder.</param>
    /// <param name="multiplexer">An open StackExchange.Redis connection multiplexer.</param>
    public static IChangeTrackingBuilder UseRedisDeduplication(
        this IChangeTrackingBuilder builder,
        IConnectionMultiplexer multiplexer)
        => builder.UseDeduplicationStore(
            new RedisDeduplicationStore(multiplexer: multiplexer, options: new RedisDeduplicationOptions()));

    /// <summary>
    /// Registers a <see cref="RedisDeduplicationStore"/> with custom options as the deduplication store
    /// for the tracker being configured.
    /// </summary>
    /// <param name="builder">The change tracking builder.</param>
    /// <param name="multiplexer">An open StackExchange.Redis connection multiplexer.</param>
    /// <param name="configure">Callback to configure <see cref="RedisDeduplicationOptions"/>.</param>
    public static IChangeTrackingBuilder UseRedisDeduplication(
        this IChangeTrackingBuilder builder,
        IConnectionMultiplexer multiplexer,
        Action<RedisDeduplicationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RedisDeduplicationOptions();
        configure(options);
        return builder.UseDeduplicationStore(
            new RedisDeduplicationStore(multiplexer: multiplexer, options: options));
    }
}
