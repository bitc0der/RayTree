using RayTree.Subscriber;

namespace RayTree.Plugins.RabbitMQ;

public static class RabbitMqSubscriberExtensions
{
    public static ChangeSubscriberConfiguration UseRabbitMq<TEntity>(
        this ChangeSubscriberConfiguration config,
        Action<RabbitMqConsumerOptions> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RabbitMqConsumerOptions();
        configure(options);
        return config.UseQueue<TEntity>(new RabbitMqConsumer(options));
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
}
