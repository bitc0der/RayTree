namespace RayTree.Subscriber;

public class SubscriberOptions
{
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
    public TimeSpan DeduplicationRetention { get; set; } = TimeSpan.FromHours(24);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public bool SkipOnFailure { get; set; }
}
