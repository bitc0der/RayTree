using RayTree.Core.Handling;
using RayTree.Core.Telemetry;

namespace RayTree.Plugins.RabbitMQ;

public static class RabbitMqSubscriberExtensions
{
    /// <summary>
    /// Configures a <see cref="RabbitMqConsumer"/> as the queue source for this entity type.
    /// </summary>
    /// <param name="meter">
    /// Optional. When supplied, the consumer emits <c>raytree.connection.*</c> metrics on
    /// SDK recovery events. The consumer itself has no logger field (existing exception to
    /// the logging-placement rule), so logs are not emitted regardless of meter setting.
    /// </param>
    public static IEntitySubscriberBuilder<TEntity> UseRabbitMq<TEntity>(
        this IEntitySubscriberBuilder<TEntity> builder,
        Action<RabbitMqConsumerOptions> configure,
        RayTreeMeter? meter = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RabbitMqConsumerOptions();
        configure(options);
        return builder.UseConsumer(new RabbitMqConsumer(options, meter));
    }

    public static RabbitMqConsumerOptions WithQueue(
        this RabbitMqConsumerOptions options,
        string queueName,
        bool durable = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.QueueName = queueName;
        options.Durable = durable;
        return options;
    }

    public static RabbitMqConsumerOptions WithPrefetch(
        this RabbitMqConsumerOptions options,
        ushort prefetchCount)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PrefetchCount = prefetchCount;
        return options;
    }

    public static RabbitMqConsumerOptions BindToExchange(
        this RabbitMqConsumerOptions options,
        string exchangeName,
        string bindingKey = "#")
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ExchangeName = exchangeName;
        options.BindingKey = bindingKey;
        return options;
    }
}
