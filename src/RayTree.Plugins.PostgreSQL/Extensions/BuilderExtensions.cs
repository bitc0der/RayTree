using Microsoft.Extensions.DependencyInjection;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;
using RayTree.Plugins.PostgreSQL.Outbox;

namespace RayTree.Plugins.PostgreSQL.Extensions;

public static class PostgreSqlBuilderExtensions
{
    public static PostgreSqlOutboxOptions UsePostgreSqlOutbox<TEntity>(
        this IServiceCollection services,
        Action<PostgreSqlOutboxOptions> configure) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new PostgreSqlOutboxOptions();
        configure(options);

        services.AddSingleton<IOutbox>(sp => new PostgreSqlOutbox<TEntity>(options));

        return options;
    }

    public static IChangeTrackingBuilder UsePostgreSqlOutbox(
        this IChangeTrackingBuilder builder,
        Func<Type, PostgreSqlOutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.UseOutbox<IOutbox>(entityType =>
        {
            var options = configure(entityType);
            var outboxType = typeof(PostgreSqlOutbox<>).MakeGenericType(entityType);
            return (IOutbox)Activator.CreateInstance(outboxType, options)!;
        });
    }

    public static PostgreSqlOutboxOptions UseNotificationChannel(
        this PostgreSqlOutboxOptions options,
        string channelName)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.UseNotificationChannel = true;
        options.NotificationChannel = channelName;
        return options;
    }

    public static PostgreSqlOutboxOptions WithFallbackPolling(
        this PostgreSqlOutboxOptions options,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.FallbackPollingInterval = interval;
        return options;
    }
}
