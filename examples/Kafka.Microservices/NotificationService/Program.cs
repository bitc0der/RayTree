using KafkaMicroservices.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Serialization;
using RayTree.Hosting;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.Kafka;
using RayTree.Plugins.Serializers.MessagePack;

var kafkaBootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";

var builder = Host.CreateApplicationBuilder(args);

// KafkaConsumer requires an ILoggerFactory at construction. We reuse a dedicated console-logging
// factory for the consumer instance (the host's own pipeline is unaffected).
var pluginLoggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information));

// Handlers close over this logger so structured log properties (OrderId, Status, etc.) flow through
// the Generic Host pipeline — level filtering, formatters, and scopes — rather than bypassing it
// via Console.WriteLine.
var handlerLogger = pluginLoggerFactory.CreateLogger("Notifications");

builder.Services.AddChangeTracking(builder.Configuration, cfg =>
{
    cfg
        // Payload pipeline MUST match OrderService exactly (MessagePack + Gzip).
        // A mismatch here means deserialization throws on the first envelope and the consumer crashes.
        .UseSerializer<IChangeSerializer>(_ => new MessagePackSerializerPlugin())
        .UseCompressor<IChangeCompressor>(_ => new GzipCompressorPlugin())
        .ForEntity<Order>(e => e
            // FromEarliest = true (default) so a fresh consumer group replays from offset 0 — this is
            // why notification-service is allowed to start before order-service. AckAfterHandler = false
            // (default) means the offset is committed on the poll thread immediately after parsing
            // (at-most-once). Switch to true plus SubscriberOptions.MaxDegreeOfParallelism = 1 for
            // at-least-once delivery.
            .UseConsumer(new KafkaConsumer(new KafkaConsumerOptions
            {
                BootstrapServers = kafkaBootstrap,
                Topic = "raytree.order_changes",
                GroupId = "notification-service",
            }, pluginLoggerFactory))
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
