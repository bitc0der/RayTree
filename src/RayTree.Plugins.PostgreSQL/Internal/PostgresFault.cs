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

    // SqlState constants come from Npgsql so the list stays in sync with the
    // canonical PostgreSQL error catalogue and is grep-friendly by name.
    private static bool IsConnectionLevelSqlState(string? sqlState) => sqlState switch
    {
        // Server-driven termination (Class 57 — operator_intervention).
        PostgresErrorCodes.AdminShutdown      => true,   // 57P01
        PostgresErrorCodes.CrashShutdown      => true,   // 57P02
        PostgresErrorCodes.CannotConnectNow   => true,   // 57P03

        // Connection exception family (Class 08) — transport / handshake failures.
        PostgresErrorCodes.ConnectionException                          => true,   // 08000
        PostgresErrorCodes.ConnectionDoesNotExist                       => true,   // 08003
        PostgresErrorCodes.ConnectionFailure                            => true,   // 08006
        PostgresErrorCodes.SqlClientUnableToEstablishSqlConnection      => true,   // 08001
        PostgresErrorCodes.SqlServerRejectedEstablishmentOfSqlConnection => true,  // 08004
        PostgresErrorCodes.TransactionResolutionUnknown                 => true,   // 08007
        PostgresErrorCodes.ProtocolViolation                            => true,   // 08P01

        _ => false,
    };
}
