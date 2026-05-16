using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;

namespace RayTree.Core.Tests;

/// <summary>
/// Tests for Shared-mode handler dispatch semantics: accumulation, sequential ordering,
/// per-handler retry, and dedup-revert behaviour.
/// Tasks 6.1–6.8.
/// </summary>
public class SharedHandlerDispatchTests
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

    private static MessageEnvelope UpdateEnvelope() => new()
    {
        EntityType    = typeof(Order).AssemblyQualifiedName!,
        EntityId      = "1",
        ChangeType    = ChangeType.Update,
        CorrelationId = Guid.NewGuid(),
        Payload       = Array.Empty<byte>(),
    };

    private static ChangeSubscriber MakeSubscriber(
        IDeduplicationStore? dedupStore = null,
        SubscriberOptions?   options    = null)
        => new(NullLogger<ChangeSubscriber>.Instance, new RayTreeMeter(),
               dedupStore: dedupStore, options: options);

    // -------------------------------------------------------------------------
    // Task 6.1 — two anonymous OnInsert handlers both invoked in registration order
    // -------------------------------------------------------------------------

    [Test]
    public async Task TwoInsertHandlers_BothInvokedInOrder()
    {
        var order = new List<string>();
        var subscriber = MakeSubscriber();
        subscriber
            .OnChange<Order>(ChangeType.Insert, (_, _) => { order.Add("A"); return Task.CompletedTask; })
            .OnChange<Order>(ChangeType.Insert, (_, _) => { order.Add("B"); return Task.CompletedTask; });

        await subscriber.ProcessMessageAsync(InsertEnvelope());

        Assert.That(order, Is.EqualTo(new[] { "A", "B" }));
    }

    // -------------------------------------------------------------------------
    // Task 6.2 — OnInsert + catch-all OnChange(null) both invoked for an Insert
    // -------------------------------------------------------------------------

    [Test]
    public async Task InsertHandler_PlusCatchAll_BothInvokedOnInsert()
    {
        var invoked = new List<string>();
        var subscriber = MakeSubscriber();
        subscriber
            .OnChange<Order>(ChangeType.Insert, (_, _) => { invoked.Add("insert"); return Task.CompletedTask; })
            .OnChange<Order>(null,              (_, _) => { invoked.Add("catchall"); return Task.CompletedTask; });

        await subscriber.ProcessMessageAsync(InsertEnvelope());

        Assert.That(invoked, Is.EqualTo(new[] { "insert", "catchall" }));
    }

    // -------------------------------------------------------------------------
    // Task 6.3 — Insert handler not invoked for Update, and vice-versa
    // -------------------------------------------------------------------------

    [Test]
    public async Task ActionFiltering_InsertHandlerNotInvokedForUpdate()
    {
        var invoked = new List<string>();
        var subscriber = MakeSubscriber();
        subscriber
            .OnChange<Order>(ChangeType.Insert, (_, _) => { invoked.Add("insert"); return Task.CompletedTask; })
            .OnChange<Order>(ChangeType.Update, (_, _) => { invoked.Add("update"); return Task.CompletedTask; });

        await subscriber.ProcessMessageAsync(InsertEnvelope());
        await subscriber.ProcessMessageAsync(UpdateEnvelope());

        Assert.That(invoked, Is.EqualTo(new[] { "insert", "update" }),
            "Insert message should invoke only the insert handler; Update only the update handler");
    }

    // -------------------------------------------------------------------------
    // Task 6.4 — three handlers for the same action invoked A → B → C
    // -------------------------------------------------------------------------

    [Test]
    public async Task ThreeHandlers_InvokedSequentiallyABC()
    {
        var order = new List<string>();
        var subscriber = MakeSubscriber();
        subscriber
            .OnChange<Order>(ChangeType.Insert, (_, _) => { order.Add("A"); return Task.CompletedTask; })
            .OnChange<Order>(ChangeType.Insert, (_, _) => { order.Add("B"); return Task.CompletedTask; })
            .OnChange<Order>(null,              (_, _) => { order.Add("C"); return Task.CompletedTask; });

        await subscriber.ProcessMessageAsync(InsertEnvelope());

        Assert.That(order, Is.EqualTo(new[] { "A", "B", "C" }));
    }

    // -------------------------------------------------------------------------
    // Task 6.5 — second handler retried; invocation counts correct
    // -------------------------------------------------------------------------

    [Test]
    public async Task SecondHandlerRetried_InvocationCountsCorrect()
    {
        var countA = 0;
        var countB = 0;
        var subscriber = MakeSubscriber(options: new SubscriberOptions
        {
            MaxRetries    = 2,
            RetryDelay    = TimeSpan.FromMilliseconds(1),
            SkipOnFailure = true,  // so test doesn't throw
        });
        subscriber
            .OnChange<Order>(ChangeType.Insert, (_, _) => { countA++; return Task.CompletedTask; })
            .OnChange<Order>(ChangeType.Insert, (_, _) =>
            {
                countB++;
                if (countB < 2) throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            });

        await subscriber.ProcessMessageAsync(InsertEnvelope());

        Assert.That(countA, Is.EqualTo(1), "handlerA invoked exactly once");
        Assert.That(countB, Is.EqualTo(2), "handlerB invoked twice (1 fail + 1 success)");
    }

    // -------------------------------------------------------------------------
    // Task 6.6 — SkipOnFailure=true: failed second handler skipped, third continues
    // -------------------------------------------------------------------------

    [Test]
    public async Task SkipOnFailure_True_FailedHandlerSkipped_NextHandlerContinues()
    {
        var invoked = new List<string>();
        var subscriber = MakeSubscriber(options: new SubscriberOptions
        {
            MaxRetries    = 0,
            RetryDelay    = TimeSpan.Zero,
            SkipOnFailure = true,
        });
        subscriber
            .OnChange<Order>(ChangeType.Insert, (_, _) => { invoked.Add("A"); return Task.CompletedTask; })
            .OnChange<Order>(ChangeType.Insert, (_, _) =>
            {
                invoked.Add("B-fail");
                throw new InvalidOperationException("always fails");
            })
            .OnChange<Order>(ChangeType.Insert, (_, _) => { invoked.Add("C"); return Task.CompletedTask; });

        // SkipOnFailure=true: should complete without throwing even though B fails
        await subscriber.ProcessMessageAsync(InsertEnvelope());

        Assert.That(invoked, Is.EqualTo(new[] { "A", "B-fail", "C" }),
            "B is skipped (logged at Error) but C still runs");
    }

    // -------------------------------------------------------------------------
    // Task 6.7 — SkipOnFailure=false: RevertProcessedAsync called with raw correlationId
    // -------------------------------------------------------------------------

    [Test]
    public async Task SkipOnFailure_False_RevertCalledWithCorrelationId_ExceptionPropagates()
    {
        var correlationId = Guid.NewGuid();
        var spy           = new DedupStoreSpy();
        var subscriber    = MakeSubscriber(dedupStore: spy, options: new SubscriberOptions
        {
            MaxRetries    = 0,
            RetryDelay    = TimeSpan.Zero,
            SkipOnFailure = false,
        });
        subscriber
            .OnChange<Order>(ChangeType.Insert, (_, _) => Task.CompletedTask)
            .OnChange<Order>(ChangeType.Insert, (_, _) => throw new InvalidOperationException("fatal"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => subscriber.ProcessMessageAsync(InsertEnvelope(correlationId)));

        // Allow any async work to flush
        await Task.Delay(20);

        Assert.That(spy.RevertedKeys, Contains.Item(correlationId.ToString()),
            "RevertProcessedAsync must be called with the raw correlation ID");
    }

    // -------------------------------------------------------------------------
    // Task 6.8 — SkipOnFailure=true: RevertProcessedAsync NOT called
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Issue #8 — handler registered for Insert only; Update envelope → no_handler skip
    // -------------------------------------------------------------------------

    [Test]
    public async Task NoHandlerForChangeType_MessageSkippedWithoutInvokingHandler()
    {
        var invoked    = false;
        var subscriber = MakeSubscriber();
        // Register only for Insert
        subscriber.OnChange<Order>(ChangeType.Insert, (_, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        // Deliver an Update — no registered handler matches
        await subscriber.ProcessMessageAsync(UpdateEnvelope());

        Assert.That(invoked, Is.False, "handler must not fire when no registration matches the change type");
    }

    [Test]
    public async Task SkipOnFailure_True_RevertNotCalled()
    {
        var spy        = new DedupStoreSpy();
        var subscriber = MakeSubscriber(dedupStore: spy, options: new SubscriberOptions
        {
            MaxRetries    = 0,
            RetryDelay    = TimeSpan.Zero,
            SkipOnFailure = true,
        });
        subscriber.OnChange<Order>(ChangeType.Insert,
            (_, _) => throw new InvalidOperationException("skipped"));

        await subscriber.ProcessMessageAsync(InsertEnvelope());

        Assert.That(spy.RevertedKeys, Is.Empty,
            "RevertProcessedAsync must NOT be called when SkipOnFailure = true");
    }
}

/// <summary>
/// Spy implementation of <see cref="IDeduplicationStore"/> that records
/// <c>TryMarkProcessedAsync</c> and <c>RevertProcessedAsync</c> calls.
/// </summary>
internal sealed class DedupStoreSpy : IDeduplicationStore
{
    public List<string> MarkedKeys  { get; } = new();
    public List<string> RevertedKeys { get; } = new();

    public Task<bool> TryMarkProcessedAsync(string correlationId, CancellationToken ct = default)
    {
        MarkedKeys.Add(correlationId);
        return Task.FromResult(true);   // always allow processing
    }

    public Task RevertProcessedAsync(string correlationId, CancellationToken ct = default)
    {
        RevertedKeys.Add(correlationId);
        return Task.CompletedTask;
    }

    public Task CleanupAsync(TimeSpan retentionPeriod, CancellationToken ct = default)
        => Task.CompletedTask;
}
