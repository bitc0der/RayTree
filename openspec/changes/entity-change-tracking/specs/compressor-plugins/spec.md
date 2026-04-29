## ADDED Requirements

### Requirement: Compressor plugin assembly
The system SHALL provide compressors as a separate assembly (`RayTree.Plugins.Compressors`) that can be referenced independently from the core library.

#### Scenario: Reference compressor assembly
- **WHEN** a project references `RayTree.Plugins.Compressors`
- **THEN** all compressor implementations SHALL be available without pulling in repository, outbox, or queue plugins

#### Scenario: Omit compressor assembly
- **WHEN** a project does not reference `RayTree.Plugins.Compressors`
- **THEN** the core library SHALL still compile and function, requiring only an `IChangeCompressor` implementation at runtime

### Requirement: Compressor plugin interface
The system SHALL define `IChangeCompressor` interface in the core assembly that compressor plugins implement.

#### Scenario: Compress message
- **WHEN** `IChangeCompressor.Compress(bytes)` is called
- **THEN** the input byte array SHALL be compressed and returned as a smaller byte array

#### Scenario: Decompress message
- **WHEN** `IChangeCompressor.Decompress(bytes)` is called
- **THEN** the compressed byte array SHALL be decompressed to the original byte array

### Requirement: Built-in Gzip compressor
The compressor assembly SHALL include a Gzip compressor using `System.IO.Compression`.

#### Scenario: Gzip compression
- **WHEN** the Gzip compressor is configured via `.UseGzipCompressor()`
- **THEN** serialized messages SHALL be compressed using Gzip format

#### Scenario: Gzip decompression
- **WHEN** a Gzip-compressed message is decompressed
- **THEN** the original serialized byte array SHALL be restored

### Requirement: Built-in Brotli compressor
The compressor assembly SHALL include a Brotli compressor using `System.IO.Compression`.

#### Scenario: Brotli compression
- **WHEN** the Brotli compressor is configured via `.UseBrotliCompressor()`
- **THEN** serialized messages SHALL be compressed using Brotli format

#### Scenario: Brotli decompression
- **WHEN** a Brotli-compressed message is decompressed
- **THEN** the original serialized byte array SHALL be restored

### Requirement: Built-in LZ4 compressor
The compressor assembly SHALL include an LZ4 compressor using lz4net.

#### Scenario: LZ4 compression
- **WHEN** the LZ4 compressor is configured via `.UseLz4Compressor()`
- **THEN** serialized messages SHALL be compressed using LZ4 format

#### Scenario: LZ4 decompression
- **WHEN** an LZ4-compressed message is decompressed
- **THEN** the original serialized byte array SHALL be restored

### Requirement: No-op compressor
The core assembly SHALL include a pass-through `NoOpCompressor` that performs no compression.

#### Scenario: No-op compression
- **WHEN** the no-op compressor is configured via `.UseNoOpCompressor()`
- **THEN** the input byte array SHALL be returned unchanged

#### Scenario: No-op decompression
- **WHEN** the no-op compressor decompresses a byte array
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
- **THEN** it SHALL be usable without modifying the core or plugin assemblies
