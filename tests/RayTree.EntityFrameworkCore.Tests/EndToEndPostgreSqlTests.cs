using Microsoft.EntityFrameworkCore;
using RayTree.EntityFrameworkCore.Interceptors;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.PostgreSQL;
using RayTree.Plugins.Serializers.Json;
using RayTree.Tracking;
using Testcontainers.PostgreSql;

namespace RayTree.EntityFrameworkCore.Tests;

[NonParallelizable]
public class EndToEndPostgreSqlTests : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
        await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new Npgsql.NpgsqlCommand("""
            CREATE TABLE test_products (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL,
                price NUMERIC(10,2) NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE test_products_outbox (
                id BIGSERIAL PRIMARY KEY,
                entity_id TEXT NOT NULL,
                change_type TEXT NOT NULL,
                timestamp TIMESTAMPTZ NOT NULL,
                version INT NOT NULL DEFAULT 0,
                correlation_id UUID NOT NULL,
                entity_type TEXT NOT NULL,
                data BYTEA,
                published BOOLEAN NOT NULL DEFAULT FALSE
            );
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Test]
    public async Task EfCore_SaveChanges_WritesToPostgreSqlOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new PostgreSqlOutbox(new PostgreSqlOutboxOptions
        {
            ConnectionString = _connectionString,
            OutboxTableName = "test_products_outbox"
        });
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);

        context.Products.Add(new Product { Name = "Widget", Price = 9.99m });
        await context.SaveChangesAsync();

        var outboxEntries = await outbox.GetUnpublishedAsync(10);
        Assert.That(outboxEntries, Has.Count.EqualTo(1));
        Assert.That(outboxEntries[0].EntityType, Does.Contain("Product"));
    }

    [Test]
    public async Task EfCore_MultipleChanges_WritesAllToOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new PostgreSqlOutbox(new PostgreSqlOutboxOptions
        {
            ConnectionString = _connectionString,
            OutboxTableName = "test_products_outbox"
        });
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);

        context.Products.Add(new Product { Name = "Gadget", Price = 19.99m });
        context.Products.Add(new Product { Name = "Doohickey", Price = 4.99m });
        await context.SaveChangesAsync();

        var outboxEntries = await outbox.GetUnpublishedAsync(10);
        Assert.That(outboxEntries, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EfCore_UpdateChange_DetectedAndStored()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new PostgreSqlOutbox(new PostgreSqlOutboxOptions
        {
            ConnectionString = _connectionString,
            OutboxTableName = "test_products_outbox"
        });
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using (var context = new TestDbContext(options))
        {
            context.Products.Add(new Product { Name = "Original", Price = 1.00m });
            await context.SaveChangesAsync();
        }

        await using (var context = new TestDbContext(options))
        {
            var product = context.Products.First();
            product.Name = "Modified";
            await context.SaveChangesAsync();
        }

        var updates = await outbox.GetUnpublishedAsync("TestProduct", changeType: ChangeType.Update, batchSize: 10);
        Assert.That(updates, Is.Not.Empty);
    }

    [Test]
    public async Task EfCore_DeleteChange_DetectedAndStored()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new PostgreSqlOutbox(new PostgreSqlOutboxOptions
        {
            ConnectionString = _connectionString,
            OutboxTableName = "test_products_outbox"
        });
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(Product), outbox);
        tracker.RegisterSerializer(typeof(Product), serializer);
        tracker.RegisterCompressor(typeof(Product), compressor);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(Product) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_connectionString)
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

        var deletes = await outbox.GetUnpublishedAsync("TestProduct", changeType: ChangeType.Delete, batchSize: 10);
        Assert.That(deletes, Is.Not.Empty);
    }

    private class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions options) : base(options) { }

        public DbSet<Product> Products { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>(b =>
            {
                b.ToTable("test_products");
                b.HasKey(e => e.Id);
                b.Property(e => e.Name).HasMaxLength(100).IsRequired();
                b.Property(e => e.Price).HasPrecision(10, 2);
            });
        }
    }

    private class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public decimal Price { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
