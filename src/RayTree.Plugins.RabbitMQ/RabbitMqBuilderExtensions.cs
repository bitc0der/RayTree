using Microsoft.Extensions.DependencyInjection;
using RayTree.Plugins.RabbitMQ;

namespace RayTree.Plugins;

public static class RabbitMqBuilderExtensions
{
    public static IChangeTrackingBuilder UseRabbitMq(this IChangeTrackingBuilder builder, Action<RabbitMqPublisherOptions> configure)
    {
        var options = new RabbitMqPublisherOptions();
        configure(options);
        return builder.UseQueue<IQueuePublisher>(_ => new RabbitMqPublisher(options));
    }

    public static RabbitMqPublisherOptions WithExchange(
        this RabbitMqPublisherOptions options,
        string exchangeName,
        string exchangeType = "topic",
        bool durable = true)
    {
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
