using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The cell-keyed broad phase: sessions sharing an interest cell resolve from one enlarged query and then filter it, instead of querying one each.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property under test is IDENTITY, not similarity.</b> The broad phase queries a larger disc than any of its members wants — the observer radius plus
/// half a cell diagonal — so every member must then narrow it back to exactly what a query at its own viewpoint would have returned. Anything else is a
/// silent behaviour change: an entity kept that should not be is bandwidth and a client seeing through walls, and an entity dropped that should not be is a
/// player who never appears. So these fixtures compare the two paths SLOT BY SLOT rather than comparing hit counts, which a compensating pair of errors would
/// satisfy.
/// </para>
/// <para>
/// <b>Both arms run on one binary.</b> <c>SubscriptionsOptions.CellKeyedInterest</c> switches the shape, which is what makes the comparison a comparison
/// rather than two builds.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CellKeyedInterestTests : TestBase<CellKeyedInterestTests>
{
    private const float Spacing = 7.5f;
    private const int Columns = 133;
    private const int CreatureCount = Columns * Columns;
    private const double Radius = 192d;

    /// <summary>The three-dimensional fixture's sheet: a square lattice, so the count inside any disc is computable rather than bounded.</summary>
    private const int FlyerColumns = 20;

    /// <summary>Metres between flyers in that lattice.</summary>
    private const float FlyerSpacing = 20f;

    /// <summary>How many flyers make up one sheet.</summary>
    private const int FlyerCount = FlyerColumns * FlyerColumns;
    private const double WorldEdge = Columns * Spacing;

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void DeclareSphere(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < CreatureCount; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i % Columns * Spacing, i / Columns * Spacing)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>How many grid points lie within a radius of a centre — the arithmetic answer both arms must reproduce exactly.</summary>
    private static int PointsWithin(double cx, double cy, double radius)
    {
        var inside = 0;
        var rsq = radius * radius;
        for (var i = 0; i < CreatureCount; i++)
        {
            var dx = (i % Columns * Spacing) - cx;
            var dy = (i / Columns * Spacing) - cy;
            if ((dx * dx) + (dy * dy) <= rsq)
            {
                inside++;
            }
        }

        return inside;
    }

    /// <summary>Runs one arm and returns each session's hit count, keyed by session.</summary>
    /// <remarks>
    /// Keyed by SESSION, not by index: the cell-keyed arm reorders this tick's sessions so that a cell's members are contiguous, so reading results by index
    /// would compare one arm's session against another's.
    /// </remarks>
    private Dictionary<uint, int> Resolve(bool cellKeyed, Vector3D[] viewpoints, string name, out SessionId[] sessions, out long sessionsShared)
    {
        var dbe = SetupEngine();
        Populate(dbe);

        var options = new SubscriptionsOptions { CellKeyedInterest = cellKeyed };
        var harness = InterestHarness.Create(dbe, DeclareSphere, name, options);
        try
        {
            sessions = harness.OpenSessions(viewpoints.Length, "near");
            harness.RunPass(1);
            harness.CreateRequestedBlocks();

            for (var i = 0; i < viewpoints.Length; i++)
            {
                Assert.That(harness.Sessions.SetViewpoint(sessions[i], viewpoints[i]), Is.True);
            }

            harness.RunPass(2);
            harness.CreateRequestedBlocks();
            harness.RunPass(3);

            var hits = new Dictionary<uint, int>();
            for (var i = 0; i < harness.Interest.TickSessionCount; i++)
            {
                hits[harness.Interest.SessionAt(i).Value] = harness.Interest.HitsCountOf(i);
            }

            sessionsShared = harness.Interest.SessionsShared;
            return hits;
        }
        finally
        {
            harness.Dispose();
        }
    }

    /// <summary>
    /// A crowd sharing one cell resolves to exactly the disc each member would have resolved alone — under both shapes.
    /// </summary>
    /// <remarks>
    /// <b>The expectation comes from the geometry, not from the other arm.</b> Each session's hit count is compared against the number of grid points inside
    /// its own radius of its own viewpoint, computed arithmetically. That catches a broad phase that keeps too much (the enlargement not narrowed back) and
    /// one that keeps too little (a filter that rejects what it should not) with one assertion, and it does not depend on the arm it is being compared to
    /// being right.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-17")]
    public void ACrowdSharingACellSeesExactlyWhatEachWouldSeeAlone([Values(false, true)] bool cellKeyed)
    {
        // Eight viewpoints inside ONE 64 m cell — the cell is a third of the 192 m radius.
        //
        // The offsets are 7.1 m and 5.3 m, deliberately NOT multiples of the 7.5 m entity spacing. A multiple translates the disc onto the same lattice and
        // every member then wants the same NUMBER of entities, so a filter centred on the cell instead of on the session would satisfy a count comparison.
        // At these offsets the eight discs hold six distinct populations, which is what makes the count a discriminator.
        // The cell comes from the ENGINE's own constant. Re-deriving "radius / 3" here would let a change to CellsPerRadius scatter these viewpoints into
        // separate cells, at which point every assertion below still passes while testing the direct path twice.
        var cell = InterestPass.CellSideFor(Radius);
        var origin = (Math.Floor(WorldEdge / 2d / cell) * cell) + (cell / 4d);
        var viewpoints = new Vector3D[8];
        for (var i = 0; i < viewpoints.Length; i++)
        {
            viewpoints[i] = new Vector3D(origin + (i % 4 * 7.1d), origin + (i / 4 * 5.3d), 0d);
        }

        var hits = Resolve(cellKeyed, viewpoints, nameof(ACrowdSharingACellSeesExactlyWhatEachWouldSeeAlone) + cellKeyed, out var sessions,
            out var sessionsShared);

        Assert.Multiple(() =>
        {
            var expectations = new HashSet<int>();
            for (var i = 0; i < viewpoints.Length; i++)
            {
                expectations.Add(PointsWithin(viewpoints[i].X, viewpoints[i].Y, Radius));
            }

            // Anti-vacuity, from the geometry rather than from the measurement: the crowd must be large, and the members must want MEASURABLY DIFFERENT
            // discs. If they all wanted the same count, a filter applied around the cell's centre instead of each session's viewpoint would pass.
            Assert.That(PointsWithin(viewpoints[0].X, viewpoints[0].Y, Radius), Is.GreaterThan(1500),
                "the fixture must reach the density this design is argued at");
            Assert.That(expectations, Has.Count.GreaterThan(4),
                $"the eight viewpoints want only {expectations.Count} distinct populations between them. The fixture needs them to differ, or it cannot tell "
                + "a filter centred on each session from one centred on the cell they share");

            // THE GROUPING ACTUALLY HAPPENED. Without this every assertion below is satisfied by the direct path: a CellKeyOf that returned NoCellKey for
            // everything, or a cell size that scattered these viewpoints, would decompose the crowd into singletons and this fixture would go green having
            // exercised nothing the switch controls.
            Assert.That(sessionsShared, Is.EqualTo(cellKeyed ? viewpoints.Length - 1 : 0),
                $"{sessionsShared} sessions were served from a shared cell resolution with the feature {(cellKeyed ? "on" : "off")}. On, all eight stand in "
                + "one cell so seven must be shared; off, nothing may be");

            for (var i = 0; i < viewpoints.Length; i++)
            {
                var expected = PointsWithin(viewpoints[i].X, viewpoints[i].Y, Radius);
                Assert.That(hits[sessions[i].Value], Is.EqualTo(expected),
                    $"session {i} at ({viewpoints[i].X}, {viewpoints[i].Y}) resolved {hits[sessions[i].Value]} entities against the {expected} inside its "
                    + $"own radius. Under the cell-keyed shape the broad phase queries a LARGER disc than any member wants, so too many means the "
                    + "per-session filter did not narrow it back and too few means it rejected what it should have kept");
            }
        });
    }

    /// <summary>
    /// Sessions one per cell take the direct path, and it is the same answer.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-17")]
    public void ScatteredSessionsAreUnaffected([Values(false, true)] bool cellKeyed)
    {
        // Two hundred metres apart, which is more than three cells, so no two share one and every group is a singleton.
        var viewpoints = new Vector3D[4];
        for (var i = 0; i < viewpoints.Length; i++)
        {
            viewpoints[i] = new Vector3D(250d + (i * 200d), 250d + (i * 200d), 0d);
        }

        var hits = Resolve(cellKeyed, viewpoints, nameof(ScatteredSessionsAreUnaffected) + cellKeyed, out var sessions, out var sessionsShared);

        Assert.Multiple(() =>
        {
            // Stated as the fact it is: these sessions are one per cell, so NOTHING is shared even with the feature on. That is what makes this the
            // control for the crowd fixture rather than a second copy of it.
            Assert.That(sessionsShared, Is.Zero,
                $"{sessionsShared} sessions were shared although every viewpoint is in a cell of its own, so the grouping is merging cells it should not");

            for (var i = 0; i < viewpoints.Length; i++)
            {
                var expected = PointsWithin(viewpoints[i].X, viewpoints[i].Y, Radius);
                Assert.That(expected, Is.GreaterThan(500), $"session {i} must see enough for its comparison to mean anything");
                Assert.That(hits[sessions[i].Value], Is.EqualTo(expected),
                    $"session {i} shares its cell with nobody, so grouping must not change its answer");
            }
        });
    }

    /// <summary>
    /// An unplaced session still sees nothing, and does not disturb the cell its neighbours share.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-17")]
    public void AnUnplacedSessionSeesNothingAndDoesNotJoinACell()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareSphere, nameof(AnUnplacedSessionSeesNothingAndDoesNotJoinACell),
            new SubscriptionsOptions { CellKeyedInterest = true });
        var sessions = harness.OpenSessions(3, "near");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();

        var centre = new Vector3D(WorldEdge / 2d, WorldEdge / 2d, 0d);
        Assert.That(harness.Sessions.SetViewpoint(sessions[0], centre), Is.True);
        Assert.That(harness.Sessions.SetViewpoint(sessions[1], new Vector3D(centre.X + 5d, centre.Y, 0d)), Is.True);

        // sessions[2] is deliberately never placed.
        harness.RunPass(2);
        harness.CreateRequestedBlocks();
        harness.RunPass(3);

        var hits = new Dictionary<uint, int>();
        for (var i = 0; i < harness.Interest.TickSessionCount; i++)
        {
            hits[harness.Interest.SessionAt(i).Value] = harness.Interest.HitsCountOf(i);
        }

        Assert.Multiple(() =>
        {
            // EXACT, not "> 1500". The pair's answers are computable and the file computes them elsewhere; a bound that loose leaves a quarter of the
            // answer free to be wrong while the test's name claims the unplaced session disturbed nothing.
            Assert.That(hits[sessions[0].Value], Is.EqualTo(PointsWithin(centre.X, centre.Y, Radius)),
                "the placed pair must resolve exactly their own discs with an unplaced session in the table");
            Assert.That(hits[sessions[1].Value], Is.EqualTo(PointsWithin(centre.X + 5d, centre.Y, Radius)));
            Assert.That(hits[sessions[2].Value], Is.Zero,
                "a session with no viewpoint watches nothing (SUB-16), and grouping must not give it one by association");

            // ONE shared session, not two: the group is the placed PAIR. A zero here would mean the pair never grouped and the fixture proved nothing about
            // association; a two would mean the unplaced session was folded in — which its own zero hit count cannot detect, because an unplaced viewpoint
            // defaults far from the cell and would filter to nothing even if it had been grouped.
            Assert.That(harness.Interest.SessionsShared, Is.EqualTo(1),
                $"{harness.Interest.SessionsShared} sessions were shared. The two placed sessions share a cell, so exactly one is served from the other's "
                + "resolution, and the unplaced one must not join them");
        });
    }

    /// <summary>A profile over TWO archetypes, which is what a real one looks like.</summary>
    private static void DeclareTwoArchetypes(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile("near2", p => p.Sphere(Radius).Of<ProjCreature>().Of<ProjRock>());
    }

    /// <summary>The same grid of creatures, plus a coarser grid of rocks so the second archetype contributes its own clusters.</summary>
    private static void PopulateTwo(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < CreatureCount; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i % Columns * Spacing, i / Columns * Spacing)));
            }

            for (var i = 0; i < RockCount; i++)
            {
                tx.Spawn<ProjRock>(ProjRock.Bounds.Set(PointAt(i % RockColumns * RockSpacing, i / RockColumns * RockSpacing)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    private const float RockSpacing = 22.5f;
    private const int RockColumns = 45;
    private const int RockCount = RockColumns * RockColumns;

    /// <summary>Grid points of one lattice within a radius.</summary>
    private static int LatticeWithin(double cx, double cy, double radius, int columns, float spacing, int count)
    {
        var inside = 0;
        var rsq = radius * radius;
        for (var i = 0; i < count; i++)
        {
            var dx = (i % columns * spacing) - cx;
            var dy = (i / columns * spacing) - cy;
            if ((dx * dx) + (dy * dy) <= rsq)
            {
                inside++;
            }
        }

        return inside;
    }

    /// <summary>
    /// A crowd sharing a cell, with a profile over TWO archetypes, still resolves each member's exact disc.
    /// </summary>
    /// <remarks>
    /// <b>This is the case the single-archetype fixture could not fail on.</b> A session's runs occupy a CONTIGUOUS span of the worker's run list, so the
    /// broad phase has to collect every archetype's candidates before any session is filtered — resolving one archetype for all members and then the next
    /// interleaves their runs, and each session's span then covers its neighbours' as well as its own. With one archetype there is nothing to interleave, so
    /// the first version of this fixture passed while the demo's real profile produced internal errors on forty of a hundred and ten sessions.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-17")]
    public void ACrowdWithAMultiArchetypeProfileResolvesEachMembersOwnDisc([Values(false, true)] bool cellKeyed)
    {
        var cell = InterestPass.CellSideFor(Radius);
        var origin = (Math.Floor(WorldEdge / 2d / cell) * cell) + (cell / 4d);
        var viewpoints = new Vector3D[6];
        for (var i = 0; i < viewpoints.Length; i++)
        {
            viewpoints[i] = new Vector3D(origin + (i % 3 * 7.1d), origin + (i / 3 * 5.3d), 0d);
        }

        var dbe = SetupEngine();
        PopulateTwo(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareTwoArchetypes,
            nameof(ACrowdWithAMultiArchetypeProfileResolvesEachMembersOwnDisc) + cellKeyed,
            new SubscriptionsOptions { CellKeyedInterest = cellKeyed });
        var sessions = harness.OpenSessions(viewpoints.Length, "near2");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();

        for (var i = 0; i < viewpoints.Length; i++)
        {
            Assert.That(harness.Sessions.SetViewpoint(sessions[i], viewpoints[i]), Is.True);
        }

        harness.RunPass(2);
        harness.CreateRequestedBlocks();
        harness.RunPass(3);

        var hits = new Dictionary<uint, int>();
        var runHits = new Dictionary<uint, int>();
        for (var i = 0; i < harness.Interest.TickSessionCount; i++)
        {
            var id = harness.Interest.SessionAt(i).Value;
            hits[id] = harness.Interest.HitsCountOf(i);

            // The hits the session's RUN SPAN actually names, summed. If the span covers a neighbour's runs it names more than the session was credited
            // with, and this is the exact statement of that — no magic bound, and it fails whichever session's span is over-wide rather than only the ones
            // whose run count happens to pass a threshold.
            var sum = 0;
            var span = harness.Interest.HitsOf(i);
            for (var r = 0; r < span.Length; r++)
            {
                sum += span[r].HitCount;
            }

            runHits[id] = sum;
        }

        Assert.Multiple(() =>
        {
            var rocks = LatticeWithin(viewpoints[0].X, viewpoints[0].Y, Radius, RockColumns, RockSpacing, RockCount);
            Assert.That(rocks, Is.GreaterThan(50),
                "the SECOND archetype must contribute a substantial share, or this fixture is the single-archetype one under another name");

            // Same guards as the single-archetype crowd: the grouping must have fired, and the members must want different totals.
            Assert.That(harness.Interest.SessionsShared, Is.EqualTo(cellKeyed ? viewpoints.Length - 1 : 0),
                $"{harness.Interest.SessionsShared} sessions shared a resolution with the feature {(cellKeyed ? "on" : "off")}");

            var totals = new HashSet<int>();
            for (var i = 0; i < viewpoints.Length; i++)
            {
                totals.Add(PointsWithin(viewpoints[i].X, viewpoints[i].Y, Radius)
                    + LatticeWithin(viewpoints[i].X, viewpoints[i].Y, Radius, RockColumns, RockSpacing, RockCount));
            }

            Assert.That(totals, Has.Count.GreaterThan(3),
                $"the members want only {totals.Count} distinct totals, so a filter centred on the shared cell would pass most of the assertions below");

            for (var i = 0; i < viewpoints.Length; i++)
            {
                var expected = PointsWithin(viewpoints[i].X, viewpoints[i].Y, Radius)
                    + LatticeWithin(viewpoints[i].X, viewpoints[i].Y, Radius, RockColumns, RockSpacing, RockCount);

                Assert.That(hits[sessions[i].Value], Is.EqualTo(expected),
                    $"session {i} resolved {hits[sessions[i].Value]} entities across two archetypes against the {expected} inside its own radius");

                Assert.That(runHits[sessions[i].Value], Is.EqualTo(expected),
                    $"session {i}'s run span names {runHits[sessions[i].Value]} hits against the {expected} it was credited with. A span that covers a "
                    + "neighbour's runs as well as its own leaves the total plausible while the frame stage walks another session's clusters");
            }
        });
    }

    /// <summary>A profile over a THREE-dimensional archetype.</summary>
    private static void DeclareThreeD(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjFlyer>(a => a
            .Motion(ProjFlyer.Bounds, m => m.Teleport(40f))
            .Field(ProjFlyer.Ai, x => x.Template, Codec.VarUInt, name: "kind"));
        subs.Profile("air", p => p.Sphere(Radius).Of<ProjFlyer>());
    }

    /// <summary>
    /// A crowd whose archetype is indexed on three axes is NOT grouped, and every member still resolves its own disc.
    /// </summary>
    /// <remarks>
    /// <b>The shared filter is two-dimensional, and so is the cluster narrowphase it repeats.</b> Where the archetype's spatial field has three axes the
    /// engine adds a Z overlap test and a dz term that the filter does not, and the one broad query is centred on a single member's Z — so a shared
    /// resolution would admit entities outside the sphere in Z for everybody and lose candidates for any member standing at a different height. Both
    /// directions are silent on the server. The pass refuses to group such a profile at all, and this is what says so: if the refusal were dropped, the
    /// sharing count below would rise and the hit counts would drift from the geometry.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-17")]
    public void AThreeDimensionalArchetypeIsNeverGrouped()
    {
        var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            // A flat sheet of flyers at Z = 0, plus a second sheet well above the radius. A correct resolve sees only the first; a 2D filter over a 3D
            // archetype would see both, because it never looks at Z.
            for (var i = 0; i < FlyerCount; i++)
            {
                var x = i % FlyerColumns * FlyerSpacing;
                var y = i / FlyerColumns * FlyerSpacing;
                tx.Spawn<ProjFlyer>(ProjFlyer.Bounds.Set(new ProjBounds3
                {
                    Bounds = new AABB3F { MinX = x, MinY = y, MinZ = 0f, MaxX = x, MaxY = y, MaxZ = 0f },
                    Speed = 1f,
                }));
                tx.Spawn<ProjFlyer>(ProjFlyer.Bounds.Set(new ProjBounds3
                {
                    Bounds = new AABB3F { MinX = x, MinY = y, MinZ = 5000f, MaxX = x, MaxY = y, MaxZ = 5000f },
                    Speed = 1f,
                }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var harness = InterestHarness.Create(dbe, DeclareThreeD, nameof(AThreeDimensionalArchetypeIsNeverGrouped),
            new SubscriptionsOptions { CellKeyedInterest = true });
        var sessions = harness.OpenSessions(4, "air");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();

        // All four inside one interest cell, which is exactly the arrangement that WOULD be grouped for a 2D archetype.
        var cell = InterestPass.CellSideFor(Radius);
        var origin = (Math.Floor(200d / cell) * cell) + (cell / 4d);
        var viewpoints = new Vector3D[sessions.Length];
        for (var i = 0; i < sessions.Length; i++)
        {
            viewpoints[i] = new Vector3D(origin + (i * 3.1d), origin, 0d);
            Assert.That(harness.Sessions.SetViewpoint(sessions[i], viewpoints[i]), Is.True);
        }

        harness.RunPass(2);
        harness.CreateRequestedBlocks();
        harness.RunPass(3);

        var hits = new Dictionary<uint, int>();
        for (var i = 0; i < harness.Interest.TickSessionCount; i++)
        {
            hits[harness.Interest.SessionAt(i).Value] = harness.Interest.HitsCountOf(i);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Interest.SessionsShared, Is.Zero,
                $"{harness.Interest.SessionsShared} sessions shared a resolution although the archetype is indexed on three axes. The shared filter tests "
                + "X and Y only, so it would admit entities the sphere excludes in Z and drop ones a member at another height should see");

            // EXACT, because it is computable: the observers stand at Z=0, so every flyer of the low sheet is at dz=0 and the three-dimensional test
            // reduces to the same disc the lattice helper counts, while the high sheet is five kilometres away and outside any of it. A bound of
            // "more than none and at most four hundred" would be satisfied by a resolve that lost half the low sheet, which is one of the two directions
            // a two-dimensional filter over a three-dimensional archetype fails in.
            for (var i = 0; i < sessions.Length; i++)
            {
                var expected = LatticeWithin(viewpoints[i].X, viewpoints[i].Y, Radius, FlyerColumns, FlyerSpacing, FlyerCount);
                Assert.That(expected, Is.GreaterThan(0), "the fixture placed no flyer inside the disc, so it would prove nothing either way");
                Assert.That(hits[sessions[i].Value], Is.EqualTo(expected),
                    $"session {i} resolved {hits[sessions[i].Value]} flyers where its own disc holds exactly {expected} of the low sheet. More means the "
                    + "high sheet at Z=5000 was admitted; fewer means the low sheet was clipped in Z that is not there");
            }
        });
    }
}
