# Database Migration Guide

RayTree can automatically generate and execute DDL scripts to create source tables, outbox tables, and triggers.

## Automatic Initialization

### At Application Startup

```csharp
await using var scope = app.Services.CreateAsyncScope();
await scope.ServiceProvider.InitializeRayTreeDatabaseAsync(options =>
{
    options.UseAttributeBasedSchema = true;
    options.TableNamePrefix = "raytree_";
});
```

### Generate DDL Without Executing

```csharp
var tracker = serviceProvider.GetRequiredService<IEntityChangeTracker>();

// Generate CREATE scripts
var createDdl = await tracker.GenerateInitializationDdl<Product>();
Console.WriteLine(createDdl);

// Generate DROP scripts
var dropDdl = await tracker.GenerateDropDdl<Product>();
Console.WriteLine(dropDdl);
```

## Attribute-Based Schema

When `UseAttributeBasedSchema = true`, the DDL generator reads EF Core/data annotation attributes:

```csharp
[Table("products", Schema = "catalog")]
public class Product
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("product_name")]
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = null!;

    [Column("unit_price")]
    public decimal Price { get; set; }

    public DateTime CreatedAt { get; set; }
}
```

Generates:

```sql
CREATE TABLE IF NOT EXISTS catalog.products (
    id SERIAL PRIMARY KEY,
    product_name VARCHAR(200) NOT NULL,
    unit_price NUMERIC(18,2) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS catalog.products_outbox (
    id BIGSERIAL PRIMARY KEY,
    entity_type TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    change_type TEXT NOT NULL,
    change_data BYTEA,
    timestamp TIMESTAMPTZ NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    correlation_id TEXT,
    is_published BOOLEAN NOT NULL DEFAULT FALSE,
    published_at TIMESTAMPTZ,
    retry_count INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_products_outbox_unpublished
    ON catalog.products_outbox (is_published, timestamp);
```

## Type Mapping

| C# Type          | PostgreSQL Type    |
|------------------|--------------------|
| `int`            | `SERIAL`           |
| `long`           | `BIGSERIAL`        |
| `string`         | `TEXT`             |
| `string` (max)   | `VARCHAR(n)`       |
| `DateTime`       | `TIMESTAMPTZ`      |
| `decimal`        | `NUMERIC(18,2)`    |
| `double`         | `DOUBLE PRECISION` |
| `float`          | `REAL`             |
| `bool`           | `BOOLEAN`          |
| `Guid`           | `UUID`             |
| `byte[]`         | `BYTEA`            |

## Manual Migration (EF Core Migrations)

If you prefer to manage schema via EF Core migrations:

```csharp
// Add the outbox table to your migration
migrationBuilder.CreateTable(
    name: "products_outbox",
    columns: table => new
    {
        id = table.Column<long>(nullable: false)
            .Annotation("Npgsql:ValueGenerationStrategy",
                NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
        entity_type = table.Column<string>(nullable: false),
        entity_id = table.Column<string>(nullable: false),
        change_type = table.Column<string>(nullable: false),
        change_data = table.Column<byte[]>(nullable: true),
        timestamp = table.Column<DateTime>(nullable: false),
        version = table.Column<int>(nullable: false, defaultValue: 1),
        correlation_id = table.Column<string>(nullable: true),
        is_published = table.Column<bool>(nullable: false, defaultValue: false),
        published_at = table.Column<DateTime>(nullable: true),
        retry_count = table.Column<int>(nullable: false, defaultValue: 0),
        created_at = table.Column<DateTime>(nullable: false,
            defaultValueSql: "NOW()")
    },
    constraints: table =>
    {
        table.PrimaryKey("pk_products_outbox", x => x.id);
    });

migrationBuilder.CreateIndex(
    name: "idx_products_outbox_unpublished",
    table: "products_outbox",
    columns: new[] { "is_published", "timestamp" });
```

## Schema Changes

### Adding a Column

If you add a column to an entity, regenerate the DDL and run an `ALTER TABLE`:

```sql
ALTER TABLE products ADD COLUMN description TEXT;
-- Outbox table does not need changes - it stores change_data as BYTEA
```

### Dropping a Table

```csharp
var dropDdl = await tracker.GenerateDropDdl<Product>();
// Executes: DROP TABLE IF EXISTS products CASCADE;
//          DROP TABLE IF EXISTS products_outbox CASCADE;
//          DROP TRIGGER IF EXISTS products_notify_trigger ON products;
```

## Outbox Table Schema

All outbox tables share this structure:

| Column         | Type              | Description                           |
|----------------|-------------------|---------------------------------------|
| `id`           | `BIGSERIAL`       | Auto-generated primary key            |
| `entity_type`  | `TEXT`            | Full type name of the entity          |
| `entity_id`    | `TEXT`            | String representation of entity ID    |
| `change_type`  | `TEXT`            | `Insert`, `Update`, or `Delete`       |
| `change_data`  | `BYTEA`           | Serialized + compressed entity data   |
| `timestamp`    | `TIMESTAMPTZ`     | When the change occurred              |
| `version`      | `INTEGER`         | Optimistic concurrency version        |
| `correlation_id` | `TEXT`          | Groups related changes                |
| `is_published` | `BOOLEAN`         | Whether the change was published      |
| `published_at` | `TIMESTAMPTZ`     | When the change was published         |
| `retry_count`  | `INTEGER`         | Number of publish attempts            |
| `created_at`   | `TIMESTAMPTZ`     | Row creation time                     |
