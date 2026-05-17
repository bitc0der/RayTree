using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;

namespace RayTree.Core.Tests;

/// <summary>
/// Tests for the optional ACK/NACK lifecycle on <see cref="IQueueConsumer"/>: the
/// <see cref="ChangeSubscriber"/> must invoke <see cref="IQueueConsumer.AcknowledgeAsync"/>
/// after a successful handler dispatch and <see cref="IQueueConsumer.NegativeAcknowledgeAsync"/>
/// when a handler exhausts retries with <c>SkipOnFailure = false</c>.
/// <para>
/// Consumers that don't override these methods inherit the default no-ops and behave
/// as at-most-once — the wrap is a pure passthrough for them.
/// </para>
/// </summary>
[TestFixture]
public class ConsumerAckLifecycleTests
{
    private class Order { public int Id { get; set; } }

    // -------------------------------------------------------------------------
    // Test consumer that records every Ack / Nack call against the in-flight envelope
    // -------------------------------------------------------------------------

    private sealed class RecordingConsumer : IQueueConsumer
    {
        private readonly List<MessageEnvelope> _items;

        public List<Guid> Acked { get; } = new();
        public List<Guid> Nacked { get; } = new();

        public RecordingConsumer(params MessageEnvelope[] items)
        {
            _items = items.ToList();
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<MessageEnvelope> ConsumeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            foreach (var item in _items)
            {
                await Task.Yield();
                yield return item;
            }
        }

        public Task AcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Acked.Add(envelope.CorrelationId);
            return Task.CompletedTask;
        }

        public Task NegativeAcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Nacked.Add(envelope.CorrelationId);
            return Task.CompletedTask;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MessageEnvelope InsertEnvelope(Guid? correlationId = null) => new()
    {
        EntityType    = typeof(Order).AssemblyQualifiedName!,
        EntityId      = "1",
        ChangeType    = ChangeType.Insert,
        CorrelationId = correlationId ?? Guid.NewGuid(),
        Payload       = Array.Empty<byte>(),
    };

    private static ChangeSubscriber MakeSubscriber(SubscriberOptions? options = null)
        => new(NullLogger<ChangeSubscriber>.Instance, new RayTreeMeter(), options: options);

    // -------------------------------------------------------------------------
    // Default no-op behaviour: existing IQueueConsumer impls that don't override
    // Ack/Nack inherit the interface defaults — there is nothing to verify on the
    // consumer side. Sanity-check that calling them directly is a no-op and returns
    // a completed Task.
    // -------------------------------------------------------------------------

    [Test]
    public async Task IQueueConsumer_DefaultAckAndNack_AreNoOp()
    {
        IQueueConsumer consumer = new NoOverridesConsumer();

        await consumer.AcknowledgeAsync(InsertEnvelope());
        await consumer.NegativeAcknowledgeAsync(InsertEnvelope());

        Assert.Pass("default interface methods completed without throwing");
    }

    private sealed class NoOverridesConsumer : IQueueConsumer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IAsyncEnumerable<MessageEnvelope> ConsumeAsync(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<MessageEnvelope>();

        private static class AsyncEnumerable
        {
            public static async IAsyncEnumerable<T> Empty<T>()
            {
                await Task.CompletedTask;
                yield break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Shared mode — ACK on successful handler dispatch
    // -------------------------------------------------------------------------

    [Test]
    public async Task ConsumeFromConsumerAsync_HandlerSucceeds_CallsAcknowledge()
    {
        var envelope   = InsertEnvelope();
        var consumer   = new RecordingConsumer(envelope);
        var subscriber = MakeSubscriber();
        subscriber.OnChange<Order>(ChangeType.Insert, (_, _) => Task.CompletedTask);

        await subscriber.ConsumeFromConsumerAsync(consumer);

        Assert.That(consumer.Acked,  Is.EquivalentTo(new[] { envelope.CorrelationId }));
        Assert.That(consumer.Nacked, Is.Empty);
    }

    // -------------------------------------------------------------------------
    // Shared mode — ACK still fires when the message is skipped because no handler matches
    // (a no-handler skip is a valid "we handled the decision" outcome, not a failure)
    // -------------------------------------------------------------------------

    [Test]
    public async Task ConsumeFromConsumerAsync_NoHandlerMatch_StillAcknowledges()
    {
        var envelope   = InsertEnvelope();
        var consumer   = new RecordingConsumer(envelope);
        var subscriber = MakeSubscriber();
        // No handler registered for Order — message will be skipped silently.

        await subscriber.ConsumeFromConsumerAsync(consumer);

        Assert.That(consumer.Acked,  Is.EquivalentTo(new[] { envelope.CorrelationId }));
        Assert.That(consumer.Nacked, Is.Empty);
    }

    // -------------------------------------------------------------------------
    // Shared mode — NACK fires when handler exhausts retries with SkipOnFailure = false
    // -------------------------------------------------------------------------

    [Test]
    public void ConsumeFromConsumerAsync_HandlerExhaustsRetries_CallsNegativeAcknowledge()
    {
        var envelope   = InsertEnvelope();
        var consumer   = new RecordingConsumer(envelope);
        var subscriber = MakeSubscriber(new SubscriberOptions
        {
            MaxRetries     = 0,                       // single attempt, no retry delay
            RetryDelay     = TimeSpan.Zero,
            SkipOnFailure  = false,                   // throw → NACK
        });
        subscriber.OnChange<Order>(ChangeType.Insert,
            (_, _) => throw new InvalidOperationException("boom"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => subscriber.ConsumeFromConsumerAsync(consumer));

        Assert.That(consumer.Acked,  Is.Empty);
        Assert.That(consumer.Nacked, Is.EquivalentTo(new[] { envelope.CorrelationId }));
    }

    // -------------------------------------------------------------------------
    // Shared mode — SkipOnFailure = true → handler swallows the exception, normal
    // completion → ACK (the message is "handled" by being intentionally dropped)
    // -------------------------------------------------------------------------

    [Test]
    public async Task ConsumeFromConsumerAsync_SkipOnFailure_StillAcknowledges()
    {
        var envelope   = InsertEnvelope();
        var consumer   = new RecordingConsumer(envelope);
        var subscriber = MakeSubscriber(new SubscriberOptions
        {
            MaxRetries     = 0,
            RetryDelay     = TimeSpan.Zero,
            SkipOnFailure  = true,                    // swallow → ACK
        });
        subscriber.OnChange<Order>(ChangeType.Insert,
            (_, _) => throw new InvalidOperationException("boom"));

        await subscriber.ConsumeFromConsumerAsync(consumer);

        Assert.That(consumer.Acked,  Is.EquivalentTo(new[] { envelope.CorrelationId }));
        Assert.That(consumer.Nacked, Is.Empty);
    }

    // -------------------------------------------------------------------------
    // Isolated mode — ACK on successful handler dispatch
    // -------------------------------------------------------------------------

    [Test]
    public async Task ConsumeIsolatedFromConsumerAsync_HandlerSucceeds_CallsAcknowledge()
    {
        var envelope   = InsertEnvelope();
        var consumer   = new RecordingConsumer(envelope);
        var subscriber = MakeSubscriber();
        subscriber.RegisterIsolatedConsumer<Order>("read-model", consumer);
        subscriber.RegisterIsolatedHandler<Order>("read-model", ChangeType.Insert,
            (_, _) => Task.CompletedTask);

        await subscriber.ConsumeIsolatedFromConsumerAsync(consumer, typeof(Order), "read-model");

        Assert.That(consumer.Acked,  Is.EquivalentTo(new[] { envelope.CorrelationId }));
        Assert.That(consumer.Nacked, Is.Empty);
    }

    // -------------------------------------------------------------------------
    // Isolated mode — NACK fires when handler exhausts retries with SkipOnFailure = false
    // -------------------------------------------------------------------------

    [Test]
    public void ConsumeIsolatedFromConsumerAsync_HandlerExhaustsRetries_CallsNegativeAcknowledge()
    {
        var envelope   = InsertEnvelope();
        var consumer   = new RecordingConsumer(envelope);
        var subscriber = MakeSubscriber(new SubscriberOptions
        {
            MaxRetries     = 0,
            RetryDelay     = TimeSpan.Zero,
            SkipOnFailure  = false,
        });
        subscriber.RegisterIsolatedConsumer<Order>("notifier", consumer);
        subscriber.RegisterIsolatedHandler<Order>("notifier", ChangeType.Insert,
            (_, _) => throw new InvalidOperationException("boom"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => subscriber.ConsumeIsolatedFromConsumerAsync(consumer, typeof(Order), "notifier"));

        Assert.That(consumer.Acked,  Is.Empty);
        Assert.That(consumer.Nacked, Is.EquivalentTo(new[] { envelope.CorrelationId }));
    }

    // -------------------------------------------------------------------------
    // Robustness — a consumer override that ignores envelopes without metadata
    // must be safe to call repeatedly (no exception, no observable effect).
    // This is the contract the RabbitMQ/Kafka consumers rely on for the "no
    // metadata" path (parse-failure path, double-Ack attempts, etc.).
    // -------------------------------------------------------------------------

    [Test]
    public async Task ConsumerOverride_EnvelopeWithoutMetadata_AckAndNackAreSilentNoOp()
    {
        var consumer = new MetadataRequiringConsumer();
        var envelope = InsertEnvelope();   // fresh envelope, empty metadata

        // Neither call should throw — the consumer's overrides correctly detect
        // the absence of its expected metadata key and return without side-effect.
        await consumer.AcknowledgeAsync(envelope);
        await consumer.NegativeAcknowledgeAsync(envelope);

        Assert.That(consumer.AckCallsApplied,  Is.Zero);
        Assert.That(consumer.NackCallsApplied, Is.Zero);
    }

    /// <summary>
    /// Consumer that mimics RabbitMqConsumer / KafkaConsumer: only acts on envelopes
    /// that carry its specific metadata key. Used to verify the silent-no-op contract.
    /// </summary>
    private sealed class MetadataRequiringConsumer : IQueueConsumer
    {
        private const string Key = "test.required_metadata";

        public int AckCallsApplied  { get; private set; }
        public int NackCallsApplied { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IAsyncEnumerable<MessageEnvelope> ConsumeAsync(CancellationToken cancellationToken = default)
            => EmptyAsyncEnumerable<MessageEnvelope>.Instance;

        public Task AcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope.Metadata.TryGetValue(Key, out _))
            {
                AckCallsApplied++;
                envelope.Metadata.Remove(Key);
            }
            return Task.CompletedTask;
        }

        public Task NegativeAcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope.Metadata.TryGetValue(Key, out _))
            {
                NackCallsApplied++;
                envelope.Metadata.Remove(Key);
            }
            return Task.CompletedTask;
        }
    }

    private static class EmptyAsyncEnumerable<T>
    {
        public static readonly IAsyncEnumerable<T> Instance = Create();

        private static async IAsyncEnumerable<T> Create()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    // -------------------------------------------------------------------------
    // Metadata bag — lazy allocation, round-trip
    // -------------------------------------------------------------------------

    [Test]
    public void MessageEnvelope_Metadata_RoundTripsValues()
    {
        var envelope = InsertEnvelope();

        envelope.Metadata["test.key"] = 42UL;

        Assert.That(envelope.Metadata, Has.Count.EqualTo(1));
        Assert.That(envelope.Metadata["test.key"], Is.EqualTo(42UL));
    }

    [Test]
    public void MessageEnvelope_Metadata_IsLazyAllocatedButStableReference()
    {
        var envelope = InsertEnvelope();

        // Two consecutive accesses return the same dictionary instance — the lazy
        // initializer must not produce a fresh dict on every property access.
        // The first access triggers allocation; the second must return the cached one.
        var first  = envelope.Metadata;
        first["sentinel"] = 1;
        var second = envelope.Metadata;

        Assert.That(second, Has.Count.EqualTo(1),
            "second access must observe the value written via the first reference — i.e. same dict");
        Assert.That(second["sentinel"], Is.EqualTo(1));
    }
}
