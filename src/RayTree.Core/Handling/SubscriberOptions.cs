namespace RayTree.Core.Handling;

public class SubscriberOptions
{
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
    public TimeSpan DeduplicationRetention { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan DeduplicationCleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public bool SkipOnFailure { get; set; }
}
