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
/// <param name="Entity">Which structure this is, for the narrow phase; <see cref="EntityId.Null"/> when there is none.</param>
internal readonly struct ProximityBox(float MinX, float MinZ, float MaxX, float MaxZ, float DistSq, EntityId Entity = default)
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

    /// <summary>
    /// The structure this box is the footprint of, so a body that actually enters it can ask what shape is inside.
    /// </summary>
    /// <remarks>
    /// <b>Carried from the query result, which costs nothing.</b> The hit already names its entity; copying the id into
    /// the box is eight bytes and no lookup. What it buys is that the narrow phase — the only place that needs to know
    /// WHICH building this is — can be reached without a second spatial query or a component read at broad phase, which
    /// is the property this whole path is built around.
    /// </remarks>
    public readonly EntityId Entity = Entity;
}

/// <summary>
/// What a structure's real shape is, asked only of a structure a body has actually entered.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the second level of the test, and the only place a component is read.</b> The spatial index answers
/// "what is near me" with footprints and no component read, which rejects almost every pair — a live run counted 335
/// contacts in 17 700 ticks. A body that has entered a footprint then costs one <c>TryOpen</c> plus the asset's own
/// boxes, which for a house is ten to fourteen.
/// </para>
/// <para>
/// <b>A default instance expands nothing</b>, which is what a run with no asset set does, and what the unit tests of
/// <see cref="SimBridge.PushOut"/> do: the footprint box then stands, exactly as it did before assets existed.
/// </para>
/// </remarks>
internal ref struct StructureShapes
{
    private readonly EntityAccessor _accessor;
    private readonly AssetColliders _colliders;

    /// <summary>
    /// The last structure resolved, because a body inside a footprint asks again on every pass of the push-out.
    /// </summary>
    /// <remarks>
    /// A body in a doorway stays inside the footprint by construction, so the gate passes on all four passes and an
    /// un-memoised resolver would open the entity and read two components four times over — on the one path this file
    /// exists to keep cheap. One entry is enough: the passes ask about the same structure in turn.
    /// </remarks>
    private float _overhang;

    private EntityId _lastEntity;
    private AssetColliders.PlanBox[] _lastPrims;
    private float _lastX;
    private float _lastZ;
    private float _lastY;
    private int _lastQuarters;
    private Shape _lastShape;

    /// <param name="accessor">The tick's accessor, for reading the structure a contact names.</param>
    /// <param name="colliders">The collider table, or <see langword="null"/> to expand nothing.</param>
    internal StructureShapes(EntityAccessor accessor, AssetColliders colliders)
    {
        _accessor = accessor;
        _colliders = colliders;
        _overhang = colliders?.MaxOverhangM ?? 0f;
    }

    /// <summary>How far past its own footprint ANY asset reaches; zero with no asset set.</summary>
    /// <remarks>
    /// The worst case over every asset, kept for the bounds that are not about one structure — and for a caller with no
    /// box to measure. A gate that HAS a box should use <see cref="OverhangOf"/> instead, which charges that structure's
    /// own reservation and is zero for six of the eight kinds.
    /// </remarks>
    internal float Overhang => _overhang;

    /// <summary>
    /// How far past this structure's reservation its own asset can reach, in metres.
    /// </summary>
    /// <param name="box">A broad-phase box, whose width IS the reservation the world made.</param>
    /// <returns>Its overhang, which the gate grows by and the disc is charged.</returns>
    /// <remarks>
    /// <b>No component read, which is the whole constraint.</b> The reservation is a property of the kind and the kind
    /// picks the asset, so the box's own width names the overhang — see
    /// <see cref="AssetColliders.OverhangFor"/>. Charging every structure the worst asset's figure instead cost a
    /// terminal, which overhangs by nothing, the 2.25 m of a house's entry ramp: a wider gate, and a disc 2.25 m
    /// smaller than the truth for every body walking past one.
    /// </remarks>
    /// <remarks>
    /// <b>With no table, the single figure is all there is</b>, which is what a run with no asset set and what a
    /// pre-seeded <see cref="ForTest"/> resolver both have: zero in the first case and whatever the test asked for in
    /// the second. Falling back to it rather than to zero is what keeps the two readings of "the overhang" from
    /// disagreeing on a resolver that has one but not the other.
    /// </remarks>
    internal float OverhangOf(in ProximityBox box) =>
        _colliders == null ? _overhang : _colliders.OverhangFor((box.MaxX - box.MinX) * 0.5f);

    /// <summary>
    /// A resolver whose memo is pre-seeded, so the paths that consume a shape can be tested without an entity.
    /// </summary>
    /// <param name="overhang">What <see cref="Overhang"/> should report.</param>
    /// <param name="entity">The entity the seeded answer is for; <see cref="Expand"/> returns it for this id only.</param>
    /// <param name="shape">Which answer to seed.</param>
    /// <param name="prims">Its primitives, in its own frame.</param>
    /// <param name="x">Where it stands, east.</param>
    /// <param name="z">Where it stands, north.</param>
    /// <param name="quarters">Its quarter turn.</param>
    /// <returns>A resolver that answers from the memo and never opens an entity.</returns>
    /// <remarks>
    /// <b>It exercises the real <see cref="Expand"/>, not a stand-in.</b> A body inside a footprint asks on every pass,
    /// so the memo is the path the live tick takes after the first ask — seeding it reaches the consumers through the
    /// same code the simulation runs, without needing a transaction to hold a structure.
    /// </remarks>
    internal static StructureShapes ForTest(float overhang, EntityId entity, Shape shape, AssetColliders.PlanBox[] prims,
        float x, float z, int quarters, float groundY = 0f)
    {
        var shapes = default(StructureShapes);
        shapes._overhang = overhang;
        shapes._lastEntity = entity;
        shapes._lastShape = shape;
        shapes._lastPrims = prims;
        shapes._lastX = x;
        shapes._lastZ = z;
        shapes._lastY = groundY;
        shapes._lastQuarters = quarters;
        return shapes;
    }

    /// <summary>What a body that has entered a footprint should be collided against.</summary>
    internal enum Shape
    {
        /// <summary>The footprint box itself, which is what every structure was before the asset pipeline.</summary>
        Footprint,

        /// <summary>The asset's own primitives: walls with a gap where its door is.</summary>
        Primitives,

        /// <summary>Nothing. The structure is one a body stands INSIDE, and its real shape is not loaded.</summary>
        None,
    }

    /// <summary>Whether this structure has primitives, and where they stand.</summary>
    /// <param name="entity">The structure a body has entered the footprint of.</param>
    /// <param name="prims">Its blocking boxes, in its own frame.</param>
    /// <param name="x">Where it stands, east.</param>
    /// <param name="z">Where it stands, north.</param>
    /// <param name="quarters">Its quarter turn.</param>
    /// <param name="groundY">The altitude the asset's own origin sits at, which its boxes' Y is measured from.</param>
    /// <returns>Whether <paramref name="prims"/> is worth using; <see langword="false"/> keeps the footprint box.</returns>
    internal Shape Expand(EntityId entity, out ReadOnlySpan<AssetColliders.PlanBox> prims, out float x, out float z, out int quarters,
        out float groundY)
    {
        if (entity == _lastEntity && !entity.IsNull)
        {
            prims = _lastPrims ?? [];
            x = _lastX;
            z = _lastZ;
            quarters = _lastQuarters;
            groundY = _lastY;
            return _lastShape;
        }

        prims = default;
        x = 0f;
        z = 0f;
        quarters = 0;
        groundY = 0f;
        // <b>A default instance expands nothing, and that includes having no accessor to expand with.</b> The claim is
        // in this type's own remarks and the push-out's pure tests rest on it, but it held only for a box carrying no
        // entity: a default resolver asked about a real one dereferenced a null accessor. The live tick always has one.
        if (entity.IsNull || _accessor == null || !_accessor.TryOpen(entity, out var e))
        {
            return Shape.Footprint;
        }

        var structure = e.Read(WorldObject.Struct);
        var array = _colliders == null ? [] : _colliders.ArrayFor(structure.Asset);
        prims = array;
        _lastEntity = entity;
        _lastPrims = array;
        if (array.Length == 0)
        {
            // <b>A structure a body stands INSIDE is not a solid box when its shape is missing.</b> The shuttleport's
            // reservation is its asset's own 46 m square and it stands at the centre of every city, which is where
            // bodies spawn: collided as a footprint it ejects them 23 m, and the suite said so in three places at
            // once. Walking through it until its walls are loaded is the smaller wrong answer — and on a run with an
            // asset set, which is every run that can draw it, the walls are there.
            _lastX = 0f;
            _lastZ = 0f;
            _lastY = 0f;
            _lastQuarters = 0;
            _lastShape = structure.Kind == StructureKind.Shuttleport ? Shape.None : Shape.Footprint;
            return _lastShape;
        }

        var place = e.Read(WorldObject.Bounds);
        x = place.X;
        z = place.Z;
        groundY = place.Y;
        quarters = structure.Yaw;
        _lastX = x;
        _lastZ = z;
        _lastY = groundY;
        _lastQuarters = quarters;
        _lastShape = Shape.Primitives;
        return _lastShape;
    }
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
    /// <summary>
    /// The published assets' collision primitives, or <see langword="null"/> on a run with no asset set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Set once, at construction, and read on contacts only.</b> A run started without <c>--assets</c> leaves it
    /// null and every structure collides as the footprint box it always did, which is what keeps the asset pipeline out
    /// of the path of a measurement that does not want it.
    /// </para>
    /// <para>
    /// <b><see langword="init"/> rather than a setter, because a setter had an ordering to get wrong and got it
    /// wrong.</b> The served path assigned this before <c>StartReplication</c> had built the bridge, so every run with
    /// <c>--assets</c> — the only runs collision is enabled on — threw a <see cref="NullReferenceException"/> at boot.
    /// The suite builds a bridge directly and never saw it. Passed through the constructor there is no window.
    /// </para>
    /// </remarks>
    internal AssetColliders AssetShapes { get; init; }

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
    /// <param name="accessor">The tick's accessor, to read the shape of a structure the body is standing in.</param>
    /// <param name="realm">The realm the body is in.</param>
    /// <param name="x">The written position, east.</param>
    /// <param name="z">The written position, north.</param>
    /// <param name="half">The body's half-extent.</param>
    private void Audit(EntityAccessor accessor, RealmId realm, float x, float z, float half, float feetY)
    {
        Interlocked.Increment(ref _auditedSteps);
        var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = half };

        // <b>Its own resolver, not the tick's.</b> A fresh one per call carries no memo, so the oracle reads the shape
        // table and the structure's own components rather than anything the neighbourhood arrived at. The table is
        // static input data; the neighbourhood's state is what must not be consulted, and is not.
        var shapes = new StructureShapes(accessor, AssetShapes);
        var e = Dbe.ClusterSpatialQuery<WorldObject>(realm).Radius(in sphere);
        try
        {
            while (e.MoveNext())
            {
                var hit = e.Current;

                // Strictly inside: a body resting exactly against a face is where the push leaves it, and is not a defect.
                if (x <= hit.MinX - half || x >= hit.MaxX + half || z <= hit.MinY - half || z >= hit.MaxY + half)
                {
                    continue;
                }

                // <b>Inside the FOOTPRINT is not a violation, and an oracle that said it was reported 7.9 % of a
                // measured run as broken collision.</b> A structure the world publishes an asset for is walls with a
                // gap where its door is: standing in the doorway, in a room, or on the shuttleport's pad is inside the
                // footprint and inside nothing solid. The oracle has to apply the same two-level rule the tick applies,
                // or it grades the footprint fallback rather than the collision that actually ran.
                var shape = shapes.Expand(hit.Entity, out var prims, out var originX, out var originZ, out var quarters, out var originY);
                if (shape == StructureShapes.Shape.None)
                {
                    continue;
                }

                if (shape == StructureShapes.Shape.Primitives && !InsideShapes(prims, originX, originZ, originY, quarters, half, x, z, feetY))
                {
                    continue;
                }

                Interlocked.Increment(ref _auditViolations);
                return;
            }
        }
        finally
        {
            e.Dispose();
        }
    }

    /// <summary>Whether a point is strictly inside one structure's own primitives, grown by the body's half-extent.</summary>
    /// <param name="prims">The asset's plan boxes, in its own frame.</param>
    /// <param name="originX">Where the structure stands, east.</param>
    /// <param name="originZ">Where it stands, north.</param>
    /// <param name="quarters">Its quarter turn, 0 to 3.</param>
    /// <param name="half">The body's half-extent.</param>
    /// <param name="x">The point, east.</param>
    /// <param name="z">The point, north.</param>
    /// <returns>Whether the body overlaps a wall.</returns>
    private static bool InsideShapes(ReadOnlySpan<AssetColliders.PlanBox> prims, float originX, float originZ, float originY, int quarters,
        float half, float x, float z, float feetY)
    {
        // Standing ON a deck is not standing IN it, and an arch overhead is not a violation either: the oracle applies
        // the same floor-or-wall rule the tick applied, or it grades geometry the simulation never collided against.
        var known = float.IsFinite(feetY);
        var stepTop = feetY + TatooineData.PlayerStepM;
        var headTop = feetY + TatooineData.PlayerHeightM;

        foreach (ref readonly var prim in prims)
        {
            var turned = TurnBox(quarters, prim);
            if (known && (originY + turned.TopAt(x - originX, z - originZ) <= stepTop || originY + turned.MinY >= headTop))
            {
                continue;
            }

            if (x > turned.MinX + originX - half && x < turned.MaxX + originX + half
                && z > turned.MinZ + originZ - half && z < turned.MaxZ + originZ + half)
            {
                return true;
            }
        }

        return false;
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

        /// <summary>Where the body stood BEFORE this step, so the resolver can walk the path rather than the endpoint.</summary>
        public readonly float[] FromX = new float[ProximityMaxMembers];
        public readonly float[] FromZ = new float[ProximityMaxMembers];

        /// <summary>The altitude of its feet, which decides whether a box is a floor, a step, a wall or a lintel.</summary>
        public readonly float[] FeetY = new float[ProximityMaxMembers];
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
            var box = new ProximityBox(
                (float)hit.MinX, (float)hit.MinY, (float)hit.MaxX, (float)hit.MaxY, (float)hit.DistanceSq, hit.Entity);
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
        out bool unresolved, scoped ref StructureShapes shapes, float feetY = float.NaN, float fromX = float.NaN, float fromZ = float.NaN)
    {
        // <b>A wall thinner than one step is walked straight through, and that is arithmetic rather than bad luck.</b>
        // The resolver pushes a body out of what it overlaps AT THE END of the tick and never looks at the path. A run
        // is 10 m/s and the server ticks at 10 Hz, so a step is 1.00 m, while the published house walls are 0.40 to
        // 0.55 m thick: the step starts outside the wall and lands outside it on the far side, there is no overlap to
        // resolve, and the body is through. Advancing in increments shorter than the thinnest thing in the world is
        // the testbench's answer too (`stepMotor`, which divides by half its radius).
        if (float.IsFinite(fromX) && float.IsFinite(fromZ))
        {
            var dx = x - fromX;
            var dz = z - fromZ;
            var span = MathF.Sqrt((dx * dx) + (dz * dz));
            var steps = (int)MathF.Ceiling(span / MathF.Max(0.05f, half * 0.5f));
            if (steps > 1)
            {
                steps = Math.Min(steps, MaxSubSteps);
                var cx = fromX;
                var cz = fromZ;
                var touchedAny = false;
                var stuckAny = false;
                for (var i = 0; i < steps; i++)
                {
                    // Advanced from where the body actually IS, so a correction is kept rather than undone by the
                    // next increment — the same composition the testbench's sub-stepped motor performs.
                    cx += dx / steps;
                    cz += dz / steps;
                    touchedAny |= Resolve(boxes, half, worldHalf, feetY, ref cx, ref cz, out var stuckHere, ref shapes);
                    stuckAny |= stuckHere;
                }

                x = cx;
                z = cz;
                unresolved = stuckAny;
                return touchedAny;
            }
        }

        return Resolve(boxes, half, worldHalf, feetY, ref x, ref z, out unresolved, ref shapes);
    }

    /// <summary>The most increments one tick's movement is split into, however far it travelled.</summary>
    /// <remarks>
    /// A bound rather than a tuning: a body teleported across the planet would otherwise ask for millions of them.
    /// Twenty covers a burst run at 10 Hz with the body's own radius to spare, and a longer jump than that is a
    /// placement rather than a walk, which the endpoint test handles as it always did.
    /// </remarks>
    private const int MaxSubSteps = 20;

    /// <summary>One resolution of a body against its neighbours, at the position it currently holds.</summary>
    /// <param name="boxes">The body's neighbours.</param>
    /// <param name="half">The body's half-extent.</param>
    /// <param name="worldHalf">Half the world's edge.</param>
    /// <param name="feetY">The altitude of the feet, or NaN when the caller has no elevation to offer.</param>
    /// <param name="x">The body, pushed in place.</param>
    /// <param name="z">The body, pushed in place.</param>
    /// <param name="unresolved">Whether the fixed point was not reached within the pass budget.</param>
    /// <param name="shapes">The tick's resolver.</param>
    /// <returns>Whether anything moved it.</returns>
    private static bool Resolve(ReadOnlySpan<ProximityBox> boxes, float half, float worldHalf, float feetY, ref float x, ref float z,
        out bool unresolved, scoped ref StructureShapes shapes)
    {
        var touched = false;
        var moved = true;
        var pass = 0;
        for (; moved && pass < ProximityPasses; pass++)
        {
            moved = false;
            foreach (ref readonly var box in boxes)
            {
                // The box grown by the body's own radius: the body is then a point, and "touching" is "inside". And by
                // the furthest any asset reaches past its own footprint, because an asset is not obliged to fit inside
                // the square the world reserved for it — see AssetColliders.MaxOverhangM.
                var grown = half + shapes.OverhangOf(box);
                var minX = box.MinX - grown;
                var maxX = box.MaxX + grown;
                var minZ = box.MinZ - grown;
                var maxZ = box.MaxZ + grown;
                if (x <= minX || x >= maxX || z <= minZ || z >= maxZ)
                {
                    continue;
                }

                // <b>Inside the footprint, which is where the cheap test stops and the real shape begins.</b> A
                // structure the world publishes an asset for is not the box it reserves: it is walls with a gap where
                // its door is. Expanding here and nowhere else is what makes that affordable — the footprint rejected
                // every other neighbour for nothing, and a house is ten to fourteen boxes once a body is actually in it.
                var shape = shapes.Expand(box.Entity, out var prims, out var originX, out var originZ, out var quarters, out var originY);
                if (shape == StructureShapes.Shape.Primitives)
                {
                    moved |= PushOutOfShapes(prims, originX, originZ, originY, quarters, half, worldHalf, feetY, ref x, ref z);
                    continue;
                }

                if (shape == StructureShapes.Shape.None)
                {
                    continue;
                }

                // No asset behind it: the footprint itself is the shape, and the gate's growth was only a reach the
                // narrow phase would have used. Re-test against the box as it stands, or a body would be pushed out of
                // a square larger than the thing it is standing next to.
                if (x <= box.MinX - half || x >= box.MaxX + half || z <= box.MinZ - half || z >= box.MaxZ + half)
                {
                    continue;
                }

                minX = box.MinX - half;
                maxX = box.MaxX + half;
                minZ = box.MinZ - half;
                maxZ = box.MaxZ + half;

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
    /// Pushes a body out of one structure's own primitives, each placed by the structure's position and quarter turn.
    /// </summary>
    /// <param name="prims">The asset's plan boxes, in its own frame.</param>
    /// <param name="originX">Where the structure stands, east.</param>
    /// <param name="originZ">Where it stands, north.</param>
    /// <param name="quarters">Its quarter turn, 0 to 3.</param>
    /// <param name="half">The body's half-extent.</param>
    /// <param name="worldHalf">Half the world's edge.</param>
    /// <param name="x">The body, pushed in place.</param>
    /// <param name="z">The body, pushed in place.</param>
    /// <returns>Whether anything moved it.</returns>
    /// <remarks>
    /// <b>A quarter turn keeps an axis-aligned box axis-aligned</b>, which is the whole reason the orientation a
    /// structure replicates is a quarter and not an angle: the transform is a swap and two negations, the boxes stay
    /// AABBs, and the push-out below is the same arithmetic it has always been.
    /// </remarks>
    private static bool PushOutOfShapes(ReadOnlySpan<AssetColliders.PlanBox> prims, float originX, float originZ, float originY, int quarters,
        float half, float worldHalf, float feetY, ref float x, ref float z)
    {
        // <b>What a box IS depends on where the feet are, and nothing else.</b> This replaced a fixed height above the
        // ground, which could only ever be wrong for a building that stands on a deck: it turned the deck into a wall
        // and discarded everything on top of it as something to walk under. Ported from the testbench's floating
        // capsule (`physics/character-motor.ts`), which collides only from the step height up to the body's height.
        var known = float.IsFinite(feetY);
        var stepTop = feetY + TatooineData.PlayerStepM;
        var headTop = feetY + TatooineData.PlayerHeightM;

        var moved = false;
        foreach (ref readonly var prim in prims)
        {
            // Into the structure's own frame first, so the quarter turn is applied about its origin.
            var turned = TurnBox(quarters, prim);
            var (minX, minZ, maxX, maxZ) = (turned.MinX, turned.MinZ, turned.MaxX, turned.MaxZ);
            if (known)
            {
                // <b>The top WHERE THE BODY IS</b>, not the highest point of the box: the foot of a ramp is a step up
                // and its head is a wall, and they are the same collider.
                var top = originY + turned.TopAt(x - originX, z - originZ);
                var bottom = originY + turned.MinY;
                if (top <= stepTop)
                {
                    // A floor, a kerb, a step or the low end of a ramp: the body stands ON it, and GroundOnShapes
                    // raises the feet to meet it.
                    continue;
                }

                if (bottom >= headTop)
                {
                    // A lintel, an arch or an overhanging roof: walked under.
                    continue;
                }
            }
            if (turned.Radius > 0f)
            {
                // <b>Out along the radius, which for a circle IS the axis of least penetration.</b> Resolving a round
                // collider on x or z instead would eject a body sideways along a tangent -- and over a 21.6 m plaza
                // that is twenty metres of travel to escape something it was standing a centimetre inside.
                var cxr = originX + ((turned.MinX + turned.MaxX) * 0.5f);
                var czr = originZ + ((turned.MinZ + turned.MaxZ) * 0.5f);
                var rx = x - cxr;
                var rz = z - czr;
                var rd = MathF.Sqrt((rx * rx) + (rz * rz));
                var want = turned.Radius + half;
                if (rd >= want)
                {
                    continue;
                }

                // Dead centre: there is no radius to push along, so any direction will do and +X is as good as the next.
                var ux = rd > 1e-6f ? rx / rd : 1f;
                var uz = rd > 1e-6f ? rz / rd : 0f;
                x = Math.Clamp(cxr + (ux * want), -worldHalf + half, worldHalf - half);
                z = Math.Clamp(czr + (uz * want), -worldHalf + half, worldHalf - half);
                moved = true;
                continue;
            }

            minX += originX - half;
            maxX += originX + half;
            minZ += originZ - half;
            maxZ += originZ + half;
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

            x = Math.Clamp(x, -worldHalf + half, worldHalf - half);
            z = Math.Clamp(z, -worldHalf + half, worldHalf - half);
            moved = true;
        }

        return moved;
    }

    /// <summary>Test seam for <see cref="PushOutOfShapes"/>, which is private because nothing else should call it.</summary>
    /// <param name="prims">The asset's plan boxes.</param>
    /// <param name="originX">Where the structure stands, east.</param>
    /// <param name="originZ">Where it stands, north.</param>
    /// <param name="quarters">Its quarter turn.</param>
    /// <param name="half">The body's half-extent.</param>
    /// <param name="worldHalf">Half the world's edge.</param>
    /// <param name="x">The body, pushed in place.</param>
    /// <param name="z">The body, pushed in place.</param>
    /// <returns>Whether anything moved it.</returns>
    internal static bool PushOutForTest(ReadOnlySpan<ProximityBox> boxes, float half, float worldHalf, ref float x, ref float z,
        out bool unresolved)
    {
        var none = default(StructureShapes);
        return PushOut(boxes, half, worldHalf, ref x, ref z, out unresolved, ref none);
    }

    /// <summary>Test seam for <see cref="PushOutOfShapes"/>, which is private because nothing else should call it.</summary>
    /// <param name="prims">The asset's plan boxes.</param>
    /// <param name="originX">Where the structure stands, east.</param>
    /// <param name="originZ">Where it stands, north.</param>
    /// <param name="quarters">Its quarter turn.</param>
    /// <param name="half">The body's half-extent.</param>
    /// <param name="worldHalf">Half the world's edge.</param>
    /// <param name="x">The body, pushed in place.</param>
    /// <param name="z">The body, pushed in place.</param>
    /// <returns>Whether anything moved it.</returns>
    internal static bool PushOutOfShapesForTest(ReadOnlySpan<AssetColliders.PlanBox> prims, float originX, float originZ,
        int quarters, float half, float worldHalf, ref float x, ref float z) =>
        PushOutOfShapes(prims, originX, originZ, 0f, quarters, half, worldHalf, float.NaN, ref x, ref z);

    /// <summary>Test seam for <see cref="Turn"/>.</summary>
    /// <param name="quarters">0 to 3.</param>
    /// <param name="b">The box in the asset's own frame.</param>
    /// <returns>Its turned edges.</returns>
    internal static (float MinX, float MinZ, float MaxX, float MaxZ) TurnForTest(int quarters, in AssetColliders.PlanBox b) =>
        Turn(quarters, in b);

    /// <summary>A plan box turned by a quarter, about the asset's origin.</summary>
    /// <param name="quarters">0 to 3, counted the way the renderer counts its yaw: +Z towards +X.</param>
    /// <param name="b">The box in the asset's own frame.</param>
    /// <returns>Its turned edges.</returns>
    /// <remarks>
    /// The renderer applies this as Babylon's <c>rotation.y</c>, which maps <c>(x, z)</c> to
    /// <c>(x cos + z sin, -x sin + z cos)</c> — so a quarter sends <c>(x, z)</c> to <c>(z, -x)</c>. Written out rather
    /// than trigonometry, because the four cases are exact and the two sides have to agree to the last bit.
    /// </remarks>
    internal static (float MinX, float MinZ, float MaxX, float MaxZ) Turn(int quarters, in AssetColliders.PlanBox b) =>
        (quarters & 3) switch
        {
            1 => (b.MinZ, -b.MaxX, b.MaxZ, -b.MinX),
            2 => (-b.MaxX, -b.MaxZ, -b.MinX, -b.MinZ),
            3 => (-b.MaxZ, b.MinX, -b.MinZ, b.MaxX),
            _ => (b.MinX, b.MinZ, b.MaxX, b.MaxZ),
        };

    /// <summary>
    /// The disc for a body that has ended up at (<paramref name="x"/>, <paramref name="z"/>): how far it may now walk
    /// before a fixed thing could touch it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three bounds, and the middle one is the one that is easy to forget. The probe is all that was looked at, so a disc
    /// may not claim knowledge past it — and the body has already spent part of that budget getting to where it ended up.
    /// The surface distances are measured from the RESOLVED position rather than read from the query's own
    /// <c>DistanceSq</c>, which was measured from the candidate that the collision then moved.
    /// </para>
    /// <para>
    /// <b>A footprint the body is INSIDE is not what blocks it, so the disc is measured against the walls instead.</b>
    /// A structure's footprint is the ground the world reserved; its asset is walls with a gap where its door is, and a
    /// body indoors is legitimately inside the one while nowhere near the others. Measured against the footprint its
    /// distance to a surface is zero, which spends the disc and buys a full radius query on the very next tick — for
    /// every body standing in a building, every tick, for as long as it stays there. The shuttleport makes it worse
    /// than it sounds: its reservation is 46 m square and it stands at the centre of every city, which is where bodies
    /// spawn and crowd, so the busiest ground on the planet was the ground that never held a disc.
    /// </para>
    /// <para>
    /// <b>Refining costs nothing that has not already been paid.</b> The push-out resolved this same structure a moment
    /// ago and <see cref="StructureShapes"/> memoises the last one, so the component read is a memo hit; outside a
    /// footprint, which is almost every box, the arithmetic is what it always was.
    /// </para>
    /// <para>
    /// <b>Outside a footprint the distance is charged the overhang</b>, because an asset's wall may lie that far
    /// outside the box being measured and the disc may not promise ground a wall is standing on. It is 0.4 m with the
    /// published assets (<see cref="AssetColliders.MaxOverhangM"/>) and zero on a run with no asset set.
    /// </para>
    /// </remarks>
    /// <param name="boxes">The body's neighbours.</param>
    /// <param name="probeX">Where the query was centred, east.</param>
    /// <param name="probeZ">Where the query was centred, north.</param>
    /// <param name="probeR">How far the query looked.</param>
    /// <param name="x">Where the body ended up, east.</param>
    /// <param name="z">Where the body ended up, north.</param>
    /// <param name="shapes">The tick's resolver, carrying the push-out's memo.</param>
    /// <param name="overflowed">Whether the neighbour set was full, so boxes were dropped.</param>
    /// <returns>The distance to the nearest surface, capped by the leash and by what the probe actually saw.</returns>
    internal static float DiscFor(ReadOnlySpan<ProximityBox> boxes, float probeX, float probeZ, float probeR, float x, float z,
        scoped ref StructureShapes shapes, bool overflowed = false, float feetY = float.NaN)
    {
        var px = probeX - x;
        var pz = probeZ - z;
        var drift = MathF.Sqrt((px * px) + (pz * pz));
        var clear = probeR - drift;

        // <b>A full set drops boxes, and "the nearest sixteen were kept" is measured from the PROBE CENTRE while the disc
        // is measured from where the body ended up.</b> Those are not the same point: the push moved the body, by up to
        // its own radius plus the overhang. So the usual argument -- every dropped box is farther than every kept one,
        // hence farther than the minimum this takes -- proves nothing about distances from the body, and a dropped box
        // on the side the body was pushed TOWARDS can be nearer to it than any box kept. The disc would then promise
        // ground a wall stands on, and the body would walk into it and only be ejected on the refresh.
        //
        // What is still true is that every dropped box is at least as far from the CENTRE as the farthest kept one, so
        // that distance less the drift bounds every dropped box from below. One pass over sixteen boxes, and only when
        // the set actually overflowed -- which a run measured at a tenth of a percent of steps.
        if (overflowed)
        {
            var farthest = 0f;
            foreach (ref readonly var box in boxes)
            {
                farthest = MathF.Max(farthest, box.DistSq);
            }

            var bound = MathF.Sqrt(farthest) - drift;
            if (bound < clear)
            {
                clear = bound;
            }
        }

        foreach (ref readonly var box in boxes)
        {
            var dx = MathF.Max(MathF.Max(box.MinX - x, 0f), x - box.MaxX);
            var dz = MathF.Max(MathF.Max(box.MinZ - z, 0f), z - box.MaxZ);
            if (dx > 0f || dz > 0f)
            {
                var d = MathF.Sqrt((dx * dx) + (dz * dz)) - shapes.OverhangOf(box);
                if (d < clear)
                {
                    clear = d;
                }

                continue;
            }

            var shape = shapes.Expand(box.Entity, out var prims, out var originX, out var originZ, out var quarters, out var originY);
            if (shape == StructureShapes.Shape.None)
            {
                // Nothing here blocks anything — the structure is one a body stands inside and its walls are not
                // loaded. It constrains the disc no more than open desert does.
                continue;
            }

            if (shape == StructureShapes.Shape.Footprint)
            {
                // A solid box the body is inside: the push-out could not resolve it, and no distance is honest but zero.
                clear = 0f;
                break;
            }

            clear = MathF.Min(clear, DistanceToShapes(prims, originX, originZ, originY, quarters, x, z, feetY));
        }

        return Math.Clamp(clear, 0f, ProximityLeashM);
    }

    /// <summary>
    /// The height the body stands at: the terrain, raised by any published surface it is standing on.
    /// </summary>
    /// <param name="boxes">The body's neighbours, from the broad phase.</param>
    /// <param name="half">The body's half-extent.</param>
    /// <param name="x">Where it is, east.</param>
    /// <param name="z">Where it is, north.</param>
    /// <param name="terrain">The ground the height field gives here.</param>
    /// <param name="feetY">Where the feet were before this step; a surface more than a step above it is not climbed.</param>
    /// <param name="shapes">The tick's resolver.</param>
    /// <returns>The altitude to write.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the half that was missing, and it is why a building could be walked through.</b> Collision had a
    /// plan and no elevation: a body's altitude came from the terrain alone, so it walked at desert level THROUGH a
    /// deck it should have been standing on, and every collider resting on that deck was discarded as something above
    /// head height. A floor is not a special kind of thing — it is a box whose top the feet can reach.
    /// </para>
    /// <para>
    /// <b>A surface more than one step above the feet is not a floor, it is a wall</b>, and
    /// <see cref="PushOutOfShapes"/> treats it as one. Walking DOWN is unbounded here rather than capped at
    /// <c>PlayerSnapM</c>: this demo has no airborne state to fall through, so a body that leaves a ledge arrives at
    /// the ground under it in one tick instead of hanging over it.
    /// </para>
    /// </remarks>
    internal static float GroundOnShapes(ReadOnlySpan<ProximityBox> boxes, float half, float x, float z, float terrain, float feetY,
        scoped ref StructureShapes shapes)
    {
        var best = terrain;
        // <b>A reach of one step from the feet, and NOT capped by the distance walked.</b> Capping it looked like the
        // fix for a body that climbs its own output -- this function's answer becomes the next tick's feetY -- but the
        // ratchet it defends against does not exist: 1 715 probes over every structure in a town found no position
        // where repeated sampling lifts a body more than 0.5 m, because a box is only a floor if its TOP is within a
        // step, and a wall's top is metres up. What the cap did do is stop a ramp being climbed at all, since the first
        // step onto one is larger than the distance a body covers in a single tick.
        var reach = float.IsFinite(feetY) ? feetY + TatooineData.PlayerStepM : float.PositiveInfinity;

        foreach (ref readonly var box in boxes)
        {
            // The same cheap gate the push-out uses, grown the same way — and the growth is the point. An entry ramp
            // reaches 2.25 m PAST the square the world reserved for its house (AssetColliders.MaxOverhangM), so a gate
            // measured on the bare footprint rejects the half of the ramp a body actually steps onto, and the body
            // walks up it at the altitude it started from. Measured that way: a body crossed a house end to end
            // without rising a centimetre.
            var grown = half + shapes.OverhangOf(box);
            if (x <= box.MinX - grown || x >= box.MaxX + grown || z <= box.MinZ - grown || z >= box.MaxZ + grown)
            {
                continue;
            }

            if (shapes.Expand(box.Entity, out var prims, out var originX, out var originZ, out var quarters, out var originY)
                != StructureShapes.Shape.Primitives)
            {
                continue;
            }

            foreach (ref readonly var prim in prims)
            {
                var turned = TurnBox(quarters, prim);
                if (!turned.Walkable)
                {
                    // Too steep to stand on. It is still solid — the push-out treats it as a wall — but it is not a floor.
                    continue;
                }

                var top = originY + turned.TopAt(x - originX, z - originZ);
                if (top <= best || top > reach)
                {
                    continue;
                }

                if (turned.Touches(x - originX, z - originZ, half))
                {
                    best = top;
                }
            }
        }

        return best;
    }

    /// <summary>A plan box turned by a quarter, slope and all.</summary>
    /// <param name="quarters">0 to 3.</param>
    /// <param name="b">The box in the asset's own frame.</param>
    /// <returns>The same box as the structure stands.</returns>
    /// <remarks>
    /// A gradient turns exactly as a position does — <c>(x, z)</c> to <c>(z, -x)</c> — because it IS a direction in
    /// the plan. Getting this wrong tilts a ramp the wrong way for three of the four orientations, which is the kind
    /// of thing that looks like "collision is flaky" rather than like a bug.
    /// </remarks>
    internal static AssetColliders.PlanBox TurnBox(int quarters, in AssetColliders.PlanBox b)
    {
        var (minX, minZ, maxX, maxZ) = Turn(quarters, b);
        var (sx, sz) = (quarters & 3) switch
        {
            1 => (b.SlopeZ, -b.SlopeX),
            2 => (-b.SlopeX, -b.SlopeZ),
            3 => (-b.SlopeZ, b.SlopeX),
            _ => (b.SlopeX, b.SlopeZ),
        };

        return new AssetColliders.PlanBox(minX, minZ, maxX, maxZ, b.MinY, b.MaxY, b.PlaneY, sx, sz, b.Radius);
    }

    /// <summary>The distance from a point to the nearest of one structure's own primitives, placed and turned.</summary>
    /// <param name="prims">The asset's plan boxes, in its own frame.</param>
    /// <param name="originX">Where the structure stands, east.</param>
    /// <param name="originZ">Where it stands, north.</param>
    /// <param name="quarters">Its quarter turn, 0 to 3.</param>
    /// <param name="x">The point, east.</param>
    /// <param name="z">The point, north.</param>
    /// <returns>Zero for a point inside a primitive; the surface distance otherwise.</returns>
    /// <remarks>
    /// The same placement arithmetic as <see cref="PushOutOfShapes"/> — a quarter turn about the structure's origin,
    /// then a translate — with the body's own radius left out, because this answers "how far to the wall" and the
    /// caller's disc is compared against a distance walked rather than against a penetration.
    /// </remarks>
    private static float DistanceToShapes(ReadOnlySpan<AssetColliders.PlanBox> prims, float originX, float originZ, float originY, int quarters,
        float x, float z, float feetY)
    {
        // <b>The same floor-or-wall question the push-out asks, and for the same reason.</b> A disc measured to the
        // deck a body is standing on would be zero, so every body indoors or on a platform would refresh its disc
        // every tick -- which is the cost this whole file exists to avoid.
        var known = float.IsFinite(feetY);
        var stepTop = feetY + TatooineData.PlayerStepM;
        var headTop = feetY + TatooineData.PlayerHeightM;

        var nearest = float.MaxValue;
        foreach (ref readonly var prim in prims)
        {
            var turned = TurnBox(quarters, prim);
            var minX = turned.MinX + originX;
            var maxX = turned.MaxX + originX;
            var minZ = turned.MinZ + originZ;
            var maxZ = turned.MaxZ + originZ;
            var outsideBy = turned.OutsideBy(x - originX, z - originZ);
            var inside = outsideBy <= 0f;

            if (known && (originY + turned.TopAt(x - originX, z - originZ) <= stepTop || originY + turned.MinY >= headTop))
            {
                // <b>Not a wall — but still something the disc has to respect, because it is the FLOOR.</b> A disc
                // says "nothing fixed can touch this body", and the ground the body stands on is a fixed thing: hold a
                // disc across a ramp and the body never refreshes, never re-samples its ground, and walks up a slope at
                // the altitude it started from — which is to say, through the building the slope leads into. Measured
                // that way before this clause existed: the body crossed a house end to end without rising a centimetre.
                if (!inside)
                {
                    // Approaching it: the disc may reach its edge and no further.
                    nearest = MathF.Min(nearest, outsideBy);
                    continue;
                }

                if (turned.SlopeX != 0f || turned.SlopeZ != 0f)
                {
                    // On a slope the ground changes with every step, so there is nothing to hold.
                    return 0f;
                }

                // On a flat floor the ground cannot change until its edge, which is exactly how far the disc may reach.
                if (turned.Radius > 0f)
                {
                    var ix = x - ((minX + maxX) * 0.5f);
                    var iz = z - ((minZ + maxZ) * 0.5f);
                    nearest = MathF.Min(nearest, turned.Radius - MathF.Sqrt((ix * ix) + (iz * iz)));
                    continue;
                }

                nearest = MathF.Min(nearest, MathF.Min(MathF.Min(x - minX, maxX - x), MathF.Min(z - minZ, maxZ - z)));
                continue;
            }

            if (outsideBy < nearest)
            {
                nearest = outsideBy;
            }
        }

        return nearest;
    }

    /// <summary>Test seam for <see cref="DiscFor"/>, for the cases that have no asset set behind them.</summary>
    /// <param name="boxes">The body's neighbours.</param>
    /// <param name="probeX">Where the query was centred, east.</param>
    /// <param name="probeZ">Where the query was centred, north.</param>
    /// <param name="probeR">How far the query looked.</param>
    /// <param name="x">Where the body ended up, east.</param>
    /// <param name="z">Where the body ended up, north.</param>
    /// <returns>What <see cref="DiscFor"/> returns with a resolver that expands nothing.</returns>
    internal static float DiscForForTest(ReadOnlySpan<ProximityBox> boxes, float probeX, float probeZ, float probeR, float x, float z,
        bool overflowed = false)
    {
        var none = default(StructureShapes);
        return DiscFor(boxes, probeX, probeZ, probeR, x, z, ref none, overflowed);
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
