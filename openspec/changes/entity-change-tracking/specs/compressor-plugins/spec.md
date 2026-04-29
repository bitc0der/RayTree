## ADDED Requirements

### Requirement: Gzip compressor assembly
The system SHALL provide a Gzip compressor in a separate assembly (`RayTree.Plugins.Compressors.Gzip`) that depends only on `RayTree.Core` and `System.IO.Compression`.

#### Scenario: Reference Gzip compressor assembly
- **WHEN** a project references `RayTree.Plugins.Compressors.Gzip`
- **THEN** no transitive dependencies beyond Core and System.IO.Compression SHALL be pulled in

#### Scenario: Gzip compression
- **WHEN** `.UseGzipCompressor()` is called
- **THEN** serialized messages SHALL be compressed using Gzip format

#### Scenario: Gzip decompression
- **WHEN** a Gzip-compressed message is decompressed
- **THEN** the original serialized byte array SHALL be restored

### Requirement: Brotli compressor assembly
The system SHALL provide a Brotli compressor in a separate assembly (`RayTree.Plugins.Compressors.Brotli`) that depends only on `RayTree.Core` and `System.IO.Compression`.

#### Scenario: Reference Brotli compressor assembly
- **WHEN** a project references `RayTree.Plugins.Compressors.Brotli`
- **THEN** no transitive dependencies beyond Core and System.IO.Compression SHALL be pulled in

#### Scenario: Brotli compression
- **WHEN** `.UseBrotliCompressor()` is called
- **THEN** serialized messages SHALL be compressed using Brotli format

#### Scenario: Brotli decompression
- **WHEN** a Brotli-compressed message is decompressed
- **THEN** the original serialized byte array SHALL be restored

### Requirement: LZ4 compressor assembly
The system SHALL provide an LZ4 compressor in a separate assembly (`RayTree.Plugins.Compressors.Lz4`) that depends only on `RayTree.Core` and `lz4net`.

#### Scenario: Reference LZ4 compressor assembly
- **WHEN** a project references `RayTree.Plugins.Compressors.Lz4`
- **THEN** no transitive dependencies beyond Core and lz4net SHALL be pulled in

#### Scenario: LZ4 compression
- **WHEN** `.UseLz4Compressor()` is called
- **THEN** serialized messages SHALL be compressed using LZ4 format

#### Scenario: LZ4 decompression
- **WHEN** an LZ4-compressed message is decompressed
- **THEN** the original serialized byte array SHALL be restored

### Requirement: Compressor stream-based interface
The `IChangeCompressor` interface in core SHALL use stream-based compression to avoid intermediate byte array allocations.

#### Scenario: Compress stream to stream
- **WHEN** `IChangeCompressor.CompressAsync(sourceStream, destinationStream)` is called
- **THEN** the compressed data SHALL be written directly to the destination stream

#### Scenario: Decompress stream to stream
- **WHEN** `IChangeCompressor.DecompressAsync(sourceStream, destinationStream)` is called
- **THEN** the decompressed data SHALL be written directly to the destination stream

#### Scenario: Stream chaining
- **WHEN** serializer writes to a stream and compressor reads from it
- **THEN** no intermediate byte array copy SHALL be created between the two operations

### Requirement: NoOp compressor
The Core assembly SHALL include a pass-through `NoOpCompressor` that performs no compression.

#### Scenario: NoOp compression
- **WHEN** `.UseNoOpCompressor()` is called
- **THEN** the input byte array SHALL be returned unchanged

#### Scenario: NoOp decompression
- **WHEN** the NoOp compressor decompresses a byte array
- **THEN** the input byte array SHALL be returned unchanged

### Requirement: Compressor registration
The system SHALL allow registering a compressor via the configuration builder or DI.

#### Scenario: Register via builder
- **WHEN** `.UseCompressor<T>()` is called on the configuration builder
- **THEN** the specified compressor SHALL be used for all message compression

#### Scenario: Register via DI
- **WHEN** `AddChangeTracking()` is called and a compressor is registered
- **THEN** the compressor SHALL be resolved from the DI container

### Requirement: Custom compressor support
Third-party compressors SHALL be usable by implementing `IChangeCompressor` and registering via the builder.

#### Scenario: Custom compressor registration
- **WHEN** a user implements `IChangeCompressor` and registers it via `.UseCompressor<CustomCompressor>()`
- **THEN** the custom compressor SHALL be used for all message compression

#### Scenario: Custom compressor in separate assembly
- **WHEN** a custom compressor is defined in a separate assembly
- **THEN** it SHALL be usable without modifying the core or any plugin assembly
