using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMqMicroservices.Shared;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Serialization;
using RayTree.Hosting;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.RabbitMQ;
using RayTree.Plugins.Serializers.MessagePack;

var rmqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChangeTracking(builder.Configuration, cfg =>
{
    cfg
        // Payload pipeline MUST match OrderService exactly (MessagePack + Gzip).
        // A mismatch here means deserialization throws on the first envelope and the consumer crashes.
        .UseSerializer<IChangeSerializer>(_ => new MessagePackSerializerPlugin())
        .UseCompressor<IChangeCompressor>(_ => new GzipCompressorPlugin())
        .ForEntity<Order>(e => e
            .UseConsumer(new RabbitMqConsumer(new RabbitMqConsumerOptions
            {
                HostName = rmqHost,
                // Durable, non-exclusive, non-auto-delete queue — survives broker restarts and supports
                // horizontal scaling (multiple consumer replicas compete for the same queue).
                QueueName = "notification-service.orders",
                Durable = true,
                DeclareQueue = true,
                // Wildcard binding receives every change type for the Order entity. Together with
                // OrderService's RoutingKeySelector this yields keys like `change.Order.insert`.
                ExchangeName = "raytree.changes",
                BindingKey = "change.Order.*",
            }))
            // Shared-handler dispatch: all three handlers run sequentially in registration order
            // on every matching delivery. Each handler binds to exactly one ChangeType.
            .OnInsert(LogInsertAsync)
            .OnUpdate(LogUpdateAsync)
            .OnDelete(LogDeleteAsync));
});

// ChangeTrackingHostedService (registered by AddChangeTracking) drives StartAsync/StopAsync.
// IHostApplicationLifetime handles graceful shutdown for Ctrl+C and `docker compose down`.
await builder.Build().RunAsync();

// ---- handlers -------------------------------------------------------------------------------

static Task LogInsertAsync(RayTree.Core.Models.EntityChange<Order> change, CancellationToken ct)
{
    var order = change.State;
    Console.WriteLine(
        $"[NOTIFY] NEW order {order?.Id} — customer={order?.CustomerName} total={order?.TotalAmount:C} status={order?.Status}");
    return Task.CompletedTask;
}

static Task LogUpdateAsync(RayTree.Core.Models.EntityChange<Order> change, CancellationToken ct)
{
    var order = change.State;
    Console.WriteLine(
        $"[NOTIFY] UPDATED order {order?.Id} — status={order?.Status} total={order?.TotalAmount:C}");
    return Task.CompletedTask;
}

static Task LogDeleteAsync(RayTree.Core.Models.EntityChange<Order> change, CancellationToken ct)
{
    Console.WriteLine($"[NOTIFY] DELETED order {change.EntityId}");
    return Task.CompletedTask;
}
