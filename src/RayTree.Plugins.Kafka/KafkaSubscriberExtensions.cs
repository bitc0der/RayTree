using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Handling;

namespace RayTree.Plugins.Kafka;

public static class KafkaSubscriberExtensions
{
    /// <summary>
    /// Configures a <see cref="KafkaConsumer"/> as the queue source for this entity type.
    /// </summary>
    /// <param name="loggerFactory">
    /// Optional logger factory forwarded to <see cref="KafkaConsumer"/>. When <c>null</c>
    /// (default), falls back to <see cref="NullLoggerFactory.Instance"/> — note that this
    /// silences the topic-wait probe logs. Supply a real logger factory when using
    /// <c>WaitForTopic = true</c> so operators can observe startup progress.
    /// </param>
    public static IEntitySubscriberBuilder<TEntity> UseKafka<TEntity>(
        this IEntitySubscriberBuilder<TEntity> builder,
        Action<KafkaConsumerOptions> configure,
        ILoggerFactory? loggerFactory = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KafkaConsumerOptions();
        configure(options);
        return builder.UseConsumer(new KafkaConsumer(options, loggerFactory ?? NullLoggerFactory.Instance));
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
