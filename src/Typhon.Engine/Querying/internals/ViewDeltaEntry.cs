using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ViewDeltaEntry  // 24 bytes exactly
{
    /// <summary>
    /// The entity this delta is about, as a FULL <see cref="EntityId"/> — never a bare 48-bit <c>EntityKey</c> and never a chunk id.
    /// </summary>
    /// <remarks>
    /// Typed rather than <c>long</c> deliberately (issue #660). The buffer previously carried three different id spaces in one
    /// <c>long</c> field — full <c>EntityId</c> from the Versioned commit path, bare <c>EntityKey</c> from the cluster spawn and both
    /// reconcile paths, and a raw chunk id from index-entry removal. Consumers all assume a full <see cref="EntityId"/>:
    /// <c>EcsView.ProcessEntry</c> mask-tests on <see cref="EntityId.ArchetypeId"/>, so a bare key yields a garbage routing id and the
    /// delta is dropped without a trace. Nothing in the type system objected, because every id space was the same primitive.
    /// </remarks>
    public EntityId EntityPK;    // 8B offset 0
    public KeyBytes8 BeforeKey;  // 8B offset 8
    public KeyBytes8 AfterKey;   // 8B offset 16
}