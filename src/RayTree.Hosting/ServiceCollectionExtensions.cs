using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Resilience;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;

namespace RayTree.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChangeTracking(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<IChangeTrackingBuilder>? configure = null)
    {
        services.TryAddSingleton(new ChangeTrackingDiContext(ConfigurationBound: configuration is not null));

        services.AddSingleton<EntityChangeTrackerFactory>();

        // RayTreeMeter is a DI singleton so callers can also inject it directly for
        // custom instrumentation. The container disposes it after the tracker.
        services.AddSingleton<RayTreeMeter>();

        services.AddSingleton<EntityChangeTracker>(sp =>
        {
            var builder = EntityChangeTracker.Create(sp.GetService<ILoggerFactory>());
            builder.UseMeter(sp.GetRequiredService<RayTreeMeter>());
            configure?.Invoke(builder);
            return builder.Build();
        });
        services.AddSingleton<IEntityChangeTracker>(sp => sp.GetRequiredService<EntityChangeTracker>());

        if (configuration != null)
        {
            services.Configure<OutboxPublisherOptions>(configuration.GetSection("ChangeTracking:Publisher"));
            services.Configure<SubscriberOptions>(configuration.GetSection("ChangeTracking:Subscriber"));

            // Connection-recovery defaults are bound as NAMED options so the same type can
            // be configured independently for publisher-side (Postgres / Kafka publisher)
            // and subscriber-side (Kafka consumer) plugins. Plugins that want to honor
            // these defaults resolve them via
            // <c>IOptionsMonitor&lt;ConnectionRecoveryOptions&gt;.Get(ChangeTrackingRecoveryKeys.Publisher)</c>
            // and merge into their own options before constructing the publisher/consumer.
            services.Configure<ConnectionRecoveryOptions>(
                ChangeTrackingRecoveryKeys.Publisher,
                configuration.GetSection("ChangeTracking:Publisher:ConnectionRecovery"));
            services.Configure<ConnectionRecoveryOptions>(
                ChangeTrackingRecoveryKeys.Subscriber,
                configuration.GetSection("ChangeTracking:Subscriber:ConnectionRecovery"));
        }

        services.AddHostedService<ChangeTrackingHostedService>();

        return services;
    }
}
