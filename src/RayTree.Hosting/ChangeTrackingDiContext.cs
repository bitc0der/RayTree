namespace RayTree.Hosting;

/// <summary>
/// DI-scoped context captured at <see cref="ServiceCollectionExtensions.AddChangeTracking"/>
/// registration time. Consumed by <see cref="ChangeTrackingHostedService"/> to emit the
/// one-shot "ChangeTracking starting" log with details from the registration call.
/// </summary>
public sealed record ChangeTrackingDiContext(bool ConfigurationBound);
