using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RayTree.Core.Handling;
using RayTree.Core.Plugins.Deduplication;

namespace RayTree.Hosting.Handling;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="ChangeSubscriber"/> singleton and a
    /// <see cref="ChangeSubscriberHostedService"/> hosted service, then returns a
    /// <see cref="IChangeSubscriberBuilder"/> for fluent per-entity configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Global defaults (serializer, compressor, retry options, deduplication) set on the
    /// returned builder apply to all entities that do not provide an explicit override inside
    /// <see cref="IChangeSubscriberBuilder.ForEntity{TEntity}"/>.
    /// </para>
    /// <para>
    /// When <paramref name="configuration"/> is supplied, the <c>ChangeTracking:Subscriber</c>
    /// section is bound to <see cref="SubscriberOptions"/> and passed to the subscriber at
    /// startup, taking precedence over any options set via the builder.  This lets
    /// <c>appsettings.json</c> override behaviour without recompiling.
    /// </para>
    /// </remarks>
    public static IChangeSubscriberBuilder AddChangeSubscriber(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        var builder = new ChangeSubscriberBuilder();

        services.AddSingleton<ChangeSubscriber>(sp =>
        {
            // Config-bound options (from appsettings) override builder options when present.
            var configOptions = sp.GetService<IOptions<SubscriberOptions>>()?.Value;
            // DI-registered dedup store (e.g. registered separately) overrides builder store.
            var dedupStore    = sp.GetService<IDeduplicationStore>();
            return builder.Build(dedupStore, configOptions);
        });

        services.AddHostedService<ChangeSubscriberHostedService>(sp =>
            new ChangeSubscriberHostedService(sp.GetRequiredService<ChangeSubscriber>()));

        if (configuration != null)
            services.Configure<SubscriberOptions>(
                configuration.GetSection("ChangeTracking:Subscriber"));

        return builder;
    }
}
