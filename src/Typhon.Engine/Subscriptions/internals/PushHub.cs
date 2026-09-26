using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The engine-wide half of push replication (Realms R4.1): the collector that turns the fence's structure words into this tick's push set, gives each
/// pushed cluster a block and marks it, and routes each block to the <see cref="PushReplication"/> of the realm its cluster is in.
/// </summary>
/// <remarks>
/// <para>
/// <b>One scan per archetype, never per realm.</b> The structure words, the repush lists and the validator are per archetype and chunk-indexed; the realm of a
/// chunk is one load from <c>ClusterRealmMap</c>. A realm with no replication here is not served: its clusters are never pushed, so its entities are never
/// projected, identified or sent (SUB-28 from the side that holds today).
/// </para>
/// <para>
/// <b>What stays per realm</b> is everything a realm's sessions read: its grid and codecs, its sessions, its index and log, its occupancy and counts, and
/// its own gap detection and bootstrap (<see cref="PushReplication.BeginRealmTick"/>), so a realm activated late re-pushes its own entities only.
/// </para>
/// </remarks>
internal sealed unsafe class PushHub
{
    private readonly ArchetypeReplicationState[] _states;
    private readonly int[] _pushIndices;

    // Per plan index: the engine, not the application, detects this archetype's changes. Every live entity is pushed every tick and the byte compare
    // keeps only those that changed.
    private readonly bool[] _automatic;

    // By realm id: the realm's replication, or null where the realm is not served. Grown when a higher realm is served.
    private PushReplication[] _byRealm;
    private PushReplication[] _active = [];
    private int _activeCount;

    // Per archetype, this tick's push set as (chunk, mask) pairs, and the blocks looked up for it once.
    private readonly int[][] _pushChunks;
    private readonly ulong[][] _pushMasks;
    private readonly int[] _pushCount;
    private readonly nint[][] _pushBlocks;

    // Per archetype, by chunk id: slots to push again next tick — still extrapolating, or denied an identity.
    private readonly long[][] _repush;

    // The realm map the collector routes the archetype it is collecting by, or null when the engine has one realm.
    private ushort[] _realmMap;

    // The pass the collector is in: every live entity (for the realms that need it this tick), or the structure words and repushes (for the others).
    private bool _everythingPass;

    // One served realm (0) and no realm map for the archetype being collected: every chunk is realm 0's, and routing is skipped.
    private bool _single;

    // ── The forgotten-push validator (explicit detection) ──
    //
    // A few clusters of each explicit archetype are projected whole every tick, round-robin. A slot among them that the application did not push and
    // that still produces an event changed without a push: counted by group, and sent anyway, so the validator heals what it finds.

    /// <summary>How many clusters per explicit archetype the validator projects whole each tick; zero turns it off.</summary>
    public int ValidateClustersPerTick = int.TryParse(Environment.GetEnvironmentVariable("TYPHON_PUSH_VALIDATE"), out var v) ? v : 0;

    private readonly int[] _validateCursor;
    private readonly Dictionary<int, ulong>[] _validating;

    /// <summary>Slots the validator found changed without a push, and of those, how many had moved (a segment) — cumulative.</summary>
    public long ForgottenPushes;
    public long ForgottenMotion;
    public long ValidatedSlots;
    private readonly long[][] _forgottenGroups;

    /// <summary>Slots marked pushed, cumulative.</summary>
    public long SlotsPushed;

    /// <summary>The blocks step's collector, cumulative, in Stopwatch ticks.</summary>
    public long PrepareTicks;

    /// <summary>Builds the hub over one served realm's replication.</summary>
    /// <param name="states">Every plan's replication state, by plan index.</param>
    /// <param name="isPush">Per plan index: some profile observes the archetype.</param>
    /// <param name="automatic">Per plan index: the engine detects its changes.</param>
    /// <param name="realm0">Realm 0's replication, served from the start.</param>
    /// <param name="maxSessions">The session table's width: the link state and the placement map are per session slot.</param>
    public PushHub(ArchetypeReplicationState[] states, bool[] isPush, bool[] automatic, PushReplication realm0, int maxSessions)
    {
        ArgumentNullException.ThrowIfNull(realm0);
        _placement = new ulong[Math.Max(1, maxSessions)];
        Links = new PushLinkState[_placement.Length];
        _states = states;
        _automatic = automatic;
        var count = 0;
        for (var a = 0; a < isPush.Length; a++)
        {
            count += isPush[a] ? 1 : 0;
        }

        _pushIndices = new int[count];
        count = 0;
        for (var a = 0; a < isPush.Length; a++)
        {
            if (isPush[a])
            {
                _pushIndices[count++] = a;
            }
        }

        var n = states.Length;
        _pushChunks = new int[n][];
        _pushMasks = new ulong[n][];
        _pushCount = new int[n];
        _pushBlocks = new nint[n][];
        _repush = new long[n][];
        _validateCursor = new int[n];
        _validating = new Dictionary<int, ulong>[n];
        _forgottenGroups = new long[n][];
        for (var a = 0; a < n; a++)
        {
            _validating[a] = [];
            _forgottenGroups[a] = new long[8];
        }

        foreach (var a in _pushIndices)
        {
            _pushChunks[a] = new int[64];
            _pushBlocks[a] = new nint[64];
            _pushMasks[a] = new ulong[64];
            _repush[a] = [];
        }

        _byRealm = new PushReplication[1];
        Serve(RealmId.Default.Value, realm0);
    }

    /// <summary>Realm 0's replication: the one every session is in until sessions are placed in realms (R4.3).</summary>
    public PushReplication Realm0 => _byRealm[0];

    /// <summary>The replications of the realms served now.</summary>
    public ReadOnlySpan<PushReplication> Active => _active.AsSpan(0, _activeCount);

    /// <summary>The replication of <paramref name="realm"/>, or <see langword="null"/> when it is not served.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PushReplication For(ushort realm) => realm < _byRealm.Length ? _byRealm[realm] : null;

    /// <summary>Starts serving <paramref name="realm"/> with <paramref name="replication"/>. Serial, between ticks.</summary>
    internal void Serve(ushort realm, PushReplication replication)
    {
        ArgumentNullException.ThrowIfNull(replication);
        if (realm >= _byRealm.Length)
        {
            Array.Resize(ref _byRealm, Math.Max(realm + 1, _byRealm.Length * 2));
        }

        if (_byRealm[realm] != null)
        {
            throw new InvalidOperationException($"realm {realm} is already served");
        }

        _byRealm[realm] = replication;
        replication.Hub = this;
        if (_activeCount == _active.Length)
        {
            Array.Resize(ref _active, Math.Max(4, _active.Length * 2));
        }

        _active[_activeCount++] = replication;
    }

    // ══ Sessions (R4.3): the realm-local slot each session holds, and the link state that crosses realms ════════════════════════════════════════════

    // Per session slot: (realm-local slot << 32) | (realm << 16) | generation of the session placed there; 0 when it holds no realm's slot.
    private readonly ulong[] _placement;

    /// <summary>Per session slot: the budget loop's state — the link's, not a realm's, so a realm switch keeps a congested session's level (09 § 10).</summary>
    internal readonly PushLinkState[] Links;

    // Realm-local slots nobody was placed in are given back every this many ticks: a session that closed or lost its profile leaves its slot until then.
    private const uint SweepEvery = 64;

    /// <summary>The realm-local slot <paramref name="session"/> holds in <paramref name="realm"/>'s replication, or 0 when it holds none there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int LocalSlot(SessionId session, ushort realm)
    {
        var slot = (uint)session.Slot;
        if (slot >= (uint)_placement.Length)
        {
            return 0;
        }

        var p = _placement[slot];
        return (ushort)p == session.Generation && (ushort)(p >> 16) == realm ? (int)(p >> 32) : 0;
    }

    /// <summary>
    /// Places <paramref name="session"/> in <paramref name="realm"/> for this tick: a realm-local slot of that realm's replication, taken on the first tick
    /// and kept after (leaving any other realm's), and the session's link state, kept across realms. Returns the replication, or <see langword="null"/>
    /// when the realm is not served — the session then holds no realm's slot. Serial (the frame prologue).
    /// </summary>
    internal PushReplication Place(SessionId session, ushort realm, uint tick)
    {
        var slot = session.Slot;
        ref var link = ref Links[slot];
        if (link.Generation != session.Generation)
        {
            link = default;
            link.Generation = session.Generation;
        }

        var replication = For(realm);
        var p = _placement[slot];
        var local = (int)(p >> 32);
        if (local != 0 && (ushort)p == session.Generation && (ushort)(p >> 16) == realm)
        {
            replication.Touch(local, tick);
            return replication;
        }

        Leave(slot);
        if (replication == null)
        {
            return null;
        }

        local = replication.Join(session);
        replication.Touch(local, tick);
        _placement[slot] = ((ulong)(uint)local << 32) | ((ulong)realm << 16) | session.Generation;
        return replication;
    }

    /// <summary>Gives back the realm-local slot session slot <paramref name="slot"/> holds, whichever realm it is in. Serial (the frame prologue).</summary>
    internal void Leave(int slot)
    {
        if ((uint)slot >= (uint)_placement.Length)
        {
            return;
        }

        var p = _placement[slot];
        var local = (int)(p >> 32);
        _placement[slot] = 0;
        if (local != 0)
        {
            For((ushort)(p >> 16))?.Release(local);
        }
    }

    /// <summary>A replication gave back <paramref name="session"/>'s slot in <paramref name="realm"/>: the placement is dropped if it still names it.</summary>
    internal void Forget(SessionId session, ushort realm)
    {
        var p = _placement[session.Slot];
        if ((ushort)p == session.Generation && (ushort)(p >> 16) == realm)
        {
            _placement[session.Slot] = 0;
        }
    }

    /// <summary>Every <see cref="SweepEvery"/> ticks, after the tick's placements: slots no session was placed in go back. Serial (the frame prologue).</summary>
    internal void SweepUnplaced(uint tick)
    {
        if (tick % SweepEvery != 0)
        {
            return;
        }

        var active = Active;
        for (var r = 0; r < active.Length; r++)
        {
            active[r].ReleaseUnseen(tick, this);
        }
    }

    /// <summary>Per plan index and change group, how many forgotten pushes changed it.</summary>
    public long ForgottenGroups(int archetype, int group) =>
        (uint)archetype < (uint)_forgottenGroups.Length && _forgottenGroups[archetype] != null && (uint)group < (uint)_forgottenGroups[archetype].Length
            ? Interlocked.Read(ref _forgottenGroups[archetype][group]) : 0;

    // ══ Blocks step (serial) ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Before the parked drain: drops last tick's push marks, collects this tick's push set, and gives every cluster in it a block — so an entity that
    /// migrated into a cluster with no block is drained into one this tick rather than dropped.
    /// </summary>
    public void PrepareBlocks(uint tick)
    {
        var from = Stopwatch.GetTimestamp();
        var active = Active;
        for (var r = 0; r < active.Length; r++)
        {
            active[r].BeginRealmTick(tick);
        }

        foreach (var a in _pushIndices)
        {
            var state = _states[a];
            var list = state.WatchedBlocks;
            for (var i = 0; i < list.Count; i++)
            {
                list[i]->WatchedMask = 0;
            }

            _pushCount[a] = 0;
            var cs = state.ClusterState;
            if (cs == null)
            {
                continue;
            }

            var capacity = cs.ClusterAabbs?.Length ?? 0;
            if (_repush[a].Length < capacity)
            {
                Array.Resize(ref _repush[a], capacity);
            }

            // On an engine with more than one realm, a chunk's realm routes it: one load. One realm: no map, and every chunk is realm 0's.
            _realmMap = (cs.RealmTableOrNull?.RegisteredCount ?? 1) > 1 ? Volatile.Read(ref cs.ClusterRealmMap) : null;
            _single = _realmMap == null && active.Length == 1 && ReferenceEquals(active[0], For(RealmId.Default.Value));
            var slotMask = state.Layout.SlotCount >= 64 ? ulong.MaxValue : (1UL << state.Layout.SlotCount) - 1;
            var structureAll = Volatile.Read(ref cs.StructureTick) == tick && cs.StructureCoversAll;

            // Per served realm: first tick, a tick after its own gap, or a tick the fence could not describe — every live entity of it is pushed, which is
            // what gives every entity an identity and an encoded state before any session asks. Every slot of an active cluster is visited, so a slot
            // emptied during a gap gives its identity back too.
            var anyEverything = false;
            var anyPartial = false;
            for (var r = 0; r < active.Length; r++)
            {
                var everything = _automatic[a] || !active[r].Bootstrapped[a] || active[r].ResumedThisTick || structureAll;
                active[r].EverythingThisTick[a] = everything;
                anyEverything |= everything;
                anyPartial |= !everything;
            }

            if (anyEverything)
            {
                _everythingPass = true;
                var ids = cs.ReadActiveClusterList(out var clusters);
                for (var i = 0; ids != null && i < clusters; i++)
                {
                    AddPush(a, ids[i], slotMask);
                }

                _everythingPass = false;
                for (var r = 0; r < active.Length; r++)
                {
                    active[r].Bootstrapped[a] |= active[r].EverythingThisTick[a];
                }
            }

            if (anyPartial && Volatile.Read(ref cs.StructureTick) == tick)
            {
                var words = cs.StructureWords;
                for (var c = 0; c < words.Length; c++)
                {
                    var w = (ulong)words[c];
                    if (w != 0UL)
                    {
                        AddPush(a, c, w & slotMask);
                    }
                }
            }

            // The validator: a few whole clusters, noting which of their slots nobody pushed. Before the repush list is consumed, which it reads.
            _validating[a].Clear();
            if (anyPartial && ValidateClustersPerTick > 0)
            {
                Validate(a, cs, tick, slotMask);
            }

            // Slots still extrapolating or denied an identity last tick: the engine's own pushes. A client dead-reckons a mover until told it stopped, so a
            // mover that stops without a write must still be visited, and an entity with no identity is invisible until it gets one.
            var repush = _repush[a];
            for (var c = 0; c < repush.Length; c++)
            {
                var w = (ulong)repush[c];
                if (w != 0UL)
                {
                    AddPush(a, c, w & slotMask, anyPass: true);
                    repush[c] = 0;
                }
            }

            // Blocks for every cluster pushed into. Serial by contract (pool and directory are single-threaded here).
            if (_pushBlocks[a].Length < _pushChunks[a].Length)
            {
                Array.Resize(ref _pushBlocks[a], _pushChunks[a].Length);
            }

            for (var i = 0; i < _pushCount[a]; i++)
            {
                var chunk = _pushChunks[a][i];
                var table = state.BlockByChunk;
                var block = (uint)chunk < (uint)table.Length ? (ReplicationBlockHeader*)table[chunk] : null;
                if (block == null && !state.TryAttachBlock(chunk, out block))
                {
                    block = null;
                }

                _pushBlocks[a][i] = (nint)block;
            }
        }

        PrepareTicks += Stopwatch.GetTimestamp() - from;
    }

    /// <summary>Adds the validator's clusters for this tick to the push set, remembering which of their slots were not pushed otherwise.</summary>
    private void Validate(int a, ArchetypeClusterState cs, uint tick, ulong slotMask)
    {
        var ids = cs.ReadActiveClusterList(out var active);
        if (ids == null || active == 0)
        {
            return;
        }

        var words = Volatile.Read(ref cs.StructureTick) == tick ? cs.StructureWords : default;
        var repush = _repush[a];
        var count = Math.Min(ValidateClustersPerTick, active);
        for (var i = 0; i < count; i++)
        {
            var cursor = _validateCursor[a]++ % active;
            var chunk = ids[cursor];

            // Only a chunk of a served realm in its partial pass: an unserved realm's is never pushed, and an everything realm's is pushed whole, so
            // either would count every slot of it as forgotten.
            var replication = _single ? null : ReplicationOf(chunk);
            if (!_single && (replication == null || replication.EverythingThisTick[a]))
            {
                continue;
            }

            var pushed = ((uint)chunk < (uint)words.Length ? (ulong)words[chunk] : 0UL) | ((uint)chunk < (uint)repush.Length ? (ulong)repush[chunk] : 0UL);
            var unpushed = slotMask & ~pushed;
            if (unpushed == 0UL || !_validating[a].TryAdd(chunk, unpushed))
            {
                continue;
            }

            AddPush(a, chunk, unpushed);
            ValidatedSlots += BitOperations.PopCount(unpushed);
        }
    }

    /// <summary>Called by a projecting worker for an event: counts it as a forgotten push when only the validator asked for the slot.</summary>
    internal void NoteIfForgotten(int archetype, ReplicationBlockHeader* block, int slot, byte flags, int groups)
    {
        var validating = _validating[archetype];
        if (validating.Count == 0 || !validating.TryGetValue(block->ChunkId, out var mask) || (mask & (1UL << slot)) == 0)
        {
            return;
        }

        Interlocked.Increment(ref ForgottenPushes);
        if ((flags & PushEvent.Segment) != 0)
        {
            Interlocked.Increment(ref ForgottenMotion);
        }

        var perGroup = _forgottenGroups[archetype];
        for (var g = 0; g < perGroup.Length; g++)
        {
            if ((groups & (1 << g)) != 0)
            {
                Interlocked.Increment(ref perGroup[g]);
            }
        }
    }

    // The replication of a chunk's realm, or null when that realm is not served here: one load of the cluster → realm map.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PushReplication ReplicationOf(int chunk)
    {
        var map = _realmMap;
        return For(map != null && (uint)chunk < (uint)map.Length ? map[chunk] : RealmId.Default.Value);
    }

    // anyPass: the repush list — slots the projection itself asked for again; the chunk is taken in whichever pass its realm runs, because an everything
    // pass walks only the active list and a chunk that left it still owes the projection that gives its identities back.
    private void AddPush(int a, int chunk, ulong mask, bool anyPass = false)
    {
        if (mask == 0UL || chunk < 0)
        {
            return;
        }

        // Routed by the chunk's realm: a realm not served here is never pushed. In the everything pass only the realms that asked for it take the chunk,
        // in the partial pass only the others — each chunk is collected once, whichever realm it is in. One realm and no map: the passes are exclusive by
        // construction (every chunk is realm 0's), so nothing needs routing.
        if (!_single)
        {
            var replication = ReplicationOf(chunk);
            if (replication == null || (!anyPass && replication.EverythingThisTick[a] != _everythingPass))
            {
                return;
            }
        }

        var n = _pushCount[a];
        if (n == _pushChunks[a].Length)
        {
            Array.Resize(ref _pushChunks[a], n * 2);
            Array.Resize(ref _pushMasks[a], n * 2);
        }

        // Adjacent duplicates (the repush list following the structure words) are merged; others are merged by the block's mask OR below.
        _pushChunks[a][n] = chunk;
        _pushMasks[a][n] = mask;
        _pushCount[a] = n + 1;
    }

    /// <summary>After the watched lists were reset: marks the push set and lists each block once, so the projection pass visits exactly it.</summary>
    public void MarkPushed(int workers, bool countInProject = false)
    {
        foreach (var a in _pushIndices)
        {
            var state = _states[a];
            var list = state.WatchedBlocks;
            for (var i = 0; i < _pushCount[a]; i++)
            {
                var block = (ReplicationBlockHeader*)_pushBlocks[a][i];
                if (block == null)
                {
                    continue;
                }

                var before = block->WatchedMask;
                block->WatchedMask = before | _pushMasks[a][i];
                if (before == 0UL)
                {
                    list.Add(block);
                }

                SlotsPushed += BitOperations.PopCount(_pushMasks[a][i]);
            }
        }

        var active = Active;
        for (var r = 0; r < active.Length; r++)
        {
            active[r].BeginMark(workers, countInProject);
        }
    }

    /// <summary>Marks slots of a cluster to be pushed again next tick. Called by the worker that owns the block — one writer per chunk.</summary>
    public void Repush(int archetype, int chunkId, ulong slots)
    {
        var r = _repush[archetype];
        if ((uint)chunkId < (uint)r.Length)
        {
            r[chunkId] |= (long)slots;
        }
    }
}
