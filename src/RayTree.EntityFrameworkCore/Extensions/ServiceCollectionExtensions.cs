using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RayTree.Core.Tracking;
using RayTree.EntityFrameworkCore.Interceptors;

namespace RayTree.EntityFrameworkCore.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChangeTracking(
        this IServiceCollection services,
        Action<ChangeTrackingOptions>? configure = null)
    {
        var options = new ChangeTrackingOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<EntityChangeTracker>();

        if (options.AutoAttachInterceptor)
        {
            services.AddSingleton<EntityChangeInterceptor>(sp =>
            {
                var tracker = sp.GetRequiredService<EntityChangeTracker>();
                return new EntityChangeInterceptor(tracker, options.TrackedEntityTypes);
            });
        }

        return services;
    }

    public static DbContextOptionsBuilder UseChangeTracking(
        this DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider)
    {
        var interceptor = serviceProvider.GetService<EntityChangeInterceptor>();
        if (interceptor != null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return optionsBuilder;
    }
}
