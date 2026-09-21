namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Stable runtime identity for a message type, used as a key for internal dictionaries
/// (readiness, metric registration, etc.).
/// </summary>
/// <remarks>
/// Uses <see cref="Type.FullName"/> (falling back to <see cref="System.Reflection.MemberInfo.Name"/>)
/// so two types that share a short name in different namespaces do not collide. This matches the
/// persistence file naming, keeping internal identity behavior consistent. This is NOT the durable
/// logical message-type name — stable persisted schema identity remains a future versioning concern.
/// </remarks>
internal static class TypeIdentity
{
    public static string Key<T>() => Key(typeof(T));

    public static string Key(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.FullName ?? type.Name;
    }
}
