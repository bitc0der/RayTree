using Microsoft.Extensions.DependencyInjection;
using RayTree.Plugins.PostgreSQL;

namespace RayTree.Plugins;

public static class PostgreSqlRepositoryExtensions
{
    public static IChangeTrackingBuilder UsePostgreSqlRepository<TEntity>(
        this IChangeTrackingBuilder builder,
        Action<PostgreSqlRepositoryOptions> configure)
        where TEntity : class
    {
        var options = new PostgreSqlRepositoryOptions();
        configure(options);

        return builder.UseOutbox<IOutbox>(_ => new PostgreSqlOutbox(new PostgreSqlOutboxOptions
        {
            ConnectionString = options.ConnectionString,
            OutboxTableName = options.TableName + "_outbox"
        }));
    }
}
