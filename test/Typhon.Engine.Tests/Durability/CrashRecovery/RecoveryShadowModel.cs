using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// The in-memory <b>shadow model</b> half of the T-5 differential recovery oracle (design 03 §4.2, 08 T-5). A workload records its committed lifecycle here as it
/// runs — which entities it spawned and which it destroyed — so the shadow holds an <i>independent</i> record of the alive-set, not a snapshot of the engine. Just
/// before the crash the committed component <b>values and enabled-bits are captured by reading them back through the engine's own public read API</b>
/// (<see cref="EntityRef.ReadRaw"/> / <see cref="EntityRef.IsEnabled(byte)"/>): "expected" is therefore produced by the identical code path that <see cref="Diff"/>
/// later uses to read "actual", so a byte comparison can never disagree on encoding — only on whether recovery faithfully reproduced the value.
/// </summary>
/// <remarks>
/// Capture/verify are <b>per-EntityId</b> (Open/IsAlive), which work for both flat (legacy) and cluster-eligible archetypes — the broad scan
/// (<see cref="Transaction.EnumerateArchetypeEntities"/>) is only used additionally to catch resurrected/leaked entities on the flat path. This keeps the oracle
/// valid regardless of an archetype's storage path: a cluster entity that recovery fails to restore surfaces as a <c>LOST</c> diff via the per-id IsAlive check, not
/// as a silently-empty broad scan.
/// <para>Single crash point only this increment (after <c>uow.Flush()</c> — every commit durable). Mid-workload crash points (the future sweep, A1.2) will key value
/// snapshots to the durable prefix; the alive-set recording seam below is already shaped for it. Collections are a marked extension point (CollectionDelta emit is
/// still 0-callers — P1.1 residual — so nothing is logged to recover).</para>
/// </remarks>
internal sealed class RecoveryShadowModel
{
    /// <summary>One committed entity the workload expects to be alive after recovery, plus its captured component values + enabled-bits (filled by <see cref="CaptureValues"/>).</summary>
    internal sealed class ShadowEntity
    {
        public ushort ArchetypeId;
        public int ComponentCount;
        public byte[][] ValueBytesBySlot;   // [slot] → the component's storage bytes at commit; null until CaptureValues runs
        public bool[] EnabledBySlot;        // [slot] → enabled state at commit

        /// <summary>Per collection field, the ELEMENTS at commit; null when the archetype has no collection. See <see cref="ICollectionProjector"/>.</summary>
        public IReadOnlyList<int[]> CollectionElements;
    }

    private readonly Dictionary<EntityId, ShadowEntity> _entities = new();
    private ICollectionProjector _collectionProjector;

    /// <summary>The expected-alive entities, keyed by id. Exposed for the AC1 non-false-green self-test (which corrupts a captured value and asserts <see cref="Diff"/> reports it).</summary>
    internal IReadOnlyDictionary<EntityId, ShadowEntity> Entities => _entities;

    // ── lifecycle recording (called by the workload at commit acknowledgment) ──

    /// <summary>Record that a committed transaction spawned <paramref name="id"/> (and did not later destroy it).</summary>
    /// <remarks>
    /// <para>
    /// <b>This used to be an overwriting indexer assignment</b>, documented as "idempotent for re-spawn-of-same-id (never happens — keys are unique)". Keys
    /// being unique is precisely the premise #697 violates: after a hard crash the entity-key watermark is not restored, so the first post-recovery spawn
    /// re-issues an id that a live recovered entity already holds. Under the old assignment the shadow silently DROPPED the first-generation entity — and an
    /// oracle that has forgotten an entity cannot report it lost. The harness built to catch #697 would have false-greened on it.
    /// </para>
    /// <para>
    /// So the invariant is now enforced rather than assumed. A duplicate is either the engine re-issuing a live id — the defect — or a workload recording the
    /// same spawn twice, a test bug; both are stated in the message because the shadow cannot tell them apart and guessing would send the reader the wrong way.
    /// </para>
    /// </remarks>
    public void RecordSpawn(EntityId id)
    {
        if (_entities.ContainsKey(id))
        {
            throw new InvalidOperationException(
                $"Shadow inconsistency: {id} was spawned while an entity with the SAME id is still alive in the shadow. Either the engine re-issued a live "
                + "EntityId — an allocation-watermark defect, #697, which silently overwrites the entity that already held it — or the workload recorded the "
                + "same spawn twice. Both are real; neither may be swallowed.");
        }

        _entities[id] = new ShadowEntity { ArchetypeId = id.ArchetypeId };
    }

    /// <summary>The ids the workload currently expects to be alive. Used by the post-recovery <c>Resume</c> harness to prove it actually wrote, and by
    /// workloads that must mutate entities a PREVIOUS phase committed rather than spawning their own (the #569 cross-frontier shape).</summary>
    public IReadOnlyCollection<EntityId> AliveIds => _entities.Keys;

    /// <summary>Record that a committed transaction destroyed <paramref name="id"/>. The entity leaves the expected alive-set (recovery must NOT resurrect it).</summary>
    public void RecordDestroy(EntityId id) => _entities.Remove(id);

    // Component value updates and enable/disable do not change the alive-set or archetype, so they need no recording — the FINAL committed value/enabled state is
    // captured by CaptureValues below (read back from the live engine just before the crash).

    /// <summary>
    /// Snapshot the committed component values + enabled-bits of every expected-alive entity by reading them back through the live (pre-crash) engine. Throws if an
    /// entity the workload recorded alive is not actually alive in the engine — that is a workload/engine inconsistency (a test bug), surfaced loudly rather than
    /// silently weakening the oracle.
    /// </summary>
    public void CaptureValues(DatabaseEngine dbe) => CaptureValues(dbe, null);

    /// <inheritdoc cref="CaptureValues(DatabaseEngine)"/>
    /// <param name="dbe">The live (pre-crash) engine.</param>
    /// <param name="collectionProjector">
    /// Required when any archetype in the shadow carries a <c>ComponentCollection</c> field; see <see cref="AssertCollectionsAreObservable"/> for why it is
    /// not optional.
    /// </param>
    public void CaptureValues(DatabaseEngine dbe, ICollectionProjector collectionProjector)
    {
        _collectionProjector = collectionProjector;
        AssertCollectionsAreObservable(dbe);

        using var tx = dbe.CreateQuickTransaction();
        foreach (var (id, e) in _entities)
        {
            if (!tx.IsAlive(id))
            {
                throw new InvalidOperationException(
                    $"Shadow inconsistency: {id} was recorded alive by the workload but is not alive in the live engine before the crash — this is a workload/engine "
                    + "bug, not a recovery failure. Fix the workload's lifecycle recording.");
            }

            var er = tx.Open(id);
            int n = er.ComponentCount;
            e.ComponentCount = n;
            e.ValueBytesBySlot = new byte[n][];
            e.EnabledBySlot = new bool[n];
            for (int s = 0; s < n; s++)
            {
                e.ValueBytesBySlot[s] = er.ReadRaw(s).ToArray();
                e.EnabledBySlot[s] = er.IsEnabled((byte)s);
            }

            e.CollectionElements = _collectionProjector?.Project(tx, id);
        }
    }

    /// <summary>
    /// Refuse to capture a collection-bearing archetype without a projector (#705 T3 / #389).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Diff"/> compares <c>ReadRaw(slot)</c> — the component's STORAGE bytes. For a <c>ComponentCollection&lt;T&gt;</c> field those bytes are a
    /// buffer DESCRIPTOR, not the elements, so the comparison is wrong in both directions: a rebuilt buffer holding the right contents can differ, and a
    /// descriptor that survives intact can point at a buffer that has been emptied. The latter is the recorded #389 symptom — <c>Diff()</c> returned 0 while a
    /// collection went 5 elements → 0.
    /// </para>
    /// <para>
    /// Making the projector optional-by-default would leave that false-green one forgotten argument away, on the exact test written to catch it. So the
    /// requirement is derived from schema metadata (<c>ComponentTable.HasCollections</c>) rather than trusted to the caller: an archetype that carries a
    /// collection cannot be captured without a way to see into it.
    /// </para>
    /// </remarks>
    private void AssertCollectionsAreObservable(DatabaseEngine dbe)
    {
        if (_collectionProjector != null)
        {
            return;
        }

        foreach (var routingId in DistinctArchetypeIds())
        {
            var state = routingId < dbe._stateByRouting.Length ? dbe._stateByRouting[routingId] : null;
            var tables = state?.SlotToComponentTable;
            if (tables == null)
            {
                continue;
            }

            for (var slot = 0; slot < tables.Length; slot++)
            {
                if (tables[slot]?.HasCollections == true)
                {
                    throw new InvalidOperationException(
                        $"Archetype (routing {routingId}) slot {slot} ({tables[slot].StorageMode}) carries a ComponentCollection, but no "
                        + $"{nameof(ICollectionProjector)} was supplied. The oracle compares component STORAGE bytes, which for a collection field is a buffer "
                        + "descriptor — so a collection emptied by recovery would compare EQUAL and the test would pass while the data was gone (#389). "
                        + $"Pass a projector to {nameof(CaptureValues)}.");
                }
            }
        }
    }

    /// <summary>
    /// Compare the recovered engine against this shadow and return every mismatch (empty ⇒ recovery is faithful). Two checks: (1) <b>per-id</b> — every expected-alive
    /// entity must be alive and its component bytes + enabled-bits must match exactly (catches loss, value corruption, wrong enabled-bits — flat AND cluster paths);
    /// (2) <b>broad-scan leak</b> — no entity absent from the shadow may appear in a flat archetype's entity map (catches resurrection of a destroyed entity). The leak
    /// check is naturally a no-op for cluster archetypes (their entities are not in the legacy EntityMap), where loss is already covered by check (1).
    /// </summary>
    public List<string> Diff(DatabaseEngine recoveredDbe)
    {
        var diffs = new List<string>();
        using var tx = recoveredDbe.CreateQuickTransaction();

        foreach (var (id, e) in _entities)
        {
            if (!tx.IsAlive(id))
            {
                diffs.Add($"LOST {id} (arch {e.ArchetypeId}): alive in shadow, not recovered");
                continue;
            }

            var er = tx.Open(id);
            if (er.ComponentCount != e.ComponentCount)
            {
                diffs.Add($"{id}: ComponentCount {er.ComponentCount} != expected {e.ComponentCount}");
                continue;
            }

            for (int s = 0; s < e.ComponentCount; s++)
            {
                if (!er.ReadRaw(s).SequenceEqual(e.ValueBytesBySlot[s]))
                {
                    diffs.Add(
                        $"{id} slot {s} ({er.GetComponentName(s)}): value bytes differ — expected [{BitConverter.ToString(e.ValueBytesBySlot[s])}], "
                        + $"got [{BitConverter.ToString(er.ReadRaw(s).ToArray())}]");
                }

                if (er.IsEnabled((byte)s) != e.EnabledBySlot[s])
                {
                    diffs.Add($"{id} slot {s} ({er.GetComponentName(s)}): enabled {er.IsEnabled((byte)s)} != expected {e.EnabledBySlot[s]}");
                }
            }

            DiffCollections(tx, id, e, diffs);
        }

        foreach (var archId in DistinctArchetypeIds())
        {
            foreach (var rid in tx.EnumerateArchetypeEntities(archId))
            {
                if (!_entities.ContainsKey(rid))
                {
                    diffs.Add($"EXTRA {rid} (arch {archId}): present after recovery but absent from shadow (resurrection / leak)");
                }
            }
        }

        return diffs;
    }

    /// <summary>
    /// Compare an entity's collection ELEMENTS against the captured ones — the check the raw-bytes comparison structurally cannot make.
    /// </summary>
    /// <remarks>
    /// Element count is reported before contents because it is the earlier and stronger signal: #389's symptom is a buffer that recovers EMPTY behind an
    /// intact descriptor, and "5 → 0 elements" localises that immediately, where a value diff on element 0 would not distinguish it from corruption.
    /// </remarks>
    private void DiffCollections(Transaction tx, EntityId id, ShadowEntity e, List<string> diffs)
    {
        if (_collectionProjector == null || e.CollectionElements == null)
        {
            return;
        }

        var actual = _collectionProjector.Project(tx, id);
        if (actual.Count != e.CollectionElements.Count)
        {
            diffs.Add($"{id}: projector returned {actual.Count} collection field(s), expected {e.CollectionElements.Count}");
            return;
        }

        for (var f = 0; f < actual.Count; f++)
        {
            var got = actual[f];
            var want = e.CollectionElements[f];
            if (got.Length != want.Length)
            {
                diffs.Add(
                    $"{id} collection field {f}: {got.Length} element(s), expected {want.Length}. A collection that recovers EMPTY behind an intact buffer "
                    + "descriptor is #389's exact shape — the raw-bytes comparison cannot see it.");
                continue;
            }

            for (var k = 0; k < got.Length; k++)
            {
                if (got[k] != want[k])
                {
                    diffs.Add($"{id} collection field {f} element {k}: {got[k]} != expected {want[k]}");
                }
            }
        }
    }

    private HashSet<ushort> DistinctArchetypeIds()
    {
        var ids = new HashSet<ushort>();
        foreach (var e in _entities.Keys)
        {
            ids.Add(e.ArchetypeId); // EntityId.ArchetypeId is the per-DB routing id (what EnumerateArchetypeEntities expects)
        }

        return ids;
    }
}
