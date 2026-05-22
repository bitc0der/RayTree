using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RayTree.Plugins.RabbitMQ;

/// <summary>
/// Probes RabbitMQ topology (exchanges, queues) with passive declares and waits for it to appear.
/// Used by <see cref="RabbitMqPublisher"/> and <see cref="RabbitMqConsumer"/> when configured with
/// <c>WaitForTopology = true</c> so that a service consuming externally-owned topology does not
/// crash on startup if the owning service has not yet declared it.
/// </summary>
internal static class TopologyProbe
{
    private const ushort NotFoundReplyCode = 404;

    public static Task WaitForExchangeAsync(
        IConnection connection,
        string exchangeName,
        TimeSpan interval,
        TimeSpan? timeout,
        ILogger? logger,
        CancellationToken cancellationToken)
        => WaitAsync(
            connection,
            entityKind: "exchange",
            entityName: exchangeName,
            probe: static (channel, name, ct) => channel.ExchangeDeclarePassiveAsync(name, ct),
            interval,
            timeout,
            logger,
            cancellationToken);

    public static Task WaitForQueueAsync(
        IConnection connection,
        string queueName,
        TimeSpan interval,
        TimeSpan? timeout,
        ILogger? logger,
        CancellationToken cancellationToken)
        => WaitAsync(
            connection,
            entityKind: "queue",
            entityName: queueName,
            probe: static async (channel, name, ct) => { _ = await channel.QueueDeclarePassiveAsync(name, ct); },
            interval,
            timeout,
            logger,
            cancellationToken);

    private static async Task WaitAsync(
        IConnection connection,
        string entityKind,
        string entityName,
        Func<IChannel, string, CancellationToken, Task> probe,
        TimeSpan interval,
        TimeSpan? timeout,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Topology wait interval must be positive.");
        if (timeout is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Topology wait timeout must be positive when set.");

        var stopwatch = Stopwatch.StartNew();
        var missCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            OperationInterruptedException notFound;
            IChannel? channel = null;
            try
            {
                channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
                await probe(channel, entityName, cancellationToken);

                if (missCount > 0)
                {
                    logger?.LogInformation(
                        "RabbitMQ {EntityKind} '{EntityName}' became available after {Misses} miss(es) ({Elapsed}).",
                        entityKind, entityName, missCount, stopwatch.Elapsed);
                }
                return;
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == NotFoundReplyCode)
            {
                notFound = ex;
                missCount++;

                if (missCount == 1)
                {
                    logger?.LogInformation(
                        "RabbitMQ {EntityKind} '{EntityName}' not found yet; waiting (interval {Interval}, timeout {Timeout}).",
                        entityKind, entityName, interval, timeout?.ToString() ?? "<none>");
                }
                else
                {
                    logger?.LogDebug(
                        "RabbitMQ {EntityKind} '{EntityName}' still missing after {Misses} attempts ({Elapsed}).",
                        entityKind, entityName, missCount, stopwatch.Elapsed);
                }
            }
            finally
            {
                if (channel is not null)
                {
                    try { await channel.CloseAsync(CancellationToken.None); } catch { /* channel may already be closed by NOT_FOUND */ }
                    channel.Dispose();
                }
            }

            // Timeout check after the failed attempt — `notFound` is guaranteed non-null here.
            if (timeout is { } limit && stopwatch.Elapsed >= limit)
            {
                logger?.LogError(
                    "RabbitMQ topology wait for {EntityKind} '{EntityName}' timed out after {Elapsed} (limit {Limit}).",
                    entityKind, entityName, stopwatch.Elapsed, limit);
                throw notFound;
            }

            await Task.Delay(interval, cancellationToken);
        }
    }
}
