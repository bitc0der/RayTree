using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Tracking;

namespace RayTree.Hosting.Publishing;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChangeTracking(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<IChangeTrackingBuilder>? configure = null)
    {
        var builder = new ChangeTrackingBuilder();
        configure?.Invoke(builder);

        services.AddSingleton<EntityChangeTrackerFactory>();
        services.AddSingleton<EntityChangeTracker>(_ => builder.Build());
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
            return new OutboxCleanupService(outboxes, options.PollingInterval * 10);
        });

        services.AddHostedService<ChangeTrackingHostedService>(sp =>
            new ChangeTrackingHostedService(sp.GetRequiredService<EntityChangeTracker>()));

        return services;
    }
}
