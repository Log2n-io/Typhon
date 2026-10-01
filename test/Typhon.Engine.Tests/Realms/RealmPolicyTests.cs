using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Profiler;
using Typhon.Profiler;
using Typhon.Profiler.Events;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms D1 (RT-3) and D3 (RT-5): each realm's policy is decided once per tick, before any dispatch; a dormant realm's clusters reach no QuerySystem on
/// any path; an observer keeps a realm active; an entry wakes a dormant realm, which sleeps again after its hold.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmPolicyTests : TestBase<RealmPolicyTests>
{
    private const int SleepAfter = 2;

    /// <summary>
    /// Detaches whatever exporter a case attached, because <c>TyphonProfiler</c>'s exporter list is static and <c>Stop</c> does not clear it.
    /// </summary>
    /// <remarks>
    /// Without this, a later <c>Start</c> in the same process spawns a consume thread over THIS fixture's already-disposed observer, and
    /// <c>GetConsumingEnumerable</c> on a disposed <c>BlockingCollection</c> throws on a background thread — which kills the test host outright. It aborted
    /// the whole gated pass after its first case, so the remaining gated tests reported as "not run" rather than as failures. Every other profiler fixture
    /// already does this (<c>ConcurrencyTracingStressTests</c>, <c>FileExporterIntegrationTests</c>, …); this one was the exception.
    /// </remarks>
    [TearDown]
    public void DetachProfilerExporters()
    {
        try { TyphonProfiler.Stop(); } catch { /* a case that never started one, or already stopped it */ }
        TyphonProfiler.ResetForTests();
    }

    private static SpatialGridConfig Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    /// <summary>The least metadata a profiler session needs — this fixture asserts on records, never on the session header.</summary>
    private static ProfilerSessionMetadata TraceMetadata() => new(
        systems: [], archetypes: [], componentTypes: [], workerCount: 0, baseTickRate: 1000f,
        startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(), stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
        startedUtc: DateTime.UtcNow);

    private static RealmPos At(float x, float y, ushort realm, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = tag };

    /// <summary>Realm 0 simulated always; realm 1 an interior that sleeps after <see cref="SleepAfter"/> unobserved ticks; realm 2 simulated always.</summary>
    private DatabaseEngine ThreeRealms(out EntityId[] in0, out EntityId[] in1, out EntityId[] in2)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid());
        dbe.Realms.Register(new RealmId(1),
            new RealmConfig { Grid = Grid(), WhenUnobserved = RealmUnobserved.Sleep, UnobservedTickDivisor = 1, SleepAfterTicks = SleepAfter });
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid()));
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            in0 = [tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 0))), tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 0)))];
            in1 = [tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 1))), tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 1))),
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(95, 95, 1)))];
            in2 = [tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 2)))];
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Runs a schedule of one tick counter and one QuerySystem recording the entities it saw per tick, until <paramref name="ticks"/> ran.</summary>
    private static Dictionary<long, HashSet<EntityId>> Dispatched(DatabaseEngine dbe, int ticks, bool parallel, SimTier tier = SimTier.All)
    {
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<RealmUnit>().ToView();
        var seen = new ConcurrentDictionary<long, ConcurrentBag<EntityId>>();
        var ticksSeen = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Walk", ctx =>
            {
                var bag = seen.GetOrAdd(ctx.TickNumber, _ => []);
                foreach (var id in ctx.Entities)
                {
                    bag.Add(id);
                }
            }, input: () => view, parallel: parallel, tier: tier, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= ticks, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        Assert.That(ticksSeen, Is.GreaterThanOrEqualTo(ticks), "the runtime ran");
        return seen.ToDictionary(p => p.Key, p => p.Value.ToHashSet());
    }

    /// <summary>
    /// #WB-05 — the census kind 66 carries is the realms this archetype lives in and how many of them get a per-realm row, and the two move apart exactly
    /// when a realm goes dormant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This tests the PREDICATE rather than the wire, deliberately, and it is the half that can run: the per-realm record is gated on the <c>Spatial</c>
    /// telemetry subtree, which the suite leaves off (a subtree root defaults to false, and turning it on would enable every spatial SPAN for every fixture).
    /// <c>EveryRunnableRealmSendsARow_OverTheWire</c> below covers the emission itself and needs the flag, so it is <c>[Explicit]</c>. What matters most is
    /// here: a census that disagrees with the rows would read as an engine bug, and the two share one definition of "runnable" so they cannot.
    /// </para>
    /// <para>
    /// The identity is asserted as it changes, not as a constant. Realm 1 is a <c>Sleep</c> realm with <c>SleepAfterTicks = 2</c>, so the run starts with
    /// three runnable realms and ends with two while the present count stays at three — which is the whole claim: a dormant realm does not vanish from the
    /// engine's accounting, it stops getting a row.
    /// </para>
    /// </remarks>
    [Test]
    public void TheRealmCensusCountsEveryPresentRealm_AndOnlyRunnableOnesGetARow()
    {
        using var dbe = ThreeRealms(out _, out _, out _);
        var state = StateOf(dbe);

        Assert.That(state.PresentRealmSpatial.Length, Is.EqualTo(3), "all three realms hold clusters of this archetype after the fence");
        Assert.That(DatabaseEngine.CountRunnableRealms(state.PresentRealmSpatial, dbe.RealmTable), Is.EqualTo(3),
            "nothing is dormant yet: a Sleep realm holds SleepAfterTicks before it sleeps");

        Dispatched(dbe, 8, parallel: true);

        Assert.That(dbe.Realms.StateOf(new RealmId(1)), Is.EqualTo(RealmRunState.Dormant), "the premise of the next assertion");
        Assert.That(state.PresentRealmSpatial.Length, Is.EqualTo(3),
            "a dormant realm still HOLDS its clusters — the present count is what stops the row count being read as the realm count");
        Assert.That(DatabaseEngine.CountRunnableRealms(state.PresentRealmSpatial, dbe.RealmTable), Is.EqualTo(2),
            "realm 1 is dormant, so it gets no row; realms 0 and 2 do");
        Assert.That(DatabaseEngine.RealmGetsATelemetryRow(state.RealmSpatial[1], dbe.RealmTable), Is.False, "specifically realm 1");
        Assert.That(DatabaseEngine.RealmGetsATelemetryRow(state.RealmSpatial[0], dbe.RealmTable), Is.True);
        Assert.That(DatabaseEngine.RealmGetsATelemetryRow(state.RealmSpatial[2], dbe.RealmTable), Is.True);
    }

    /// <summary>
    /// #WB-05 — the per-realm record (kind 67) reaches the trace once per runnable realm, carrying that realm's own grid.
    /// </summary>
    /// <remarks>
    /// <c>[Category("TelemetryGated")]</c> because it needs the <c>Spatial</c> telemetry subtree on, and a subtree root defaults to off: the suite's
    /// <c>typhon.telemetry.json</c> enables the profiler but not this subtree, and enabling it there would turn on every spatial SPAN for every fixture.
    /// <c>TelemetryConfig</c> reads its configuration in a static constructor, before the first test, so no fixture can flip the flag — which is why this
    /// used to be <c>[Explicit] [Category("Manual")]</c> and therefore ran nowhere. The merge gate now runs it in a dedicated process with the flag set
    /// (<c>GATED_PASSES</c> in <c>bench/aws/shard.py</c>), and the category keeps it out of the parallel shards, where it would fail bare. Locally:
    /// <code>
    /// $env:TYPHON__PROFILER__SPATIAL__ENABLED = 'true'; dotnet test --filter "Category=TelemetryGated"
    /// </code>
    /// The wire LAYOUT is covered without the flag by
    /// <c>TypedDtoRoundTripTests.SpatialRealmTelemetry_DecodesTheDocumentedLayout_AndAShorterRecordStopsAtItsOwnSize</c>, and the emission PREDICATE by
    /// <see cref="TheRealmCensusCountsEveryPresentRealm_AndOnlyRunnableOnesGetARow"/>; what only this test covers is that the fence actually calls the
    /// emitter.
    /// </remarks>
    [Test]
    [Category("TelemetryGated")]
    public void EveryRunnableRealmSendsARow_OverTheWire()
    {
        using var observer = new TraceRingObserver(ResourceRegistry.Profiler, captureRawBytes: true);
        TyphonProfiler.AttachExporter(observer);
        TyphonProfiler.Start(ResourceRegistry.Profiler, TraceMetadata());
        try
        {
            using var dbe = ThreeRealms(out _, out _, out _);
            Dispatched(dbe, 8, parallel: true);
        }
        finally
        {
            TyphonProfiler.Stop();
            // DETACH, or the observer stays on the global exporter list after this test disposes it — and the NEXT test's Stop() drains into a disposed
            // BlockingCollection. Attachment is process-global and survives the fixture; `using` only disposes the observer, it does not unregister it.
            TyphonProfiler.DetachExporter(observer);
        }

        var archetypeId = Archetype<RealmUnit>.Metadata.ArchetypeId;
        var rows = new List<SpatialRealmTelemetryEventDto>();
        var census = new List<(int Present, int Runnable)>();
        foreach (var (kind, bytes) in observer.GetRecords())
        {
            if (kind == TraceEventKind.SpatialRealmTelemetry)
            {
                var dto = SpatialRealmTelemetryEventDto.Decode(bytes, 0, 1);
                if (dto.ArchetypeId == archetypeId)
                {
                    rows.Add(dto);
                }
            }
            else if (kind == TraceEventKind.SpatialArchetypeTelemetry)
            {
                var dto = SpatialArchetypeTelemetryEventDto.Decode(bytes, 0, 1);
                if (dto.ArchetypeId == archetypeId)
                {
                    census.Add((dto.PresentRealms, dto.RunnableRealms));
                }
            }
        }

        Assert.That(rows, Is.Not.Empty, $"records seen: {observer.RecordsProcessed}; is TYPHON__PROFILER__SPATIAL__ENABLED set?");
        Assert.That(census, Is.Not.Empty, "the archetype record carries the census on every tick");
        Assert.That(census.Select(c => c.Present).Distinct(), Is.EqualTo(new[] { 3 }), "three realms hold clusters for the whole run");
        Assert.That(census.Any(c => c.Runnable < c.Present), Is.True, "realm 1 sleeps within eight ticks — otherwise this proves nothing about the filter");
        Assert.That(rows.Select(r => r.RunState), Has.No.Member((byte)RealmRunState.Dormant), "a dormant realm sends no row");
        Assert.That(rows.Select(r => r.RealmId).Distinct(), Is.SupersetOf(new ushort[] { 0, 2 }));
        Assert.That(rows.All(r => r.CellSize > 0), Is.True, "every row carries its realm's own grid");
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [VerifiesRule("RLM-03")]
    [VerifiesRule("RLM-04")]
    public void DormantRealm_ZeroClustersDispatched(bool parallel, bool tierSystem)
    {
        using var dbe = ThreeRealms(out var in0, out var in1, out var in2);
        if (tierSystem)
        {
            // Every entity's cell, in every realm, in Tier0: the tier index, not the runnable list, is what this system selects from.
            foreach (var realm in dbe.RealmTable.Registered)
            {
                foreach (var p in (ReadOnlySpan<float>)[5f, 55f, 95f])
                {
                    realm.Grid.SetCellTier(realm.Grid.WorldToCellKey(p, p, 0), SimTier.Tier0);
                }
            }
        }

        var perTick = Dispatched(dbe, 8, parallel, tierSystem ? SimTier.Tier0 : SimTier.All);

        var ticks = perTick.Keys.OrderBy(t => t).ToArray();
        var first = perTick[ticks[0]];
        Assert.That(first, Is.SupersetOf(in1), "a Sleep realm holds SleepAfterTicks before going dormant: its entities run at first");
        var last = perTick[ticks[^1]];
        Assert.That(last, Is.EquivalentTo(in0.Concat(in2)), "once dormant, realm 1's clusters reach no system; realms 0 and 2 still run");
        Assert.That(dbe.Realms.StateOf(new RealmId(1)), Is.EqualTo(RealmRunState.Dormant));
        Assert.That(dbe.Realms.StateOf(new RealmId(0)), Is.EqualTo(RealmRunState.Simulated));
        Assert.That(StateOf(dbe).RealmDispatch.ExcludedCount, Is.GreaterThan(0));
    }

    [Test]
    public void Observer_KeepsTheRealmActive()
    {
        using var dbe = ThreeRealms(out var in0, out var in1, out var in2);
        using var pin = dbe.Realms.Observe(new RealmId(1));
        var perTick = Dispatched(dbe, 8, true);

        foreach (var (_, ids) in perTick)
        {
            Assert.That(ids, Is.SupersetOf(in1), "an observed realm is dispatched every tick");
        }

        Assert.That(dbe.Realms.StateOf(new RealmId(1)), Is.EqualTo(RealmRunState.Active));
        Assert.That(StateOf(dbe).RealmDispatch, Is.Null, "no realm ever dormant: the runnable index was never needed (RLM-04)");
    }

    [Test]
    [VerifiesRule("RLM-03")]
    public void AllRealmsRunnable_NothingIsFiltered()
    {
        using var dbe = ThreeRealms(out var in0, out var in1, out var in2);
        dbe.Realms.Wake(new RealmId(1));
        var table = dbe.RealmTable;
        table.EvaluatePolicy();
        Assert.That((table.NonRunnableCount, table.StateOf(1)), Is.EqualTo((0, RealmRunState.Simulated)));

        // An entry-free realm with no pin sleeps after its hold — counted in evaluations, one per tick (RLM-03).
        for (var i = 0; i < SleepAfter; i++)
        {
            table.EvaluatePolicy();
            Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Simulated), $"still holding after {i + 2} evaluations");
        }

        table.EvaluatePolicy();
        Assert.That((table.NonRunnableCount, table.StateOf(1)), Is.EqualTo((1, RealmRunState.Dormant)));
    }

    [Test]
    [VerifiesRule("RLM-03")]
    public void Unregister_ClosesAtOnce_ButThePolicyStateMovesOnlyAtTheNextEvaluation()
    {
        // Review #4: MarkClosing wrote the state mid-tick (two systems of one tick could see different divisors) and raced the evaluation.
        using var dbe = ThreeRealms(out _, out _, out _);
        var table = dbe.RealmTable;
        table.EvaluatePolicy();
        var before = table.StateOf(2);
        dbe.Realms.Unregister(new RealmId(2));
        Assert.That(table.Get(2).Closing, Is.True, "entries are refused at once");
        Assert.That(table.StateOf(2), Is.EqualTo(before), "the tick's decided state holds until the next evaluation");
        table.EvaluatePolicy();
        Assert.That((table.StateOf(2), table.DivisorOf(2)), Is.EqualTo((RealmRunState.Closing, 1)));
        Assert.That(dbe.Realms.Counts.Closing, Is.EqualTo(1));
    }

    [Test]
    public void AnObserverOfARemovedRealm_ReleasesWithoutThrowing()
    {
        using var dbe = ThreeRealms(out _, out _, out var in2);
        var pin = dbe.Realms.Observe(new RealmId(2));
        dbe.Realms.Unregister(new RealmId(2));
        using (var tx = dbe.CreateQuickTransaction())
        {
            dbe.Realms.DestroyContents(new RealmId(2), tx);
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.That(dbe.RealmTable.IsRegistered(2), Is.False);
        Assert.DoesNotThrow(() => pin.Dispose(), "review #4: the removal zeroed the count and the release went negative");
    }

    /// <summary>Tick start's job without a runtime: evaluate the policy, then bring the archetype's runnable index up to date.</summary>
    private static void EvaluateAndIndex(DatabaseEngine dbe)
    {
        dbe.RealmTable.EvaluatePolicy();
        var cs = StateOf(dbe);
        cs.RealmDispatch ??= new RealmDispatchIndex();
        cs.RealmDispatch.Update(cs, dbe.RealmTable);
    }

    [Test]
    [VerifiesRule("DM-04")]
    public void DormantRealm_WrittenEntity_MigratedAndIndexed()
    {
        // Dormancy freezes elective work only: a write to an entity of a dormant realm is still detected, migrated and indexed at the fence.
        using var dbe = ThreeRealms(out _, out var in1, out _);
        for (var i = 0; i <= SleepAfter; i++)
        {
            EvaluateAndIndex(dbe);
        }

        Assert.That(dbe.RealmTable.StateOf(1), Is.EqualTo(RealmRunState.Dormant));
        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var pos = ref tx.OpenMut(in1[0]).Write(RealmUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 85, MinY = 15, MaxX = 85, MaxY = 15 };
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var near = new AABB2F { MinX = 80, MinY = 10, MaxX = 90, MaxY = 20 };
        Assert.That(dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(1)).AABB(near).Count(), Is.EqualTo(1), "moved across cells and indexed in its realm");
    }

    [Test]
    [VerifiesRule("DM-04")]
    public void DormantRealm_ClustersNeitherSleepNorHeartbeat()
    {
        // A dormant realm's clusters are frozen by the sweep; a runnable realm's go to sleep as usual.
        using var dbe = ThreeRealms(out _, out _, out _);
        var cs = StateOf(dbe);
        cs.SleepThresholdTicks = 3;
        cs.HeartbeatIntervalTicks = 4;
        for (var i = 0; i <= SleepAfter; i++)
        {
            EvaluateAndIndex(dbe);
        }

        for (var tick = 2; tick < 40; tick++)
        {
            dbe.WriteTickFence(tick);
        }

        var active = cs.ReadActiveClusterList(out var count);
        for (var i = 0; i < count; i++)
        {
            var chunkId = active[i];
            var realm = cs.ClusterRealmMap[chunkId];
            if (realm == 1)
            {
                Assert.That((cs.SleepStates[chunkId], cs.SleepCounters[chunkId]), Is.EqualTo((ClusterSleepState.Active, (ushort)0)),
                    "a dormant realm's cluster: no counter advance, so no sleep and no heartbeat");
            }
            else
            {
                Assert.That(cs.SleepStates[chunkId], Is.Not.EqualTo(ClusterSleepState.Active), $"a runnable realm's idle cluster sleeps (realm {realm})");
            }
        }
    }

    [Test]
    public void Counts_AndOccupancy_SpanEveryRealm()
    {
        using var dbe = ThreeRealms(out _, out _, out _);
        for (var i = 0; i <= SleepAfter; i++)
        {
            dbe.RealmTable.EvaluatePolicy();
        }

        var counts = dbe.Realms.Counts;
        Assert.That((counts.Simulated, counts.Dormant, counts.Active, counts.Closing), Is.EqualTo((2, 1, 0, 0)));

        // Realms D6: the engine-wide occupancy is every realm's grid summed; each realm's is its own.
        var total = dbe.GetSpatialGridOccupancy();
        var perRealm = new[] { 0, 1, 2 }.Select(r => dbe.GetSpatialGridOccupancy(new RealmId((ushort)r))).ToArray();
        Assert.That(total.OccupiedCellCount, Is.EqualTo(perRealm.Sum(o => o.OccupiedCellCount)).And.GreaterThan(perRealm[0].OccupiedCellCount));
        Assert.That(total.ResidentBytes, Is.EqualTo(perRealm.Sum(o => o.ResidentBytes)));
        Assert.That(dbe.GetSpatialGridOccupancy(new RealmId(7)), Is.EqualTo(default(SpatialGridOccupancy)), "an unregistered realm reads empty");
    }

    [Test]
    public void AnEntry_WakesADormantRealm_WhichSleepsAgainAfterItsHold()
    {
        using var dbe = ThreeRealms(out var in0, out _, out _);
        var table = dbe.RealmTable;
        for (var i = 0; i <= SleepAfter; i++)
        {
            table.EvaluatePolicy();
        }

        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Dormant));
        var epoch = table.PolicyEpoch;

        // A teleport into the dormant realm: the fence's migration is the entry, and it wakes the realm from the next evaluation on.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(in0[0], RealmUnit.Pos, new RealmId(1), At(50, 50, 1));
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Dormant), "the policy moves at tick start, never mid-tick");
        table.EvaluatePolicy();
        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Simulated), "the entry woke it");
        Assert.That(table.PolicyEpoch, Is.Not.EqualTo(epoch));

        for (var i = 0; i < SleepAfter; i++)
        {
            table.EvaluatePolicy();
        }

        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Simulated), "the hold restarts at the entry");
        table.EvaluatePolicy();
        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Dormant));
    }

    [Test]
    [VerifiesRule("RLM-04")]
    public void ChangeFilter_DirtyInDormantRealm_NotDelivered()
    {
        using var dbe = ThreeRealms(out var in0, out var in1, out _);
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<RealmUnit>().ToView();
        var delivered = new ConcurrentDictionary<long, ConcurrentBag<EntityId>>();
        var ticksSeen = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");

            // Writes one entity of realm 0 and one of realm 1 every tick, through the tick transaction: a callback is not realm-filtered.
            dag.CallbackSystem("Write", ctx =>
            {
                ctx.Transaction.OpenMut(in0[0]).Write(RealmUnit.Pos).Tag++;
                ctx.Transaction.OpenMut(in1[0]).Write(RealmUnit.Pos).Tag++;
                Interlocked.Increment(ref ticksSeen);
            });
            dag.QuerySystem("Reactive", ctx =>
            {
                var bag = delivered.GetOrAdd(ctx.TickNumber, _ => []);
                foreach (var id in ctx.Entities)
                {
                    bag.Add(id);
                }
            }, input: () => view, parallel: true, changeFilter: [typeof(RealmPos)], after: "Write");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 8, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        var ticks = delivered.Keys.OrderBy(t => t).ToArray();
        var early = ticks.Where(t => delivered[t].Contains(in1[0])).ToArray();
        Assert.That(early, Is.Not.Empty, "while the realm held, its changes were delivered");
        var last = delivered[ticks[^1]].ToHashSet();
        Assert.That(last, Does.Contain(in0[0]));
        Assert.That(last, Does.Not.Contain(in1[0]), "a change in a dormant realm reaches no change-filtered system");
    }
}
