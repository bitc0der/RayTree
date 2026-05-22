using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMqMicroservices.Shared;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Serialization;
using RayTree.Hosting;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.RabbitMQ;
using RayTree.Plugins.Serializers.MessagePack;

var rmqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";

var builder = Host.CreateApplicationBuilder(args);

// Handlers close over this logger so structured log properties (OrderId, Status, etc.) flow through
// the Generic Host logging pipeline rather than bypassing it via Console.WriteLine.
var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information));
var handlerLogger = loggerFactory.CreateLogger("Notifications");

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
                // Probe the exchange passively until order-service declares it. This replaces
                // the compose-level depends_on: order-service with an application-level readiness
                // check, correctly decoupling startup order without tight container coupling.
                WaitForTopology = true,
                TopologyWaitInterval = TimeSpan.FromSeconds(5),
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
// Local functions close over handlerLogger — non-static so they capture the variable above.

Task LogInsertAsync(EntityChange<Order> change, CancellationToken ct)
{
    var order = change.State;
    handlerLogger.LogInformation(
        "[NOTIFY] NEW order {OrderId} — customer={Customer} total={Total:C} status={Status}",
        order?.Id, order?.CustomerName, order?.TotalAmount, order?.Status);
    return Task.CompletedTask;
}

Task LogUpdateAsync(EntityChange<Order> change, CancellationToken ct)
{
    var order = change.State;
    handlerLogger.LogInformation(
        "[NOTIFY] UPDATED order {OrderId} — status={Status} total={Total:C}",
        order?.Id, order?.Status, order?.TotalAmount);
    return Task.CompletedTask;
}

Task LogDeleteAsync(EntityChange<Order> change, CancellationToken ct)
{
    handlerLogger.LogInformation("[NOTIFY] DELETED order {OrderId}", change.EntityId);
    return Task.CompletedTask;
}
