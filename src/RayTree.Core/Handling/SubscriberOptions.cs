namespace RayTree.Core.Handling;

public class SubscriberOptions
{
    /// <summary>
    /// Maximum number of messages dispatched to handlers concurrently.
    /// Defaults to 1 (sequential) to preserve per-partition message ordering.
    /// Increase only when handlers are order-independent and throughput matters more than ordering.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;
    public TimeSpan DeduplicationRetention { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan DeduplicationCleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public bool SkipOnFailure { get; set; }
}
