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
    }

    private readonly Dictionary<EntityId, ShadowEntity> _entities = new();

    /// <summary>The expected-alive entities, keyed by id. Exposed for the AC1 non-false-green self-test (which corrupts a captured value and asserts <see cref="Diff"/> reports it).</summary>
    internal IReadOnlyDictionary<EntityId, ShadowEntity> Entities => _entities;

    // ── lifecycle recording (called by the workload at commit acknowledgment) ──

    /// <summary>Record that a committed transaction spawned <paramref name="id"/> (and did not later destroy it). Idempotent for re-spawn-of-same-id (never happens — keys are unique).</summary>
    public void RecordSpawn(EntityId id) => _entities[id] = new ShadowEntity { ArchetypeId = id.ArchetypeId };

    /// <summary>Record that a committed transaction destroyed <paramref name="id"/>. The entity leaves the expected alive-set (recovery must NOT resurrect it).</summary>
    public void RecordDestroy(EntityId id) => _entities.Remove(id);

    // Component value updates and enable/disable do not change the alive-set or archetype, so they need no recording — the FINAL committed value/enabled state is
    // captured by CaptureValues below (read back from the live engine just before the crash).

    /// <summary>
    /// Snapshot the committed component values + enabled-bits of every expected-alive entity by reading them back through the live (pre-crash) engine. Throws if an
    /// entity the workload recorded alive is not actually alive in the engine — that is a workload/engine inconsistency (a test bug), surfaced loudly rather than
    /// silently weakening the oracle.
    /// </summary>
    public void CaptureValues(DatabaseEngine dbe)
    {
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
