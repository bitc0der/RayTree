using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;

namespace RayTree.Core.Tests;

/// <summary>
/// Tests for Isolated-mode handler dispatch: per-handler consumer, dedup key derivation,
/// cross-handler isolation, per-handler retry budget, and handler-name stability.
/// Tasks 7.1–7.8.
/// </summary>
public class IsolatedHandlerDispatchTests
{
    private class Order { public int Id { get; set; } }

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

    private static ChangeSubscriber MakeSubscriber(
        IDeduplicationStore? dedupStore = null,
        SubscriberOptions?   options    = null)
        => new(NullLogger<ChangeSubscriber>.Instance, new RayTreeMeter(),
               dedupStore: dedupStore, options: options);

    // -------------------------------------------------------------------------
    // Task 7.1 — factory invoked exactly once per unique handler name
    // -------------------------------------------------------------------------

    [Test]
    public void IsolatedHandlerBuilder_FactoryInvokedOncePerName()
    {
        var factoryInvocations = new List<string>();
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<Order>(e =>
            e.UseConsumerFactory(name =>
            {
                factoryInvocations.Add(name);
                return new InMemoryQueue();
            })
            .OnInsert("read-model", (_, _) => Task.CompletedTask)
            .OnUpdate("read-model", (_, _) => Task.CompletedTask)  // same name, different action
            .OnInsert("notifier",   (_, _) => Task.CompletedTask));

        // Build triggers IsolatedHandlerBuilder.Apply which calls the factory
        builder.Build();

        Assert.That(factoryInvocations, Has.Count.EqualTo(2),
            "Factory must be called exactly once per unique name");
        Assert.That(factoryInvocations, Is.EquivalentTo(new[] { "read-model", "notifier" }));
    }

    // -------------------------------------------------------------------------
    // Task 7.2 — hosted service starts one loop per (entity, handlerName)
    // -------------------------------------------------------------------------

    [Test]
    public void IsolatedQueues_ContainsOneEntryPerHandlerName()
    {
        var subscriber = MakeSubscriber();
        var consumerA  = new InMemoryQueue();
        var consumerB  = new InMemoryQueue();

        subscriber.RegisterIsolatedConsumer<Order>("read-model", consumerA);
        subscriber.RegisterIsolatedConsumer<Order>("notifier",   consumerB);

        var keys = subscriber.IsolatedQueueKeys.ToList();
        Assert.That(keys, Has.Count.EqualTo(2));
        Assert.That(keys, Contains.Item(new EntityHandlerKey(typeof(Order), "read-model")));
        Assert.That(keys, Contains.Item(new EntityHandlerKey(typeof(Order), "notifier")));
    }

    // -------------------------------------------------------------------------
    // Task 7.3 — each loop dispatches only its own named handler
    // -------------------------------------------------------------------------

    [Test]
    public async Task IsolatedLoop_DispatchesOnlyOwnHandler()
    {
        var readModelInvoked = 0;
        var notifierInvoked  = 0;

        var subscriber = MakeSubscriber();
        subscriber.RegisterIsolatedHandler<Order>("read-model", ChangeType.Insert,
            (_, _) => { readModelInvoked++; return Task.CompletedTask; });
        subscriber.RegisterIsolatedHandler<Order>("notifier", ChangeType.Insert,
            (_, _) => { notifierInvoked++; return Task.CompletedTask; });

        var env = InsertEnvelope();

        // Simulate what the hosted service does: call each named loop with its own consumer
        await subscriber.ProcessMessageAsync(env);          // shares dedup — skip second call
        // Reset dedup by using a fresh envelope for isolated path
        var env2 = InsertEnvelope();
        await subscriber.ConsumeIsolatedFromConsumerAsync(
            ProduceSingleMessage(env2), typeof(Order), "read-model", CancellationToken.None);

        var env3 = InsertEnvelope();
        await subscriber.ConsumeIsolatedFromConsumerAsync(
            ProduceSingleMessage(env3), typeof(Order), "notifier", CancellationToken.None);

        Assert.That(readModelInvoked, Is.EqualTo(1), "read-model handler should only be invoked by its own loop");
        Assert.That(notifierInvoked,  Is.EqualTo(1), "notifier handler should only be invoked by its own loop");
    }

    // -------------------------------------------------------------------------
    // Task 7.4 — dedup key is "{correlationId}:{handlerName}"
    // -------------------------------------------------------------------------

    [Test]
    public async Task IsolatedDedup_KeyIsCorrelationIdColonHandlerName()
    {
        var spy        = new DedupStoreSpy();
        var subscriber = MakeSubscriber(dedupStore: spy);
        subscriber.RegisterIsolatedHandler<Order>("read-model", ChangeType.Insert,
            (_, _) => Task.CompletedTask);

        var correlationId = Guid.NewGuid();
        var env           = InsertEnvelope(correlationId);

        await subscriber.ConsumeIsolatedFromConsumerAsync(
            ProduceSingleMessage(env), typeof(Order), "read-model", CancellationToken.None);

        Assert.That(spy.MarkedKeys, Contains.Item($"{correlationId}:read-model"),
            "Isolated dedup key must be '{correlationId}:{handlerName}'");
    }

    // -------------------------------------------------------------------------
    // Task 7.5 — RevertProcessedAsync on failed handler uses its own dedup key
    // -------------------------------------------------------------------------

    [Test]
    public async Task IsolatedFailure_RevertUsesHandlerScopedDedupKey()
    {
        var spy        = new DedupStoreSpy();
        var subscriber = MakeSubscriber(dedupStore: spy, options: new SubscriberOptions
        {
            MaxRetries    = 0,
            RetryDelay    = TimeSpan.Zero,
            SkipOnFailure = false,
        });
        subscriber.RegisterIsolatedHandler<Order>("notifier", ChangeType.Insert,
            (_, _) => throw new InvalidOperationException("fatal"));

        var correlationId = Guid.NewGuid();
        var env           = InsertEnvelope(correlationId);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => subscriber.ConsumeIsolatedFromConsumerAsync(
                ProduceSingleMessage(env), typeof(Order), "notifier", CancellationToken.None));

        await Task.Delay(20);

        Assert.That(spy.RevertedKeys, Contains.Item($"{correlationId}:notifier"),
            "Revert must use the per-handler dedup key");
        Assert.That(spy.RevertedKeys, Has.None.EqualTo(correlationId.ToString()),
            "Revert must NOT use the raw correlationId");
    }

    // -------------------------------------------------------------------------
    // Task 7.6 — handler A success is independent of handler B failure
    // -------------------------------------------------------------------------

    [Test]
    public async Task HandlerASuccess_IsIndependentOfHandlerBFailure()
    {
        var countA = 0;
        var spy    = new DedupStoreSpy();
        var subA   = MakeSubscriber(dedupStore: spy, options: new SubscriberOptions
        {
            MaxRetries    = 0,
            RetryDelay    = TimeSpan.Zero,
            SkipOnFailure = false,
        });
        var subB = MakeSubscriber(dedupStore: spy, options: new SubscriberOptions
        {
            MaxRetries    = 0,
            RetryDelay    = TimeSpan.Zero,
            SkipOnFailure = false,
        });

        subA.RegisterIsolatedHandler<Order>("read-model", ChangeType.Insert,
            (_, _) => { countA++; return Task.CompletedTask; });
        subB.RegisterIsolatedHandler<Order>("notifier", ChangeType.Insert,
            (_, _) => throw new InvalidOperationException("notifier fails"));

        var envA = InsertEnvelope();
        var envB = InsertEnvelope();  // separate correlationId so dedup doesn't block

        await subA.ConsumeIsolatedFromConsumerAsync(
            ProduceSingleMessage(envA), typeof(Order), "read-model", CancellationToken.None);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => subB.ConsumeIsolatedFromConsumerAsync(
                ProduceSingleMessage(envB), typeof(Order), "notifier", CancellationToken.None));

        await Task.Delay(20);

        Assert.That(countA, Is.EqualTo(1), "Handler A must not be re-invoked when handler B fails");
    }

    // -------------------------------------------------------------------------
    // Task 7.7 — per-handler retry budget
    // -------------------------------------------------------------------------

    [Test]
    public async Task PerHandlerRetryBudget_EachHandlerGetOwnMaxRetries()
    {
        var attempts = 0;
        var spy      = new DedupStoreSpy();
        var subscriber = MakeSubscriber(dedupStore: spy, options: new SubscriberOptions
        {
            MaxRetries    = 2,
            RetryDelay    = TimeSpan.FromMilliseconds(1),
            SkipOnFailure = true,
        });
        subscriber.RegisterIsolatedHandler<Order>("notifier", ChangeType.Insert, (_, _) =>
        {
            attempts++;
            if (attempts <= 2) throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        });

        var env = InsertEnvelope();
        await subscriber.ConsumeIsolatedFromConsumerAsync(
            ProduceSingleMessage(env), typeof(Order), "notifier", CancellationToken.None);

        // MaxRetries=2 means 3 total attempts (1 initial + 2 retries)
        Assert.That(attempts, Is.EqualTo(3),
            "Isolated handler should get its own full retry budget");
    }

    // -------------------------------------------------------------------------
    // Task 7.8 — reordering registrations does not affect factory or dedup keys
    // -------------------------------------------------------------------------

    [Test]
    public void ReorderingRegistrations_ProducesIdenticalFactoryInvocations()
    {
        var orderAB = new List<string>();
        var orderBA = new List<string>();

        var builderAB = new ChangeTrackingBuilder();
        builderAB.ForEntity<Order>(e =>
            e.UseConsumerFactory(name => { orderAB.Add(name); return new InMemoryQueue(); })
             .OnInsert("read-model", (_, _) => Task.CompletedTask)
             .OnInsert("notifier",   (_, _) => Task.CompletedTask));
        builderAB.Build();

        var builderBA = new ChangeTrackingBuilder();
        builderBA.ForEntity<Order>(e =>
            e.UseConsumerFactory(name => { orderBA.Add(name); return new InMemoryQueue(); })
             .OnInsert("notifier",   (_, _) => Task.CompletedTask)
             .OnInsert("read-model", (_, _) => Task.CompletedTask));
        builderBA.Build();

        // The set of handler names passed to the factory must be identical regardless of order
        Assert.That(orderAB.ToHashSet(), Is.EquivalentTo(orderBA.ToHashSet()),
            "Factory invocations must be the same regardless of registration order");
    }

    // -------------------------------------------------------------------------
    // Helper: produce a consumer that yields one pre-built envelope then ends
    // -------------------------------------------------------------------------

    private static IQueueConsumer ProduceSingleMessage(MessageEnvelope envelope)
    {
        var q = new InMemoryQueue();
        q.PublishAsync(envelope).GetAwaiter().GetResult();
        q.Complete();
        return q;
    }
}
