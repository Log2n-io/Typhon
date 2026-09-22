using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// A spatial predicate answers at the reader's snapshot, exactly as the scan path does (#899).
/// </summary>
/// <remarks>
/// <para>
/// <b>The asymmetry this exists for.</b> The cluster SoA scan gates every entity it emits through <c>IsVisibleAtSnapshot</c>, and its own comment says why:
/// the scan walks CURRENT occupancy and reads the committed HEAD, neither of which knows about the reader's snapshot, so without the gate an entity
/// committed AFTER the snapshot is emitted — the phantom read <c>claude/overview/04-data.md</c> "Isolation guarantees" says the fixed snapshot prevents.
/// The spatial collectors tested only the archetype routing mask, which knows nothing about TSNs.
/// </para>
/// <para>
/// <b>Why this is a cardinality assertion over a Versioned shape.</b> Snapshot isolation is promised by <c>Versioned</c> alone — the storage-mode matrix is
/// explicit that <c>SingleVersion</c> and <c>Transient</c> offer none — so the cells here are the two the kit builds that carry a Versioned
/// <c>[SpatialIndex]</c> field. Running the SV shapes would assert a guarantee they do not make.
/// </para>
/// <para>
/// <b>It is a disagreement between two paths in ONE transaction, which is what makes it a defect rather than a preference.</b> The same reader's
/// <c>WhereField</c> scan already hides the late commit. A spatial predicate that shows it means two predicates in one transaction disagree about which
/// entities exist.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class SpatialSnapshotIsolationTests : TestBase<SpatialSnapshotIsolationTests>
{
    private const int EntityCount = 40;

    /// <summary>Wide enough that every seeded entity and every late one falls inside the box, so the count is the whole population.</summary>
    private const float Max = 10_000f;

    private long _tick;

    [SetUp]
    public void ResetTick() => _tick = 0;

    /// <summary>The spatial compositions whose indexed component is <c>Versioned</c>, and so promise snapshot isolation.</summary>
    public static IEnumerable<TestCaseData> VersionedSpatialCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsSpatial(c) && c.Reopen == ReopenKind.None
            && c.Shape is StorageShape.PureVersioned or StorageShape.VerPlusTransient);

    private DatabaseEngine Open(Cell cell)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe, cell);   // also configures the engine-wide grid — it must precede InitializeArchetypes
        dbe.InitializeArchetypes();
        return dbe;
    }

    private EntityId[] Seed(DatabaseEngine dbe, Cell cell, int count)
    {
        var ids = new EntityId[count];
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < count; i++)
            {
                ids[i] = AxisArchetypes.Spawn(t, cell, i);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        dbe.WriteTickFence(++_tick);
        return ids;
    }

    /// <summary>
    /// An entity committed after the reader's snapshot must not appear in that reader's spatial query.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(VersionedSpatialCells))]
    [VerifiesRule("SQ-07")]
    public void ASpatialQueryDoesNotSeeEntitiesCommittedAfterItsSnapshot(Cell cell)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var txRead = dbe.CreateQuickTransaction();
        var before = AxisArchetypes.QueryInBox(txRead, cell, Max, QueryTerminal.Execute);
        Assert.That(before, Is.EqualTo(AxisArchetypes.ExpectedInBox(EntityCount, Max)),
            $"{cell}: precondition — the reader must see its own seeded population before anything else commits");

        // Committed AFTER txRead took its snapshot, and inside the query box. The fence publishes them to the spatial index.
        using (var txWrite = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = EntityCount; i < EntityCount + 5; i++)
            {
                AxisArchetypes.Spawn(txWrite, cell, i);
            }

            Assert.That(txWrite.Commit(), Is.True, $"{cell}: late spawn commit");
        }

        dbe.WriteTickFence(++_tick);

        // A reader created AFTER the commit must see all 45. Without this, "the old reader still sees 40" would be satisfied just as well by five entities
        // that never landed inside the box or never reached the index — the assertion would hold for the wrong reason, and the case would pin nothing.
        using (var txFresh = dbe.CreateQuickTransaction())
        {
            Assert.That(AxisArchetypes.QueryInBox(txFresh, cell, Max, QueryTerminal.Execute),
                Is.EqualTo(AxisArchetypes.ExpectedInBox(EntityCount + 5, Max)),
                $"{cell}: precondition — the late entities must be inside the query box and published, or the assertion below is vacuous");
        }

        var after = AxisArchetypes.QueryInBox(txRead, cell, Max, QueryTerminal.Execute);
        Assert.That(after, Is.EqualTo(before),
            $"{cell}: the spatial query surfaced {after - before} entities committed after this reader's snapshot — a phantom read, which the same "
            + "transaction's scan path already hides, so two predicates in one transaction disagree about which entities exist");
    }

    /// <summary>
    /// The ray and frustum shapes hide a late commit too — the two collectors whose hits carry no cluster id, so they gate per entity.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these two need their own case at all.</b> The box test above drives <c>QueryAabb</c>, whose hits carry a <c>ClusterChunkId</c> and so
    /// take the per-cluster shortcut. Ray and frustum return bare entity ids and must probe the EntityMap per hit — a different branch, and one the suite
    /// could not reach before: every existing ray/frustum fixture runs on <c>RetirePos3</c>, which is <c>SingleVersion</c>, so the gate is a no-op there and
    /// an un-wired collector would have passed all of them.</para>
    /// <para><b>The second assertion is what stops this being vacuous.</b> "The old reader does not see the late entity" is satisfied just as well by a
    /// query whose geometry misses it entirely. So a FRESH transaction runs the identical query and must see it: that proves the shape reaches the entity,
    /// which turns the first assertion into a statement about visibility rather than about aim.</para>
    /// </remarks>
    [Test]
    [TestCaseSource(nameof(VersionedSpatialCells))]
    [VerifiesRule("SQ-07")]
    public void RayAndFrustumAlsoHideEntitiesCommittedAfterTheSnapshot(Cell cell)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var txRead = dbe.CreateQuickTransaction();

        // Entity `late` is a POINT box on the lattice diagonal, committed after txRead's snapshot.
        const int Late = EntityCount;
        var p = Late * AxisArchetypes.Spacing;

        EntityId lateId;
        using (var txWrite = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            lateId = AxisArchetypes.Spawn(txWrite, cell, Late);
            Assert.That(txWrite.Commit(), Is.True, $"{cell}: late spawn commit");
        }

        dbe.WriteTickFence(++_tick);

        // A ray fired straight down the lattice diagonal — every entity is a POINT box at (i·Spacing, i·Spacing, i·Spacing), so the diagonal from just
        // outside the origin passes exactly through all of them — and a frustum that is the whole world as six axis-aligned planes.
        // Dispatched on the shape exactly as AxisArchetypes.QueryInBox is: the two Versioned spatial cells live on DIFFERENT archetypes, and a hardcoded
        // one queries an archetype the other cell never populated — which returns empty and would have made both assertions below vacuous. The
        // preconditions caught precisely that.
        HashSet<long> Ray(Transaction t, double target)
        {
            const double Inv = 0.577350269189625764509d;   // 1/sqrt(3): WhereRay takes a unit direction
            var maxDist = (target * 2d) + 100d;
            return cell.Shape == StorageShape.PureVersioned
                ? RawIds(t.Query<AxSpPureVer>().WhereRay<AxVerSpatial>(-1d, -1d, -1d, Inv, Inv, Inv, maxDist).Execute())
                : RawIds(t.Query<AxSpVerTr>().WhereRay<AxVerSpatial>(-1d, -1d, -1d, Inv, Inv, Inv, maxDist).Execute());
        }

        HashSet<long> Frustum(Transaction t)
        {
            // Six inward half-spaces of the box [-1, 1e6]: (nx, ny, nz, d) with the plane's inside being nx*x + ny*y + nz*z + d >= 0.
            double[] planes =
            [
                1, 0, 0, 1, -1, 0, 0, 1_000_000,
                0, 1, 0, 1, 0, -1, 0, 1_000_000,
                0, 0, 1, 1, 0, 0, -1, 1_000_000,
            ];
            return cell.Shape == StorageShape.PureVersioned
                ? RawIds(t.Query<AxSpPureVer>().WhereFrustum<AxVerSpatial>(planes, 6, -1d, -1d, -1d, 1_000_000d, 1_000_000d, 1_000_000d).Execute())
                : RawIds(t.Query<AxSpVerTr>().WhereFrustum<AxVerSpatial>(planes, 6, -1d, -1d, -1d, 1_000_000d, 1_000_000d, 1_000_000d).Execute());
        }

        // A FRESH reader proves both shapes actually REACH the late entity, so the assertions below are about visibility and not about aim. Without these
        // two, a query whose geometry simply misses it would satisfy the real assertions and the case would pin nothing.
        var lateRaw = (long)lateId.RawValue;
        using (var txFresh = dbe.CreateQuickTransaction())
        {
            var freshRay = Ray(txFresh, p);
            var freshFrustum = Frustum(txFresh);

            Assert.Multiple(() =>
            {
                Assert.That(freshFrustum, Does.Contain(lateRaw),
                    $"{cell}: precondition — a reader created after the commit must see the late entity through the frustum");
                Assert.That(freshRay, Does.Contain(lateRaw),
                    $"{cell}: precondition — the ray must actually reach the late entity, or the ray assertion below is vacuous");
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(Frustum(txRead), Does.Not.Contain(lateRaw),
                $"{cell}: the frustum surfaced entity {lateRaw}, committed after this reader's snapshot");
            Assert.That(Ray(txRead, p), Does.Not.Contain(lateRaw),
                $"{cell}: the ray surfaced entity {lateRaw}, committed after this reader's snapshot");
        });
    }

    private static HashSet<long> RawIds(IEnumerable<EntityId> ids)
    {
        var set = new HashSet<long>();
        foreach (var id in ids)
        {
            set.Add((long)id.RawValue);
        }

        return set;
    }
}
