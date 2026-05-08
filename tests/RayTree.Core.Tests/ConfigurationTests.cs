using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Plugins;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests;

public class ChangeTrackingBuilderTests
{
    [Test]
    public void Build_CreatesTracker_WithRegisteredPlugins()
    {
        var outbox = new InMemoryOutbox();
        var queue = new InMemoryQueue();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        var builder = new ChangeTrackingBuilder();
        builder.UseOutbox<IOutbox>(_ => outbox);
        builder.UseQueue<IQueuePublisher>(_ => queue);
        builder.UseSerializer<IChangeSerializer>(_ => serializer);
        builder.UseCompressor<IChangeCompressor>(_ => compressor);

        builder.ForEntity<object>(e => e
            .UseOutbox(outbox)
            .UseQueue(queue)
            .UseSerializer(serializer)
            .UseCompressor(compressor));

        var tracker = builder.Build();

        Assert.That(tracker.Publisher.GetOutbox(typeof(object)), Is.SameAs(outbox));
        Assert.That(tracker.Publisher.GetPublisher(typeof(object)), Is.SameAs(queue));
        Assert.That(tracker.Publisher.GetSerializer(typeof(object)), Is.SameAs(serializer));
        Assert.That(tracker.Publisher.GetCompressor(typeof(object)), Is.SameAs(compressor));
    }

    [Test]
    public void Build_Throws_WhenNoOutboxConfigured()
    {
        var builder = new ChangeTrackingBuilder();
        builder.UseQueue<IQueuePublisher>(_ => new InMemoryQueue());
        builder.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
        builder.UseCompressor<IChangeCompressor>(_ => new NoOpCompressorPlugin());

        builder.ForEntity<object>(e => e
            .UseQueue(new InMemoryQueue())
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new NoOpCompressorPlugin()));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Test]
    public void ForEntity_ReturnsSelf_ForFluentChaining()
    {
        var builder = new ChangeTrackingBuilder();

        // ForEntity now returns the parent builder (IChangeTrackingBuilder), not IEntityBuilder,
        // enabling chaining of multiple entity registrations on the same builder instance.
        var result = builder.ForEntity<object>(e => e.UseOutbox(new InMemoryOutbox()));

        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void ForEntity_Callback_ReceivesEntityBuilder()
    {
        var builder = new ChangeTrackingBuilder();
        IEntityBuilder<object>? capturedEntityBuilder = null;

        builder.ForEntity<object>(e =>
        {
            capturedEntityBuilder = e;
            e.UseOutbox(new InMemoryOutbox());
        });

        Assert.That(capturedEntityBuilder, Is.Not.Null);
    }

    [Test]
    public void Build_SupportsMultipleEntityTypes()
    {
        var builder = new ChangeTrackingBuilder();
        builder.UseOutbox<IOutbox>(_ => new InMemoryOutbox());
        builder.UseQueue<IQueuePublisher>(_ => new InMemoryQueue());
        builder.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
        builder.UseCompressor<IChangeCompressor>(_ => new NoOpCompressorPlugin());

        builder
            .ForEntity<string>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UseQueue(new InMemoryQueue())
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .ForEntity<Exception>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UseQueue(new InMemoryQueue())
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()));

        var tracker = builder.Build();

        Assert.That(tracker.Publisher.GetOutboxes(), Has.Count.EqualTo(2));
    }
}

public class ChangeTrackingConfigurationTests
{
    [Test]
    public void Configuration_ReturnsSelf_ForFluentChaining()
    {
        var config = new ChangeTrackingConfiguration();

        Assert.That(config.UseOutbox<IOutbox>(_ => new InMemoryOutbox()), Is.SameAs(config));
        Assert.That(config.UseQueue<IQueuePublisher>(_ => new InMemoryQueue()), Is.SameAs(config));
        Assert.That(config.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin()), Is.SameAs(config));
        Assert.That(config.UseCompressor<IChangeCompressor>(_ => new NoOpCompressorPlugin()), Is.SameAs(config));
    }

    [Test]
    public void Configuration_Build_ReturnsTracker()
    {
        var config = new ChangeTrackingConfiguration();
        config.UseOutbox<IOutbox>(_ => new InMemoryOutbox());
        config.UseQueue<IQueuePublisher>(_ => new InMemoryQueue());
        config.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
        config.UseCompressor<IChangeCompressor>(_ => new NoOpCompressorPlugin());

        var tracker = config.Build();

        Assert.That(tracker, Is.Not.Null);
        Assert.That(tracker, Is.InstanceOf<EntityChangeTracker>());
    }

    [Test]
    public void Configuration_Throws_WhenBuiltTwice()
    {
        var config = new ChangeTrackingConfiguration();
        config.UseOutbox<IOutbox>(_ => new InMemoryOutbox());
        config.UseQueue<IQueuePublisher>(_ => new InMemoryQueue());
        config.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
        config.UseCompressor<IChangeCompressor>(_ => new NoOpCompressorPlugin());

        config.Build();

        Assert.Throws<InvalidOperationException>(() => config.UseOutbox<IOutbox>(_ => new InMemoryOutbox()));
    }

    [Test]
    public void Configuration_WithPollingInterval_SetsOptions()
    {
        var config = new ChangeTrackingConfiguration();
        config.UseOutbox<IOutbox>(_ => new InMemoryOutbox());
        config.UseQueue<IQueuePublisher>(_ => new InMemoryQueue());
        config.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
        config.UseCompressor<IChangeCompressor>(_ => new NoOpCompressorPlugin());
        config.WithPollingInterval(TimeSpan.FromSeconds(30));
        config.WithBatchSize(50);

        var tracker = config.Build();

        Assert.That(tracker, Is.Not.Null);
    }

}
