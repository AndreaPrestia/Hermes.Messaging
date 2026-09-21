using Hermes.Messaging.Infrastructure;

namespace Hermes.Messaging.Tests.Infrastructure;

public class HermesRuntimeStateTests
{
    [Fact]
    public void Default_Is_Created_NotReady()
    {
        var state = new HermesRuntimeState();
        Assert.Equal(RuntimeState.Created, state.Current);
        Assert.False(state.IsReady);
    }

    [Fact]
    public void TryTransition_AppliesOnlyFromExpected()
    {
        var state = new HermesRuntimeState();

        Assert.True(state.TryTransition(RuntimeState.Created, RuntimeState.Starting));
        Assert.Equal(RuntimeState.Starting, state.Current);

        // Wrong expected -> no-op.
        Assert.False(state.TryTransition(RuntimeState.Created, RuntimeState.Ready));
        Assert.Equal(RuntimeState.Starting, state.Current);
    }

    [Fact]
    public void EnsureReady_ThrowsWhenNotReady_AndPassesWhenReady()
    {
        var state = new HermesRuntimeState();

        var ex = Assert.Throws<HermesNotReadyException>(state.EnsureReady);
        Assert.Equal(RuntimeState.Created, ex.State);

        state.Set(RuntimeState.Ready);
        Assert.True(state.IsReady);
        state.EnsureReady(); // does not throw

        state.Set(RuntimeState.Stopping);
        Assert.Throws<HermesNotReadyException>(state.EnsureReady);
    }
}
