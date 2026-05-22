using KafkaMicroservices.OrderService;
using KafkaMicroservices.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;
using RayTree.Hosting;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.Kafka;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Repository;
using RayTree.Plugins.Serializers.MessagePack;

// Connection info comes from environment variables so the same binary works under
// docker-compose (service names 'postgres' / 'kafka') and against a developer's localhost.
var pgConnection = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=raytree_example;Username=postgres;Password=postgres";
var kafkaBootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";

var builder = Host.CreateApplicationBuilder(args);

// Plugin instances need an ILoggerFactory at construction; AddChangeTracking's configure callback
// runs inside a DI singleton factory which doesn't expose the IServiceProvider. We build a
// dedicated console-logging factory and reuse it for the outbox, repository, and consumer plugins.
var pluginLoggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information));

// Single PostgreSqlRepository<Order> instance is shared between:
//   1) The EntityChangeTracker (via .UseRepository) — so its InitializeAsync runs on startup.
//   2) DI (as IRepository<Order>) — so OrderSimulator can call repository.InsertAsync/UpdateAsync/DeleteAsync.
var orderRepository = new PostgreSqlRepository<Order>(
    new PostgreSqlRepositoryOptions
    {
        ConnectionString = pgConnection,
    },
    pluginLoggerFactory);

builder.Services.AddSingleton<IRepository<Order>>(orderRepository);

// Register RayTree via the documented "primary registration path" from RayTree.Hosting.
// AddChangeTracking wires the EntityChangeTracker singleton, RayTreeMeter, and ChangeTrackingHostedService.
builder.Services.AddChangeTracking(builder.Configuration, cfg =>
{
    cfg
        // 500 ms polling keeps the demo snappy without hammering the DB.
        // For production, prefer NOTIFY/LISTEN via PostgreSqlOutboxOptions.UseNotificationChannel.
        .UsePublisherOptions(o => o.PollingInterval = TimeSpan.FromMilliseconds(500))
        // MessagePack + Gzip on the payload pipeline. NotificationService MUST register the same pair.
        .UseSerializer<IChangeSerializer>(_ => new MessagePackSerializerPlugin())
        .UseCompressor<IChangeCompressor>(_ => new GzipCompressorPlugin())
        .ForEntity<Order>(e => e
            .UseRepository(orderRepository)
            .UseOutbox(new PostgreSqlOutbox<Order>(
                new PostgreSqlOutboxOptions
                {
                    ConnectionString = pgConnection,
                },
                pluginLoggerFactory))
            // Default KeySelector is "{EntityType}:{EntityId}" — all changes for the same Order land on
            // the same Kafka partition, preserving per-entity ordering. Override KeySelector to shard
            // by tenant or aggregate root.
            .UsePublisher(new KafkaPublisher(new KafkaPublisherOptions
            {
                BootstrapServers = kafkaBootstrap,
                Topic = "raytree.order_changes",
            })));
});

// OrderSimulator drives the demo: inserts, updates, and deletes Orders in a loop so the
// notification side has a steady stream of events to react to.
builder.Services.AddHostedService<OrderSimulator>();

// Graceful shutdown (Ctrl+C / SIGTERM / `docker compose down`) is driven by IHostApplicationLifetime
// — no manual cancellation wiring is needed.
await builder.Build().RunAsync();
