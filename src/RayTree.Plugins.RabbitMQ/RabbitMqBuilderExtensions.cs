using Microsoft.Extensions.Logging;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.RabbitMQ;

public static class RabbitMqBuilderExtensions
{
    public static IChangeTrackingBuilder UseRabbitMq(
        this IChangeTrackingBuilder builder,
        Action<RabbitMqPublisherOptions> configure,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RabbitMqPublisherOptions();
        configure(options);
        return builder.UsePublisher<IQueuePublisher>(_ => new RabbitMqPublisher(options, loggerFactory));
    }

    public static RabbitMqPublisherOptions WithExchange(
        this RabbitMqPublisherOptions options,
        string exchangeName,
        string exchangeType = "topic",
        bool durable = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exchangeType);
        ArgumentNullException.ThrowIfNull(exchangeName);

        options.ExchangeName = exchangeName;
        options.ExchangeType = exchangeType;
        options.Durable = durable;
        return options;
    }

    public static RabbitMqPublisherOptions WithRoutingKey(
        this RabbitMqPublisherOptions options,
        string routingKey)
    {
        options.RoutingKey = routingKey;
        return options;
    }
}
