using RayTree.Plugins.Kafka;

namespace RayTree.Plugins.Kafka.Tests;

[TestFixture]
public class KafkaConnectionRecoveryOptionsTests
{
    [Test]
    public void Defaults_MatchSpec()
    {
        var options = new KafkaConnectionRecoveryOptions();

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
        var options = new KafkaConnectionRecoveryOptions();
        Assert.DoesNotThrow(() => options.Validate());
    }

    [Test]
    public void InitialDelay_Zero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new KafkaConnectionRecoveryOptions { InitialDelay = TimeSpan.Zero });
        Assert.That(ex!.ParamName, Is.EqualTo("InitialDelay"));
    }

    [Test]
    public void InitialDelay_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new KafkaConnectionRecoveryOptions { InitialDelay = TimeSpan.FromSeconds(-1) });
    }

    [Test]
    public void MaxDelay_Zero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new KafkaConnectionRecoveryOptions { MaxDelay = TimeSpan.Zero });
        Assert.That(ex!.ParamName, Is.EqualTo("MaxDelay"));
    }

    [Test]
    public void MaxDelay_LessThanInitialDelay_ValidateThrows()
    {
        var options = new KafkaConnectionRecoveryOptions
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
            () => _ = new KafkaConnectionRecoveryOptions { Factor = 0.5 });
        Assert.That(ex!.ParamName, Is.EqualTo("Factor"));
    }

    [Test]
    public void Factor_Exactly1_Allowed()
    {
        Assert.DoesNotThrow(() => _ = new KafkaConnectionRecoveryOptions { Factor = 1.0 });
    }

    [Test]
    public void JitterFraction_Negative_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new KafkaConnectionRecoveryOptions { JitterFraction = -0.1 });
        Assert.That(ex!.ParamName, Is.EqualTo("JitterFraction"));
    }

    [Test]
    public void JitterFraction_GreaterThanOne_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new KafkaConnectionRecoveryOptions { JitterFraction = 1.5 });
        Assert.That(ex!.ParamName, Is.EqualTo("JitterFraction"));
    }

    [Test]
    public void JitterFraction_Zero_Allowed()
    {
        Assert.DoesNotThrow(() => _ = new KafkaConnectionRecoveryOptions { JitterFraction = 0.0 });
    }

    [Test]
    public void JitterFraction_One_Allowed()
    {
        Assert.DoesNotThrow(() => _ = new KafkaConnectionRecoveryOptions { JitterFraction = 1.0 });
    }

    [Test]
    public void MaxAttempts_Zero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new KafkaConnectionRecoveryOptions { MaxAttempts = 0 });
        Assert.That(ex!.ParamName, Is.EqualTo("MaxAttempts"));
    }

    [Test]
    public void MaxAttempts_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new KafkaConnectionRecoveryOptions { MaxAttempts = -3 });
    }

    [Test]
    public void MaxAttempts_Null_Allowed()
    {
        Assert.DoesNotThrow(() => _ = new KafkaConnectionRecoveryOptions { MaxAttempts = null });
    }

    [Test]
    public void MaxAttempts_Positive_Allowed()
    {
        var options = new KafkaConnectionRecoveryOptions { MaxAttempts = 5 };
        Assert.That(options.MaxAttempts, Is.EqualTo(5));
    }
}
