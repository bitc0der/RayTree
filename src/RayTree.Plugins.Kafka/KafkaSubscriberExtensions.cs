using RayTree.Subscriber;

namespace RayTree.Plugins.Kafka;

public static class KafkaSubscriberExtensions
{
    public static ChangeSubscriberConfiguration UseKafka<TEntity>(
        this ChangeSubscriberConfiguration config,
        Action<KafkaConsumerOptions> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KafkaConsumerOptions();
        configure(options);
        return config.UseQueue<TEntity>(new KafkaConsumer(options));
    }

    public static KafkaConsumerOptions WithTopic(this KafkaConsumerOptions options, string topic)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Topic = topic;
        return options;
    }

    public static KafkaConsumerOptions WithGroupId(this KafkaConsumerOptions options, string groupId)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.GroupId = groupId;
        return options;
    }
}
