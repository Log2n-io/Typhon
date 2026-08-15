using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Profiler.Events;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Producer-side coverage for the ECS lifecycle events the Workbench entity lens (#620, design §4.4) folds into spawn/destroy cohorts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this fixture exists.</b> Before #620, <c>SpawnBatch</c> and <c>SpawnBatchAllocate</c> emitted nothing at all: only the single-entity
/// <c>Spawn</c> / <c>Destroy</c> paths carried telemetry, so a bulk-loaded world produced an empty cohort set while looking perfectly healthy. That
/// hole was invisible because no test asserted the *pairing* between what the API returns and what the trace records. These tests assert exactly
/// that pairing, in both directions, so the two cannot drift apart again silently.
/// </para>
/// <para>
/// <b>Why a dedicated thread, and why captures are filtered to its ring slot.</b> <c>TelemetryConfig.ProfilerActive</c> is true suite-wide (see
/// <c>typhon.telemetry.json</c>) and the profiler drains <i>every</i> thread slot, so a capture taken here also contains whatever prior fixtures left
/// buffered in theirs. That is not merely noise: entity ids are unique within a database, not across the suite's many databases, so a foreign record can
/// carry the same id as the entity under test. Running the workload on a fresh thread and keeping only that slot's records is what makes these
/// assertions about this workload rather than about whatever ran before it.
/// </para>
/// <para>
/// <b>Why every assertion is scoped to specific entity ids.</b> Opening an engine is not telemetry-silent: <c>InitializeArchetypes</c> spawns and
/// destroys its own entities, so a capture of "one spawn and one destroy" actually contains five spawns and two destroys. Asserting on whole-capture
/// counts would be asserting on engine internals, and would break the first time they change. Each test therefore checks what happened *to the ids
/// its workload produced*.
/// </para>
/// <para>
/// <b>The transactions here are not committed</b> — <c>using var t = dbe.CreateQuickTransaction()</c> disposes without commit, so the workload ends in
/// a rollback. That is deliberate and matches the emission contract: the trace records the *attempt*, at the moment of the call. A rolled-back cohort
/// is then correctly reported by the Workbench as spawned-but-not-alive, because the alive check asks the database, not the trace.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable] // drives the process-global profiler pipeline; must not run concurrently with other fixtures
internal sealed class EcsLifecycleTelemetryTests : TestBase<EcsLifecycleTelemetryTests>
{
    [TearDown]
    public void TearDownProfiler()
    {
        try { TyphonProfiler.Stop(); } catch { /* belt-and-braces if a test already stopped it */ }
        TyphonProfiler.ResetForTests();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Batch spawn — the hole #620 closed
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnBatch_EmitsOneRangeRecord_ThatReconstructsExactlyTheReturnedIds()
    {
        EntityId[] spawned = null;

        var events = CaptureEngineWorkload(dbe =>
        {
            using var t = dbe.CreateQuickTransaction();
            Span<EntityId> ids = stackalloc EntityId[7];
            t.SpawnBatch<EcsUnit>(ids, EcsUnit.Position.Set(new EcsPosition(1, 2, 3)));
            spawned = ids.ToArray();
        });

        var batches = events.OfType<EcsSpawnBatchEventDto>().ToArray();

        // One record for the whole batch — not one per entity. That is the point of the range encoding: seven entities here, 200,000 in a real
        // bulk load, both cost a single record.
        Assert.That(batches, Has.Length.EqualTo(1), "a batch spawn must record exactly one range");

        var batch = batches[0];
        Assert.That(batch.Count, Is.EqualTo(7));

        // The batch must not ALSO appear as per-entity records — that would double-count the cohort. Scoped to the batch's own ids, because the
        // engine's own initialization spawns entities through the single-entity path.
        // `EcsSpawnEventDto.EntityId` is nullable — it is an [Optional] field the producer fills in after SpawnInternal returns.
        var batchIds = spawned.Select(id => (ulong)id.RawValue).ToHashSet();
        Assert.That(events.OfType<EcsSpawnEventDto>().Any(s => s.EntityId is { } eid && batchIds.Contains(eid)), Is.False,
            "the batch path must not also emit per-entity spawn records for the same ids");

        // The whole safety claim of the range encoding: (BaseKey, Count, RoutingId) rebuilds the exact ids the API handed back. If this ever
        // diverges, the Workbench cohort would list entity ids that were never spawned — and they would look entirely plausible.
        var reconstructed = Enumerable.Range(0, batch.Count)
            .Select(n => new EntityId(batch.BaseKey + n, batch.RoutingId))
            .ToArray();
        Assert.That(reconstructed, Is.EqualTo(spawned), "the recorded range must reconstruct the returned ids exactly");
    }

    [Test]
    public void SpawnBatch_RecordsTheRoutingId_NotTheCatalogArchetypeId()
    {
        EntityId[] spawned = null;

        var events = CaptureEngineWorkload(dbe =>
        {
            using var t = dbe.CreateQuickTransaction();
            Span<EntityId> ids = stackalloc EntityId[3];
            t.SpawnBatch<EcsUnit>(ids);
            spawned = ids.ToArray();
        });

        var batch = events.OfType<EcsSpawnBatchEventDto>().Single();

        // `EntityId.ArchetypeId` is the *routing* id (low 16 bits), while `ArchetypeMetadata.ArchetypeId` is the per-process *catalog* id. Same
        // property name, two different id spaces — design §5.3's landmine sitting right in the API surface. The record must carry the routing id,
        // because a consumer rebuilding ids from BaseKey has nothing else to source it from.
        Assert.That(batch.RoutingId, Is.EqualTo(spawned[0].ArchetypeId), "RoutingId must match the id space embedded in the entity ids");
        foreach (var id in spawned)
        {
            Assert.That(id.ArchetypeId, Is.EqualTo(batch.RoutingId));
        }
    }

    [Test]
    public void SpawnBatchAllocate_TheGeneratedSoaPath_AlsoEmitsItsRange()
    {
        // The second hole. `SpawnBatch` and `SpawnBatchAllocate` are separate entry points with separate key reservations, so fixing one and not
        // the other would leave every source-generated SOA bulk load invisible to the entity lens.
        EntityId[] spawned = null;

        var events = CaptureEngineWorkload(dbe =>
        {
            using var t = dbe.CreateQuickTransaction();
            Span<EntityId> ids = stackalloc EntityId[5];
            t.SpawnBatchAllocate<EcsUnit>(5, ids);
            spawned = ids.ToArray();
        });

        var batch = events.OfType<EcsSpawnBatchEventDto>().Single();

        Assert.That(batch.Count, Is.EqualTo(5));
        var reconstructed = Enumerable.Range(0, batch.Count)
            .Select(n => new EntityId(batch.BaseKey + n, batch.RoutingId))
            .ToArray();
        Assert.That(reconstructed, Is.EqualTo(spawned));
    }

    [Test]
    public void SpawnBatchAllocate_WithZeroCount_EmitsNothing()
    {
        // A zero-length batch reserves no keys, so a range record would describe an empty cohort. Absent beats an empty row the reader has to
        // special-case.
        var events = CaptureEngineWorkload(dbe =>
        {
            using var t = dbe.CreateQuickTransaction();
            Span<EntityId> ids = stackalloc EntityId[1];
            t.SpawnBatchAllocate<EcsUnit>(0, ids);
        });

        Assert.That(events.OfType<EcsSpawnBatchEventDto>().Any(), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // The single-entity paths — regression guards, since #620 touched their file
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_StillEmitsOneRecordPerEntity_CarryingItsId()
    {
        var spawned = new List<EntityId>();

        var events = CaptureEngineWorkload(dbe =>
        {
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < 4; i++)
            {
                spawned.Add(t.Spawn<EcsUnit>());
            }
        });

        var recorded = events.OfType<EcsSpawnEventDto>().Select(s => s.EntityId).ToList();

        foreach (var id in spawned)
        {
            Assert.That(recorded.Count(r => r == (ulong)id.RawValue), Is.EqualTo(1), $"entity {id} should be recorded exactly once");
        }
        Assert.That(events.OfType<EcsSpawnBatchEventDto>().Any(), Is.False, "single spawns must not be recorded as ranges");
    }

    [Test]
    public void Destroy_EmitsTheDestroyedId_WhichIsHowACohortLosesMembers()
    {
        EntityId victim = default;

        var events = CaptureEngineWorkload(dbe =>
        {
            using var t = dbe.CreateQuickTransaction();
            victim = t.Spawn<EcsUnit>();
            t.Destroy(victim);
        });

        var forVictim = events.OfType<EcsDestroyEventDto>().Where(d => d.EntityId == (ulong)victim.RawValue).ToArray();

        Assert.That(forVictim, Has.Length.EqualTo(1),
            $"expected exactly one destroy record for {victim}; saw ids "
            + $"[{string.Join(", ", events.OfType<EcsDestroyEventDto>().Select(d => d.EntityId))}]");
    }

    [Test]
    public void SpawnBatchThenDestroy_LeavesTheCohortAndItsLossBothRecorded()
    {
        // The shape the entity lens actually consumes: a cohort, then one of its members leaving it. Both halves have to be present and joinable
        // on the id, since the panel's "N spawned, M still alive" is exactly this difference computed against the database.
        EntityId[] spawned = null;
        EntityId victim = default;

        var events = CaptureEngineWorkload(dbe =>
        {
            using var t = dbe.CreateQuickTransaction();
            Span<EntityId> ids = stackalloc EntityId[4];
            t.SpawnBatch<EcsUnit>(ids);
            spawned = ids.ToArray();
            victim = spawned[2];
            t.Destroy(victim);
        });

        var batch = events.OfType<EcsSpawnBatchEventDto>().Single();
        var cohort = Enumerable.Range(0, batch.Count).Select(n => new EntityId(batch.BaseKey + n, batch.RoutingId)).ToArray();

        Assert.That(cohort, Is.EqualTo(spawned));
        Assert.That(cohort, Does.Contain(victim), "the destroyed entity must be a member of the recorded cohort");
        Assert.That(events.OfType<EcsDestroyEventDto>().Count(d => d.EntityId == (ulong)victim.RawValue), Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Harness
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs <paramref name="workload"/> against a fresh engine on a dedicated thread with a <see cref="TraceRingObserver"/> attached, then returns
    /// every decoded event the observer saw. The engine is created and disposed inside that thread because transactions are thread-affine.
    /// </summary>
    private List<TraceEventDto> CaptureEngineWorkload(Action<DatabaseEngine> workload)
    {
        // The observer needs a real resource parent — its base ResourceNode ctor dereferences it, despite the parameter's "null = orphan" doc.
        var profilerNode = ResourceRegistry.Profiler;
        using var observer = new TraceRingObserver(profilerNode, captureRawBytes: true);
        TyphonProfiler.AttachExporter(observer);
        TyphonProfiler.Start(profilerNode, BuildMetadata());

        Exception failure = null;
        var workloadSlot = -1;
        var thread = new Thread(() =>
        {
            try
            {
                using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
                dbe.RegisterComponentFromAccessor<EcsPosition>();
                dbe.RegisterComponentFromAccessor<EcsVelocity>();
                dbe.RegisterComponentFromAccessor<EcsHealth>();
                dbe.InitializeArchetypes();

                // Force a slot assignment with one explicit emit before reading the index. The registry hands a thread its slot lazily on first emit,
                // and which engine-internal events actually fire depends on per-subsystem telemetry gates — so relying on "opening an engine must have
                // emitted something" made this read return -1 nondeterministically under the full suite.
                TyphonEvent.EmitTickStart(System.Diagnostics.Stopwatch.GetTimestamp());
                workloadSlot = ThreadSlotRegistry.CurrentThreadSlotIndex;

                workload(dbe);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "engine workload thread did not finish");
        TyphonProfiler.Stop();

        if (failure != null)
        {
            throw new AssertionException($"engine workload threw: {failure}", failure);
        }

        if (workloadSlot < 0)
        {
            // The ring has a fixed slot pool shared by the whole process. If this thread never got one, its records were dropped before reaching the
            // observer and there is nothing to assert against — inconclusive is the honest verdict, since a pass would be vacuous and a failure would
            // blame the code under test for the harness running out of slots.
            Assert.Inconclusive("The workload thread was not assigned a profiler ring slot; no records could be captured.");
        }

        var decoded = new List<TraceEventDto>();
        foreach (var (_, bytes) in observer.GetRecords())
        {
            var dto = TraceEventDecoder.Decode(bytes, 0, 1);
            if (dto.ThreadSlot == workloadSlot)
            {
                decoded.Add(dto);
            }
        }
        return decoded;
    }

    private static ProfilerSessionMetadata BuildMetadata() => new(
        systems: [],
        archetypes: [],
        componentTypes: [],
        workerCount: 0,
        baseTickRate: 60.0f,
        startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
        stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
        startedUtc: DateTime.UtcNow);
}
