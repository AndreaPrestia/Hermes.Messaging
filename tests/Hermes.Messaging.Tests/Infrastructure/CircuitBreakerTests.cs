using Hermes.Messaging.Infrastructure;

namespace Hermes.Messaging.Tests.Infrastructure;

public class CircuitBreakerTests
{
    [Fact]
    public void IsOpen_InitialState_ReturnsFalse()
    {
        var circuitBreaker = new CircuitBreaker();

        var isOpen = circuitBreaker.IsOpen("test-route");

        Assert.False(isOpen);
    }

    [Fact]
    public void GetState_InitialState_ReturnsClosed()
    {
        var circuitBreaker = new CircuitBreaker();

        var state = circuitBreaker.GetState("test-route");

        Assert.Equal(CircuitStateEnum.Closed, state);
    }

    [Fact]
    public void RecordFailure_BelowThreshold_RemainsClosedAndNotOpen()
    {
        var circuitBreaker = new CircuitBreaker(failureThreshold: 5);

        for (int i = 0; i < 4; i++)
        {
            circuitBreaker.RecordFailure("test-route");
        }

        Assert.False(circuitBreaker.IsOpen("test-route"));
        Assert.Equal(CircuitStateEnum.Closed, circuitBreaker.GetState("test-route"));
    }

    [Fact]
    public void RecordFailure_ReachesThreshold_OpensCircuit()
    {
        var circuitBreaker = new CircuitBreaker(failureThreshold: 5);

        for (int i = 0; i < 5; i++)
        {
            circuitBreaker.RecordFailure("test-route");
        }

        Assert.True(circuitBreaker.IsOpen("test-route"));
        Assert.Equal(CircuitStateEnum.Open, circuitBreaker.GetState("test-route"));
    }

    [Fact]
    public void RecordSuccess_InClosedState_ResetsFailureCount()
    {
        var circuitBreaker = new CircuitBreaker(failureThreshold: 5);

        circuitBreaker.RecordFailure("test-route");
        circuitBreaker.RecordFailure("test-route");
        circuitBreaker.RecordSuccess("test-route");

        // Should still be closed and need 5 more failures to open
        for (int i = 0; i < 4; i++)
        {
            circuitBreaker.RecordFailure("test-route");
        }

        Assert.False(circuitBreaker.IsOpen("test-route"));
    }

    [Fact]
    public async Task IsOpen_AfterOpenDuration_TransitionsToHalfOpen()
    {
        var circuitBreaker = new CircuitBreaker(
            failureThreshold: 3,
            openDuration: TimeSpan.FromMilliseconds(100));

        // Open the circuit
        for (int i = 0; i < 3; i++)
        {
            circuitBreaker.RecordFailure("test-route");
        }

        Assert.True(circuitBreaker.IsOpen("test-route"));
        Assert.Equal(CircuitStateEnum.Open, circuitBreaker.GetState("test-route"));

        // Wait for open duration to expire
        await Task.Delay(150);

        var state = circuitBreaker.GetState("test-route");
        Assert.Equal(CircuitStateEnum.HalfOpen, state);
        Assert.False(circuitBreaker.IsOpen("test-route")); // Half-open allows test requests
    }

    [Fact]
    public async Task RecordSuccess_InHalfOpenState_ClosesCircuit()
    {
        var circuitBreaker = new CircuitBreaker(
            failureThreshold: 3,
            openDuration: TimeSpan.FromMilliseconds(100));

        // Open the circuit
        for (int i = 0; i < 3; i++)
        {
            circuitBreaker.RecordFailure("test-route");
        }

        // Wait for half-open
        await Task.Delay(150);
        var stateBeforeSuccess = circuitBreaker.GetState("test-route");
        Assert.Equal(CircuitStateEnum.HalfOpen, stateBeforeSuccess);

        // Success in half-open should close
        circuitBreaker.RecordSuccess("test-route");

        // Need to check state again after RecordSuccess, which updates it
        Assert.False(circuitBreaker.IsOpen("test-route"));
        var stateAfterSuccess = circuitBreaker.GetState("test-route");
        Assert.Equal(CircuitStateEnum.Closed, stateAfterSuccess);
    }

    [Fact]
    public async Task RecordFailure_InHalfOpenState_ReopensCircuit()
    {
        var circuitBreaker = new CircuitBreaker(
            failureThreshold: 3,
            openDuration: TimeSpan.FromMilliseconds(100));

        // Open the circuit
        for (int i = 0; i < 3; i++)
        {
            circuitBreaker.RecordFailure("test-route");
        }

        // Wait for half-open
        await Task.Delay(150);
        var stateBeforeFailure = circuitBreaker.GetState("test-route");
        Assert.Equal(CircuitStateEnum.HalfOpen, stateBeforeFailure);

        // Failure in half-open should reopen
        circuitBreaker.RecordFailure("test-route");

        // Circuit should be open again
        Assert.True(circuitBreaker.IsOpen("test-route"));
        var stateAfterFailure = circuitBreaker.GetState("test-route");
        Assert.Equal(CircuitStateEnum.Open, stateAfterFailure);
    }

    [Fact]
    public void IsOpen_DifferentRoutes_IndependentCircuits()
    {
        var circuitBreaker = new CircuitBreaker(failureThreshold: 3);

        // Open circuit for route-1
        for (int i = 0; i < 3; i++)
        {
            circuitBreaker.RecordFailure("route-1");
        }

        // route-2 should still be closed
        Assert.True(circuitBreaker.IsOpen("route-1"));
        Assert.False(circuitBreaker.IsOpen("route-2"));
    }

    [Fact]
    public void RecordFailure_ExceedsThresholdMultipleTimes_RemainsOpen()
    {
        var circuitBreaker = new CircuitBreaker(failureThreshold: 5);

        for (int i = 0; i < 10; i++)
        {
            circuitBreaker.RecordFailure("test-route");
        }

        Assert.True(circuitBreaker.IsOpen("test-route"));
        Assert.Equal(CircuitStateEnum.Open, circuitBreaker.GetState("test-route"));
    }
}
