namespace RayTree.Plugins.Kafka.Tests;

public class KafkaOptionsDefaultsTests
{
    [Test]
    public void KafkaPublisherOptions_TopicWaitDefaults_AreCorrect()
    {
        var options = new KafkaPublisherOptions();

        Assert.That(options.WaitForTopic,      Is.False);
        Assert.That(options.TopicWaitInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.TopicWaitTimeout,  Is.Null);
    }

    [Test]
    public void KafkaConsumerOptions_TopicWaitDefaults_AreCorrect()
    {
        var options = new KafkaConsumerOptions();

        Assert.That(options.WaitForTopic,      Is.False);
        Assert.That(options.TopicWaitInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.TopicWaitTimeout,  Is.Null);
    }
}
