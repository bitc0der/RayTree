using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RayTree.Subscriber.Plugins.Deduplication;

namespace RayTree.Subscriber;

public static class ServiceCollectionExtensions
{
    public static ChangeSubscriberConfiguration AddChangeSubscriber(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        var config = new ChangeSubscriberConfiguration(services);

        services.AddSingleton<ChangeSubscriber>(sp =>
        {
            var options = sp.GetService<IOptions<SubscriberOptions>>()?.Value;
            var dedupStore = sp.GetService<IDeduplicationStore>();
            return config.Build(dedupStore, options);
        });

        services.AddHostedService<ChangeSubscriberHostedService>(sp =>
        {
            return new ChangeSubscriberHostedService(sp.GetRequiredService<ChangeSubscriber>());
        });

        if (configuration != null)
        {
            services.Configure<SubscriberOptions>(configuration.GetSection("ChangeTracking:Subscriber"));
        }

        return config;
    }
}
