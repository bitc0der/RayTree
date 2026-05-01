## ADDED Requirements

### Requirement: EntityChange SHALL include content payload properties
The EntityChange model SHALL include `BeforeContent` and `AfterContent` properties to store entity state as JSON strings.

#### Scenario: EntityChange contains content properties
- **WHEN** a new EntityChange is created
- **THEN** it SHALL have `BeforeContent` (string, nullable) and `AfterContent` (string, nullable) properties

#### Scenario: Content properties default to null
- **WHEN** an EntityChange is instantiated without content
- **THEN** `BeforeContent` and `AfterContent` SHALL be null

### Requirement: Content tracking configuration SHALL be available
The system SHALL provide `ContentTrackingOptions` to configure what content is tracked during change tracking.

#### Scenario: Default configuration disables content tracking
- **WHEN** ContentTrackingOptions is created without explicit configuration
- **THEN** content tracking mode SHALL be `None`

#### Scenario: Configure AfterOnly mode
- **WHEN** content tracking is configured with `AfterOnly` mode
- **THEN** only the state after the change SHALL be captured

#### Scenario: Configure BeforeAndAfter mode
- **WHEN** content tracking is configured with `BeforeAndAfter` mode
- **THEN** both state before and after the change SHALL be captured for updates

### Requirement: Insert changes SHALL capture after state
When an entity is inserted and content tracking is enabled, the system SHALL capture the entity state after insertion as JSON in `AfterContent`.

#### Scenario: Insert with AfterOnly mode
- **WHEN** an entity is inserted with content tracking mode `AfterOnly`
- **THEN** `AfterContent` SHALL contain the serialized entity state
- **AND** `BeforeContent` SHALL be null

#### Scenario: Insert with BeforeAndAfter mode
- **WHEN** an entity is inserted with content tracking mode `BeforeAndAfter`
- **THEN** `AfterContent` SHALL contain the serialized entity state
- **AND** `BeforeContent` SHALL be null (no before state for new entities)

### Requirement: Update changes SHALL capture configured content
When an entity is updated and content tracking is enabled, the system SHALL capture entity state according to the configured mode.

#### Scenario: Update with AfterOnly mode
- **WHEN** an entity is updated with content tracking mode `AfterOnly`
- **THEN** `AfterContent` SHALL contain the serialized entity state after update
- **AND** `BeforeContent` SHALL be null

#### Scenario: Update with BeforeAndAfter mode
- **WHEN** an entity is updated with content tracking mode `BeforeAndAfter`
- **THEN** `BeforeContent` SHALL contain the serialized entity state before update
- **AND** `AfterContent` SHALL contain the serialized entity state after update

#### Scenario: Update with None mode
- **WHEN** an entity is updated with content tracking mode `None`
- **THEN** both `BeforeContent` and `AfterContent` SHALL be null

### Requirement: Delete changes SHALL capture before state when configured
When an entity is deleted and content tracking is enabled with `BeforeAndAfter` mode, the system SHALL capture the entity state before deletion.

#### Scenario: Delete with AfterOnly mode
- **WHEN** an entity is deleted with content tracking mode `AfterOnly`
- **THEN** both `BeforeContent` and `AfterContent` SHALL be null

#### Scenario: Delete with BeforeAndAfter mode
- **WHEN** an entity is deleted with content tracking mode `BeforeAndAfter`
- **THEN** `BeforeContent` SHALL contain the serialized entity state before deletion
- **AND** `AfterContent` SHALL be null

### Requirement: Outbox SHALL persist content payloads
The outbox storage SHALL persist `BeforeContent` and `AfterContent` alongside change metadata.

#### Scenario: Write change with content to outbox
- **WHEN** an EntityChange with `BeforeContent` and `AfterContent` is written to the outbox
- **THEN** the content SHALL be stored and retrievable with the change

#### Scenario: Read change with content from outbox
- **WHEN** an EntityChange is retrieved from the outbox
- **THEN** `BeforeContent` and `AfterContent` SHALL contain the values that were stored

#### Scenario: Example PostgreSQL trigger-based DDL with plain entity fields
- **WHEN** implementing trigger-based outbox capture with PostgreSQL using plain (non-serialized) entity fields
- **THEN** the following DDL SHALL be used for source table, outbox table, trigger function, and trigger attachment:
```sql
-- Source table
CREATE TABLE products (
    id SERIAL PRIMARY KEY,
    name VARCHAR(255) NOT NULL,
    price DECIMAL(10, 2) NOT NULL,
    stock INT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Outbox table with plain fields
CREATE TABLE products_outbox (
    outbox_id BIGSERIAL PRIMARY KEY,
    change_type VARCHAR(10) NOT NULL,
    change_timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published BOOLEAN NOT NULL DEFAULT FALSE,
    correlation_id UUID NOT NULL DEFAULT gen_random_uuid(),
    version INT NOT NULL DEFAULT 1,
    id INT,
    name VARCHAR(255),
    price DECIMAL(10, 2),
    stock INT,
    created_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ
);

CREATE INDEX idx_products_outbox_unpublished ON products_outbox (published, change_timestamp) WHERE published = FALSE;

-- Trigger function
CREATE OR REPLACE FUNCTION capture_products_changes()
RETURNS TRIGGER AS $$
BEGIN
    IF (TG_OP = 'INSERT') THEN
        INSERT INTO products_outbox (change_type, id, name, price, stock, created_at, updated_at)
        VALUES ('INSERT', NEW.id, NEW.name, NEW.price, NEW.stock, NEW.created_at, NEW.updated_at);
        RETURN NEW;
    ELSIF (TG_OP = 'UPDATE') THEN
        INSERT INTO products_outbox (change_type, id, name, price, stock, created_at, updated_at)
        VALUES ('UPDATE', NEW.id, NEW.name, NEW.price, NEW.stock, NEW.created_at, NEW.updated_at);
        RETURN NEW;
    ELSIF (TG_OP = 'DELETE') THEN
        INSERT INTO products_outbox (change_type, id, name, price, stock, created_at, updated_at)
        VALUES ('DELETE', OLD.id, OLD.name, OLD.price, OLD.stock, OLD.created_at, OLD.updated_at);
        RETURN OLD;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Attach trigger
CREATE TRIGGER products_change_trigger
AFTER INSERT OR UPDATE OR DELETE ON products
FOR EACH ROW EXECUTE FUNCTION capture_products_changes();
```

### Requirement: Serialization pipeline SHALL handle content payloads
The serialization and compression pipeline SHALL include `BeforeContent` and `AfterContent` in the serialized output.

#### Scenario: Serialize change with content
- **WHEN** an EntityChange with content is serialized
- **THEN** the serialized output SHALL include `BeforeContent` and `AfterContent` values

#### Scenario: Deserialize change with content
- **WHEN** a serialized EntityChange with content is deserialized
- **THEN** the resulting EntityChange SHALL have `BeforeContent` and `AfterContent` restored

### Requirement: ChangeTrackingConfiguration SHALL support content tracking configuration
The ChangeTrackingConfiguration class SHALL provide a method to configure content tracking options.

#### Scenario: Configure content tracking via ChangeTrackingConfiguration
- **WHEN** `WithContentTracking(mode)` is called on ChangeTrackingConfiguration
- **THEN** the content tracking mode SHALL be set for the tracker

#### Scenario: Content tracking configured per entity type
- **WHEN** content tracking is configured for a specific entity type via `ForEntity<T>()`
- **THEN** the content tracking mode SHALL apply to that entity type only
