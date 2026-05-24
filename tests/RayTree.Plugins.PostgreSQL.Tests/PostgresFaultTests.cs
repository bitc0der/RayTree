using System.Net.Sockets;
using Npgsql;
using RayTree.Plugins.PostgreSQL.Internal;

namespace RayTree.Plugins.PostgreSQL.Tests;

[TestFixture]
public class PostgresFaultTests
{
    [Test]
    public void ObjectDisposedException_IsConnectionFault()
    {
        Assert.That(PostgresFault.IsConnectionFault(new ObjectDisposedException("conn")), Is.True);
    }

    [Test]
    public void NpgsqlException_WithSocketExceptionInner_IsConnectionFault()
    {
        var ex = new NpgsqlException("transport", new SocketException(10054));
        Assert.That(PostgresFault.IsConnectionFault(ex), Is.True);
    }

    [Test]
    public void NpgsqlException_WithIOExceptionInner_IsConnectionFault()
    {
        var ex = new NpgsqlException("io", new System.IO.IOException("broken pipe"));
        Assert.That(PostgresFault.IsConnectionFault(ex), Is.True);
    }

    // SqlState constants sourced from Npgsql.PostgresErrorCodes; the inline comments
    // record the underlying SQLSTATE so the test reads grep-friendly against the catalogue.
    [TestCase(PostgresErrorCodes.AdminShutdown)]                            // 57P01
    [TestCase(PostgresErrorCodes.CrashShutdown)]                            // 57P02
    [TestCase(PostgresErrorCodes.CannotConnectNow)]                         // 57P03
    [TestCase(PostgresErrorCodes.ConnectionException)]                      // 08000
    [TestCase(PostgresErrorCodes.ConnectionDoesNotExist)]                   // 08003
    [TestCase(PostgresErrorCodes.ConnectionFailure)]                        // 08006
    [TestCase(PostgresErrorCodes.SqlClientUnableToEstablishSqlConnection)]  // 08001
    [TestCase(PostgresErrorCodes.SqlServerRejectedEstablishmentOfSqlConnection)] // 08004
    [TestCase(PostgresErrorCodes.TransactionResolutionUnknown)]             // 08007
    [TestCase(PostgresErrorCodes.ProtocolViolation)]                        // 08P01
    public void PostgresException_ConnectionSqlState_IsConnectionFault(string sqlState)
    {
        var ex = MakePostgresException(sqlState);
        Assert.That(PostgresFault.IsConnectionFault(ex), Is.True, $"SqlState {sqlState} should be a connection fault");
    }

    [TestCase(PostgresErrorCodes.UniqueViolation)]                          // 23505 — application-level
    [TestCase(PostgresErrorCodes.ForeignKeyViolation)]                      // 23503
    [TestCase(PostgresErrorCodes.UndefinedTable)]                           // 42P01
    [TestCase(PostgresErrorCodes.UndefinedColumn)]                          // 42703
    [TestCase(PostgresErrorCodes.InvalidAuthorizationSpecification)]        // 28000
    public void PostgresException_ApplicationSqlState_IsNotConnectionFault(string sqlState)
    {
        var ex = MakePostgresException(sqlState);
        Assert.That(PostgresFault.IsConnectionFault(ex), Is.False, $"SqlState {sqlState} should NOT be a connection fault");
    }

    [Test]
    public void InvalidOperationException_IsNotConnectionFault()
    {
        Assert.That(PostgresFault.IsConnectionFault(new InvalidOperationException("nope")), Is.False);
    }

    [Test]
    public void ArgumentException_IsNotConnectionFault()
    {
        Assert.That(PostgresFault.IsConnectionFault(new ArgumentException("bad arg")), Is.False);
    }

    // PostgresException's full constructor takes many fields; the minimum useful for our
    // classifier is SqlState plus the required positional args.
    private static PostgresException MakePostgresException(string sqlState)
        => new(
            messageText:      $"test {sqlState}",
            severity:         "ERROR",
            invariantSeverity: "ERROR",
            sqlState:         sqlState);
}
