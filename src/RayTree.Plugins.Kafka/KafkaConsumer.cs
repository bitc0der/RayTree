using System.Runtime.CompilerServices;
using System.Text;
using Confluent.Kafka;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Kafka;

public class KafkaConsumer : IQueueConsumer, IDisposable
{
    private readonly KafkaConsumerOptions _options;
    private IConsumer<string, byte[]>? _consumer;

    public KafkaConsumer(KafkaConsumerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId          = _options.GroupId,
            AutoOffsetReset  = _options.FromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };

        _consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        _consumer.Subscribe(_options.Topic);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(EntityChange Change, byte[] Payload)> ConsumeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromMilliseconds(_options.PollTimeoutMs);

        while (!cancellationToken.IsCancellationRequested)
        {
            ConsumeResult<string, byte[]>? result;
            try
            {
                result = await Task.Run(() => _consumer!.Consume(timeout), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (result?.Message == null) continue;

            var change = ParseEntityChange(result.Message);
            yield return (change, result.Message.Value);
            _consumer!.Commit(result);
        }
    }

    private static EntityChange ParseEntityChange(Message<string, byte[]> message)
    {
        return new EntityChange
        {
            EntityType    = GetHeader(message.Headers, "entity_type"),
            EntityId      = GetHeader(message.Headers, "entity_id"),
            ChangeType    = Enum.Parse<ChangeType>(GetHeader(message.Headers, "change_type")),
            CorrelationId = TryParseGuid(GetHeaderBytes(message.Headers, "correlation_id")),
            Version       = int.TryParse(GetHeader(message.Headers, "version"), out var v) ? v : 0,
            Timestamp     = DateTime.TryParse(GetHeader(message.Headers, "timestamp"), out var ts)
                ? ts
                : DateTime.UtcNow
        };
    }

    private static string GetHeader(Headers headers, string key)
    {
        var bytes = GetHeaderBytes(headers, key);
        return bytes != null ? Encoding.UTF8.GetString(bytes) : string.Empty;
    }

    private static byte[]? GetHeaderBytes(Headers headers, string key)
    {
        foreach (var header in headers)
            if (header.Key == key) return header.GetValueBytes();
        return null;
    }

    private static Guid TryParseGuid(byte[]? bytes)
    {
        if (bytes == null || bytes.Length != 16) return Guid.Empty;
        try { return new Guid(bytes); }
        catch { return Guid.Empty; }
    }

    public void Dispose()
    {
        _consumer?.Close();
        _consumer?.Dispose();
    }
}
