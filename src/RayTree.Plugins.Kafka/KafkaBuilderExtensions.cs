using Microsoft.Extensions.Logging;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Kafka;

public static class KafkaBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="KafkaPublisher"/> as the queue publisher for every entity type.
    /// </summary>
    public static IChangeTrackingBuilder UseKafka(
        this IChangeTrackingBuilder builder,
        Action<KafkaPublisherOptions> configure,
        ILoggerFactory? loggerFactory = null)
    {
        var options = new KafkaPublisherOptions();
        configure(options);
        return builder.UsePublisher<IQueuePublisher>(_ => new KafkaPublisher(options, loggerFactory));
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
