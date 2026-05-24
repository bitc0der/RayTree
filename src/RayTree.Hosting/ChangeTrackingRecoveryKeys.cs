namespace RayTree.Hosting;

/// <summary>
/// Named-options keys for the bound <c>ConnectionRecoveryOptions</c> defaults registered by
/// <see cref="ServiceCollectionExtensions.AddChangeTracking"/>. Plugins that want to honor
/// the host's configured defaults resolve them via
/// <c>IOptionsMonitor&lt;ConnectionRecoveryOptions&gt;.Get(ChangeTrackingRecoveryKeys.Publisher)</c>
/// (or <see cref="Subscriber"/>) and merge into their own plugin options before constructing
/// the publisher/consumer instance.
/// </summary>
public static class ChangeTrackingRecoveryKeys
{
    /// <summary>
    /// Key for publisher-side recovery defaults (Postgres LISTEN reconnect, Kafka publisher
    /// fatal-error rebuild). Bound from <c>ChangeTracking:Publisher:ConnectionRecovery</c>.
    /// </summary>
    public const string Publisher = "RayTree.Publisher.ConnectionRecovery";

    /// <summary>
    /// Key for subscriber-side recovery defaults (Kafka consumer fatal-error rebuild).
    /// Bound from <c>ChangeTracking:Subscriber:ConnectionRecovery</c>.
    /// </summary>
    public const string Subscriber = "RayTree.Subscriber.ConnectionRecovery";
}
