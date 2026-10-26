namespace Tia.Core.Model;

internal static class EdgeKinds
{
    internal const EdgeKind Upward = EdgeKind.ImplementationToInterface | EdgeKind.OverrideToVirtual;

    internal const EdgeKind Downward = EdgeKind.InterfaceToImplementation | EdgeKind.VirtualToOverride;

    internal static bool IsOnly(this EdgeKind kind, EdgeKind mask) => (kind & mask) != 0 && (kind & ~mask) == 0;
}
