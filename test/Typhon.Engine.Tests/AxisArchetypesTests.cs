using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The shape kit's own tests (#704 AC3), and the first fixture in the suite that spawns and reads back an entity on EVERY storage shape.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs. First, prove the kit works: every composition it claims to support must register, spawn and read back, because ~45 converted fixtures will trust
/// that it does and a broken composition would surface as a confusing failure inside whichever fixture happened to hit it. Second, keep the kit's GAPS
/// visible — <see cref="Kit_CoverageOfTheValidMatrix_IsReported"/> counts the valid cells the kit cannot yet build, so an unbuilt region is a number that can
/// only shrink rather than a silence that reads as coverage.
/// </para>
/// <para>
/// The round-trip test below is deliberately the plainest thing the engine can do — spawn N entities, read them back, check every field. That it had never
/// been run across the storage-mode axis before is the measurement this epic rests on.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class AxisArchetypesTests : TestBase<AxisArchetypesTests>
{
    // 64 entities spans more than one cluster at any ClusterSize in [8,64], so the cluster-placement paths (ClaimSlot, the EntityMap rewrite) are genuinely
    // exercised rather than degenerating to "slot 0 is the only correct answer" — the tautology SchemaEvolutionMatrixTests documents at its own :54.
    private const int EntityCount = 64;

    /// <summary>Reopen cells need WAL segments that outlive an engine dispose; the base class defaults to an in-memory backend that does not.</summary>
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    /// <summary>Cells with no reopen — the kit's core contract, and the fastest signal that a composition is broken.</summary>
    public static IEnumerable<TestCaseData> InSessionCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None);

    /// <summary>
    /// Cells that cross a reopen AND whose values the storage contract promises will still be there. A clean close persists everything; a hard crash keeps
    /// SingleVersion values only under <see cref="DurabilityDiscipline.Commit"/> — see <see cref="AxisArchetypes.SvValuesAreCrashDurable"/>.
    /// </summary>
    public static IEnumerable<TestCaseData> DurableReopenCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c)
            && (c.Reopen == ReopenKind.Clean || (c.Reopen == ReopenKind.Crash && AxisArchetypes.SvValuesAreCrashDurable(c))));

    /// <summary>
    /// The other side of that contract: SingleVersion values written under TickFence and then hard-crashed. The entities must survive — lifecycle records are
    /// durable — while the values are legitimately gone. Excludes the unique-index cells, which are #710.
    /// </summary>
    public static IEnumerable<TestCaseData> TickFenceCrashLossCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c)
            && c.Reopen == ReopenKind.Crash
            && !AxisArchetypes.SvValuesAreCrashDurable(c)
            && c.Index != IndexShape.Unique);

    [Test]
    [TestCaseSource(nameof(InSessionCells))]
    public void EveryCell_SpawnsAndReadsBack(Cell cell)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe, cell);
        dbe.InitializeArchetypes();

        var ids = new EntityId[EntityCount];
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < EntityCount; i++)
            {
                ids[i] = AxisArchetypes.Spawn(t, cell, i);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        using var read = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            AxisArchetypes.AssertRoundTrip(read, cell, ids[i], i);
        }
    }

    [Test]
    [TestCaseSource(nameof(DurableReopenCells))]
    public void EveryDurableCell_SurvivesItsReopen(Cell cell)
    {
        var ids = SeedAndReopen(cell);

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe2, cell);
        dbe2.InitializeArchetypes();

        using var read = dbe2.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            // includeTransient: false — a Transient neighbour is heap-only and is SUPPOSED to be gone here. The durable members are still asserted.
            AxisArchetypes.AssertRoundTrip(read, cell, ids[i], i, includeTransient: false);
        }
    }

    /// <summary>
    /// The documented SingleVersion loss, asserted rather than assumed. A hard crash under TickFence keeps every ENTITY (lifecycle records are durable) and
    /// loses its SV VALUES — that is what the discipline knob is for.
    /// </summary>
    /// <remarks>
    /// This test exists because the loss looked like a bug the first time the matrix ran it, and separating "contract" from "defect" is what made #710
    /// visible. Pinning the contract means the next person does not have to re-derive that distinction — and if the engine ever starts preserving these
    /// values, this test fails and says so, which is the right way round.
    /// </remarks>
    [Test]
    [TestCaseSource(nameof(TickFenceCrashLossCells))]
    public void TickFenceSingleVersion_LosesValuesButKeepsEntities_AcrossAHardCrash(Cell cell)
    {
        var ids = SeedAndReopen(cell);

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe2, cell);
        dbe2.InitializeArchetypes();

        using var read = dbe2.CreateQuickTransaction();
        var live = AxisArchetypes.Dispatch(cell, read, new CountVisitor());

        Assert.That(live, Is.EqualTo(ids.Length),
            $"{cell}: every entity must survive the crash — the lifecycle records ARE durable, only the SV values are not");
    }

    /// <summary>
    /// The value half of the same contract, on one concrete cell so the component can be read through its static type: the SV values do not merely differ
    /// after the crash, they read back ZEROED. Asserting the specific value rather than "not what we wrote" is what stops this passing on a corruption.
    /// </summary>
    [Test]
    public void TickFenceSingleVersion_ReadsBackZeroed_AfterAHardCrash()
    {
        var cell = new Cell(StorageShape.PureSv, DurabilityMode.Immediate, IndexShape.None, ReopenKind.Crash);
        var ids = SeedAndReopen(cell);

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe2, cell);
        dbe2.InitializeArchetypes();

        using var read = dbe2.CreateQuickTransaction();
        var v = read.Open(ids[0]).Read(AxPureSvNone.P);

        Assert.Multiple(() =>
        {
            Assert.That(v.Key, Is.Zero, "a TickFence SV int reads back zeroed after a hard crash");
            Assert.That(v.Tag, Is.Zero, "…and so does the long");
            Assert.That(v.Weight, Is.Zero, "…and the float");
        });
    }

    /// <summary>
    /// #710 — a hard crash on an SV archetype with a UNIQUE index leaves the database permanently unopenable: every surviving entity reads Key == 0, and
    /// rebuilding a unique index over 64 identical keys throws out of <c>InitializeArchetypes</c>. Quarantined, not deleted: when #710 is fixed this is the
    /// regression lock, and it fails today for exactly the documented reason.
    /// </summary>
    [Test]
    [Category("Quarantine")]
    public void SvWithUniqueIndex_AfterHardCrash_StillOpens()
    {
        var cell = new Cell(StorageShape.PureSv, DurabilityMode.Immediate, IndexShape.Unique, ReopenKind.Crash);
        var ids = SeedAndReopen(cell);

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe2, cell);

        // #710: this throws UniqueConstraintViolationException from RebuildIndexesFromData. The values are legitimately lost (TickFence), but an unopenable
        // database is not a documented consequence of losing them.
        Assert.DoesNotThrow(() => dbe2.InitializeArchetypes(), $"{cell}: #710 — the database must open even when the rebuild's source data was lost");

        using var read = dbe2.CreateQuickTransaction();
        Assert.That(AxisArchetypes.Dispatch(cell, read, new CountVisitor()), Is.EqualTo(ids.Length));
    }

    private sealed class CountVisitor : ICellVisitor<Transaction, int>
    {
        public int Visit<TArch>(Transaction t) where TArch : Archetype<TArch> => t.Query<TArch>().Count();
    }

    /// <summary>Seeds <see cref="EntityCount"/> entities in one session and closes it the way the cell's <see cref="ReopenKind"/> asks for.</summary>
    private EntityId[] SeedAndReopen(Cell cell)
    {
        var ids = new EntityId[EntityCount];

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            AxisArchetypes.Register(dbe, cell);
            dbe.InitializeArchetypes();

            // The explicit UoW form, not CreateQuickTransaction: Flush() lives on the UnitOfWork, and under DurabilityMode.Deferred nothing is durable without
            // it — a crash cell would then legitimately find an empty database and the test would be asserting the wrong thing.
            using (var uow = dbe.CreateUnitOfWork(cell.Durability))
            {
                using (var t = uow.CreateTransaction(cell.Discipline))
                {
                    for (var i = 0; i < EntityCount; i++)
                    {
                        ids[i] = AxisArchetypes.Spawn(t, cell, i);
                    }

                    Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
                }

                uow.Flush();
            }

            if (cell.Reopen == ReopenKind.Crash)
            {
                dbe.SimulateHardCrash();
            }
        }

        return ids;
    }

    // ── The kit's contract with its callers ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void UnsupportedCell_Throws_RatherThanSkipping()
    {
        // A cell the kit has no composition for. Both payload carriers are built now, but only at Index=None and not crossed with each other — see
        // AxisArchetypes.SupportsCollection / SupportsSpatial for why. A collection carrier PLUS a unique index is therefore still a KIT gap rather than an
        // engine impossibility, which is exactly the distinction this test pins.
        var gap = new Cell(StorageShape.PureSv, DurabilityMode.Deferred, IndexShape.Unique, ReopenKind.None, Collection: CollectionShape.Present);
        Assert.That(EngineAxes.IsValid(gap), Is.True, "precondition: the engine can express this cell; only the kit cannot build it");
        Assert.That(AxisArchetypes.SupportsCollection(gap), Is.False, "precondition: the kit's collection carrier is Index=None only");

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        var ex = Assert.Throws<NotSupportedException>(() => AxisArchetypes.Register(dbe, gap));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain(gap.ToString()), "the message must name the cell — otherwise the caller cannot tell which one it was");
            Assert.That(ex.Message, Does.Contain("Supports"), "the message must say how to narrow");
            Assert.That(ex.Message, Does.Contain("counts as coverage in the test count"), "the message must say WHY skipping is not the alternative");
        });
    }

    [Test]
    public void ImpossibleCell_Throws_AndSaysTheEngineCannotExpressIt()
    {
        // Distinct from the case above: this one is rejected by EngineAxes.IsValid, so no amount of work on the kit would make it buildable. The two must not
        // report the same reason, or a genuine engine constraint reads as a to-do.
        var impossible = new Cell(StorageShape.PureTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.Clean);

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        var ex = Assert.Throws<NotSupportedException>(() => AxisArchetypes.Register(dbe, impossible));
        Assert.That(ex.Message, Does.Contain("cannot express this combination at all"));
    }

    /// <summary>
    /// The kit's coverage of the valid matrix, reported as a number rather than left implicit. A cell the kit cannot build is a cell no converted fixture
    /// tests, and the whole point of #704 is that untested regions must be counted rather than assumed away.
    /// </summary>
    [Test]
    public void Kit_CoverageOfTheValidMatrix_IsReported()
    {
        var valid = 0;
        var supported = 0;
        var gapsByReason = new SortedDictionary<string, int>();

        foreach (StorageShape shape in Enum.GetValues(typeof(StorageShape)))
        {
            foreach (var dur in new[] { DurabilityMode.Deferred, DurabilityMode.GroupCommit, DurabilityMode.Immediate })
            {
                foreach (IndexShape ix in Enum.GetValues(typeof(IndexShape)))
                {
                    foreach (ReopenKind reopen in Enum.GetValues(typeof(ReopenKind)))
                    {
                        foreach (var disc in new[] { DurabilityDiscipline.TickFence, DurabilityDiscipline.Commit })
                        {
                            foreach (CollectionShape coll in Enum.GetValues(typeof(CollectionShape)))
                            {
                                foreach (SpatialShape sp in Enum.GetValues(typeof(SpatialShape)))
                                {
                                    var cell = new Cell(shape, dur, ix, reopen, disc, coll, sp);
                                    if (!EngineAxes.IsValid(cell))
                                    {
                                        continue;
                                    }

                                    valid++;
                                    if (AxisArchetypes.Supports(cell))
                                    {
                                        supported++;
                                        continue;
                                    }

                                    var reason = coll != CollectionShape.None && sp != SpatialShape.None ? "collection+spatial"
                                        : coll != CollectionShape.None ? "collection"
                                        : "spatial";
                                    gapsByReason.TryGetValue(reason, out var n);
                                    gapsByReason[reason] = n + 1;
                                }
                            }
                        }
                    }
                }
            }
        }

        TestContext.Out.WriteLine($"AxisArchetypes kit coverage: {supported}/{valid} valid cells ({100.0 * supported / valid:F1} %)");
        foreach (var (reason, n) in gapsByReason)
        {
            TestContext.Out.WriteLine($"  gap — {reason}: {n} cells");
        }

        // A ratchet, not a target. It fails if the kit LOSES coverage; widening it is always allowed and only makes the assertion more comfortable. The
        // floor is set just under the value measured when the kit landed, so a composition deleted by accident is loud.
        // Ratchet, raised as carriers land: 280 with the storage/index compositions alone, 390 with the spatial carrier, 485 with the collection carrier.
        // The floor trails the measured value slightly so an unrelated IsValid change does not trip it, and it only ever goes UP.
        //
        // The residual 672 cells are the payload axes crossed with the INDEX kind (192 + 192) and with each other (288). Both narrowings are stated at
        // AxisArchetypes.SupportsSpatial / SupportsCollection: no spatial or collection defect on record implicates the index flavour, so building those
        // would triple the archetype count for a dimension nothing points at. They are a counted decision, not an oversight.
        Assert.That(supported, Is.GreaterThanOrEqualTo(485),
            "the kit lost coverage of the valid matrix — a composition was removed or Supports() narrowed. Widening is fine; shrinking silently is not");
    }

    [Test]
    public void Payload_IsDistinctiveOnEveryFieldAndBetweenFields()
    {
        // The property SchemaEvolutionMatrixTests learned the hard way: with an undistinctive payload a slot mix-up reads back as a plausible value instead of
        // as another entity's. Assert it once here so every converted fixture inherits the guarantee.
        var cell = new Cell(StorageShape.PureSv, DurabilityMode.Deferred, IndexShape.Unique, ReopenKind.None);

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe, cell);
        dbe.InitializeArchetypes();

        var keys = new HashSet<int>();
        var weights = new HashSet<float>();
        var tags = new HashSet<long>();
        var buckets = new HashSet<int>();

        using var t = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var id = AxisArchetypes.Spawn(t, cell, i);
            var v = t.Open(id).Read(AxPureSvUniq.P);
            keys.Add(v.Key);
            weights.Add(v.Weight);
            tags.Add(v.Tag);
            buckets.Add(v.Bucket);

            Assert.That((float)v.Key, Is.Not.EqualTo(v.Weight), $"entity {i}: Key and Weight must differ, or a field swap reads as correct");
            Assert.That(v.Key, Is.Not.EqualTo((int)v.Tag), $"entity {i}: Key and Tag must differ");
        }

        Assert.Multiple(() =>
        {
            Assert.That(keys, Has.Count.EqualTo(EntityCount), "Key must be unique per entity — it is also the unique index key");
            Assert.That(weights, Has.Count.EqualTo(EntityCount), "Weight must be unique per entity");
            Assert.That(tags, Has.Count.EqualTo(EntityCount), "Tag must be unique per entity");
            Assert.That(buckets, Has.Count.EqualTo(AxisArchetypes.BucketCount),
                "Bucket must REPEAT — an AllowMultiple index over distinct values never exercises the duplicate path");
        });
    }
}
