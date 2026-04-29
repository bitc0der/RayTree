## ADDED Requirements

### Requirement: JSON serializer assembly
The system SHALL provide a JSON serializer in a separate assembly (`RayTree.Plugins.Serializers.Json`) that depends only on `RayTree.Core` and `System.Text.Json`.

#### Scenario: Reference JSON serializer assembly
- **WHEN** a project references `RayTree.Plugins.Serializers.Json`
- **THEN** no transitive dependencies beyond Core and System.Text.Json SHALL be pulled in

#### Scenario: JSON serialization
- **WHEN** `.UseJsonSerializer()` is called
- **THEN** entity changes SHALL be serialized to UTF-8 encoded JSON

#### Scenario: JSON deserialization
- **WHEN** the JSON serializer deserializes a previously serialized change
- **THEN** the original entity change SHALL be reconstructed with all metadata intact

### Requirement: Protobuf serializer assembly
The system SHALL provide a Protobuf serializer in a separate assembly (`RayTree.Plugins.Serializers.Protobuf`) that depends only on `RayTree.Core` and `protobuf-net`.

#### Scenario: Reference Protobuf serializer assembly
- **WHEN** a project references `RayTree.Plugins.Serializers.Protobuf`
- **THEN** no transitive dependencies beyond Core and protobuf-net SHALL be pulled in

#### Scenario: Protobuf serialization
- **WHEN** `.UseProtobufSerializer()` is called
- **THEN** entity changes SHALL be serialized to Protobuf binary format

#### Scenario: Protobuf deserialization
- **WHEN** the Protobuf serializer deserializes a previously serialized change
- **THEN** the original entity change SHALL be reconstructed with all metadata intact

### Requirement: MessagePack serializer assembly
The system SHALL provide a MessagePack serializer in a separate assembly (`RayTree.Plugins.Serializers.MessagePack`) that depends only on `RayTree.Core` and `MessagePack-CSharp`.

#### Scenario: Reference MessagePack serializer assembly
- **WHEN** a project references `RayTree.Plugins.Serializers.MessagePack`
- **THEN** no transitive dependencies beyond Core and MessagePack-CSharp SHALL be pulled in

#### Scenario: MessagePack serialization
- **WHEN** `.UseMessagePackSerializer()` is called
- **THEN** entity changes SHALL be serialized to MessagePack binary format

#### Scenario: MessagePack deserialization
- **WHEN** the MessagePack serializer deserializes a previously serialized change
- **THEN** the original entity change SHALL be reconstructed with all metadata intact

### Requirement: Serializer stream-based interface
The `IChangeSerializer` interface in core SHALL use stream-based serialization to avoid intermediate byte array allocations.

#### Scenario: Serialize to stream
- **WHEN** `IChangeSerializer.SerializeAsync(change, destinationStream)` is called
- **THEN** the entity change SHALL be written directly to the destination stream as serialized bytes

#### Scenario: Deserialize from stream
- **WHEN** `IChangeSerializer.DeserializeAsync(sourceStream, entityType)` is called
- **THEN** the entity change SHALL be read and reconstructed from the source stream

#### Scenario: Stream-based pipeline
- **WHEN** the serialization/compression pipeline runs
- **THEN** data SHALL flow through streams without loading the entire payload into memory

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
- **THEN** it SHALL be usable without modifying the core or any plugin assembly
