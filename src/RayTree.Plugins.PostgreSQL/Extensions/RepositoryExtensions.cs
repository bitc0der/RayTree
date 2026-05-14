using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Repository;

namespace RayTree.Plugins.PostgreSQL.Extensions;

public static class PostgreSqlRepositoryExtensions
{
    public static IChangeTrackingBuilder UsePostgreSqlRepository<TEntity>(
        this IChangeTrackingBuilder builder,
        Action<PostgreSqlRepositoryOptions> configure,
        ILoggerFactory? loggerFactory = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PostgreSqlRepositoryOptions();
        configure(options);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;

        return builder.UseOutbox<IOutbox>(_ => new PostgreSqlOutbox<TEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = options.ConnectionString, OutboxTableName = options.TableName + "_outbox"
        }, factory));
    }
}
