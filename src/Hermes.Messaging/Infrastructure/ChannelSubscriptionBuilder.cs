using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Fluent builder returned by <c>Subscribe&lt;T&gt;</c> that allows optional
/// dead-letter handler registration for the same message type.
/// The channel subscription is already registered when this builder is created,
/// so there is no terminal <c>Build()</c> call required.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public sealed class ChannelSubscriptionBuilder<T>
{
    private readonly IServiceCollection _services;

    internal ChannelSubscriptionBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Registers an <see cref="IDeadLetterHandler{T}"/> implementation that will be
    /// invoked by the <see cref="DeadLetterQueueProcessor"/> when a message of type
    /// <typeparamref name="T"/> lands in the dead-letter queue.
    /// </summary>
    /// <typeparam name="THandler">
    /// The concrete handler type. Resolved as <c>Scoped</c> from the DI container.
    /// </typeparam>
    public ChannelSubscriptionBuilder<T> WithDeadLetterHandler<THandler>()
        where THandler : class, IDeadLetterHandler<T>
    {
        _services.TryAddScoped<IDeadLetterHandler<T>, THandler>();
        return this;
    }
}
