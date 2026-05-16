using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;

namespace RayTree.Core.Tests;

/// <summary>
/// Build-time validation tests: ArgumentException on empty/null names, InvalidOperationException
/// on duplicate (action, name) pairs, null factory result, and duplicate consumer instances.
/// Tasks 8.1–8.6.
/// </summary>
public class HandlerBuilderValidationTests
{
    private class Order { public int Id { get; set; } }

    // -------------------------------------------------------------------------
    // Task 8.1 — empty handlerName throws ArgumentException immediately
    // -------------------------------------------------------------------------

    [Test]
    public void OnInsert_EmptyHandlerName_ThrowsArgumentException()
    {
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<Order>(e =>
        {
            var isolated = e.UseConsumerFactory(_ => new InMemoryQueue());
            Assert.Throws<ArgumentException>(() =>
                isolated.OnInsert("", (_, _) => Task.CompletedTask));
        });
    }

    // -------------------------------------------------------------------------
    // Task 8.2 — null handlerName throws ArgumentException immediately
    // -------------------------------------------------------------------------

    [Test]
    public void OnInsert_NullHandlerName_ThrowsArgumentException()
    {
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<Order>(e =>
        {
            var isolated = e.UseConsumerFactory(_ => new InMemoryQueue());
            Assert.Throws<ArgumentException>(() =>
                isolated.OnInsert(null!, (_, _) => Task.CompletedTask));
        });
    }

    // -------------------------------------------------------------------------
    // Task 8.3 — duplicate (action, handlerName) throws InvalidOperationException at Build()
    // -------------------------------------------------------------------------

    [Test]
    public void DuplicateActionHandlerNamePair_ThrowsAtBuild()
    {
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<Order>(e =>
            e.UseConsumerFactory(_ => new InMemoryQueue())
             .OnInsert("read-model", (_, _) => Task.CompletedTask)
             .OnInsert("read-model", (_, _) => Task.CompletedTask));   // duplicate!

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("read-model"),
            "Exception message must identify the duplicate handler name");
        Assert.That(ex.Message, Does.Contain(nameof(Order)).Or.Contain("Order"),
            "Exception message must identify the entity type");
    }

    // -------------------------------------------------------------------------
    // Task 8.4 — factory returning null throws InvalidOperationException at Build()
    // -------------------------------------------------------------------------

    [Test]
    public void FactoryReturningNull_ThrowsAtBuild()
    {
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<Order>(e =>
            e.UseConsumerFactory(_ => null!)          // returns null
             .OnInsert("notifier", (_, _) => Task.CompletedTask));

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("notifier"),
            "Exception message must contain the handler name");
    }

    // -------------------------------------------------------------------------
    // Task 8.5 — factory returning the same instance for two names throws at Build()
    // -------------------------------------------------------------------------

    [Test]
    public void FactoryReturningSameInstance_ThrowsAtBuild()
    {
        var sharedConsumer = new InMemoryQueue();    // same instance for both names
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<Order>(e =>
            e.UseConsumerFactory(_ => sharedConsumer)
             .OnInsert("a", (_, _) => Task.CompletedTask)
             .OnInsert("b", (_, _) => Task.CompletedTask));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    // -------------------------------------------------------------------------
    // Task 8.6 — compile-time: anonymous overload not on IIsolatedHandlerBuilder;
    //            named overload not on ISharedHandlerBuilder.
    //
    // These are enforced by the type system — there is no runtime assertion.
    // The test below confirms that the methods exist on the correct interfaces
    // and are absent on the other, verifying the compile-time contract through
    // reflection (the code also will not compile if the guard is missing).
    // -------------------------------------------------------------------------

    [Test]
    public void CompileTimeGuard_AnonymousOverloads_OnlyOnSharedBuilder()
    {
        // ISharedHandlerBuilder<T> — anonymous overload (no handlerName param) must exist
        var sharedMethods = typeof(ISharedHandlerBuilder<Order>).GetMethods();
        Assert.That(sharedMethods.Any(m =>
                m.Name == "OnInsert" &&
                m.GetParameters().Length == 1),   // only the handler delegate
            Is.True, "ISharedHandlerBuilder must have anonymous OnInsert(handler)");

        // IIsolatedHandlerBuilder<T> — anonymous overload must NOT exist
        var isolatedMethods = typeof(IIsolatedHandlerBuilder<Order>).GetMethods();
        Assert.That(isolatedMethods.Any(m =>
                m.Name == "OnInsert" &&
                m.GetParameters().Length == 1),
            Is.False, "IIsolatedHandlerBuilder must NOT have anonymous OnInsert(handler)");
    }

    [Test]
    public void CompileTimeGuard_NamedOverloads_OnlyOnIsolatedBuilder()
    {
        // IIsolatedHandlerBuilder<T> — named overload (string handlerName + handler) must exist
        var isolatedMethods = typeof(IIsolatedHandlerBuilder<Order>).GetMethods();
        Assert.That(isolatedMethods.Any(m =>
                m.Name == "OnInsert" &&
                m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(string)),
            Is.True, "IIsolatedHandlerBuilder must have named OnInsert(string, handler)");

        // ISharedHandlerBuilder<T> — named overload must NOT exist
        var sharedMethods = typeof(ISharedHandlerBuilder<Order>).GetMethods();
        Assert.That(sharedMethods.Any(m =>
                m.Name == "OnInsert" &&
                m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(string)),
            Is.False, "ISharedHandlerBuilder must NOT have named OnInsert(string, handler)");
    }
}
