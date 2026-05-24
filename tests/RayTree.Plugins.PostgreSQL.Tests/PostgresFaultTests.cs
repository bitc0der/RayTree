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

    [TestCase("57P01")]   // admin_shutdown
    [TestCase("57P02")]   // crash_shutdown
    [TestCase("57P03")]   // cannot_connect_now
    [TestCase("08000")]   // connection_exception
    [TestCase("08003")]   // connection_does_not_exist
    [TestCase("08006")]   // connection_failure
    [TestCase("08001")]   // sqlclient_unable_to_establish_sqlconnection
    [TestCase("08004")]   // sqlserver_rejected_establishment_of_sqlconnection
    [TestCase("08007")]   // transaction_resolution_unknown
    public void PostgresException_ConnectionSqlState_IsConnectionFault(string sqlState)
    {
        var ex = MakePostgresException(sqlState);
        Assert.That(PostgresFault.IsConnectionFault(ex), Is.True, $"SqlState {sqlState} should be a connection fault");
    }

    [TestCase("23505")]   // unique_violation — application-level
    [TestCase("23503")]   // foreign_key_violation
    [TestCase("42P01")]   // undefined_table
    [TestCase("42703")]   // undefined_column
    [TestCase("28000")]   // invalid_authorization_specification
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
