namespace RayTree.Core.Resilience;

/// <summary>
/// Tunes the connection-recovery retry shape for plugins that own their own reconnect loop
/// (Postgres LISTEN, Kafka fatal-error rebuild). The values describe an exponential-backoff
/// schedule: the Nth retry waits <c>min(InitialDelay × Factor^(N-1), MaxDelay)</c> seconds,
/// jittered by <c>±JitterFraction</c>. RabbitMQ does not consume these options — the
/// RabbitMQ.Client SDK owns its own recovery policy.
/// </summary>
public sealed class ConnectionRecoveryOptions
{
    private readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _maxDelay = TimeSpan.FromSeconds(30);
    private readonly double _factor = 2.0;
    private readonly double _jitterFraction = 0.2;
    private readonly int? _maxAttempts;

    /// <summary>
    /// Master switch. When <c>false</c>, plugins SHALL surface connection-fault exceptions
    /// to the caller without attempting any reconnect.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The delay before the second attempt (first retry). Must be greater than zero.</summary>
    public TimeSpan InitialDelay
    {
        get => _initialDelay;
        init
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(InitialDelay), value, "must be greater than zero");
            _initialDelay = value;
        }
    }

    /// <summary>The maximum delay between attempts. Must be greater than or equal to <see cref="InitialDelay"/>.</summary>
    public TimeSpan MaxDelay
    {
        get => _maxDelay;
        init
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(MaxDelay), value, "must be greater than zero");
            _maxDelay = value;
        }
    }

    /// <summary>The geometric backoff multiplier. Must be >= 1.0.</summary>
    public double Factor
    {
        get => _factor;
        init
        {
            if (value < 1.0)
                throw new ArgumentOutOfRangeException(nameof(Factor), value, "must be greater than or equal to 1.0");
            _factor = value;
        }
    }

    /// <summary>
    /// The symmetric jitter fraction applied independently to each scheduled delay.
    /// A value of <c>0.2</c> yields a uniform draw from <c>[delay × 0.8, delay × 1.2]</c>.
    /// Must be in <c>[0, 1]</c>.
    /// </summary>
    public double JitterFraction
    {
        get => _jitterFraction;
        init
        {
            if (value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(JitterFraction), value, "must be in [0, 1]");
            _jitterFraction = value;
        }
    }

    /// <summary>
    /// Maximum number of attempts before giving up and rethrowing the last exception.
    /// <c>null</c> means unlimited. When non-null, must be greater than zero.
    /// </summary>
    public int? MaxAttempts
    {
        get => _maxAttempts;
        init
        {
            if (value is not null && value.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxAttempts), value, "must be greater than zero (or null for unlimited)");
            _maxAttempts = value;
        }
    }

    /// <summary>
    /// Validates cross-field invariants (e.g. <c>MaxDelay &gt;= InitialDelay</c>). Per-field
    /// invariants are enforced at init time; this method catches combinations that the init
    /// accessors cannot see because object-initializer ordering is undefined. Plugins SHALL
    /// call this before entering their retry loop.
    /// </summary>
    public void Validate()
    {
        if (_maxDelay < _initialDelay)
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), _maxDelay, "must be greater than or equal to InitialDelay");
    }
}
