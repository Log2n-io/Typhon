using MemoryPack;

namespace Typhon.Protocol;

/// <summary>
/// Full snapshot of a single component's data. Used in <see cref="EntityDelta"/> for Added entities.
/// </summary>
[MemoryPackable]
public partial struct ComponentSnapshot
{
    /// <summary>Component type identifier, assigned when the component type is registered.</summary>
    public ushort ComponentId;

    /// <summary>Raw component bytes (full struct data, <c>ComponentStorageSize</c> bytes).</summary>
    public byte[] Data;
}
