using Microsoft.Extensions.DependencyInjection;
using RayTree.Plugins.PostgreSQL;

namespace RayTree.Plugins;

public static class PostgreSqlBuilderExtensions
{
    public static PostgreSqlOutboxOptions UsePostgreSqlOutbox(
        this IServiceCollection services,
        Action<PostgreSqlOutboxOptions> configure)
    {
        var options = new PostgreSqlOutboxOptions();
        configure(options);

        services.AddSingleton<IOutbox>(sp => new PostgreSqlOutbox(options));

        return options;
    }

    public static IChangeTrackingBuilder UsePostgreSqlOutbox(
        this IChangeTrackingBuilder builder,
        Func<Type, PostgreSqlOutboxOptions> configure)
    {
        return builder.UseOutbox<IOutbox>(entityType =>
        {
            var options = configure(entityType);
            return new PostgreSqlOutbox(options);
        });
    }

    public static PostgreSqlOutboxOptions UseNotificationChannel(
        this PostgreSqlOutboxOptions options,
        string channelName)
    {
        options.UseNotificationChannel = true;
        options.NotificationChannel = channelName;
        return options;
    }

    public static PostgreSqlOutboxOptions WithFallbackPolling(
        this PostgreSqlOutboxOptions options,
        TimeSpan interval)
    {
        options.FallbackPollingInterval = interval;
        return options;
    }
}
