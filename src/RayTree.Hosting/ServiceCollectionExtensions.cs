using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Tracking;

namespace RayTree.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChangeTracking(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<IChangeTrackingBuilder>? configure = null)
    {
        services.AddSingleton<EntityChangeTrackerFactory>();
        services.AddSingleton<EntityChangeTracker>(sp =>
        {
            var builder = new ChangeTrackingBuilder(sp.GetService<ILoggerFactory>());
            configure?.Invoke(builder);
            return builder.Build();
        });
        services.AddSingleton<IEntityChangeTracker>(sp => sp.GetRequiredService<EntityChangeTracker>());

        if (configuration != null)
        {
            services.Configure<OutboxPublisherOptions>(configuration.GetSection("ChangeTracking:Publisher"));
            services.Configure<SubscriberOptions>(configuration.GetSection("ChangeTracking:Subscriber"));
        }

        services.AddSingleton<OutboxCleanupService>(sp =>
        {
            var options = sp.GetService<IOptions<OutboxPublisherOptions>>()?.Value ?? new OutboxPublisherOptions();
            var tracker = sp.GetRequiredService<EntityChangeTracker>();
            var outboxes = tracker.Publisher.GetOutboxes().Values;
            var logger = sp.GetRequiredService<ILogger<OutboxCleanupService>>();
            return new OutboxCleanupService(outboxes, logger, options.PollingInterval * 10);
        });

        services.AddHostedService<ChangeTrackingHostedService>();

        return services;
    }
}
