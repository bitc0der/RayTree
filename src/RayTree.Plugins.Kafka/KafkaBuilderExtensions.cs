using Microsoft.Extensions.Logging;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Kafka;

public static class KafkaBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="KafkaPublisher"/> as the queue publisher for every entity type.
    /// </summary>
    /// <param name="meter">
    /// Optional. When supplied, the publisher emits <c>raytree.connection.*</c> metrics on
    /// fatal-error disposes and rebuilds. Pass the same <c>RayTreeMeter</c> the rest of the
    /// tracker uses (e.g. resolved from the host DI container) so connection metrics share
    /// the meter listener / OTel collection. When <c>null</c>, the publisher works normally
    /// but emits no connection metrics.
    /// </param>
    public static IChangeTrackingBuilder UseKafka(
        this IChangeTrackingBuilder builder,
        Action<KafkaPublisherOptions> configure,
        ILoggerFactory? loggerFactory = null,
        RayTreeMeter? meter = null)
    {
        var options = new KafkaPublisherOptions();
        configure(options);
        return builder.UsePublisher<IQueuePublisher>(_ => new KafkaPublisher(options, loggerFactory, meter));
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
