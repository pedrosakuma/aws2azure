using Aws2Azure.Modules.Sqs.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class QueueNameTests
{
    [Theory]
    [InlineData("my-queue")]
    [InlineData("My_Queue123")]
    [InlineData("a")]
    [InlineData("queue.fifo")]
    [InlineData("ABC-_123.fifo")]
    public void Valid_names_accepted(string name) => Assert.True(QueueName.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("has.dot")] // dots are only allowed as the .fifo suffix
    [InlineData(".fifo")]   // empty prefix
    [InlineData("name+plus")]
    public void Invalid_names_rejected(string? name) => Assert.False(QueueName.IsValid(name));

    [Fact]
    public void Length_cap_at_80_chars_enforced()
    {
        var ok = new string('a', 80);
        var tooLong = new string('a', 81);
        Assert.True(QueueName.IsValid(ok));
        Assert.False(QueueName.IsValid(tooLong));
    }

    [Fact]
    public void Fifo_suffix_counts_toward_limit()
    {
        // 75 chars + 5 chars (".fifo") = 80 total → still allowed
        var fifoOk = new string('a', 75) + ".fifo";
        var fifoOver = new string('a', 76) + ".fifo";
        Assert.True(QueueName.IsValid(fifoOk));
        Assert.False(QueueName.IsValid(fifoOver));
    }

    [Theory]
    [InlineData("queue.fifo", true)]
    [InlineData("queue", false)]
    [InlineData("queue.FIFO", false)] // case-sensitive per AWS docs
    public void IsFifo_matches_lowercase_suffix(string name, bool expected) =>
        Assert.Equal(expected, QueueName.IsFifo(name));
}
