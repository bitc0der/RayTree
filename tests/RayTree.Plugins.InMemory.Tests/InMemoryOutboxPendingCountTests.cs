using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.InMemory.Tests;

[TestFixture]
public class InMemoryOutboxPendingCountTests
{
    private class Order { public int Id { get; set; } }
    private class Customer { public int Id { get; set; } }

    [Test]
    public async Task GetPendingCountAsync_OnEmptyOutbox_ReturnsZero()
    {
        var outbox = new InMemoryOutbox();
        var count = await outbox.GetPendingCountAsync(typeof(Order));
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetPendingCountAsync_CountsOnlyUnpublishedForGivenType()
    {
        var outbox = new InMemoryOutbox();

        await outbox.WriteAsync(new EntityChange<Order>
            { EntityType = typeof(Order).FullName!, EntityId = "1", ChangeType = ChangeType.Insert });
        await outbox.WriteAsync(new EntityChange<Order>
            { EntityType = typeof(Order).FullName!, EntityId = "2", ChangeType = ChangeType.Update });
        var published = new EntityChange<Order>
            { EntityType = typeof(Order).FullName!, EntityId = "3", ChangeType = ChangeType.Insert };
        await outbox.WriteAsync(published);
        await outbox.MarkPublishedAsync(published.Id);

        await outbox.WriteAsync(new EntityChange<Customer>
            { EntityType = typeof(Customer).FullName!, EntityId = "100", ChangeType = ChangeType.Insert });

        Assert.That(await outbox.GetPendingCountAsync(typeof(Order)),    Is.EqualTo(2));
        Assert.That(await outbox.GetPendingCountAsync(typeof(Customer)), Is.EqualTo(1));
    }
}
