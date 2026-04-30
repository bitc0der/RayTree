# EF Core Integration Guide

RayTree integrates with Entity Framework Core via `ISaveChangesInterceptor`, automatically detecting entity changes and writing them to the outbox within the same transaction.

## Basic Setup

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddChangeTracking(tracking =>
{
    tracking.ForEntity<Product>()
        .UsePostgreSqlOutbox(connectionString, "products");

    tracking.UseJsonSerializer();
    tracking.UseGzipCompressor();
});
```

The interceptor is automatically attached to all registered `DbContext` types.

## How It Works

1. **SavingChanges** - Interceptor detects `Added`, `Modified`, and `Deleted` entities
2. **SavedChanges** - Writes changes to the outbox **within the same EF Core transaction**
3. **Publisher** - Background service reads unpublished outbox entries and publishes to the queue

If the transaction rolls back, the outbox entry is also rolled back - ensuring exactly-once semantics.

## Entity Registration

### Single Entity

```csharp
tracking.ForEntity<Product>()
    .UsePostgreSqlOutbox(connectionString, "products");
```

### Multiple Entities

```csharp
tracking.ForEntity<Product>()
    .UsePostgreSqlOutbox(conn, "products");

tracking.ForEntity<Order>()
    .UsePostgreSqlOutbox(conn, "orders");
```

### With Filters

```csharp
tracking.ForEntity<Product>(filter: e => e.Property<string>("Name").CurrentValue != null)
    .UsePostgreSqlOutbox(conn, "products");
```

## Multi-DbContext Support

### Apply to All DbContexts

By default, the interceptor is attached to all `DbContext` types registered in DI.

### Exclude Specific DbContext

```csharp
tracking.ExcludeDbContext<ReportingDbContext>();
```

### Per-DbContext Configuration

```csharp
tracking.ForDbContext<SalesDbContext>(ctx =>
{
    ctx.ForEntity<Order>()
        .UsePostgreSqlOutbox(conn, "orders");

    ctx.ForEntity<Customer>()
        .UsePostgreSqlOutbox(conn, "customers");
});

tracking.ForDbContext<InventoryDbContext>(ctx =>
{
    ctx.ForEntity<Product>()
        .UsePostgreSqlOutbox(conn, "products");
});
```

## Async Support

The interceptor implements both sync and async methods:

```csharp
// Works with both
await context.SaveChangesAsync();  // Async path
context.SaveChanges();             // Sync path
```

## Transaction Behavior

```csharp
using var tx = await context.Database.BeginTransactionAsync();

context.Products.Add(new Product { Name = "New" });
await context.SaveChangesAsync();
// Outbox entry is written here, within the transaction

// If you roll back, the outbox entry is also removed
await tx.RollbackAsync();
```

## Manual Change Tracking (outside EF Core)

You can track changes manually even with the EF Core interceptor configured:

```csharp
var tracker = serviceProvider.GetRequiredService<IEntityChangeTracker>();

await tracker.TrackChangesAsync(new[]
{
    new EntityChange
    {
        EntityType = typeof(ExternalEntity).AssemblyQualifiedName!,
        EntityId = "ext-1",
        ChangeType = ChangeType.Update,
        Timestamp = DateTime.UtcNow
    }
});
```
