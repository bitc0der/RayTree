using Microsoft.EntityFrameworkCore;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.EntityFrameworkCore.Interceptors;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.EntityFrameworkCore.Tests;

public class EndToEndInMemoryTests
{
    private static EntityChangeTracker BuildTracker(
        InMemoryOutbox outbox, bool withQueue = false, bool withGzip = false)
        => EntityChangeTracker.Create()
            .ForEntity<Product>(e =>
            {
                e.UseOutbox(outbox)
                 .UseSerializer(new JsonSerializerPlugin())
                 .UseCompressor(withGzip ? new GzipCompressorPlugin() : (IChangeCompressor)new NoOpCompressorPlugin());
                if (withQueue)
                    e.UsePublisher(new InMemoryQueue());
            })
            .Build();

    [Test]
    public async Task EfCore_SaveChanges_WritesToInMemoryOutbox()
    {
        // Arrange
        var outbox = new InMemoryOutbox();
        var tracker = BuildTracker(outbox);
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_write_outbox")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);
        context.Products.Add(new Product { Name = "Widget", Price = 9.99m });

        // Act
        await context.SaveChangesAsync();

        // Assert
        var outboxEntries = outbox.GetAll();
        Assert.That(outboxEntries, Has.Count.EqualTo(1));
        Assert.That(outboxEntries[0].EntityType, Does.Contain("Product"));
        Assert.That(outboxEntries[0].ChangeType, Is.EqualTo(ChangeType.Insert));
    }

    [Test]
    public async Task EfCore_MultipleChanges_WritesAllToOutbox()
    {
        // Arrange
        var outbox = new InMemoryOutbox();
        var tracker = BuildTracker(outbox);
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_multiple_changes")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);
        context.Products.Add(new Product { Name = "Gadget", Price = 19.99m });
        context.Products.Add(new Product { Name = "Doohickey", Price = 4.99m });

        // Act
        await context.SaveChangesAsync();

        // Assert
        Assert.That(outbox.GetAll(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EfCore_UpdateChange_DetectedAndStored()
    {
        // Arrange
        var outbox = new InMemoryOutbox();
        var tracker = BuildTracker(outbox);
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_update")
            .AddInterceptors(interceptor)
            .Options;

        int productId;
        await using (var context = new TestDbContext(options))
        {
            context.Products.Add(new Product { Name = "Original", Price = 1.00m });
            await context.SaveChangesAsync();
            productId = context.Products.First().Id;
        }

        // Act
        await using (var context = new TestDbContext(options))
        {
            var product = context.Products.Find(productId);
            if (product != null)
            {
                product.Name = "Modified";
                await context.SaveChangesAsync();
            }
        }

        // Assert
        var updates = outbox.GetAll().Where(c => c.ChangeType == ChangeType.Update).ToList();
        Assert.That(updates, Is.Not.Empty);
    }

    [Test]
    public async Task EfCore_DeleteChange_DetectedAndStored()
    {
        // Arrange
        var outbox = new InMemoryOutbox();
        var tracker = BuildTracker(outbox);
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_delete")
            .AddInterceptors(interceptor)
            .Options;

        int productId;
        await using (var context = new TestDbContext(options))
        {
            context.Products.Add(new Product { Name = "ToDelete", Price = 0.50m });
            await context.SaveChangesAsync();
            productId = context.Products.First().Id;
        }

        // Act
        await using (var context = new TestDbContext(options))
        {
            var product = context.Products.Find(productId);
            if (product != null)
            {
                context.Products.Remove(product);
                await context.SaveChangesAsync();
            }
        }

        // Assert
        var deletes = outbox.GetAll().Where(c => c.ChangeType == ChangeType.Delete).ToList();
        Assert.That(deletes, Is.Not.Empty);
    }

    [Test]
    public async Task EfCore_WithQueue_OutboxAndQueueRegistered()
    {
        // Arrange
        var outbox = new InMemoryOutbox();
        var queue = new InMemoryQueue();
        var tracker = EntityChangeTracker.Create()
            .ForEntity<Product>(e => e
                .UseOutbox(outbox)
                .UsePublisher(queue)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_queue")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);
        context.Products.Add(new Product { Name = "Queued", Price = 42.0m });

        // Act
        await context.SaveChangesAsync();

        // Assert
        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
        Assert.That(tracker.Publisher.GetPublisher(typeof(Product)), Is.Not.Null);
        Assert.That(tracker.Publisher.GetOutbox(typeof(Product)), Is.Not.Null);
    }

    [Test]
    public async Task EfCore_OutboxPublisher_DeliversToQueue()
    {
        // Arrange
        var queue = new InMemoryQueue();
        using var tracker = EntityChangeTracker.Create()
            .UsePublisherOptions(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
            .ForEntity<Product>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UsePublisher(queue)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();

        // Act
        await tracker.TrackInsertAsync(new Product { Id = 1, Name = "Widget" });

        // Assert
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await queue.Reader.ReadAsync(cts.Token);
        Assert.That(message.EntityId, Is.EqualTo("1"));
        Assert.That(message.ChangeType, Is.EqualTo(ChangeType.Insert));
    }

    [Test]
    public async Task EfCore_WithCompression_RoundTripPreservesData()
    {
        // Arrange
        var outbox = new InMemoryOutbox();
        var tracker = BuildTracker(outbox, withGzip: true);
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_compression")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);
        context.Products.Add(new Product { Name = "Compressed", Price = 15.0m });

        // Act
        await context.SaveChangesAsync();

        // Assert
        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
    }

    private class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions options) : base(options) { }

        public DbSet<Product> Products { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>(b =>
            {
                b.ToTable("products");
                b.HasKey(e => e.Id);
                b.Property(e => e.Name).HasMaxLength(100).IsRequired();
            });
        }
    }

    private class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public decimal Price { get; set; }
    }
}
