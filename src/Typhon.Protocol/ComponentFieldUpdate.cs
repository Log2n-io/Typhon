using MemoryPack;

namespace Typhon.Protocol;

/// <summary>
/// Per-component change data for a Modified entity. Carries a field dirty bitmask and the values of changed fields.
/// </summary>
/// <remarks>
/// <para>v1: <see cref="FieldDirtyBits"/> is always <c>~0UL</c> (all fields dirty) and <see cref="FieldValues"/> contains the full component bytes.
/// Per-field tracking will be added in v1.1 via output-phase diffing.</para>
/// <para>The wire format is forward-compatible: clients read <see cref="FieldDirtyBits"/> to determine which fields are present in <see cref="FieldValues"/>.
/// When all bits are set, <see cref="FieldValues"/> is the complete component struct.</para>
/// </remarks>
[MemoryPackable]
public partial struct ComponentFieldUpdate
{
    /// <summary>Component type identifier, assigned when the component type is registered.</summary>
    public ushort ComponentId;

    /// <summary>
    /// Bitmask of which fields changed (bit N = field N). Up to 64 fields per component.
    /// v1: always <c>~0UL</c> (all fields). v1.1: accurate per-field bits via output-phase diff.
    /// </summary>
    public ulong FieldDirtyBits;

    /// <summary>
    /// Raw bytes of changed field values, concatenated in field-index order.
    /// v1: full component struct bytes. v1.1: only bytes for fields with set bits in <see cref="FieldDirtyBits"/>.
    /// </summary>
    public byte[] FieldValues;
}
