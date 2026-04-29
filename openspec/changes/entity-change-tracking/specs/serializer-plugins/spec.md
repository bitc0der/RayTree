## ADDED Requirements

### Requirement: Serializer plugin assembly
The system SHALL provide serializers as a separate assembly (`RayTree.Plugins.Serializers`) that can be referenced independently from the core library.

#### Scenario: Reference serializer assembly
- **WHEN** a project references `RayTree.Plugins.Serializers`
- **THEN** all serializer implementations SHALL be available without pulling in repository, outbox, or queue plugins

#### Scenario: Omit serializer assembly
- **WHEN** a project does not reference `RayTree.Plugins.Serializers`
- **THEN** the core library SHALL still compile and function, requiring only an `IChangeSerializer` implementation at runtime

### Requirement: Serializer plugin interface
The system SHALL define `IChangeSerializer` interface in the core assembly that serializer plugins implement.

#### Scenario: Serialize entity change
- **WHEN** `IChangeSerializer.Serialize(change)` is called
- **THEN** the entity change SHALL be converted to a byte array representation

#### Scenario: Deserialize entity change
- **WHEN** `IChangeSerializer.Deserialize(bytes, entityType)` is called
- **THEN** the byte array SHALL be converted back to an `EntityChange` object

### Requirement: Built-in JSON serializer
The serializer assembly SHALL include a JSON serializer using System.Text.Json.

#### Scenario: JSON serialization
- **WHEN** the JSON serializer is configured via `.UseJsonSerializer()`
- **THEN** entity changes SHALL be serialized to UTF-8 encoded JSON

#### Scenario: JSON deserialization
- **WHEN** the JSON serializer deserializes a previously serialized change
- **THEN** the original entity change SHALL be reconstructed with all metadata intact

### Requirement: Built-in Protobuf serializer
The serializer assembly SHALL include a Protobuf serializer using protobuf-net.

#### Scenario: Protobuf serialization
- **WHEN** the Protobuf serializer is configured via `.UseProtobufSerializer()`
- **THEN** entity changes SHALL be serialized to Protobuf binary format

#### Scenario: Protobuf deserialization
- **WHEN** the Protobuf serializer deserializes a previously serialized change
- **THEN** the original entity change SHALL be reconstructed with all metadata intact

### Requirement: Built-in MessagePack serializer
The serializer assembly SHALL include a MessagePack serializer using MessagePack-CSharp.

#### Scenario: MessagePack serialization
- **WHEN** the MessagePack serializer is configured via `.UseMessagePackSerializer()`
- **THEN** entity changes SHALL be serialized to MessagePack binary format

#### Scenario: MessagePack deserialization
- **WHEN** the MessagePack serializer deserializes a previously serialized change
- **THEN** the original entity change SHALL be reconstructed with all metadata intact

### Requirement: Serializer registration
The system SHALL allow registering a serializer via the configuration builder or DI.

#### Scenario: Register via builder
- **WHEN** `.UseSerializer<T>()` is called on the configuration builder
- **THEN** the specified serializer SHALL be used for all message serialization

#### Scenario: Register via DI
- **WHEN** `AddChangeTracking()` is called and a serializer is registered
- **THEN** the serializer SHALL be resolved from the DI container

### Requirement: Custom serializer support
Third-party serializers SHALL be usable by implementing `IChangeSerializer` and registering via the builder.

#### Scenario: Custom serializer registration
- **WHEN** a user implements `IChangeSerializer` and registers it via `.UseSerializer<CustomSerializer>()`
- **THEN** the custom serializer SHALL be used for all message serialization

#### Scenario: Custom serializer in separate assembly
- **WHEN** a custom serializer is defined in a separate assembly
- **THEN** it SHALL be usable without modifying the core or plugin assemblies
