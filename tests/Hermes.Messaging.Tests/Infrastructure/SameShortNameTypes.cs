// Two message types sharing the same short CLR name ("Order") in different namespaces, used to
// prove internal identity keys (TypeIdentity) do not collide (HERMES-006 P6).

namespace NamespaceA
{
    internal sealed record Order(string Value);
}

namespace NamespaceB
{
    internal sealed record Order(string Value);
}
