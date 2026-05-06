using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Kafka;

public static class KafkaBuilderExtensions
{
    public static IChangeTrackingBuilder UseKafka(
        this IChangeTrackingBuilder builder,
        Action<KafkaPublisherOptions> configure)
    {
        var options = new KafkaPublisherOptions();
        configure(options);
        return builder.UseQueue<IQueuePublisher>(_ => new KafkaPublisher(options));
    }

    public static KafkaPublisherOptions WithTopic(this KafkaPublisherOptions options, string topic)
    {
        options.Topic = topic;
        return options;
    }

    public static KafkaPublisherOptions WithAcks(this KafkaPublisherOptions options, string acks)
    {
        options.Acks = acks;
        return options;
    }
}
