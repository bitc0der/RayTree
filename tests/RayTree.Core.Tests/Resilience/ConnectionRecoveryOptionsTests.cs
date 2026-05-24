using RayTree.Core.Resilience;

namespace RayTree.Core.Tests.Resilience;

[TestFixture]
public class ConnectionRecoveryOptionsTests
{
    [Test]
    public void Defaults_MatchSpec()
    {
        var options = new ConnectionRecoveryOptions();

        Assert.That(options.Enabled,        Is.True);
        Assert.That(options.InitialDelay,   Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(options.MaxDelay,       Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(options.Factor,         Is.EqualTo(2.0));
        Assert.That(options.JitterFraction, Is.EqualTo(0.2));
        Assert.That(options.MaxAttempts,    Is.Null);
    }

    [Test]
    public void Defaults_ValidateDoesNotThrow()
    {
        var options = new ConnectionRecoveryOptions();
        Assert.DoesNotThrow(() => options.Validate());
    }

    [Test]
    public void InitialDelay_Zero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ConnectionRecoveryOptions { InitialDelay = TimeSpan.Zero });
        Assert.That(ex!.ParamName, Is.EqualTo("InitialDelay"));
    }

    [Test]
    public void InitialDelay_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ConnectionRecoveryOptions { InitialDelay = TimeSpan.FromSeconds(-1) });
    }

    [Test]
    public void MaxDelay_Zero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ConnectionRecoveryOptions { MaxDelay = TimeSpan.Zero });
        Assert.That(ex!.ParamName, Is.EqualTo("MaxDelay"));
    }

    [Test]
    public void MaxDelay_LessThanInitialDelay_ValidateThrows()
    {
        var options = new ConnectionRecoveryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            MaxDelay     = TimeSpan.FromSeconds(5)
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.That(ex!.ParamName, Is.EqualTo("MaxDelay"));
    }

    [Test]
    public void Factor_LessThanOne_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ConnectionRecoveryOptions { Factor = 0.5 });
        Assert.That(ex!.ParamName, Is.EqualTo("Factor"));
    }

    [Test]
    public void Factor_Exactly1_Allowed()
    {
        Assert.DoesNotThrow(() => _ = new ConnectionRecoveryOptions { Factor = 1.0 });
    }

    [Test]
    public void JitterFraction_Negative_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ConnectionRecoveryOptions { JitterFraction = -0.1 });
        Assert.That(ex!.ParamName, Is.EqualTo("JitterFraction"));
    }

    [Test]
    public void JitterFraction_GreaterThanOne_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ConnectionRecoveryOptions { JitterFraction = 1.5 });
        Assert.That(ex!.ParamName, Is.EqualTo("JitterFraction"));
    }

    [Test]
    public void JitterFraction_Zero_Allowed()
    {
        Assert.DoesNotThrow(() => _ = new ConnectionRecoveryOptions { JitterFraction = 0.0 });
    }

    [Test]
    public void JitterFraction_One_Allowed()
    {
        Assert.DoesNotThrow(() => _ = new ConnectionRecoveryOptions { JitterFraction = 1.0 });
    }

    [Test]
    public void MaxAttempts_Zero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ConnectionRecoveryOptions { MaxAttempts = 0 });
        Assert.That(ex!.ParamName, Is.EqualTo("MaxAttempts"));
    }

    [Test]
    public void MaxAttempts_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ConnectionRecoveryOptions { MaxAttempts = -3 });
    }

    [Test]
    public void MaxAttempts_Null_Allowed()
    {
        Assert.DoesNotThrow(() => _ = new ConnectionRecoveryOptions { MaxAttempts = null });
    }

    [Test]
    public void MaxAttempts_Positive_Allowed()
    {
        var options = new ConnectionRecoveryOptions { MaxAttempts = 5 };
        Assert.That(options.MaxAttempts, Is.EqualTo(5));
    }
}
