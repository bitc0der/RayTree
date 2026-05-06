using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RayTree.Core.Distribution;
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
            sp.GetRequiredService<EntityChangeTrackerFactory>().Create(configure));
        services.AddSingleton<IEntityChangeTracker>(sp => sp.GetRequiredService<EntityChangeTracker>());

        if (configuration != null)
        {
            services.Configure<OutboxPublisherOptions>(configuration.GetSection("ChangeTracking:Publisher"));
        }

        services.AddSingleton<OutboxCleanupService>(sp =>
        {
            var options = sp.GetService<IOptions<OutboxPublisherOptions>>()?.Value ?? new OutboxPublisherOptions();
            var tracker = sp.GetRequiredService<EntityChangeTracker>();
            var outboxes = tracker.GetOutboxes().Values;
            return new OutboxCleanupService(outboxes, options.PollingInterval * 10);
        });

        services.AddHostedService<OutboxPublisherHostedService>(sp =>
        {
            var tracker = sp.GetRequiredService<EntityChangeTracker>();
            var options = sp.GetService<IOptions<OutboxPublisherOptions>>()?.Value ?? new OutboxPublisherOptions();
            var cleanup = sp.GetRequiredService<OutboxCleanupService>();
            return new OutboxPublisherHostedService(tracker, options, cleanup);
        });

        return services;
    }
}
