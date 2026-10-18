using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// One entity's visibility tag mask before and after an event (14 § 6, P20): what a session's visibility test reads on each side of the frame.
/// </summary>
internal struct TagChange
{
    public uint NetId;
    public uint Old;
    public uint New;
}

// Visibility tags (14 § 6), the shape-independent half: the tick's mask changes, each session's own mask, and the test.
internal abstract unsafe partial class PushReplication
{
    // Per worker, this tick's mask changes: appended by the projecting worker, merged at the index's finish. The fence's orphans have their own list, under
    // the orphan lock.
    private protected TagChange[][] _tagChanges = [];
    private protected int[] _tagChangeCount = [];
    private const int TagCountStride = 16;
    private protected TagChange[] _orphanTagChanges = new TagChange[16];
    private protected int _orphanTagChangeCount;

    // Per archetype: where its cold entry keeps the mask, 0 for an archetype that declares none — whose every visibility test is this one load (the mask is
    // never at an entry's offset 0, which the fixed header holds). And where the mask of slot 0 is from a block's start, with the cold stride: a mask read
    // from a block is two loads and a multiply-add.
    private protected int[] _tagOffset = [];
    private int[] _tagColdBase = [];
    private int[] _tagColdStride = [];

    // Per realm-local session slot: the session's mask, and the generation it belongs to.
    private protected uint[] _sessionTags = new uint[InitialSessionSlots];
    private protected ushort[] _sessionTagsGeneration = new ushort[InitialSessionSlots];

    // And the mask its last published frame was built against — what its client holds by: a command's reference is judged by it (SUB-26), not by a mask
    // whose RESET is still unpublished.
    private protected uint[] _committedTags = new uint[InitialSessionSlots];
    private protected ushort[] _committedTagsGeneration = new ushort[InitialSessionSlots];

    /// <summary>Whether any archetype here declares a visibility mask: false keeps every tag path a single test.</summary>
    internal bool HasTags { get; private set; }

    /// <summary>Mask changes this replication recorded, summed over ticks. Diagnostic.</summary>
    internal long TagChangesRecorded;

    /// <summary>Session mask changes that reset a session's view (P20). Diagnostic.</summary>
    internal long VisibilityResets;

    /// <summary>Resolves where each archetype keeps its mask. Once, at construction.</summary>
    private protected void InitTags(CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states)
    {
        _tagOffset = new int[plans.Length];
        _tagColdBase = new int[plans.Length];
        _tagColdStride = new int[plans.Length];
        for (var a = 0; a < plans.Length; a++)
        {
            if (!plans[a].HasTagMask || states[a] == null)
            {
                continue;
            }

            var layout = states[a].Layout;
            _tagOffset[a] = layout.TagMaskOffsetInColdEntry;
            _tagColdBase[a] = layout.ColdOffset + layout.TagMaskOffsetInColdEntry;
            _tagColdStride[a] = layout.ColdStride;
            HasTags = true;
        }
    }

    /// <summary>Sizes and empties the per-worker mask-change lists for this tick's projection.</summary>
    private protected void ResetTagChanges(int workers)
    {
        if (!HasTags)
        {
            return;
        }

        if (_tagChanges.Length < workers)
        {
            Array.Resize(ref _tagChanges, workers);
            Array.Resize(ref _tagChangeCount, workers * TagCountStride);
        }

        for (var w = 0; w < _tagChanges.Length; w++)
        {
            _tagChanges[w] ??= new TagChange[64];
            _tagChangeCount[w * TagCountStride] = 0;
        }
    }

    /// <summary>
    /// Records an entity's mask change — a new mask, a new entity's first non-zero one, or a hidden entity's leave (its mask to 0). Called by the worker
    /// projecting it, so the list is the worker's own.
    /// </summary>
    internal void NoteTagChange(int worker, int archetype, uint netId, uint oldMask, uint newMask)
    {
        if (_unplaced[archetype])
        {
            Hub.NoteUnplacedTagChange(worker, netId, oldMask, newMask);
            return;
        }

        var list = _tagChanges[worker];
        var n = _tagChangeCount[worker * TagCountStride];
        if (n == list.Length)
        {
            Array.Resize(ref _tagChanges[worker], n * 2);
            list = _tagChanges[worker];
        }

        list[n] = new TagChange { NetId = netId, Old = oldMask, New = newMask };
        _tagChangeCount[worker * TagCountStride] = n + 1;
    }

    /// <summary>
    /// An orphaned hidden entity leaves with its mask, read from the cold entry before the entry is lost: true when it had one. Under the orphan lock.
    /// </summary>
    private protected bool NoteOrphanTags(int archetype, byte* cold, uint netId)
    {
        var offset = _tagOffset[archetype];
        var old = offset != 0 ? *(uint*)(cold + offset) : 0u;
        if (old == 0)
        {
            return false;
        }

        if (_orphanTagChangeCount == _orphanTagChanges.Length)
        {
            Array.Resize(ref _orphanTagChanges, _orphanTagChangeCount * 2);
        }

        _orphanTagChanges[_orphanTagChangeCount++] = new TagChange { NetId = netId, Old = old, New = 0 };
        return true;
    }

    /// <summary>
    /// Merges the tick's mask changes into <paramref name="into"/>, sorted by netId; an identity that changed twice — a leave recorded by the projection and
    /// an orphan by the fence — keeps its first old mask and its last new one. Returns the count.
    /// </summary>
    private protected int MergeTagChanges(ref TagChange[] into)
    {
        var total = _orphanTagChangeCount;
        for (var w = 0; w < _tagChanges.Length; w++)
        {
            total += _tagChangeCount[w * TagCountStride];
        }

        if (into.Length < total)
        {
            into = new TagChange[Math.Max(total, into.Length * 2)];
        }

        var n = 0;
        // The fence's first: recorded before this tick's projection began.
        for (var i = 0; i < _orphanTagChangeCount; i++)
        {
            into[n++] = _orphanTagChanges[i];
        }

        _orphanTagChangeCount = 0;
        for (var w = 0; w < _tagChanges.Length; w++)
        {
            var list = _tagChanges[w];
            for (var i = 0; i < _tagChangeCount[w * TagCountStride]; i++)
            {
                into[n++] = list[i];
            }
        }

        n = FoldTagChanges(into, n);
        TagChangesRecorded += n;
        return n;
    }

    // The fold's order and sorted copy, reused: a tick allocates nothing once they are grown.
    [ThreadStatic]
    private static TagChange[] _foldScratch;

    [ThreadStatic]
    private static ulong[] _foldOrder;

    /// <summary>
    /// Sorts <paramref name="n"/> mask changes by netId and folds each identity's into one — its first old mask and its last new one, in the order given.
    /// Returns the count left.
    /// </summary>
    internal static int FoldTagChanges(TagChange[] into, int n)
    {
        if (n <= 1)
        {
            return n;
        }

        // Insertion order is the tie-break (orphans before the projection's): a stable sort on (netId, position).
        if (_foldOrder == null || _foldOrder.Length < n)
        {
            _foldOrder = new ulong[Math.Max(64, n * 2)];
        }

        var order = _foldOrder.AsSpan(0, n);
        for (var i = 0; i < n; i++)
        {
            order[i] = ((ulong)into[i].NetId << 32) | (uint)i;
        }

        order.Sort();
        if (_foldScratch == null || _foldScratch.Length < n)
        {
            _foldScratch = new TagChange[Math.Max(64, n * 2)];
        }

        var sorted = _foldScratch;
        for (var i = 0; i < n; i++)
        {
            sorted[i] = into[(int)(order[i] & 0xFFFFFFFFUL)];
        }

        var kept = 0;
        for (var i = 0; i < n; i++)
        {
            if (kept > 0 && sorted[i].NetId == into[kept - 1].NetId)
            {
                into[kept - 1].New = sorted[i].New;
                continue;
            }

            into[kept++] = sorted[i];
        }

        return kept;
    }

    /// <summary>One tick's mask change of <paramref name="netId"/> in a merged list, by binary search; false when it has none.</summary>
    internal static bool FindTagChange(TagChange[] changes, int count, uint netId, out uint oldMask, out uint newMask)
    {
        var lo = 0;
        var hi = count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var id = changes[mid].NetId;
            if (id == netId)
            {
                oldMask = changes[mid].Old;
                newMask = changes[mid].New;
                return true;
            }

            if (id < netId)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        oldMask = newMask = 0;
        return false;
    }

    /// <summary>
    /// The session's visibility tag mask for this frame (P18). A change of it — not the session's first — resets its view (P20): the gather that follows
    /// delivers everything under the new mask, and nothing is decided against the old one. Called by the frame stage before the session's gather.
    /// </summary>
    internal void NoteVisibility(SessionId session, uint mask)
    {
        if (!HasTags)
        {
            return;
        }

        var local = L(session);
        var known = _sessionTagsGeneration[local] == session.Generation;
        if (known && _sessionTags[local] == mask)
        {
            return;
        }

        _sessionTags[local] = mask;
        _sessionTagsGeneration[local] = session.Generation;
        ref var st = ref _sessions[local];
        if (known && st.Bound && st.Generation == session.Generation && st.Anchored)
        {
            st.NeedsReset = true;
            System.Threading.Interlocked.Increment(ref VisibilityResets);
        }
    }

    /// <summary>Whether the session in <paramref name="local"/> sees an entity with <paramref name="mask"/> (P19): it is public, or they share a bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected bool Sees(int local, uint mask) => mask == 0 || (mask & _sessionTags[local]) != 0;

    /// <summary>An entity's current mask, from its cold entry; 0 for an archetype that declares none, or no block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected uint MaskAt(int archetype, nint block, int slot)
    {
        if (_tagOffset[archetype] == 0 || block == 0)
        {
            return 0;
        }

        return *(uint*)((byte*)block + _tagColdBase[archetype] + (slot * _tagColdStride[archetype]));
    }

    /// <summary>
    /// Whether the session sees the entity at (<paramref name="archetype"/>, <paramref name="block"/>, <paramref name="slot"/>) by its current mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected bool SeesNow(int local, int archetype, nint block, int slot) => _tagOffset[archetype] == 0
        || Sees(local, MaskAt(archetype, block, slot));

    /// <summary>Whether <paramref name="session"/> sees an entity with <paramref name="mask"/>: the event routes' and the references' test (P19).</summary>
    internal bool SessionSees(SessionId session, uint mask)
    {
        if (mask == 0)
        {
            return true;
        }

        var local = L(session);
        return _sessionTagsGeneration[local] == session.Generation && (mask & _sessionTags[local]) != 0;
    }

    /// <summary>
    /// Whether <paramref name="session"/>'s last published frame saw an entity with <paramref name="mask"/>: a command reference's test (SUB-26).
    /// </summary>
    internal bool CommittedSees(SessionId session, uint mask)
    {
        if (mask == 0)
        {
            return true;
        }

        var local = L(session);
        return _committedTagsGeneration[local] == session.Generation && (mask & _committedTags[local]) != 0;
    }

    /// <summary>The frame being published was built against the session's current mask: it is now the committed one.</summary>
    private protected void CommitTags(int local)
    {
        if (HasTags)
        {
            _committedTags[local] = _sessionTags[local];
            _committedTagsGeneration[local] = _sessionTagsGeneration[local];
        }
    }

    /// <summary>An entity's current mask by its block and slot, for the frame stage's event routes and references (P19).</summary>
    internal uint MaskOf(int archetype, nint block, int slot) => (uint)archetype < (uint)_tagOffset.Length ? MaskAt(archetype, block, slot) : 0u;
}
