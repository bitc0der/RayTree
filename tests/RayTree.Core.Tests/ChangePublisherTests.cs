using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RayTree.Core.Distribution;
using RayTree.Core.Telemetry;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Core.Tests;

public class ChangePublisherTests
{
    private class SampleEntity { public int Id { get; set; } }

    [Test]
    public void RegisterOutbox_AddsOutbox_ForEntityType()
    {
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        var outbox = new Mock<IOutbox>().Object;

        publisher.RegisterOutbox(typeof(SampleEntity), outbox);

        Assert.That(publisher.GetOutbox(typeof(SampleEntity)), Is.SameAs(outbox));
    }

    [Test]
    public void RegisterPublisher_AddsPublisher_ForEntityType()
    {
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        var queue = new Mock<IQueuePublisher>().Object;

        publisher.RegisterPublisher(typeof(SampleEntity), queue);

        Assert.That(publisher.GetPublisher(typeof(SampleEntity)), Is.SameAs(queue));
    }

    [Test]
    public void GetOutboxes_ReturnsAllRegisteredOutboxes()
    {
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        var outbox1 = new Mock<IOutbox>().Object;
        var outbox2 = new Mock<IOutbox>().Object;

        publisher.RegisterOutbox(typeof(string), outbox1);
        publisher.RegisterOutbox(typeof(int), outbox2);

        var outboxes = publisher.GetOutboxes();

        Assert.That(outboxes, Has.Count.EqualTo(2));
        Assert.That(outboxes.ContainsKey(typeof(string)), Is.True);
        Assert.That(outboxes.ContainsKey(typeof(int)), Is.True);
    }

    [Test]
    public void GetOutbox_Throws_WhenNotRegistered()
    {
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());

        Assert.Throws<InvalidOperationException>(() => publisher.GetOutbox(typeof(SampleEntity)));
    }

    [Test]
    public void RegisterAndGetOutbox_IsThreadSafe_WithConcurrentAccess()
    {
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        var outboxes = Enumerable.Range(0, 50).Select(i => new Mock<IOutbox>().Object).ToArray();
        var types = Enumerable.Range(0, 50).Select(_ => typeof(SampleEntity)).ToArray();

        var registerTask = Task.Run(() =>
        {
            Parallel.For(0, outboxes.Length, i => publisher.RegisterOutbox(types[i], outboxes[i]));
        });

        registerTask.Wait();

        var getTask = Task.Run(() =>
        {
            Parallel.For(0, outboxes.Length, i => publisher.GetOutbox(types[i]));
        });

        Assert.DoesNotThrowAsync(async () => await getTask);
    }
}
