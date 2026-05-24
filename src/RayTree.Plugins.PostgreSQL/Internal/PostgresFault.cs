using System.Net.Sockets;
using Npgsql;

namespace RayTree.Plugins.PostgreSQL.Internal;

/// <summary>
/// Shared classifier for connection-level Postgres exceptions. The result drives the
/// disconnect / reconnect / metric-emission decisions in <c>NotificationBasedPublisher</c>
/// and <c>PostgreSqlOutbox&lt;TEntity&gt;</c>. Both delegate here so the classification
/// stays consistent across the LISTEN and outbox-read paths.
/// </summary>
internal static class PostgresFault
{
    /// <summary>
    /// Returns <c>true</c> when the exception indicates a transient connection-level fault
    /// the plugin SHOULD treat as a reconnect signal: TCP/socket drops, transient broker
    /// shutdown, or admin-initiated termination. Application-level SQL errors (unique
    /// violation, syntax error, permission denied) return <c>false</c> — those belong to
    /// the caller's exception path, not the recovery layer.
    /// </summary>
    public static bool IsConnectionFault(Exception ex) => ex switch
    {
        NpgsqlException { IsTransient: true }                                  => true,
        NpgsqlException { InnerException: SocketException }                    => true,
        NpgsqlException { InnerException: System.IO.IOException }              => true,
        PostgresException pg when IsConnectionLevelSqlState(pg.SqlState)       => true,
        ObjectDisposedException                                                 => true,
        _                                                                       => false,
    };

    private static bool IsConnectionLevelSqlState(string? sqlState) => sqlState switch
    {
        // admin_shutdown / crash_shutdown / cannot_connect_now (server-driven termination)
        "57P01" or "57P02" or "57P03" => true,
        // connection_exception family (08xxx) — covers transport / handshake failures
        "08000" or "08003" or "08006" or "08001" or "08004" or "08007" => true,
        _ => false,
    };
}
