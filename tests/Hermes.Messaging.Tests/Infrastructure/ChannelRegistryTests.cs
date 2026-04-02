using System.Threading.Channels;
using Hermes.Messaging.Infrastructure;

namespace Hermes.Messaging.Tests.Infrastructure;

public class ChannelRegistryTests
{
    [Fact]
    public void GetOrCreate_WithSameConfiguration_ReusesChannel()
    {
        var registry = new ChannelRegistry();

        var first = registry.GetOrCreate<SampleMessage>(options =>
        {
            options.Capacity = 128;
            options.FullMode = BoundedChannelFullMode.DropWrite;
        });

        var second = registry.GetOrCreate<SampleMessage>(options =>
        {
            options.Capacity = 128;
            options.FullMode = BoundedChannelFullMode.DropWrite;
        });

        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrCreate_WithNullConfiguration_ReusesExistingChannel()
    {
        var registry = new ChannelRegistry();

        var channel = registry.GetOrCreate<DefaultMessage>();
        var again = registry.GetOrCreate<DefaultMessage>();

        Assert.Same(channel, again);
    }

    [Fact]
    public void GetOrCreate_WithConflictingConfiguration_Throws()
    {
        var registry = new ChannelRegistry();

        _ = registry.GetOrCreate<ConflictingMessage>(options =>
        {
            options.Capacity = 64;
            options.FullMode = BoundedChannelFullMode.Wait;
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.GetOrCreate<ConflictingMessage>(options =>
            {
                options.Capacity = 32;
                options.FullMode = BoundedChannelFullMode.DropWrite;
            }));

        Assert.Contains("ConflictingMessage", exception.Message);
    }

    [Fact]
    public void Clear_RemovesAllChannels()
    {
        var registry = new ChannelRegistry();
        var first = registry.GetOrCreate<SampleMessage>();
        registry.Clear();
        var second = registry.GetOrCreate<SampleMessage>();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Constructor_WithCustomCapacity_UsesCapacity()
    {
        var registry = new ChannelRegistry(256);
        var channel = registry.GetOrCreate<DefaultMessage>();

        Assert.NotNull(channel);
    }

    private sealed record SampleMessage(string Value);
    private sealed record DefaultMessage(string Value);
    private sealed record ConflictingMessage(string Value);
}
