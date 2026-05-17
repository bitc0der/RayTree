using Microsoft.EntityFrameworkCore;
using Moq;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;
using RayTree.EntityFrameworkCore.Interceptors;
using RayTree.EntityFrameworkCore.Extensions;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.EntityFrameworkCore.Tests;

public class EntityChangeInterceptorTests
{
    private class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions options) : base(options) { }

        public DbSet<TestEntity> TestEntities { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestEntity>(b =>
            {
                b.ToTable("test_entities");
                b.HasKey(e => e.Id);
                b.Property(e => e.Name).HasMaxLength(100).IsRequired();
            });
        }
    }

    private class TestEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
    }

    private static EntityChangeTracker BuildTracker(IOutbox outbox)
        => EntityChangeTracker.Create()
            .ForEntity<TestEntity>(e => e
                .UseOutbox(outbox)
                .UsePublisher(new InMemoryQueue())
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();

    [Test]
    public async Task SavingChangesAsync_DetectsAddedEntities()
    {
        // Arrange
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<TestEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tracker = BuildTracker(outbox.Object);
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(TestEntity) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_add")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);
        context.TestEntities.Add(new TestEntity { Name = "New", CreatedAt = DateTime.UtcNow });

        // Act
        await context.SaveChangesAsync();

        // Assert
        outbox.Verify(o => o.WriteAsync(It.IsAny<EntityChange<TestEntity>>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task SavingChangesAsync_DetectsModifiedEntities()
    {
        // Arrange
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<TestEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tracker = BuildTracker(outbox.Object);
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(TestEntity) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_modify")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);
        context.TestEntities.Add(new TestEntity { Name = "Original", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var entity = context.TestEntities.First();
        entity.Name = "Modified";

        // Act
        await context.SaveChangesAsync();

        // Assert
        outbox.Verify(o => o.WriteAsync(It.Is<EntityChange<TestEntity>>(c => c.ChangeType == ChangeType.Update), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task SavingChangesAsync_DetectsDeletedEntities()
    {
        // Arrange
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<TestEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tracker = BuildTracker(outbox.Object);
        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(TestEntity) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_delete")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);
        context.TestEntities.Add(new TestEntity { Name = "To Delete", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var entity = context.TestEntities.First();
        context.TestEntities.Remove(entity);

        // Act
        await context.SaveChangesAsync();

        // Assert
        outbox.Verify(o => o.WriteAsync(It.Is<EntityChange<TestEntity>>(c => c.ChangeType == ChangeType.Delete), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public void ServiceCollectionExtensions_AddChangeTracking_RegistersServices()
    {
        // Arrange
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        // Act
        var result = services.AddChangeTracking();

        // Assert
        Assert.That(result, Is.Not.Null);
    }
}
