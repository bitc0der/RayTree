## ADDED Requirements

### Requirement: DI container registration
The system SHALL provide extension methods on `IServiceCollection` to register all change tracking components.

#### Scenario: Register with defaults
- **WHEN** `AddChangeTracking()` is called on IServiceCollection
- **THEN** all core change tracking services SHALL be registered with default implementations

#### Scenario: Register with builder
- **WHEN** `AddChangeTracking()` is called with a builder action
- **THEN** the builder action SHALL be executed to configure plugins and entity tracking

#### Scenario: Singleton service lifetime
- **WHEN** change tracking services are registered via DI
- **THEN** core services SHALL be registered as singletons

### Requirement: HostedService for outbox publisher
The system SHALL register an `IHostedService` that runs the outbox polling and publishing loop.

#### Scenario: Start hosted service
- **WHEN** the application host starts
- **THEN** the outbox publisher hosted service SHALL start polling for unpublished changes

#### Scenario: Stop hosted service
- **WHEN** the application host stops
- **THEN** the outbox publisher hosted service SHALL gracefully stop, completing in-flight publish operations

#### Scenario: Background polling loop
- **WHEN** the hosted service is running
- **THEN** it SHALL poll for unpublished changes at the configured interval

### Requirement: DbContext auto-registration
The system SHALL automatically attach the change tracking interceptor to DbContext instances registered in DI.

#### Scenario: Auto-attach to DbContext
- **WHEN** a DbContext is registered in the DI container and `AddChangeTracking()` is called
- **THEN** the change tracking interceptor SHALL be attached to that DbContext's options

#### Scenario: Opt-out from auto-registration
- **WHEN** a DbContext is explicitly excluded from change tracking
- **THEN** the interceptor SHALL NOT be attached to that DbContext

### Requirement: Configuration via Microsoft.Extensions.Options
The system SHALL use `IOptions<T>` for configuration binding, supporting appsettings.json and environment variables.

#### Scenario: Bind from appsettings.json
- **WHEN** change tracking configuration is present in appsettings.json
- **THEN** the configuration SHALL be bound to the options object

#### Scenario: Environment variable override
- **WHEN** an environment variable overrides a configuration value
- **THEN** the environment variable value SHALL take precedence
