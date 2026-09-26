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
    internal PushReplication Place(SessionId session, ushort realm, uint tick, out bool joined)
    {
        joined = false;
        var slot = session.Slot;
        ref var link = ref Links[slot];
        if (link.Generation != session.Generation)
        {
            link = default;
            link.Generation = session.Generation;
        }

        var replication = For(realm) ?? Activate(realm, tick);
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
        joined = true;
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

    /// <summary>
    /// Every <see cref="SweepEvery"/> ticks, after the tick's placements: slots no session was placed in go back, and a realm left with no session stops being
    /// served (R4.4, SUB-13) — kept, dormant, until a session is placed in it again. Realm 0 is always served. Serial (the frame prologue).
    /// </summary>
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

        for (var r = _activeCount - 1; r >= 0; r--)
        {
            var replication = _active[r];
            if (replication.ServedRealm != RealmId.Default.Value && replication.SessionsHere == 0)
            {
                Deactivate(r);
            }
        }
    }

    // ══ Realms served on demand (R4.4) ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Builds a realm's replication the first time a session is placed in it; null when the realm has none, or its grid cannot be served.</summary>
    internal Func<ushort, PushReplication> Factory;

    /// <summary>
    /// The identity of the realm registered under an id now (its table entry), set with <see cref="Factory"/>: a realm removed and registered again under
    /// the same id is another realm, whose dormant replication and refusal are not its predecessor's (Realms D5).
    /// </summary>
    internal Func<ushort, object> RealmIdentity;

    // Per realm id: the identity the dormant replication or the refusal was recorded for.
    private object[] _recordedFor = [];

    /// <summary>The worker count the tick's projection was marked for, which a replication activated in the frame prologue sizes its lists by.</summary>
    internal int Workers = 1;

    // Replications of realms no session is in any more, kept for their next session; and realms whose replication could not be built, not retried.
    private PushReplication[] _dormant = [];
    private bool[] _unservable = [];

    /// <summary>Realms whose replication was built or woken for a session — cumulative.</summary>
    public long RealmsActivated;

    /// <summary>Realms that stopped being served when their last session left — cumulative.</summary>
    public long RealmsDeactivated;

    /// <summary>Realms a session was placed in whose replication could not be built (counted once per realm).</summary>
    public long RealmsUnservable;

    private PushReplication Activate(ushort realm, uint tick)
    {
        if (Factory == null)
        {
            return null;
        }

        var identity = RealmIdentity?.Invoke(realm);
        if (realm < _recordedFor.Length && _recordedFor[realm] != null && !ReferenceEquals(_recordedFor[realm], identity))
        {
            // Registered again since: what was recorded for the id belonged to the realm removed.
            _recordedFor[realm] = null;
            if (realm < _dormant.Length)
            {
                _dormant[realm] = null;
            }

            if (realm < _unservable.Length)
            {
                _unservable[realm] = false;
            }
        }

        if (realm < _unservable.Length && _unservable[realm])
        {
            return null;
        }

        var replication = realm < _dormant.Length ? _dormant[realm] : null;
        if (replication != null)
        {
            _dormant[realm] = null;
        }
        else
        {
            try
            {
                replication = Factory(realm);
            }
            catch (Exception)
            {
                // Anything the factory did not foresee refuses the realm like a refusal it did: the prologue never throws, and is not retried every tick.
                replication = null;
            }

            Record(realm, identity);
            if (replication == null)
            {
                if (realm >= _unservable.Length)
                {
                    Array.Resize(ref _unservable, Math.Max(realm + 1, Math.Max(8, _unservable.Length * 2)));
                }

                _unservable[realm] = true;
                RealmsUnservable++;
                return null;
            }
        }

        Serve(realm, replication);
        replication.PrimeForTick(tick, Workers);
        RealmsActivated++;
        return replication;
    }

    private void Record(ushort realm, object identity)
    {
        if (realm >= _recordedFor.Length)
        {
            Array.Resize(ref _recordedFor, Math.Max(realm + 1, Math.Max(8, _recordedFor.Length * 2)));
        }

        _recordedFor[realm] = identity;
    }

    private void Deactivate(int index)
    {
        var replication = _active[index];
        var realm = replication.ServedRealm;
        _byRealm[realm] = null;
        _active[index] = _active[--_activeCount];
        _active[_activeCount] = null;
        if (realm >= _dormant.Length)
        {
            Array.Resize(ref _dormant, Math.Max(realm + 1, Math.Max(8, _dormant.Length * 2)));
        }

        _dormant[realm] = replication;
        RealmsDeactivated++;
    }

    // ══ The stages, over the served realms: one realm (the common case) calls straight through ════════════════════════════════════════════════════════

    // Per stage, chunk i is served realm r's chunk (i − start[r]): the index merge's plan and the far fold's.
    private int[] _indexStarts = new int[2];
    private int[] _farStarts = new int[2];

    /// <summary>A projection chunk's end: every served realm sorts the run it holds from this worker.</summary>
    internal void CountWorkers(int worker)
    {
        for (var r = 0; r < _activeCount; r++)
        {
            _active[r].CountWorker(worker);
        }
    }

    /// <summary>The index merge's plan over every served realm; its chunk count.</summary>
    internal int BeginParallelIndex() => _activeCount == 1 ? _active[0].BeginParallelIndex() : Plan(ref _indexStarts, static (r, _) => r.BeginParallelIndex(), 0);

    /// <summary>One chunk of the index merge.</summary>
    internal void PlaceWorker(int chunk)
    {
        if (_activeCount == 1)
        {
            _active[0].PlaceWorker(chunk);
            return;
        }

        var r = Locate(_indexStarts, chunk);
        if (r >= 0)
        {
            _active[r].PlaceWorker(chunk - _indexStarts[r]);
        }
    }

    /// <summary>Every served realm's index finished, then the far fold's plan over them — none without a session; its chunk count.</summary>
    internal int PrepareFar(int workers, bool sessions)
    {
        for (var r = 0; r < _activeCount; r++)
        {
            _active[r].FinishIndex();
        }

        if (!sessions)
        {
            return 0;
        }

        return _activeCount == 1 ? _active[0].BeginFarFold(workers) : Plan(ref _farStarts, static (r, w) => r.BeginFarFold(w), workers);
    }

    /// <summary>One chunk of the far fold.</summary>
    internal void FoldFarChunk(int chunk)
    {
        if (_activeCount == 1)
        {
            _active[0].FoldFarChunk(chunk);
            return;
        }

        var r = Locate(_farStarts, chunk);
        if (r >= 0)
        {
            _active[r].FoldFarChunk(chunk - _farStarts[r]);
        }
    }

    private int Plan(ref int[] starts, Func<PushReplication, int, int> prepare, int workers)
    {
        if (starts.Length < _activeCount + 1)
        {
            starts = new int[Math.Max(_activeCount + 1, starts.Length * 2)];
        }

        var total = 0;
        for (var r = 0; r < _activeCount; r++)
        {
            starts[r] = total;
            total += prepare(_active[r], workers);
        }

        starts[_activeCount] = total;
        return total;
    }

    private int Locate(int[] starts, int chunk)
    {
        for (var r = 0; r < _activeCount; r++)
        {
            if (chunk < starts[r + 1])
            {
                return chunk >= starts[r] ? r : -1;
            }
        }

        return -1;
    }

    /// <summary>The frame prologue's serial share, per served realm: the index built where its stage did not, and the occupied cells in order.</summary>
    internal void BuildIndexes()
    {
        for (var r = 0; r < _activeCount; r++)
        {
            if (!_active[r].Indexed)
            {
                _active[r].BuildIndex();
            }

            _active[r].PrepareWorldOrder();
        }
    }

    /// <summary>Every served realm's far flushes into the tick's log slot.</summary>
    internal void EndFarFolds()
    {
        for (var r = 0; r < _activeCount; r++)
        {
            _active[r].EndFarFold();
        }
    }

    /// <summary>Every served realm's LOD census, over the tick's push sessions (each counts its own).</summary>
    internal void RecountLevels(SessionId[] sessions, int count)
    {
        for (var r = 0; r < _activeCount; r++)
        {
            _active[r].RecountLevels(sessions, count);
        }
    }

    /// <summary>The overload step (09 § 10), for every served realm's gathers and budget loops.</summary>
    internal void SetOverloadStep(int step)
    {
        for (var r = 0; r < _activeCount; r++)
        {
            _active[r].OverloadStep = step;
        }
    }

    /// <summary>The shadow oracle's queued checks, in every served realm that keeps shadows.</summary>
    internal void RunQueuedShadowChecks()
    {
        for (var r = 0; r < _activeCount; r++)
        {
            if (_active[r].Shadow)
            {
                _active[r].RunQueuedShadowChecks();
            }
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
                if (_single)
                {
                    var ids = cs.ReadActiveClusterList(out var clusters);
                    for (var i = 0; ids != null && i < clusters; i++)
                    {
                        AddPush(a, ids[i], slotMask);
                    }
                }
                else
                {
                    // Only the realms that asked for everything: their own lists (RM-07). AddPush would drop every other realm's chunk anyway, so a
                    // one-room interior bootstrapping no longer walks the planet's clusters.
                    for (var r = 0; r < active.Length; r++)
                    {
                        if (!active[r].EverythingThisTick[a])
                        {
                            continue;
                        }

                        var ids = cs.ReadRealmClusterList(active[r].ServedRealm, out var clusters);
                        for (var i = 0; i < clusters; i++)
                        {
                            AddPush(a, ids[i], slotMask);
                        }
                    }
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
        Workers = workers;
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
