# Database Migration Guide

RayTree automatically initializes and migrates database schemas when you call `Build()` or `BuildAsync()`.

## Automatic Initialization and Migration

Calling `Build()` / `BuildAsync()` automatically initializes every registered outbox and repository. Each one inspects the live database and:

- **Creates the table** if it does not exist (`CREATE TABLE IF NOT EXISTS` with all columns and indexes in one statement).
- **Migrates the table** if it already exists — adds missing columns, syncs indexes, and logs warnings for anything that requires manual attention.

No manual migration step is needed for either initial setup or schema evolution.

```csharp
var tracker = builder.Build();       // sync
var tracker = await builder.BuildAsync(); // async
```

---

## Fresh Table Path

When the table has never been created, a single `CREATE TABLE IF NOT EXISTS` statement is issued that includes all columns and all indexes. The `IF NOT EXISTS` guard is retained as a safety net for concurrent startup scenarios (e.g. two application instances starting at the same time).

---

## Existing Table Path (Schema Migration)

When the table already exists, two diff passes run automatically every time the application starts.

### Column diff

The desired column set (entity property columns for the outbox; key columns for the repository) is compared against `information_schema.columns`.

| Situation | Action |
|---|---|
| Column is in the entity but not in the table | `ALTER TABLE … ADD COLUMN IF NOT EXISTS` |
| Column is `NOT NULL`, has no default, and the table already has rows | `InvalidOperationException` thrown — add a `DEFAULT` or migrate manually |
| Column is in the table but not in the entity | `Warning` logged — drop it manually when ready |
| Column exists in both but types differ | `Warning` logged — type changes must be migrated manually |

### Index diff

The desired index set is compared against the live `pg_index` catalog. Each index is compared by uniqueness, column order, and WHERE clause (comparison is case-insensitive and trimmed, so `published = FALSE` matches `published = false`).

| Situation | Action |
|---|---|
| Index is in the schema but not in the database | `CREATE INDEX IF NOT EXISTS …` |
| Index exists but its definition changed (columns, uniqueness, or WHERE) | `DROP INDEX IF EXISTS public.{name}` then `CREATE INDEX IF NOT EXISTS …` |
| Index exists in the database but is not in the entity schema | `Warning` logged — drop it manually when ready |

---

## Schema Change Scenarios

### Adding a property to an entity

Add the property to your entity class. On the next startup, the missing `state_*` column is detected and added automatically.

```csharp
public class Product
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = null!;
    public string Description { get; set; } = null!;   // new property — added on next startup
}
```

> **NOT NULL without a default on a non-empty table** — if the new property has `[Required]` (or is a non-nullable value type) and the outbox table already contains rows, an `InvalidOperationException` is thrown on startup with a message like:
> ```
> Cannot add column 'state_description': it is NOT NULL with no default and table
> 'product_outbox' already has rows. Add a DEFAULT or migrate manually.
> ```
> Either make the property nullable, provide a `DEFAULT` via a manual `ALTER TABLE`, or truncate the table first.

### Removing a property from an entity

Remove the property from your entity class. The corresponding `state_*` column remains in the database — a `Warning` is logged on startup and it is left alone. Drop it manually when you are ready:

```sql
ALTER TABLE product_outbox DROP COLUMN state_description;
```

### Changing a property type

Change the C# type. The mismatch between the live column type and the expected type is detected on startup and a `Warning` is logged. Type changes must be applied manually:

```sql
ALTER TABLE product_outbox ALTER COLUMN state_price TYPE NUMERIC(18,4);
```

### Renaming a property

Rename the C# property (or change the `[Column]` name override). From RayTree's perspective this is a removal of the old column and an addition of the new one:

1. The new `state_*` column is added automatically.
2. The old `state_*` column logs a `Warning` as an orphan.

If the data in the old column needs to be preserved, migrate manually before starting the application:

```sql
ALTER TABLE product_outbox RENAME COLUMN state_old_name TO state_new_name;
```

---

## Outbox Table Schema

Each outbox table contains fixed metadata columns plus one `state_*` column per entity property.

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
    public int      Id    { get; set; }
    public string   Name  { get; set; } = null!;
    public decimal  Price { get; set; }
    public string[] Tags  { get; set; } = [];
}
```

Generates additional columns:

| Column         | Type      |
|----------------|-----------|
| `state_id`     | `INTEGER` |
| `state_name`   | `TEXT`    |
| `state_price`  | `NUMERIC` |
| `state_tags`   | `TEXT[]`  |

### Indexes

Three indexes are created for the outbox table and kept in sync by `IndexMigrator` on every startup:

| Index                             | Columns                                   | Partial                   |
|-----------------------------------|-------------------------------------------|---------------------------|
| `idx_<entity>_outbox_unpublished` | `published`, `timestamp`                  | `WHERE published = FALSE` |
| `idx_<entity>_outbox_cleanup`     | `timestamp`                               | `WHERE published = TRUE`  |
| `idx_<entity>_outbox_entity`      | `entity_type`, `published`, `timestamp`   | —                         |

If an index definition is changed in a future version of RayTree, `IndexMigrator` will automatically drop and recreate it on the next startup.

---

## C# → PostgreSQL Type Mapping

`EntityColumnMapper` maps entity property types to PostgreSQL column types for `state_*` columns:

| C# Type                         | PostgreSQL Type      |
|---------------------------------|----------------------|
| `int`                           | `INTEGER`            |
| `long`                          | `BIGINT`             |
| `short`, `byte`, `sbyte`        | `SMALLINT`           |
| `string`                        | `TEXT`               |
| `decimal`                       | `NUMERIC`            |
| `float`                         | `REAL`               |
| `double`                        | `DOUBLE PRECISION`   |
| `bool`                          | `BOOLEAN`            |
| `Guid`                          | `UUID`               |
| `DateTime`, `DateTimeOffset`    | `TIMESTAMPTZ`        |
| `T[]` (1D array of any above)   | `<mapped type>[]`    |
| anything else                   | `TEXT`               |

Array rules:
- 1D arrays of any supported primitive type are mapped to the corresponding PostgreSQL array column, e.g. `int[]` → `INTEGER[]`, `string[]` → `TEXT[]`, `Guid[]` → `UUID[]`.
- Nullable-element arrays (e.g. `int?[]`) strip the nullable wrapper before mapping the element type — the column type is the same as for a non-nullable element array.
- Multi-dimensional arrays are not supported; declare the column type explicitly via `[Column(TypeName = "INTEGER[][]")]` if needed.

Nullable types and reference types (including arrays) produce nullable columns. Value types produce `NOT NULL` columns. Add `[Required]` to force `NOT NULL` on a reference type or nullable value type.

---

## Table Names

Table names are derived from the entity type — apply `[Table("name")]` to override the default, or rely on the snake_case convention for the type name:

| Entity type                       | Outbox table        | Source table  |
|-----------------------------------|---------------------|---------------|
| `Product`                         | `product_outbox`    | `product`     |
| `OrderLine`                       | `order_line_outbox` | `order_line`  |
| `[Table("orders")] class Order`   | `orders_outbox`     | `orders`      |

---

## Generate DDL for Inspection

To preview the SQL that will be executed without running it:

```csharp
var outboxSchema = OutboxTableSchema.Create("Product", "products_outbox");
outboxSchema.AddEntityPropertyColumn("Name", "state_name", "TEXT", isNullable: true);

var ddl = OutboxSchemaGenerator.GenerateCreateTable(outboxSchema, includeIndexes: true);
Console.WriteLine(ddl);
```

---

## Dropping an Outbox Table

```sql
DROP TRIGGER IF EXISTS products_outbox_notify_trigger ON products_outbox;
DROP FUNCTION IF EXISTS notify_products_outbox_change();
DROP TABLE IF EXISTS products_outbox;
```
