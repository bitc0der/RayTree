using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RayTree.Core.Resilience;
using RayTree.Hosting;

namespace RayTree.Core.Tests.Resilience;

/// <summary>
/// Verifies that <c>AddChangeTracking</c> binds <c>ChangeTracking:Publisher:ConnectionRecovery</c>
/// and <c>ChangeTracking:Subscriber:ConnectionRecovery</c> as NAMED <see cref="ConnectionRecoveryOptions"/>
/// retrievable via the documented <see cref="ChangeTrackingRecoveryKeys"/>.
/// </summary>
[TestFixture]
public class ConnectionRecoveryConfigurationTests
{
    [Test]
    public void AddChangeTracking_BindsPublisherRecoveryOptions_FromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChangeTracking:Publisher:ConnectionRecovery:MaxAttempts"]      = "10",
                ["ChangeTracking:Publisher:ConnectionRecovery:InitialDelay"]    = "00:00:00.500",
                ["ChangeTracking:Publisher:ConnectionRecovery:MaxDelay"]        = "00:01:00",
                ["ChangeTracking:Publisher:ConnectionRecovery:Factor"]          = "3.0",
                ["ChangeTracking:Publisher:ConnectionRecovery:JitterFraction"]  = "0.1",
                ["ChangeTracking:Publisher:ConnectionRecovery:Enabled"]         = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChangeTracking(config);

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<ConnectionRecoveryOptions>>();
        var publisher = monitor.Get(ChangeTrackingRecoveryKeys.Publisher);

        Assert.That(publisher.MaxAttempts,    Is.EqualTo(10));
        Assert.That(publisher.InitialDelay,   Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        Assert.That(publisher.MaxDelay,       Is.EqualTo(TimeSpan.FromMinutes(1)));
        Assert.That(publisher.Factor,         Is.EqualTo(3.0));
        Assert.That(publisher.JitterFraction, Is.EqualTo(0.1));
        Assert.That(publisher.Enabled,        Is.True);
    }

    [Test]
    public void AddChangeTracking_BindsSubscriberRecoveryOptions_Independently()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChangeTracking:Publisher:ConnectionRecovery:MaxAttempts"]  = "10",
                ["ChangeTracking:Subscriber:ConnectionRecovery:MaxAttempts"] = "5",
                ["ChangeTracking:Subscriber:ConnectionRecovery:Enabled"]     = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChangeTracking(config);

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<ConnectionRecoveryOptions>>();
        var pub = monitor.Get(ChangeTrackingRecoveryKeys.Publisher);
        var sub = monitor.Get(ChangeTrackingRecoveryKeys.Subscriber);

        Assert.That(pub.MaxAttempts, Is.EqualTo(10));
        Assert.That(sub.MaxAttempts, Is.EqualTo(5));
        Assert.That(sub.Enabled,     Is.False);
    }

    [Test]
    public void AddChangeTracking_WithoutConfiguration_DefaultsAreUsed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChangeTracking(configuration: null);

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<ConnectionRecoveryOptions>>();
        var publisher = monitor.Get(ChangeTrackingRecoveryKeys.Publisher);

        // No config bound — the named options resolve to the parameterless-constructor defaults.
        Assert.That(publisher.Enabled,        Is.True);
        Assert.That(publisher.InitialDelay,   Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(publisher.MaxDelay,       Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(publisher.Factor,         Is.EqualTo(2.0));
        Assert.That(publisher.JitterFraction, Is.EqualTo(0.2));
        Assert.That(publisher.MaxAttempts,    Is.Null);
    }
}
