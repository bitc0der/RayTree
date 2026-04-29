using Npgsql;
using RayTree.Plugins;

namespace RayTree.Plugins.PostgreSQL;

public class PostgreSqlDdlExecutor : IDdlExecutor
{
    private readonly string _connectionString;

    public PostgreSqlDdlExecutor(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task ExecuteAsync(string ddl, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var statements = SplitDdlStatements(ddl);

        foreach (var statement in statements)
        {
            var trimmed = statement.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("--"))
                continue;

            await using var cmd = new NpgsqlCommand(trimmed, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task ExecuteFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var ddl = await File.ReadAllTextAsync(filePath, cancellationToken);
        await ExecuteAsync(ddl, cancellationToken);
    }

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_name = @TableName
            )
            """, conn);

        cmd.Parameters.AddWithValue("TableName", tableName);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> TriggerExistsAsync(string triggerName, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM information_schema.triggers
                WHERE trigger_name = @TriggerName
            )
            """, conn);

        cmd.Parameters.AddWithValue("TriggerName", triggerName);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> FunctionExistsAsync(string functionName, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_proc
                WHERE proname = @FunctionName
            )
            """, conn);

        cmd.Parameters.AddWithValue("FunctionName", functionName);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static IEnumerable<string> SplitDdlStatements(string ddl)
    {
        var currentStatement = new System.Text.StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inDollarQuote = false;
        var dollarTag = string.Empty;

        for (var i = 0; i < ddl.Length; i++)
        {
            var c = ddl[i];
            var next = i + 1 < ddl.Length ? ddl[i + 1] : '\0';

            if (inDollarQuote)
            {
                currentStatement.Append(c);
                if (c == '$' && i + dollarTag.Length + 1 <= ddl.Length)
                {
                    var potentialTag = ddl.Substring(i, dollarTag.Length + 1);
                    if (potentialTag == "$" + dollarTag)
                    {
                        i += dollarTag.Length;
                        currentStatement.Append(dollarTag);
                        currentStatement.Append('$');
                        inDollarQuote = false;
                    }
                }
                continue;
            }

            if (c == '\'' && !inDoubleQuote && next != '\'')
                inSingleQuote = !inSingleQuote;

            if (c == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;

            if (c == '$' && !inSingleQuote && !inDoubleQuote)
            {
                var j = i + 1;
                while (j < ddl.Length && (char.IsLetterOrDigit(ddl[j]) || ddl[j] == '_'))
                    j++;

                if (j < ddl.Length && ddl[j] == '$')
                {
                    dollarTag = ddl.Substring(i + 1, j - i - 1);
                    inDollarQuote = true;
                    currentStatement.Append(c);
                    continue;
                }
            }

            if (c == ';' && !inSingleQuote && !inDoubleQuote && !inDollarQuote)
            {
                yield return currentStatement.ToString();
                currentStatement.Clear();
                continue;
            }

            currentStatement.Append(c);
        }

        var remaining = currentStatement.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(remaining))
            yield return remaining;
    }
}
