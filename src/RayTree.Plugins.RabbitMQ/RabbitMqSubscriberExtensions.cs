using RayTree.Core.Handling;

namespace RayTree.Plugins.RabbitMQ;

public static class RabbitMqSubscriberExtensions
{
    /// <summary>
    /// Configures a <see cref="RabbitMqConsumer"/> as the queue source for this entity type.
    /// </summary>
    public static IEntitySubscriberBuilder<TEntity> UseRabbitMq<TEntity>(
        this IEntitySubscriberBuilder<TEntity> builder,
        Action<RabbitMqConsumerOptions> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RabbitMqConsumerOptions();
        configure(options);
        return builder.UseQueue(new RabbitMqConsumer(options));
    }

    public static RabbitMqConsumerOptions WithQueue(
        this RabbitMqConsumerOptions options,
        string queueName,
        bool durable = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.QueueName = queueName;
        options.Durable   = durable;
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
        options.BindingKey   = bindingKey;
        return options;
    }
}
