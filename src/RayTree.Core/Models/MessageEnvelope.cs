namespace RayTree.Models;

public class MessageEnvelope
{
    public Guid CorrelationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Serializer { get; set; } = string.Empty;
    public string Compressor { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}
