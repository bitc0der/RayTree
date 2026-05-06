# Database Migration Guide

RayTree automatically initializes database schemas when you call `Build()` or `BuildAsync()`.

## Automatic Initialization

Calling `Build()` / `BuildAsync()` runs `tracker.InitializeAsync()`, which:

1. **Creates outbox tables** (`CREATE TABLE IF NOT EXISTS`)
2. **Creates source tables** if a repository is registered (`CREATE TABLE IF NOT EXISTS`)
3. **Creates NOTIFY triggers** on the outbox table if `UseNotificationChannel = true`
4. **Creates indexes** for unpublished-change queries

No manual migration step is needed for initial setup.

```csharp
var tracker = builder.Build();    // sync
var tracker = await builder.BuildAsync(); // async
```

## Outbox Table Schema

Each outbox table contains fixed metadata columns plus one `state_*` column per entity property:

### Fixed columns

| Column           | Type             | Description                                |
|------------------|------------------|--------------------------------------------|
| `id`             | `BIGSERIAL`      | Auto-generated primary key                 |
| `entity_id`      | `TEXT`           | String representation of the entity's ID   |
| `change_type`    | `VARCHAR(10)`    | `Insert`, `Update`, or `Delete`            |
| `timestamp`      | `TIMESTAMPTZ`    | When the change occurred (default `NOW()`) |
| `published`      | `BOOLEAN`        | Whether the change was published (default `FALSE`) |
| `version`        | `INTEGER`        | Optimistic concurrency version (default `1`) |
| `correlation_id` | `UUID`           | Auto-generated per change (`gen_random_uuid()`) |
| `entity_type`    | `TEXT`           | Simple type name of the entity             |

### Per-property state columns

Each public read/write property on the entity gets a `state_<snake_case_name>` column. For example:

```csharp
public class Product
{
    public int    Id    { get; set; }
    public string Name  { get; set; } = null!;
    public decimal Price { get; set; }
}
```

Generates additional columns:

| Column         | Type      |
|----------------|-----------|
| `state_id`     | `INTEGER` |
| `state_name`   | `TEXT`    |
| `state_price`  | `NUMERIC` |

### Indexes

| Index                                  | Columns                               | Partial                  |
|----------------------------------------|---------------------------------------|--------------------------|
| `idx_<entity>_outbox_unpublished`      | `published`, `timestamp`              | `WHERE published = FALSE` |
| `idx_<entity>_outbox_entity`           | `entity_type`, `published`, `timestamp` | —                       |

## C# → PostgreSQL Type Mapping

`EntityColumnMapper` maps entity property types to PostgreSQL column types for `state_*` columns:

| C# Type                         | PostgreSQL Type    |
|---------------------------------|--------------------|
| `int`                           | `INTEGER`          |
| `long`                          | `BIGINT`           |
| `short`, `byte`, `sbyte`        | `SMALLINT`         |
| `string`                        | `TEXT`             |
| `decimal`                       | `NUMERIC`          |
| `float`                         | `REAL`             |
| `double`                        | `DOUBLE PRECISION` |
| `bool`                          | `BOOLEAN`          |
| `Guid`                          | `UUID`             |
| `DateTime`, `DateTimeOffset`    | `TIMESTAMPTZ`      |
| anything else                   | `TEXT`             |

Nullable types and reference types produce nullable columns. Value types produce `NOT NULL` columns.

## Default Table Names

If `OutboxTableName` or `TableName` is not specified, names are derived from the entity type:

| Entity type   | Outbox table       | Source table  |
|---------------|--------------------|---------------|
| `Product`     | `product_outbox`   | `product`     |
| `OrderLine`   | `order_line_outbox`| `order_line`  |

## Generate DDL for Inspection

To preview the SQL that will be executed without running it:

```csharp
var outboxSchema = OutboxTableSchema.Create("Product", "products_outbox");
// Add property columns if needed
outboxSchema.AddEntityPropertyColumn("Name", "state_name", "TEXT", isNullable: true);

var ddl = OutboxSchemaGenerator.GenerateCreateTable(outboxSchema, includeIndexes: true);
Console.WriteLine(ddl);
```

## Schema Changes

### Adding a property to an entity

The outbox table needs a new `state_*` column. Add it with a migration:

```sql
ALTER TABLE products_outbox ADD COLUMN state_description TEXT;
```

### Dropping an outbox table

```sql
DROP TRIGGER IF EXISTS products_outbox_notify_trigger ON products_outbox;
DROP FUNCTION IF EXISTS notify_products_outbox_change();
DROP TABLE IF EXISTS products_outbox;
```
