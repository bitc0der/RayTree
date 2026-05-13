using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;
using RayTree.Plugins.PostgreSQL.Outbox;

namespace RayTree.Plugins.PostgreSQL.Extensions;

public static class PostgreSqlBuilderExtensions
{
    public static PostgreSqlOutboxOptions UsePostgreSqlOutbox<TEntity>(
        this IServiceCollection services,
        Action<PostgreSqlOutboxOptions> configure,
        ILoggerFactory? loggerFactory = null) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new PostgreSqlOutboxOptions();
        configure(options);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;

        services.AddSingleton<IOutbox>(sp => new PostgreSqlOutbox<TEntity>(options, factory));

        return options;
    }

    public static IChangeTrackingBuilder UsePostgreSqlOutbox(
        this IChangeTrackingBuilder builder,
        Func<Type, PostgreSqlOutboxOptions> configure,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        return builder.UseOutbox<IOutbox>(entityType =>
        {
            var options = configure(entityType);
            var outboxType = typeof(PostgreSqlOutbox<>).MakeGenericType(entityType);
            return (IOutbox)Activator.CreateInstance(outboxType, options, factory)!;
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
