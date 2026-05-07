using Microsoft.EntityFrameworkCore;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.EntityFrameworkCore.Interceptors;
using RayTree.Plugins;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.EntityFrameworkCore.Tests;

public class EndToEndInMemoryTests
{
    [Test]
    public async Task EfCore_SaveChanges_WritesToInMemoryOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_write_outbox")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);

        context.Products.Add(new Product { Name = "Widget", Price = 9.99m });
        await context.SaveChangesAsync();

        var outboxEntries = outbox.GetAll();
        Assert.That(outboxEntries, Has.Count.EqualTo(1));
        Assert.That(outboxEntries[0].EntityType, Does.Contain("Product"));
        Assert.That(outboxEntries[0].ChangeType, Is.EqualTo(ChangeType.Insert));
    }

    [Test]
    public async Task EfCore_MultipleChanges_WritesAllToOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_multiple_changes")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);

        context.Products.Add(new Product { Name = "Gadget", Price = 19.99m });
        context.Products.Add(new Product { Name = "Doohickey", Price = 4.99m });
        await context.SaveChangesAsync();

        var outboxEntries = outbox.GetAll();
        Assert.That(outboxEntries, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EfCore_UpdateChange_DetectedAndStored()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

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

        await using (var context = new TestDbContext(options))
        {
            var product = context.Products.Find(productId);
            if (product != null)
            {
                product.Name = "Modified";
                await context.SaveChangesAsync();
            }
        }

        var updates = outbox.GetAll().Where(c => c.ChangeType == ChangeType.Update).ToList();
        Assert.That(updates, Is.Not.Empty);
    }

    [Test]
    public async Task EfCore_DeleteChange_DetectedAndStored()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

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

        await using (var context = new TestDbContext(options))
        {
            var product = context.Products.Find(productId);
            if (product != null)
            {
                context.Products.Remove(product);
                await context.SaveChangesAsync();
            }
        }

        var deletes = outbox.GetAll().Where(c => c.ChangeType == ChangeType.Delete).ToList();
        Assert.That(deletes, Is.Not.Empty);
    }

    [Test]
    public async Task EfCore_WithQueue_OutboxAndQueueRegistered()
    {
        var tracker = new EntityChangeTracker();
        var queue = new InMemoryQueue();
        var outbox = new InMemoryOutbox();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterPublisher(typeof(Product), queue);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_queue")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);

        context.Products.Add(new Product { Name = "Queued", Price = 42.0m });
        await context.SaveChangesAsync();

        var outboxEntries = outbox.GetAll();
        Assert.That(outboxEntries, Has.Count.EqualTo(1));

        Assert.That(tracker.GetPublisher(typeof(Product)), Is.Not.Null);
        Assert.That(tracker.GetOutbox(typeof(Product)), Is.Not.Null);
    }

    [Test]
    public async Task EfCore_OutboxPublisher_DeliversToQueue()
    {
        var tracker = new EntityChangeTracker();
        var queue = new InMemoryQueue();
        var outbox = new InMemoryOutbox();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterPublisher(typeof(Product), queue);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        tracker.PublisherOptions.PollingInterval = TimeSpan.FromMilliseconds(50);
        await tracker.InitializeAsync();

        await tracker.TrackInsertAsync(new Product { Id = 1, Name = "Widget" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await queue.Reader.ReadAsync(cts.Token);
        Assert.That(message.EntityId, Is.EqualTo("1"));
        Assert.That(message.ChangeType, Is.EqualTo(ChangeType.Insert));

        tracker.Dispose();
    }

    [Test]
    public async Task EfCore_WithCompression_RoundTripPreservesData()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        var queue = new InMemoryQueue();
        var serializer = new JsonSerializerPlugin();
        var compressor = new GzipCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterPublisher(typeof(Product), queue);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_compression")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);

        context.Products.Add(new Product { Name = "Compressed", Price = 15.0m });
        await context.SaveChangesAsync();

        var outboxEntries = outbox.GetAll();
        Assert.That(outboxEntries, Has.Count.EqualTo(1));
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
