using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMqMicroservices.OrderService;
using RabbitMqMicroservices.Shared;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;
using RayTree.Hosting;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Repository;
using RayTree.Plugins.RabbitMQ;
using RayTree.Plugins.Serializers.MessagePack;

// Connection info comes from environment variables so the same binary works under
// docker-compose (services 'postgres' / 'rabbitmq') and against a developer's localhost.
var pgConnection = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=raytree_example;Username=postgres;Password=postgres";
var rmqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";

var builder = Host.CreateApplicationBuilder(args);

// Plugin instances need an ILoggerFactory at construction; AddChangeTracking's configure callback
// is invoked inside a DI singleton factory which doesn't expose the IServiceProvider. We build a
// dedicated console-logging factory and reuse it for the outbox and repository plugins.
// The host's own logger pipeline is unaffected — OrderSimulator and ChangeTrackingHostedService
// still log through DI.
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
        TableName = "orders",
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
                    OutboxTableName = "order_outbox",
                },
                pluginLoggerFactory))
            .UsePublisher(new RabbitMqPublisher(new RabbitMqPublisherOptions
            {
                HostName = rmqHost,
                ExchangeName = "raytree.changes",
                ExchangeType = "topic",
                Durable = true,
                DeclareExchange = true,
                // EntityType comes through as the CLR full name (e.g. RabbitMqMicroservices.Shared.Order).
                // Strip the namespace prefix so consumers can bind with the readable pattern `change.Order.*`.
                RoutingKeySelector = envelope =>
                {
                    var shortName = envelope.EntityType.Split('.').Last();
                    return $"change.{shortName}.{envelope.ChangeType.ToString().ToLowerInvariant()}";
                },
            })));
});

// OrderSimulator drives the demo: inserts, updates, and deletes Orders in a loop so the
// notification side has a steady stream of events to react to.
builder.Services.AddHostedService<OrderSimulator>();

// Graceful shutdown (Ctrl+C / SIGTERM / `docker compose down`) is driven by IHostApplicationLifetime
// — no manual cancellation wiring is needed.
await builder.Build().RunAsync();
