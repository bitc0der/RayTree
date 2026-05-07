# Serializer & Compressor Plugins

Each serializer and compressor is distributed as a separate NuGet package to avoid transitive dependency bloat.

## Serializer Plugins

### JSON Serializer (`RayTree.Plugins.Serializers.Json`)

Default serializer using `System.Text.Json`. No additional dependencies beyond .NET.

```csharp
tracking.ForEntity<Product>()
    .UseJsonSerializer();
```

**Package:** `RayTree.Plugins.Serializers.Json`

**Features:**
- UTF-8 encoding
- Supports polymorphic types
- Configurable via `JsonSerializerOptions`

### Protobuf Serializer (`RayTree.Plugins.Serializers.Protobuf`)

Binary serializer using `protobuf-net`. Compact messages, fast serialization.

```csharp
tracking.ForEntity<Product>()
    .UseProtobufSerializer();
```

**Package:** `RayTree.Plugins.Serializers.Protobuf`

**Features:**
- Binary format (smaller payload than JSON)
- Requires `[ProtoContract]` and `[ProtoMember]` attributes on entity types
- Schema evolution support (additive changes are backward-compatible)

**Entity setup:**

```csharp
[ProtoContract]
public class Product
{
    [ProtoMember(1)]
    public int Id { get; set; }

    [ProtoMember(2)]
    public string Name { get; set; } = null!;

    [ProtoMember(3)]
    public decimal Price { get; set; }
}
```

### MessagePack Serializer (`RayTree.Plugins.Serializers.MessagePack`)

Binary serializer using `MessagePack-CSharp`. Faster than Protobuf for some scenarios.

```csharp
tracking.ForEntity<Product>()
    .UseMessagePackSerializer();
```

**Package:** `RayTree.Plugins.Serializers.MessagePack`

**Features:**
- Binary format
- Uses `ContractlessStandardResolver` (no attributes required on entity types)
- Extremely fast serialization/deserialization

## Compressor Plugins

### Gzip Compressor (`RayTree.Plugins.Compressors.Gzip`)

Standard gzip compression using `System.IO.Compression`. Universal compatibility.

```csharp
tracking.ForEntity<Product>()
    .UseGzipCompressor();
```

**Package:** `RayTree.Plugins.Compressors.Gzip`

**Best for:** General-purpose compression, wide tool support.

### Brotli Compressor (`RayTree.Plugins.Compressors.Brotli`)

Modern compression using `System.IO.Compression`. Better ratio than gzip.

```csharp
tracking.ForEntity<Product>()
    .UseBrotliCompressor();
```

**Package:** `RayTree.Plugins.Compressors.Brotli`

**Best for:** Text-heavy data where size matters more than CPU.

### LZ4 Compressor (`RayTree.Plugins.Compressors.Lz4`)

Ultra-fast compression using `K4os.Compression.LZ4`.

```csharp
tracking.ForEntity<Product>()
    .UseLz4Compressor();
```

**Package:** `RayTree.Plugins.Compressors.Lz4`

**Best for:** High-throughput scenarios where CPU time is critical.

### NoOp Compressor (built-in)

Pass-through compressor (no compression).

```csharp
tracking.ForEntity<Product>()
    .UseNoOpCompressor();
```

**Best for:** Small messages, local testing, or when the message broker handles compression.

## Mixing Serializers and Compressors

Each entity can have its own combination:

```csharp
// High-throughput: fast serializer + fast compressor
tracking.ForEntity<Order>()
    .UseMessagePackSerializer()
    .UseLz4Compressor();

// Storage-optimized: compact serializer + best compressor
tracking.ForEntity<AuditLog>()
    .UseProtobufSerializer()
    .UseBrotliCompressor();

// Simple: JSON + Gzip
tracking.ForEntity<Product>()
    .UseJsonSerializer()
    .UseGzipCompressor();
```

## Important: Subscriber Must Match Publisher

The subscriber configuration must use the same serializer and compressor as the publisher. The publisher serializes and compresses into `MessageEnvelope.Payload`; the subscriber decompresses and deserializes in the reverse order.

```csharp
// Publisher
tracking.ForEntity<Product>()
    .UseProtobufSerializer()
    .UseBrotliCompressor();

// Subscriber (must match!)
builder.Services
    .AddChangeSubscriber(configuration)
    .ConsumeEntity<Product>()
    .UseQueue<Product>(myConsumer)
    .UseSerializer<Product>(new ProtobufSerializerPlugin())   // same as publisher
    .UseCompressor<Product>(new BrotliCompressorPlugin())     // same as publisher
    .OnInsert<Product>(async (change, ct) =>
    {
        var product = change.State; // typed Product
    });
```

If no serializer is registered on the subscriber, `change.State` will be `null` and only the metadata fields (EntityId, ChangeType, CorrelationId, etc.) are available.
