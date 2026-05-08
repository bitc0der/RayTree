using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Tracking;
using RayTree.Hosting.Handling;

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

        services.AddSingleton<ChangeSubscriber>(sp =>
        {
            var configOptions = sp.GetService<IOptions<SubscriberOptions>>()?.Value;
            var dedupStore = sp.GetService<IDeduplicationStore>();
            return builder.BuildSubscriber(dedupStore, configOptions);
        });

        if (configuration != null)
        {
            services.Configure<OutboxPublisherOptions>(configuration.GetSection("ChangeTracking:Publisher"));
            services.Configure<SubscriberOptions>(configuration.GetSection("ChangeTracking:Subscriber"));
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

        services.AddHostedService<ChangeSubscriberHostedService>(sp =>
            new ChangeSubscriberHostedService(sp.GetRequiredService<ChangeSubscriber>()));

        return services;
    }
}
