using Testcontainers.PostgreSql;

namespace RayTree.Plugins.PostgreSQL.Tests;

internal static class PostgresContainerFactory
{
    public static PostgreSqlContainer Create()
    {
        return new PostgreSqlBuilder(image: "postgres:16-alpine").Build();
    }
}
