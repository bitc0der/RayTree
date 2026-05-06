using Microsoft.EntityFrameworkCore;
using Moq;
using RayTree.EntityFrameworkCore.Interceptors;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;
using RayTree.EntityFrameworkCore.Extensions;

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

    [Test]
    public async Task SavingChangesAsync_DetectsAddedEntities()
    {
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<TestEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tracker = new EntityChangeTracker();
        tracker.RegisterOutbox(typeof(TestEntity), outbox.Object);

        var interceptor = new EntityChangeInterceptor(tracker, new[] { typeof(TestEntity) });

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase("test_add")
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);

        context.TestEntities.Add(new TestEntity { Name = "New", CreatedAt = DateTime.UtcNow });

        await context.SaveChangesAsync();

        outbox.Verify(o => o.WriteAsync(It.IsAny<EntityChange<TestEntity>>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task SavingChangesAsync_DetectsModifiedEntities()
    {
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<TestEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tracker = new EntityChangeTracker();
        tracker.RegisterOutbox(typeof(TestEntity), outbox.Object);

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

        await context.SaveChangesAsync();

        outbox.Verify(o => o.WriteAsync(It.Is<EntityChange<TestEntity>>(c => c.ChangeType == ChangeType.Update), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task SavingChangesAsync_DetectsDeletedEntities()
    {
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<TestEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tracker = new EntityChangeTracker();
        tracker.RegisterOutbox(typeof(TestEntity), outbox.Object);

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

        await context.SaveChangesAsync();

        outbox.Verify(o => o.WriteAsync(It.Is<EntityChange<TestEntity>>(c => c.ChangeType == ChangeType.Delete), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public void ServiceCollectionExtensions_AddChangeTracking_RegistersServices()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var result = services.AddChangeTracking();

        Assert.That(result, Is.Not.Null);
    }
}
