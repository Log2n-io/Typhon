using System;
using System.Threading;

namespace SwgTatooine;

/// <summary>
/// One structure's footprint as the neighbourhood passes it around: the tight world box the narrowphase already read, and
/// how far it was from the query centre.
/// </summary>
/// <remarks>
/// <b>Array of structures, against the house preference, and for the reason the preference exists.</b> All four edges are
/// read by the same comparison — the overlap test reads every one of them or none — so splitting them into four columns
/// would touch four cache lines to answer a question that fits in one record. The preference is about cache lines rather
/// than about layout for its own sake, and here it points the other way.
/// </remarks>
/// <param name="MinX">West edge.</param>
/// <param name="MinZ">South edge.</param>
/// <param name="MaxX">East edge.</param>
/// <param name="MaxZ">North edge.</param>
/// <param name="DistSq">Squared distance from the query centre to the closest point on this box, as the query reported it.</param>
internal readonly struct ProximityBox(float MinX, float MinZ, float MaxX, float MaxZ, float DistSq)
{
    /// <summary>West edge.</summary>
    public readonly float MinX = MinX;

    /// <summary>South edge.</summary>
    public readonly float MinZ = MinZ;

    /// <summary>East edge.</summary>
    public readonly float MaxX = MaxX;

    /// <summary>North edge.</summary>
    public readonly float MaxZ = MaxZ;

    /// <summary>Squared distance from the query centre to the closest point on this box. Orders the set when it is full.</summary>
    public readonly float DistSq = DistSq;
}

/// <summary>
/// The static neighbourhood: what fixed thing is near a body, answered without a query per body per tick, and the player
/// collision built on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fixed thing does not move, so a body that has not moved far enough cannot have changed its answer.</b> Each body
/// remembers the position its last query was taken from and the distance from there to the nearest blocking surface that
/// query found (<see cref="PlayerProximity"/>). While it stays inside that disc nothing fixed can be touching it, so there
/// is no test to run and nothing to ask the spatial layer. Only a body that leaves its disc pays for a query, and the
/// query it pays for hands it the next disc.
/// </para>
/// <para>
/// <b>One cluster is one batch, and that is not a coincidence.</b> A cluster holds at most 64 slots and the batched radius
/// query takes at most 64 members, so the cluster walk draws the batch boundary for free — and because clusters are
/// spatial, a cluster's members are co-located, which is the case the batch exists for: the cells they share are walked
/// and the clusters they share are opened once for the whole set rather than once per body.
/// </para>
/// <para>
/// <b>Every consumer the gameplay plan adds attaches by widening the probe, not by adding a query</b> — a door, a bench, a
/// terminal, an extractor. See <c>claude/design/SwgTatooine/design/proximity.md</c> for how, for the two questions that
/// deliberately stay out of it, and for why each consumer will need its own neighbour budget rather than sharing
/// collision's.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>
    /// Collision off, as a <see langword="static readonly"/> the JIT folds after the static constructor — so both arms are
    /// the same binary and an A/B measures one build against itself.
    /// </summary>
    /// <remarks>Same mechanism and same reason as the slope's own switch; see <c>SlopeDisabled</c>.</remarks>
    private static readonly bool CollisionDisabled = Environment.GetEnvironmentVariable("SWG_NO_COLLISION") == "1";

    /// <summary>
    /// How far a body may walk from its anchor before its disc is refreshed, in metres — the cap on
    /// <see cref="PlayerProximity.ClearM"/>.
    /// </summary>
    /// <remarks>
    /// The disc is usually narrower than this: it is the distance to the nearest surface, and the leash only stops a body
    /// in open desert from holding one disc for ever. At a run of 5 m/s and ten ticks a second, 32 m is 64 ticks of
    /// walking — so the refresh population is a fraction of the bodies that moved, which is the whole point.
    /// </remarks>
    internal const float ProximityLeashM = 32f;

    /// <summary>
    /// How wide a body's probe is beyond its own radius: the leash, and nothing more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It does NOT need to reach a structure's centre, and assuming it did cost 2.2× the search for nothing.</b> The
    /// engine's radius query reports every entity "whose spatial bounds are within the sphere's radius of its center, using
    /// the closest-point-on-AABB distance semantic" — so a box whose <i>surface</i> reaches the probe is returned however far
    /// away its centre is. The probe used to carry the largest structure's half-extent on top of this, which widened the
    /// circle from 33 m to 49 m and bought no soundness at all.
    /// </para>
    /// <para>
    /// <b>Soundness comes from <see cref="DiscFor"/>, not from the radius.</b> The disc is capped there by
    /// <c>probeR − |anchor − probeCentre|</c>, so <c>ClearM + |anchor − probeCentre| ≤ probeR</c> holds by construction for
    /// <i>any</i> probe. A box whose surface lies within <c>ClearM</c> of the anchor is therefore within <c>probeR</c> of the
    /// probe centre, and was reported. What a wider probe buys is a wider disc where there is nothing to hit — which the
    /// leash caps anyway.
    /// </para>
    /// </remarks>
    private const float ProbeReachM = ProximityLeashM;

    /// <summary>Members in one batch: a cluster's occupancy word is 64 bits and the batched query takes 64 spheres.</summary>
    private const int ProximityMaxMembers = 64;

    /// <summary>
    /// Neighbours kept per member — the <b>nearest</b> ones, which is what makes a full set still correct.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keeping the nearest rather than the first is the whole difference between a cap and a defect.</b> The disc is the
    /// minimum surface distance over the set, so holding the nearest means the disc is right whether or not the set
    /// overflowed: every box dropped is farther than every box kept, hence farther than the disc.
    /// </para>
    /// <para>
    /// <b>Collision is sound on the same argument, with one bound worth stating.</b> A box blocks the body only when its
    /// surface is within the body's own radius; for a dropped box to be missed, seventeen boxes would have to be within one
    /// metre of a single point without overlapping each other, which no footprint in the table can do. The counter is kept
    /// anyway, so a world that manages it says so instead of quietly degrading.
    /// </para>
    /// </remarks>
    internal const int ProximityMaxNeighbours = 16;

    /// <summary>Resolution passes before a body is left where it is — see <see cref="PushOut"/>.</summary>
    internal const int ProximityPasses = 4;

    private long _discsHeld;
    private long _discsRefreshed;
    private long _proximityBatches;
    private long _neighboursSeen;
    private long _contacts;
    private long _discOverflows;
    private long _unresolvedContacts;
    private long _auditedSteps;
    private long _auditViolations;
    private int _structureGeneration;

    /// <summary>
    /// What the independent oracle saw, with <see cref="SimConfig.CollisionAudit"/> on: positions checked, and positions
    /// that were inside a structure anyway.
    /// </summary>
    public (long Checked, long Violations) CollisionAuditTotals
        => (Interlocked.Read(ref _auditedSteps), Interlocked.Read(ref _auditViolations));

    /// <summary>
    /// Asks the spatial layer from scratch whether (<paramref name="x"/>, <paramref name="z"/>) is inside a structure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It consults nothing the neighbourhood produced, which is the entire point.</b> No disc, no anchor, no cached box —
    /// a fresh radius query per call. That makes it an oracle rather than a tautology: a disc that wrongly held, a push that
    /// stopped half a metre short, or a member index transposed inside the batch all show up here even though every one of
    /// them would leave the neighbourhood's own state self-consistent.
    /// </para>
    /// <para>
    /// A radius of the body's own half-extent is sufficient and not an approximation: the query reports an entity whose
    /// bounds come within the radius of the centre under closest-point-on-AABB distance, and a box overlaps the body exactly
    /// when its surface is nearer than that.
    /// </para>
    /// </remarks>
    /// <param name="realm">The realm the body is in.</param>
    /// <param name="x">The written position, east.</param>
    /// <param name="z">The written position, north.</param>
    /// <param name="half">The body's half-extent.</param>
    private void Audit(RealmId realm, float x, float z, float half)
    {
        Interlocked.Increment(ref _auditedSteps);
        var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = half };
        var e = Dbe.ClusterSpatialQuery<WorldObject>(realm).Radius(in sphere);
        try
        {
            while (e.MoveNext())
            {
                var hit = e.Current;

                // Strictly inside: a body resting exactly against a face is where the push leaves it, and is not a defect.
                if (x > hit.MinX - half && x < hit.MaxX + half && z > hit.MinY - half && z < hit.MaxY + half)
                {
                    Interlocked.Increment(ref _auditViolations);
                    return;
                }
            }
        }
        finally
        {
            e.Dispose();
        }
    }

    /// <summary>
    /// Bumped whenever a positioned-static entity is created, destroyed or moved, which retires every disc taken before it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing calls this yet, and that is exactly why it exists.</b> No code path creates a <c>WorldObject</c> after world
    /// build today, so every disc is safe — but <c>PlayerHouse</c>, <c>Harvester</c> and <c>CampObject</c> are already in the
    /// footprint table, and the first runtime placement would leave a body standing inside a new building with nothing
    /// anywhere to notice. Two bytes on the disc and one comparison close that before somebody has to find it in a profiler.
    /// A per-cell counter is the refinement when placement becomes frequent (the economy places installations constantly);
    /// a global one is the right size while placement is rare, because it costs one increment and no table.
    /// </remarks>
    public void InvalidateStaticNeighbourhoods() => Interlocked.Increment(ref _structureGeneration);

    /// <summary>
    /// What the neighbourhood did over the run. Held against refreshed is the claim that movement, not population, sets
    /// the cost — counted over <b>colliding</b> bodies only, since a scripted body never consults a disc.
    /// </summary>
    public (long Held, long Refreshed, long Batches, long Contacts, long Neighbours, long Overflows, long Unresolved) ProximityTotals
        => (Interlocked.Read(ref _discsHeld), Interlocked.Read(ref _discsRefreshed), Interlocked.Read(ref _proximityBatches),
            Interlocked.Read(ref _contacts), Interlocked.Read(ref _neighboursSeen), Interlocked.Read(ref _discOverflows),
            Interlocked.Read(ref _unresolvedContacts));

    /// <summary>Per-worker scratch, allocated once: the query's neighbours live here for the one call that resolves against them.</summary>
    /// <remarks>
    /// <b>Transient by design.</b> Nothing here outlives the batch that filled it — the whole point of the disc is that the
    /// neighbours never have to be stored per body — so this is reused rather than carried, and a <see langword="stackalloc"/>
    /// would zero 20 KB on every cluster and bill it to the move.
    /// </remarks>
    private sealed class ProximityScratch
    {
        public readonly BSphere2F[] Probe = new BSphere2F[ProximityMaxMembers];
        public readonly int[] Slot = new int[ProximityMaxMembers];
        public readonly float[] Half = new float[ProximityMaxMembers];
        public readonly float[] CandX = new float[ProximityMaxMembers];
        public readonly float[] CandZ = new float[ProximityMaxMembers];
        public readonly int[] Count = new int[ProximityMaxMembers];
        public readonly bool[] Overflow = new bool[ProximityMaxMembers];
        public readonly ProximityBox[] Boxes = new ProximityBox[ProximityMaxMembers * ProximityMaxNeighbours];

        /// <summary>The boxes collected for one member of the batch.</summary>
        public ReadOnlySpan<ProximityBox> Neighbours(int member) => Boxes.AsSpan(member * ProximityMaxNeighbours, Count[member]);
    }

    [ThreadStatic]
    private static ProximityScratch _proximity;

    /// <summary>Collects a batch's nearest neighbouring boxes into <see cref="ProximityScratch"/>, one call per (member, hit).</summary>
    /// <remarks>
    /// A <see langword="readonly"/> struct over a reference to the scratch: the batch copies the sink in and back, and every
    /// write here goes through that reference, so what it collects is visible whichever copy the batch holds.
    /// </remarks>
    private readonly struct ProximitySink : IRadiusBatchSink
    {
        private readonly ProximityScratch _s;

        public ProximitySink(ProximityScratch scratch) => _s = scratch;

        public bool Hit(int member, in ClusterSpatialQueryResult hit)
        {
            var first = member * ProximityMaxNeighbours;
            var n = _s.Count[member];

            // The archetype is 2D (AABB2F), so Y here is the world's Z; MinZ/MaxZ on the result are the query's own Z range
            // rather than geometry, which is why they are not touched. The bounds come off the result, so the box costs no
            // second component-table read.
            var box = new ProximityBox((float)hit.MinX, (float)hit.MinY, (float)hit.MaxX, (float)hit.MaxY, (float)hit.DistanceSq);
            if (n < ProximityMaxNeighbours)
            {
                _s.Boxes[first + n] = box;
                _s.Count[member] = n + 1;
                return true;
            }

            // Full: keep the NEAREST sixteen rather than the first sixteen, which is what leaves the disc correct -- every
            // box dropped is farther than every box kept. The member is not retired, because a later hit may still be
            // nearer than one already held and the query yields in cluster order rather than by distance.
            _s.Overflow[member] = true;
            var worst = 0;
            for (var i = 1; i < ProximityMaxNeighbours; i++)
            {
                if (_s.Boxes[first + i].DistSq > _s.Boxes[first + worst].DistSq)
                {
                    worst = i;
                }
            }

            if (box.DistSq < _s.Boxes[first + worst].DistSq)
            {
                _s.Boxes[first + worst] = box;
            }

            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="prox"/>'s disc still proves that nothing fixed can touch a body of half-extent
    /// <paramref name="half"/> at (<paramref name="x"/>, <paramref name="z"/>).
    /// </summary>
    /// <remarks>
    /// The disc holds the distance to the nearest SURFACE, so the body's own radius comes off it here. A body that has just
    /// been pushed off a wall sits exactly its own radius from one and holds no slack at all, which is correct: its next
    /// step asks again.
    /// </remarks>
    /// <param name="prox">The body's disc.</param>
    /// <param name="realm">The realm it is now in.</param>
    /// <param name="generation">The world's current static generation; a disc from an older one is retired.</param>
    /// <param name="x">Where it now is, east.</param>
    /// <param name="z">Where it now is, north.</param>
    /// <param name="half">Its half-extent.</param>
    /// <returns>Whether the disc still answers for this position.</returns>
    internal static bool DiscHolds(in PlayerProximity prox, RealmId realm, int generation, float x, float z, float half)
    {
        if (prox.Realm != realm.Value)
        {
            // A disc taken in another realm says nothing about this one. Interior coordinates are local and small, so
            // geometry alone could place a room position inside a planet anchor's disc by arithmetic coincidence.
            return false;
        }

        if (prox.Generation != (ushort)generation)
        {
            // Something fixed was created, destroyed or moved since this disc was taken.
            return false;
        }

        var slack = prox.ClearM - half;
        if (slack <= 0f)
        {
            return false;
        }

        var dx = x - prox.AnchorX;
        var dz = z - prox.AnchorZ;
        return (dx * dx) + (dz * dz) <= slack * slack;
    }

    /// <summary>
    /// Pushes a body out of every neighbouring box it is inside, along the axis of least penetration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Least penetration is what makes a doorway usable rather than sticky.</b> A body walking obliquely into a wall has
    /// its motion corrected along the wall's normal and keeps its tangential component, so it slides along the wall instead
    /// of stopping dead — and stopping dead on every oblique contact is the commonest way collision makes a game feel
    /// broken.
    /// </para>
    /// <para>
    /// <b>It iterates to a fixed point, and does not assume one exists.</b> Pushing a body out of one box can push it into
    /// the box beside it, which is what happens where two buildings meet — so the loop runs until a pass moves nothing. An
    /// earlier version ran exactly two passes and justified that by "the planner guarantees no two structures overlap",
    /// which is false twice over: the boxes tested here are grown by the body's own radius, and the planner's street is
    /// 2 m while a body is 2 m across, so grown boxes abut at zero clearance in the tightest legal case; and
    /// <c>SitePlanner.Place</c> deliberately plants a structure overlapping its neighbour when a site has run out of room,
    /// counting it in <c>Crowded</c>. Overlapping grown boxes make the push oscillate — out of A into B, out of B into A —
    /// so the pass count is a budget rather than a proof, and a body that exhausts it is <b>counted</b> instead of being
    /// quietly left inside a wall.
    /// </para>
    /// </remarks>
    /// <param name="boxes">The body's neighbours.</param>
    /// <param name="half">The body's half-extent.</param>
    /// <param name="worldHalf">Half the world's edge, for the clamp applied after each push.</param>
    /// <param name="x">The body's position, pushed in place.</param>
    /// <param name="z">The body's position, pushed in place.</param>
    /// <param name="unresolved">Set when the passes ran out with the body still inside something.</param>
    /// <returns>Whether anything moved the body.</returns>
    internal static bool PushOut(ReadOnlySpan<ProximityBox> boxes, float half, float worldHalf, ref float x, ref float z,
        out bool unresolved)
    {
        var touched = false;
        var moved = true;
        var pass = 0;
        for (; moved && pass < ProximityPasses; pass++)
        {
            moved = false;
            foreach (ref readonly var box in boxes)
            {
                // The box grown by the body's own radius: the body is then a point, and "touching" is "inside".
                var minX = box.MinX - half;
                var maxX = box.MaxX + half;
                var minZ = box.MinZ - half;
                var maxZ = box.MaxZ + half;
                if (x <= minX || x >= maxX || z <= minZ || z >= maxZ)
                {
                    continue;
                }

                var west = x - minX;
                var east = maxX - x;
                var south = z - minZ;
                var north = maxZ - z;
                var dx = west < east ? -west : east;
                var dz = south < north ? -south : north;
                if (MathF.Abs(dx) <= MathF.Abs(dz))
                {
                    x += dx;
                }
                else
                {
                    z += dz;
                }

                // Held inside the world after each push rather than once at the end: a push that crossed the edge and was
                // pulled back afterwards would land the body back inside the box it had just been pushed out of, and the
                // next pass would have no way to tell that from a corner.
                x = Math.Clamp(x, -worldHalf + half, worldHalf - half);
                z = Math.Clamp(z, -worldHalf + half, worldHalf - half);
                moved = true;
            }

            touched |= moved;
        }

        // The last pass still moved something, so the fixed point was not reached within the budget.
        unresolved = moved;
        return touched;
    }

    /// <summary>
    /// The disc for a body that has ended up at (<paramref name="x"/>, <paramref name="z"/>): how far it may now walk
    /// before a fixed thing could touch it.
    /// </summary>
    /// <remarks>
    /// Three bounds, and the middle one is the one that is easy to forget. The probe is all that was looked at, so a disc
    /// may not claim knowledge past it — and the body has already spent part of that budget getting to where it ended up.
    /// The surface distances are measured from the RESOLVED position rather than read from the query's own
    /// <c>DistanceSq</c>, which was measured from the candidate that the collision then moved.
    /// </remarks>
    /// <param name="boxes">The body's neighbours.</param>
    /// <param name="probeX">Where the query was centred, east.</param>
    /// <param name="probeZ">Where the query was centred, north.</param>
    /// <param name="probeR">How far the query looked.</param>
    /// <param name="x">Where the body ended up, east.</param>
    /// <param name="z">Where the body ended up, north.</param>
    /// <returns>The distance to the nearest surface, capped by the leash and by what the probe actually saw.</returns>
    internal static float DiscFor(ReadOnlySpan<ProximityBox> boxes, float probeX, float probeZ, float probeR, float x, float z)
    {
        var px = probeX - x;
        var pz = probeZ - z;
        var clear = probeR - MathF.Sqrt((px * px) + (pz * pz));

        foreach (ref readonly var box in boxes)
        {
            var dx = MathF.Max(MathF.Max(box.MinX - x, 0f), x - box.MaxX);
            var dz = MathF.Max(MathF.Max(box.MinZ - z, 0f), z - box.MaxZ);
            var d = MathF.Sqrt((dx * dx) + (dz * dz));
            if (d < clear)
            {
                clear = d;
            }
        }

        return Math.Clamp(clear, 0f, ProximityLeashM);
    }

    /// <summary>What the neighbourhood and the collision did over the run.</summary>
    public void PrintProximityReport()
    {
        if (CollisionDisabled)
        {
            Console.WriteLine();
            Console.WriteLine("  proximity: collision OFF (SWG_NO_COLLISION=1) — no body consulted a disc");
            return;
        }

        var (held, refreshed, batches, contacts, neighbours, overflows, unresolved) = ProximityTotals;
        var steps = held + refreshed;
        if (steps == 0)
        {
            return;
        }

        Console.WriteLine();

        // "Colliding steps", not "steps": a scripted body never consults a disc, so this denominator is the client
        // population's and reads wrong without the word.
        Console.WriteLine($"  proximity (leash {ProximityLeashM:G} m, probe {ProbeReachM:G} m + body): {steps:N0} colliding steps, "
            + $"{held:N0} held their disc ({100d * held / steps:F1} %), {refreshed:N0} refreshed it in {batches:N0} queries "
            + $"({(batches == 0 ? 0d : (double)refreshed / batches):F1} bodies per query, "
            + $"{(refreshed == 0 ? 0d : (double)neighbours / refreshed):F2} neighbours each); "
            + $"{contacts:N0} contacts resolved, {unresolved:N0} unresolved, {overflows:N0} sets full");

        var (audited, violations) = CollisionAuditTotals;
        if (audited > 0)
        {
            Console.WriteLine($"  proximity audit: {audited:N0} written positions re-checked against a fresh query, {violations:N0} inside a structure");
        }
    }
}
