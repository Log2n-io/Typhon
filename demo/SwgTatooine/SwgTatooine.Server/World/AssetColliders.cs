using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SwgTatooine.Server;

/// <summary>
/// The collision primitives of the published assets, reduced to what a two-dimensional simulation can use: per asset, a
/// list of plan-view boxes in the asset's own frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the narrow half of a two-level test.</b> The spatial index already answers "which structures are near
/// this body" with their footprints and no component read, which rejects almost every pair — 335 contacts in 17 700
/// ticks on a live run. Only a body that has actually entered a footprint pays for the primitives behind it, and then a
/// house is ten to fourteen boxes, which is nothing.
/// </para>
/// <para>
/// <b>Why a doorway works without anything knowing what a door is.</b> The walls of an asset are separate primitives,
/// so the opening between them is simply a place with no box in it. Collision needs no notion of an entrance; it needs
/// the geometry the artist authored, which is exactly what the descriptor carries.
/// </para>
/// </remarks>
public sealed class AssetColliders
{
    /// <summary>
    /// The height, in metres above an asset's own origin, that a primitive must cross to block a body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A two-dimensional simulation has no "up", so it has to be told what a person would walk over.</b> The
    /// published assets make the answer easy, because their collision is authored in a band above the walkable floor
    /// and everything else is furniture of a different height:
    /// </para>
    /// <code>
    ///   houses:  walls, pilasters, towers   y 0.60 .. 2.70   cross 1.6   BLOCK
    ///            steps / step top           y 0.50 .. 1.00   below       walk over
    ///            plinth                     y 0.00 .. 0.70   below       walk over
    ///            entry ramp                 below ground     below       walk over
    ///            court gate lintel          y 2.46 .. 2.70   above       walk under
    ///   port:    walls, towers, pylons      y 0.30 .. 2.30   cross 1.6   BLOCK
    ///            pad, stairs, stair top     y 0.30 .. 1.26   below       walk over
    ///            lamp post                  y 0.30 .. 1.05   below       walk over
    ///            plaza, entry ramp          below 0.30       below       walk over
    /// </code>
    /// <para>
    /// So one comparison selects the walls and leaves the doorway between them open, keeps the steps that lead to it
    /// passable, and lets a body walk under a gate's lintel.
    /// </para>
    /// <para>
    /// <b>It was 1.0, and that was wrong.</b> The shuttleport's landing deck and its stairs reach 1.26 m, so a line
    /// below that turned the surface a player is meant to stand on into a wall — and the shuttleport now stands at the
    /// centre of every city, where players spawn. The suite said so in three places at once: a body moving 24 m in a
    /// tick, which is the push out of a 46 m square. At 1.6 the deck is walkable, the ring wall and the gate pylons
    /// still block, and the houses' selection is unchanged — every wall of theirs spans 0.6 to 2.7 and nothing else of
    /// theirs reaches 1.6 at all. The heights were measured from the published descriptors by the `blender-models`
    /// session; this is still a heuristic, and still why a per-collider role in the descriptor is the real fix.
    /// </para>
    /// </remarks>
    internal const float ChestM = 1.6f;

    /// <summary>
    /// The assets whose primitives this reads. Everything else keeps the single footprint box the world planted it with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An allow-list, because <see cref="ChestM"/> is a heuristic and heuristics have to be checked per asset.</b>
    /// Each of these was read collider by collider — the houses here, the shuttleport by the `blender-models` session —
    /// and the rule selects exactly their walls. An asset nobody has read keeps the single footprint box the world
    /// planted it with, which is what every structure had before the asset pipeline.
    /// </para>
    /// <para>
    /// <b>The real fix is in the descriptor, not here.</b> The exporter knows what each collider is for; a
    /// <c>role: "solid" | "walkable"</c> per collider would make this list and this constant both unnecessary, and is
    /// the proposal in front of Loïc. Until then, an asset is added here only after its colliders have been read.
    /// </para>
    /// <para>
    /// <b>The two interiors were read the same way</b> (catalogue 4): walls 0 to 2 m, furniture 0.45 to 1.72 m, and the floor slab below 0, which the
    /// per-contact floor-or-wall decision sorts exactly as it sorts a house's steps and walls.
    /// </para>
    /// <para>
    /// <b>The town hall and its room the same way</b> (catalogue with <c>tat_townhall-1</c>): a plinth 0.9 m up reached by a ramp, its walls 0.9 to 2.9 m on
    /// it — a house's deck and walls, higher; the room's walls 0 to 2 m, its furniture 0.45 to 1.6 m, and a stage 0.3 m up with its step, whose seats,
    /// table and lectern stand on it.
    /// </para>
    /// </remarks>
    private static readonly string[] Verified =
    [
        "tat_house_small", "tat_house_large", "tat_shuttleport", "tat_house_large_interior", "tat_house_small_interior", "tat_townhall",
        "tat_townhall_interior",
    ];

    /// <summary>One primitive as this simulation sees it: an axis-aligned box in the asset's own plan.</summary>
    /// <param name="MinX">West edge, metres from the asset origin.</param>
    /// <param name="MinZ">South edge.</param>
    /// <param name="MaxX">East edge.</param>
    /// <param name="MaxZ">North edge.</param>
    /// <param name="MinX">West edge, in the asset's frame.</param>
    /// <param name="MinZ">South edge.</param>
    /// <param name="MaxX">East edge.</param>
    /// <param name="MaxZ">North edge.</param>
    /// <param name="MinY">Underside, measured from the asset's own origin — the ground it stands on.</param>
    /// <param name="MaxY">Top.</param>
    /// <remarks>
    /// <b>The vertical span is what turns a box into a floor, a step or a wall, and it used to be thrown away.</b>
    /// A collider was kept only if it spanned a fixed height above the GROUND, so a building's deck became a wall and
    /// everything standing on that deck was discarded as something you walk under. Which it is depends on where the
    /// body's feet are, not on an absolute line, so the span travels with the box and the decision is made per contact.
    /// </remarks>
    internal readonly record struct PlanBox(
        float MinX, float MinZ, float MaxX, float MaxZ, float MinY = 0f, float MaxY = 0f, float PlaneY = float.NaN,
        float SlopeX = 0f, float SlopeZ = 0f, float Radius = 0f)
    {
        /// <summary>
        /// How far (<paramref name="x"/>, <paramref name="z"/>) lies outside this collider's plan, in metres; zero inside it.
        /// </summary>
        /// <param name="x">The point, east.</param>
        /// <param name="z">The point, north.</param>
        /// <returns>The distance to the plan's boundary from outside, or zero within it.</returns>
        /// <remarks>
        /// <b>A round collider is round, and treating one as the square around it is what walled off the shuttleport.</b>
        /// Its plaza is a 21.6 m cylinder; as an AABB that is a 43.2 m square whose corners stand 8.9 m out into open
        /// desert, 1.3 m thick with a flat top. A body walking in along the diagonal was pushed back to a radius of 25.7
        /// to 30.5 m -- it could not reach the plaza at all, on ground where nothing is drawn. The same is true of its
        /// landing pad, its tower and every lamp post, and the error grows with the radius, which is why the largest
        /// collider in the world was the worst affected.
        /// </remarks>
        internal readonly float OutsideBy(float x, float z)
        {
            if (Radius > 0f)
            {
                var rx = x - ((MinX + MaxX) * 0.5f);
                var rz = z - ((MinZ + MaxZ) * 0.5f);
                var d = MathF.Sqrt((rx * rx) + (rz * rz)) - Radius;
                return d > 0f ? d : 0f;
            }

            var dx = MathF.Max(MathF.Max(MinX - x, 0f), x - MaxX);
            var dz = MathF.Max(MathF.Max(MinZ - z, 0f), z - MaxZ);
            return MathF.Sqrt((dx * dx) + (dz * dz));
        }

        /// <summary>Whether a body of this half-extent standing here overlaps the collider's plan.</summary>
        /// <param name="x">The body, east.</param>
        /// <param name="z">The body, north.</param>
        /// <param name="half">Its half-extent.</param>
        /// <returns>Whether the two plans meet.</returns>
        /// <remarks>
        /// Grown by the body rather than shrunk to a point, which is the same convention the push-out and the ground rule
        /// have always used: the body is a square and this is generous by its corner, deliberately.
        /// </remarks>
        internal readonly bool Touches(float x, float z, float half) => OutsideBy(x, z) < half;

        /// <summary>
        /// The top of this collider above (<paramref name="x"/>, <paramref name="z"/>), in the asset's own frame.
        /// </summary>
        /// <param name="x">A point inside the plan box, east.</param>
        /// <param name="z">The same point, north.</param>
        /// <returns>The height of the top face there.</returns>
        /// <remarks>
        /// <b>A ramp is a slab rotated about its length, and its bounding box is a wall.</b> `tat_house_small`'s entry
        /// ramp is 0.3 m thick and tilted 10 degrees; flattened to an AABB it becomes a 1.57 m block, which is three
        /// steps tall and therefore impassable. Keeping the top PLANE instead costs two floats and makes the same
        /// geometry climbable at its low end and solid at its high one — which is what a ramp is.
        ///
        /// Flat for every axis-aligned collider, where both slopes are zero and this is just <see cref="MaxY"/>.
        /// </remarks>
        internal readonly float TopAt(float x, float z)
        {
            if (SlopeX == 0f && SlopeZ == 0f)
            {
                return MaxY;
            }

            // <b>Over the box's own plan, and nowhere else.</b> Every caller evaluates the top at the BODY's position
            // and only afterwards asks whether the body overlaps the box -- and the overlap test is generous by the
            // body's half-extent. So the plane was being read up to `half` metres outside the slab it describes, which
            // on a 0.4 gradient is 0.12 m of lift that is not there. That answer becomes the next tick's feet, and the
            // next tick reads a little further out again: a ratchet that no single evaluation can show, because each
            // one looks reasonable. A top face exists over its own footprint; past the edge, the edge's height stands.
            x = x < MinX ? MinX : (x > MaxX ? MaxX : x);
            z = z < MinZ ? MinZ : (z > MaxZ ? MaxZ : z);

            var cx = (MinX + MaxX) * 0.5f;
            var cz = (MinZ + MaxZ) * 0.5f;
            // <b>NOT clamped to MaxY, and that is measured rather than assumed.</b> A clamp looks obviously right — a
            // plane extrapolated past the ends of the tilted slab it describes climbs into empty air — but MaxY is the
            // plane's height AT THE CENTRE on the wire, not the box's roof, so clamping to it flattens every ramp to a
            // kerb. Measured on the published house: its entry ramp spans z 6.889..11.25 with MaxY −0.128 and a −0.40
            // gradient, so the true surface at z = 8 is +0.30 and the clamp returned −0.128. The ramp stopped being
            // climbable the moment the clamp landed.
            //
            // The over-reach a clamp would remove is small: for a 0.3 m plank at 22 degrees it is 4 cm at the corner of
            // its own bounding box. Fixing it properly means publishing the AABB top as a NINTH number so there is
            // something honest to clamp against — see PlaneY, which is carried but not yet on the wire.
            var plane = float.IsNaN(PlaneY) ? MaxY : PlaneY;
            return plane + (SlopeX * (x - cx)) + (SlopeZ * (z - cz));
        }

        /// <summary>Whether the top face is shallow enough to stand on rather than slide off.</summary>
        /// <remarks>
        /// The gradient of the steepest line on the plane. <see cref="MaxWalkableGrade"/> is the testbench's 46 degrees,
        /// which the published entry ramps (21.8 degrees) and stairs (about 10) sit well inside.
        /// </remarks>
        internal readonly bool Walkable => ((SlopeX * SlopeX) + (SlopeZ * SlopeZ)) <= MaxWalkableGrade * MaxWalkableGrade;
    }

    /// <summary>The plane height a box publishes: its own, or its AABB top for a box that has no separate plane.</summary>
    private static float Plane(in PlanBox b) => float.IsNaN(b.PlaneY) ? b.MaxY : b.PlaneY;

    /// <summary>
    /// The published format revision this reader was written against, and the descriptor <c>schema</c> it accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>demo/SwgTatooine/SwgTatooine.Assets/ASSET_FORMAT.md</c> is the contract</b>, and it carries a revision number
    /// with a changelog. A consumer that knows revision N reads the entries
    /// after N and nothing else, so recording the number here is what makes the next diff mechanical instead of a
    /// re-read of the whole spec.
    /// </para>
    /// <para>
    /// <b><see cref="DescriptorSchema"/> is the breaking-change number and is NOT the revision.</b> It moves only when a
    /// field is removed, renamed, or changes type or meaning; an added field or a new enum value keeps it. A descriptor
    /// carrying a schema this build does not know is not read at all — the structure keeps the footprint box every
    /// structure had before the asset pipeline — rather than being parsed hopefully and collided wrongly.
    /// </para>
    /// </remarks>
    internal const int FormatRevision = 7;

    /// <summary>The one descriptor schema this reader understands. See <see cref="FormatRevision"/>.</summary>
    internal const int DescriptorSchema = 1;

    /// <summary>Whether a descriptor announces a schema this build reads.</summary>
    /// <param name="root">The descriptor's root object.</param>
    /// <returns>Whether to read it.</returns>
    /// <remarks>
    /// Rule 1 of the format's own reader rules. Refusing by returning nothing rather than by throwing is the same
    /// judgement the rest of this file makes: a descriptor this build cannot read is a published asset to fix, not a
    /// server to stop.
    /// </remarks>
    private static bool Readable(JsonElement root) =>
        root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number
            && schema.GetInt32() == DescriptorSchema;

    /// <summary>The steepest top face a body walks up, as a gradient: <c>tan(46 degrees)</c>.</summary>
    /// <remarks>The testbench's `maxSlopeDeg`, so a ramp that is climbable there is climbable here.</remarks>
    internal const float MaxWalkableGrade = 1.0355f;

    /// <summary>By <c>Structure.Asset</c>: the index the wire carries, so a contact needs no name lookup.</summary>
    private readonly PlanBox[][] _byIndex;

    private AssetColliders(PlanBox[][] byIndex) => _byIndex = byIndex;

    /// <summary>How many assets carry collision, for the startup line.</summary>
    internal int AssetCount
    {
        get
        {
            var n = 0;
            foreach (var boxes in _byIndex)
            {
                if (boxes.Length > 0)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>
    /// How far the furthest primitive of any asset reaches PAST the ground the world reserved for it, in metres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The amount by which the cheap gate must grow.</b> A footprint is what the world reserved, and an asset is
    /// not obliged to fit inside it: `tat_house_large` is 18.8 m across against an 18 m footprint, so 0.4 m of its
    /// wall lies outside the box that decides whether to look at its walls at all — a body arriving along the long
    /// axis would be rejected while already inside one. Growing the gate by this fixes it without touching the world's
    /// packing or what the renderer draws, both of which the footprint also decides.
    /// </para>
    /// <para>
    /// <b>The overhang, not the reach — and the difference is the whole value of the gate.</b> This was briefly the
    /// absolute distance from an asset's origin to its furthest primitive, which for `tat_shuttleport` is 23 m. Every
    /// footprint in the world then grew by 23 m, so a body anywhere in a city passed the gate for several structures
    /// at once and paid a <c>TryOpen</c> and two component reads for each, on the one path built to avoid exactly
    /// that. Measured against each kind's own reservation the figure is 0.4 m, and the gate is a gate again.
    /// </para>
    /// </remarks>
    internal float MaxOverhangM { get; private set; }

    /// <summary>Each asset's published doorway, indexed as <see cref="StructureAssets.Table"/> counts them.</summary>
    private DoorPlan[] _doorByIndex = [];

    /// <summary>Each interior's navigation, same indexing; null for an asset that publishes none.</summary>
    private NavGraph[] _navByIndex = [];

    /// <summary>
    /// The overhang of each reserved footprint SIZE, so a gate can charge a structure its own asset's reach rather
    /// than the worst asset's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed on the reservation's half-extent, because that is the one thing about a structure a gate already
    /// knows.</b> The broad phase answers with the box the spatial index holds and nothing else — no component, no
    /// asset id, no kind — and that box IS the reservation
    /// (<c>WorldBuilder</c> writes <see cref="StructureFootprint.HalfExtentM"/> of the kind straight into it). The
    /// reservation is a function of the kind and the kind picks the asset, so the half-extent identifies the overhang
    /// without opening anything.
    /// </para>
    /// <para>
    /// <b>Why this is worth a table.</b> <see cref="MaxOverhangM"/> is the maximum over every asset — 2.25 m, which the
    /// house ramps set — and charging it to every structure made a terminal that overhangs nothing by a centimetre
    /// behave like a house with a ramp: its gate grew by 2.25 m, and worse, <see cref="SimBridge.DiscFor"/> took 2.25 m
    /// off the clearance to it, so every body near one held a disc 2.25 m smaller than the truth and refreshed sooner.
    /// Six of the eight kinds overhang by nothing at all.
    /// </para>
    /// <para>
    /// <b>Entries are merged by MAX</b>, so two kinds reserving the same square share the larger figure and the answer
    /// stays conservative. An unrecognised size falls back to <see cref="MaxOverhangM"/>, which is what the whole world
    /// used before this existed.
    /// </para>
    /// </remarks>
    private (float HalfM, float OverhangM)[] _overhangByHalf = [];

    /// <summary>
    /// How much the sizes in <see cref="_overhangByHalf"/> may be off by and still be recognised, in metres.
    /// </summary>
    /// <remarks>
    /// <b>A tolerance, because the half-extent is measured back out of a box and not read.</b> The index holds
    /// <c>centre ± half</c> as two floats, so a structure 8 km from the origin rounds each end and
    /// <c>(MaxX − MinX) / 2</c> comes back a fraction of a millimetre off the constant that produced it. The published
    /// reservations are 0.8, 2.5, 3, 7, 9, 11 and 23.1 m, so the closest pair is 0.5 m apart — a hundred times this —
    /// and <c>AssetCollisionChecks.NoTwoReservationsAreCloseEnoughToBeConfused</c> pins that, because a size matched to
    /// the wrong kind would under-charge the overhang and that is a correctness bug rather than a slower gate.
    /// </remarks>
    internal const float HalfExtentToleranceM = 0.05f;

    /// <summary>
    /// How far past the reservation an asset standing on a square of this size reaches, in metres.
    /// </summary>
    /// <param name="halfM">The reservation's half-extent, as the broad-phase box gives it.</param>
    /// <returns>That size's overhang, or <see cref="MaxOverhangM"/> for a size this table does not know.</returns>
    internal float OverhangFor(float halfM)
    {
        foreach (ref readonly var entry in _overhangByHalf.AsSpan())
        {
            if (MathF.Abs(entry.HalfM - halfM) <= HalfExtentToleranceM)
            {
                return entry.OverhangM;
            }
        }

        return MaxOverhangM;
    }

    /// <summary>The table <see cref="OverhangFor"/> reads, for the test that pins its sizes apart.</summary>
    internal ReadOnlySpan<(float HalfM, float OverhangM)> OverhangTable => _overhangByHalf;

    /// <summary>How many blocking primitives were kept in total.</summary>
    internal int BoxCount
    {
        get
        {
            var n = 0;
            foreach (var boxes in _byIndex)
            {
                n += boxes.Length;
            }

            return n;
        }
    }

    /// <summary>The blocking primitives of an asset, in its own frame, or an empty span for one with none.</summary>
    /// <param name="assetIndex">A <c>Structure.Asset</c>.</param>
    /// <returns>Its plan boxes.</returns>
    internal ReadOnlySpan<PlanBox> For(int assetIndex) => ArrayFor(assetIndex);

    /// <summary>
    /// The same boxes as the array behind them, for a caller that has to STORE the reference.
    /// </summary>
    /// <param name="assetIndex">A <c>Structure.Asset</c>.</param>
    /// <returns>Its plan boxes; never <see langword="null"/>.</returns>
    /// <remarks>
    /// A <see langword="ref"/> struct may not hold a span whose provenance the compiler cannot see, even when the
    /// array behind it outlives everything — so the one caller that memoises keeps the array and converts at use.
    /// </remarks>
    internal PlanBox[] ArrayFor(int assetIndex) =>
        (uint)assetIndex < (uint)_byIndex.Length ? _byIndex[assetIndex] : [];

    /// <summary>
    /// The blocking primitives of every asset, as the JSON the client collides against.
    /// </summary>
    /// <returns>An array of arrays, indexed by <c>Structure.Asset</c>; an empty inner array means "use the footprint".</returns>
    /// <remarks>
    /// <b>Published rather than re-derived, and that is the whole point.</b> The client used to read the same
    /// descriptors and apply the same chest-height rule, which meant it also had to know which assets that rule had
    /// been checked against — a list that lives here and was on no wire. It invented a wall across the shuttleport's
    /// landing pad, at the centre of every city, and fought the authority about it for ever. Sending the geometry
    /// instead leaves exactly one reader of a descriptor's collision, one copy of the rule, and one list of what it has
    /// been verified against.
    /// </remarks>
    internal string ToJson()
    {
        var json = new System.Text.StringBuilder("[");
        for (var index = 0; index < _byIndex.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append('[');
            var boxes = _byIndex[index];
            for (var i = 0; i < boxes.Length; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                var b = boxes[i];
                // Ten numbers, not four: the vertical span, the top plane and the plan radius all travel, because the
                // client has to make the same floor-or-wall decision the server makes or its prediction fights the
                // authority on every step. The radius is zero for a box, which is most of them.
                json.Append(FormattableString.Invariant(
                    $"[{b.MinX:R},{b.MinZ:R},{b.MaxX:R},{b.MaxZ:R},{b.MinY:R},{b.MaxY:R},{Plane(b):R},{b.SlopeX:R},{b.SlopeZ:R},{b.Radius:R}]"));
            }

            json.Append(']');
        }

        return json.Append(']').ToString();
    }

    /// <summary>
    /// Reads the descriptors of every asset the world may place, from the folder the origin serves.
    /// </summary>
    /// <param name="origin">The pinned asset set.</param>
    /// <returns>The collider table, empty when the catalogue publishes none of the assets this world uses.</returns>
    /// <remarks>
    /// Only the assets on <see cref="Verified"/> are read; everything else keeps its footprint box.
    /// </remarks>
    internal static AssetColliders Load(AssetOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        var wanted = new HashSet<string>(Verified, StringComparer.Ordinal);
        var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(origin.Directory, origin.CatalogueName)));
        var texts = new string[StructureAssets.Table.Length];
        var byIndex = new PlanBox[StructureAssets.Table.Length][];
        for (var i = 0; i < byIndex.Length; i++)
        {
            byIndex[i] = [];
        }

        using (catalogue)
        {
            if (!catalogue.RootElement.TryGetProperty("assets", out var assets))
            {
                return new AssetColliders(byIndex);
            }

            for (var index = 0; index < StructureAssets.Table.Length; index++)
            {
                var id = StructureAssets.Table[index];
                if (!wanted.Contains(id) || !assets.TryGetProperty(id, out var entry)
                    || !entry.TryGetProperty("descriptor", out var file))
                {
                    continue;
                }

                var path = Path.Combine(origin.Directory, file.GetString() ?? string.Empty);
                if (File.Exists(path))
                {
                    texts[index] = File.ReadAllText(path);
                    byIndex[index] = ReadBlocking(texts[index]);
                }
            }
        }

        return FromDescriptors(byIndex, texts);
    }

    /// <summary>
    /// The table from descriptor texts already in memory: each asset's blocking boxes, its doorway and its navigation.
    /// </summary>
    /// <param name="byIndex">Each asset's plan boxes, indexed as <see cref="StructureAssets.Table"/> counts them.</param>
    /// <param name="texts">Each asset's descriptor, same indexing; null where none was read.</param>
    /// <returns>The table.</returns>
    /// <remarks>
    /// <b>A door names the asset behind it, and that asset's spawn is where a body arrives.</b> So a door is resolved against
    /// the descriptor of its target, which is why the texts are read first and the doors second: the house's door needs the
    /// room's <c>entry</c>, and the room's door the house's <c>front</c>.
    /// </remarks>
    internal static AssetColliders FromDescriptors(PlanBox[][] byIndex, string[] texts)
    {
        ArgumentNullException.ThrowIfNull(byIndex);
        ArgumentNullException.ThrowIfNull(texts);

        var doorByIndex = new DoorPlan[StructureAssets.Table.Length];
        var navByIndex = new NavGraph[StructureAssets.Table.Length];
        for (var index = 0; index < texts.Length && index < doorByIndex.Length; index++)
        {
            if (texts[index] == null)
            {
                continue;
            }

            var door = ReadDoor(texts[index], out var target, out var targetSpawn);
            var targetIndex = Array.IndexOf(StructureAssets.Table, target);
            if (door.Exists && targetIndex >= 0 && targetIndex < texts.Length && texts[targetIndex] != null
                && TryReadSpawn(texts[targetIndex], targetSpawn, out var arriveX, out var arriveY, out var arriveZ))
            {
                doorByIndex[index] = door with { TargetAsset = targetIndex, ArriveX = arriveX, ArriveY = arriveY, ArriveZ = arriveZ };
            }

            navByIndex[index] = NavGraph.Read(texts[index]);
        }

        return new AssetColliders(byIndex)
        {
            _overhangByHalf = OverhangTableOf(byIndex),
            MaxOverhangM = OverhangOf(byIndex),
            _doorByIndex = doorByIndex,
            _navByIndex = navByIndex,
        };
    }

    /// <summary>A table built straight from boxes, for a test that does not want a catalogue on disk.</summary>
    /// <param name="byIndex">Each asset's plan boxes, indexed as <see cref="StructureAssets.Table"/> counts them.</param>
    /// <returns>The table, with its overhang measured the way <see cref="Load"/> measures it.</returns>
    internal static AssetColliders FromBoxesForTest(PlanBox[][] byIndex) =>
        new(byIndex) { _overhangByHalf = OverhangTableOf(byIndex), MaxOverhangM = OverhangOf(byIndex) };

    /// <summary>
    /// The largest amount by which any asset's primitives reach past the ground its kind reserves, in metres.
    /// </summary>
    /// <param name="byIndex">Each asset's plan boxes, indexed as <see cref="StructureAssets.Table"/> counts them.</param>
    /// <returns>The overhang, never negative — an asset that fits inside its reservation asks for no growth.</returns>
    /// <remarks>
    /// Measured per asset against <see cref="StructureFootprint.HalfExtentM"/> of the kind that picks it
    /// (<see cref="StructureAssets.KindOf"/>), then maximised. Taking the maximum over assets rather than keeping the
    /// figure per asset is deliberate: the gate's whole purpose is to decide WITHOUT reading a component, and which
    /// asset a structure is lives in one.
    /// </remarks>
    internal static float OverhangOf(PlanBox[][] byIndex)
    {
        var overhang = 0f;
        foreach (var (_, each) in OverhangTableOf(byIndex))
        {
            overhang = MathF.Max(overhang, each);
        }

        return overhang;
    }

    /// <summary>
    /// The overhang of each reserved footprint size, merged by maximum over the assets that stand on it.
    /// </summary>
    /// <param name="byIndex">Each asset's plan boxes, indexed as <see cref="StructureAssets.Table"/> counts them.</param>
    /// <returns>One entry per reservation size any asset names, never negative.</returns>
    /// <remarks>
    /// Measured per asset against <see cref="StructureFootprint.HalfExtentM"/> of the kind that picks it
    /// (<see cref="StructureAssets.KindOf"/>). An asset that fits inside its reservation still earns an entry, with an
    /// overhang of zero: that is the whole point — six of the eight kinds are such, and the old single maximum charged
    /// them the house ramps' 2.25 m.
    /// </remarks>
    internal static (float HalfM, float OverhangM)[] OverhangTableOf(PlanBox[][] byIndex)
    {
        ArgumentNullException.ThrowIfNull(byIndex);

        var table = new List<(float HalfM, float OverhangM)>();
        for (var index = 0; index < byIndex.Length; index++)
        {
            var reach = 0f;
            foreach (var box in byIndex[index])
            {
                reach = MathF.Max(reach, MathF.Max(MathF.Abs(box.MinX), MathF.Abs(box.MaxX)));
                reach = MathF.Max(reach, MathF.Max(MathF.Abs(box.MinZ), MathF.Abs(box.MaxZ)));
            }

            if (index >= StructureAssets.KindOf.Length)
            {
                // No asset in this slot at all, so there is no reservation to compare a reach to. A kind with no
                // published asset gets no entry either, and falls back to the worst case the way the whole world did.
                continue;
            }

            // <b>An asset that publishes NO colliders overhangs by zero, which is a measurement and not an absence.</b>
            // Skipping it here was the bug this comment replaces: the terminals declare no collision, so they earned no
            // entry, so every terminal fell back to the worst case - the house ramps 2.25 m - which is the one case the
            // table was built to fix. A structure with no collider geometry is collided as its footprint box, and a
            // footprint box reaches exactly as far as itself.

            var reserved = StructureFootprint.HalfExtentM(StructureAssets.KindOf[index]);
            var overhang = MathF.Max(0f, reach - reserved);
            var at = -1;
            for (var i = 0; i < table.Count; i++)
            {
                if (MathF.Abs(table[i].HalfM - reserved) <= HalfExtentToleranceM)
                {
                    at = i;
                    break;
                }
            }

            if (at < 0)
            {
                table.Add((reserved, overhang));
            }
            else if (overhang > table[at].OverhangM)
            {
                // Two assets on one reservation: the larger reach decides, or the smaller one's gate would reject the
                // larger one's walls.
                table[at] = (reserved, overhang);
            }
        }

        return [.. table];
    }

    /// <summary>
    /// An asset's doorway and the two places it connects, in the asset's own plan, in the WORLD's frame convention.
    /// </summary>
    /// <remarks>
    /// <para><b>Mirrored in X like every collider, and it was not.</b> A descriptor is in the glTF frame and the world in the renderer's, which mirrors
    /// X (<see cref="ReadBlocking"/>). The door used to be read unmirrored, so on every placement the plane a body had to cross stood at the mirror image of
    /// the doorway drawn and collided: 3 m to the side of the large house's door, 4 m on the small one, in the facade. A body walking through the door it
    /// could see met a wall; the crossing fired only if it pressed the facade on the other side.</para>
    /// <para><b>Only the first portal.</b> Every enterable asset in this pack publishes exactly one. The world spawns one <c>Portal</c> entity per side
    /// of a door, so a second portal is a second pair of entities into the same realm; reading it is the only thing missing.</para>
    /// </remarks>
    /// <param name="CenterX">Where the doorway's mid-plane crosses the floor, east of the asset origin.</param>
    /// <param name="CenterZ">The same point, north.</param>
    /// <param name="HalfX">Half the opening's width.</param>
    /// <param name="HalfZ">Half its depth.</param>
    /// <param name="ForwardX">The direction a body crosses it in, east component.</param>
    /// <param name="ForwardZ">The same direction, north component.</param>
    /// <param name="ApproachX">Where a program-driven body starts walking to the door: the published <c>approach</c>'s first point.</param>
    /// <param name="ApproachZ">The same point, north.</param>
    /// <param name="ApproachEndX">The approach's last point, past the mid-plane: walking to it fires the crossing.</param>
    /// <param name="ApproachEndZ">The same point, north.</param>
    /// <param name="ExitX">This asset's <c>exit_spawn</c>: where a body arrives when it comes back out through this door.</param>
    /// <param name="ExitZ">The same point, north.</param>
    /// <param name="ArriveX">The target's <c>target_spawn</c>, in the TARGET asset's plan: where a body going through arrives.</param>
    /// <param name="ArriveZ">The same point, north.</param>
    /// <param name="TargetAsset">The asset behind the door, as <see cref="StructureAssets.Table"/> indexes it.</param>
    /// <param name="Exists">Whether the door was read whole: geometry, both spawns and its target.</param>
    /// <param name="ExitY">The exit spawn's height above the asset's origin: the feet of a body that comes back out stand there — on a front step,
    /// not on the terrain under it. Not read when <paramref name="ExitOnGround"/>.</param>
    /// <param name="ArriveY">The arrival spawn's height above the target asset's origin.</param>
    /// <param name="ExitOnGround">Whether nothing published stands under the exit, so a body coming out lands on the terrain there — a footprint's stand-in
    /// door. A flag rather than a height of -∞: the height is stored in the database, and an infinity there is a sentinel every reader must know.</param>
    internal readonly record struct DoorPlan(
        float CenterX,
        float CenterZ,
        float HalfX,
        float HalfZ,
        float ForwardX,
        float ForwardZ,
        float ApproachX,
        float ApproachZ,
        float ApproachEndX,
        float ApproachEndZ,
        float ExitX,
        float ExitZ,
        float ArriveX = 0f,
        float ArriveZ = 0f,
        int TargetAsset = StructureAssets.NoAsset,
        bool Exists = false,
        float ExitY = 0f,
        float ArriveY = 0f,
        bool ExitOnGround = false);

    /// <summary>
    /// The doorway a structure stands in for when no asset is published: its kind's footprint with a door in the front face.
    /// </summary>
    /// <param name="halfExtentM">The structure's reserved half extent.</param>
    /// <returns>A door whose geometry is the footprint's, leading nowhere in particular.</returns>
    /// <remarks>
    /// <b>A run with no asset set still has doors</b> — a measured run without <c>--assets</c>, and every check, since the published assets are not in
    /// the repository. The crowd walks through them exactly as it walks through a published one, so the workload's shape does not depend on whether a folder
    /// was found. The plane stands 0.5 m off the face, outside a body pressed against the footprint box, which is the published setback's arithmetic.
    /// </remarks>
    internal static DoorPlan StandInDoor(float halfExtentM) => new(
        CenterX: 0f,
        CenterZ: halfExtentM + 0.5f,
        HalfX: 1f,
        HalfZ: 0.4f,
        ForwardX: 0f,
        ForwardZ: -1f,
        ApproachX: 0f,
        ApproachZ: halfExtentM + 3f,
        ApproachEndX: 0f,
        ApproachEndZ: halfExtentM + 0.25f,
        ExitX: 0f,
        ExitZ: halfExtentM + 1.5f,
        ArriveX: StandInRoomDoor.ExitX,
        ArriveZ: StandInRoomDoor.ExitZ,
        TargetAsset: StructureAssets.NoAsset,
        Exists: true,

        // Nothing published stands under the way back out of a footprint: the body lands on the terrain.
        ExitOnGround: true);

    /// <summary>The way out of a room no asset describes: an open floor, arrived at the origin, left through a door 3 m north of it.</summary>
    internal static readonly DoorPlan StandInRoomDoor = new(
        CenterX: 0f,
        CenterZ: 3f,
        HalfX: 1f,
        HalfZ: 0.4f,
        ForwardX: 0f,
        ForwardZ: 1f,
        ApproachX: 0f,
        ApproachZ: 2f,
        ApproachEndX: 0f,
        ApproachEndZ: 3.25f,
        ExitX: 0f,
        ExitZ: 0f,
        Exists: true);

    /// <summary>The doorway published for an asset, or a <see cref="DoorPlan"/> whose <c>Exists</c> is false.</summary>
    /// <param name="assetIndex">The asset, indexed as <see cref="StructureAssets.Table"/> counts them.</param>
    /// <returns>The door.</returns>
    internal DoorPlan DoorFor(int assetIndex) =>
        (uint)assetIndex < (uint)_doorByIndex.Length ? _doorByIndex[assetIndex] : default;

    /// <summary>An interior's navigation, or <see langword="null"/> for an asset that publishes none.</summary>
    /// <param name="assetIndex">The asset, indexed as <see cref="StructureAssets.Table"/> counts them.</param>
    /// <returns>The graph.</returns>
    internal NavGraph NavFor(int assetIndex) =>
        (uint)assetIndex < (uint)_navByIndex.Length ? _navByIndex[assetIndex] : null;

    /// <summary>How many assets publish a door, for the boot line.</summary>
    internal int DoorCount
    {
        get
        {
            var n = 0;
            foreach (var door in _doorByIndex)
            {
                n += door.Exists ? 1 : 0;
            }

            return n;
        }
    }

    /// <summary>Reads the first doorway an asset publishes, without resolving the spawn on its far side.</summary>
    /// <param name="json">The descriptor.</param>
    /// <returns>The door, or one whose <c>Exists</c> is false.</returns>
    internal static DoorPlan ReadDoor(string json) => ReadDoor(json, out _, out _);

    /// <summary>Reads the first doorway an asset publishes, and which asset and spawn it leads to.</summary>
    /// <param name="json">The descriptor.</param>
    /// <param name="target">The asset id behind it, or null.</param>
    /// <param name="targetSpawn">The spawn in that asset a body arrives at, or null.</param>
    /// <returns>The door with its own side read — geometry, approach, exit spawn — or one whose Exists is false.</returns>
    internal static DoorPlan ReadDoor(string json, out string target, out string targetSpawn)
    {
        target = null;
        targetSpawn = null;
        using var doc = JsonDocument.Parse(json);
        if (!Readable(doc.RootElement))
        {
            return default;
        }

        if (!doc.RootElement.TryGetProperty("portals", out var portals) || portals.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        foreach (var portal in portals.EnumerateArray())
        {
            if (!TryVec3(portal, "center", out var cx, out _, out var cz) || !TryVec3(portal, "half", out var hx, out _, out var hz))
            {
                continue;
            }

            var (fx, fz) = ForwardOf(portal, out var known);
            if (!known)
            {
                continue;
            }

            // Mirrored once, here: a point (x, z) is (-x, z) in the world's convention and a direction likewise.
            cx = -cx;
            fx = -fx;
            var exitSpawn = portal.TryGetProperty("exit_spawn", out var es) ? es.GetString() : null;
            if (!TryReadSpawn(doc.RootElement, exitSpawn, out var exitX, out var exitY, out var exitZ))
            {
                // A door with nowhere to come back out to cannot be used in both directions, which is what a door is for.
                continue;
            }

            // <b>Absent and empty are the same</b> (rule 5), and an asset with no approach still has a door a program can walk through: straight in
            // along the forward, from 3 m out to a quarter metre past the plane — the same shape every published approach has.
            var (ax, az, ex, ez) = ReadApproach(portal, out var have);
            if (!have)
            {
                (ax, az) = (cx - (fx * 3f), cz - (fz * 3f));
                (ex, ez) = (cx + (fx * 0.25f), cz + (fz * 0.25f));
            }

            target = portal.TryGetProperty("target", out var t) ? t.GetString() : null;
            targetSpawn = portal.TryGetProperty("target_spawn", out var ts) ? ts.GetString() : null;
            return new DoorPlan(cx, cz, hx, hz, fx, fz, ax, az, ex, ez, exitX, exitZ, Exists: true, ExitY: exitY);
        }

        return default;
    }

    /// <summary>The first and last points of a portal's approach path, mirrored; the points between are not needed by anything here.</summary>
    /// <param name="portal">The portal element.</param>
    /// <param name="have">Whether it has a usable path of at least two points.</param>
    /// <returns>The start and the end.</returns>
    /// <remarks>
    /// <b>Two points, because a program needs no more:</b> where to walk to first — in front of the ramp — and the direction to follow from there, which
    /// the end gives. Every path the pack publishes is straight in plan (one heading, its points only change height), so following the line from the
    /// first to the last is following the path. A bent path would need the points between, and would be read then.
    /// </remarks>
    private static (float X, float Z, float EndX, float EndZ) ReadApproach(JsonElement portal, out bool have)
    {
        have = false;
        if (!portal.TryGetProperty("approach", out var path) || path.ValueKind != JsonValueKind.Array || path.GetArrayLength() < 2)
        {
            return default;
        }

        var first = path[0];
        var last = path[path.GetArrayLength() - 1];
        if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() < 3 || last.ValueKind != JsonValueKind.Array || last.GetArrayLength() < 3)
        {
            return default;
        }

        have = true;
        return (-first[0].GetSingle(), first[2].GetSingle(), -last[0].GetSingle(), last[2].GetSingle());
    }

    /// <summary>A named spawn's position, mirrored: feet height above the asset's origin, and its plan position.</summary>
    /// <param name="json">The descriptor holding it.</param>
    /// <param name="spawnId">Its id.</param>
    /// <param name="x">East of the asset origin.</param>
    /// <param name="y">Feet height above it.</param>
    /// <param name="z">North.</param>
    /// <returns>Whether it was found.</returns>
    internal static bool TryReadSpawn(string json, string spawnId, out float x, out float y, out float z)
    {
        using var doc = JsonDocument.Parse(json);
        return TryReadSpawn(doc.RootElement, spawnId, out x, out y, out z);
    }

    private static bool TryReadSpawn(JsonElement root, string spawnId, out float x, out float y, out float z)
    {
        x = 0f;
        y = 0f;
        z = 0f;
        if (spawnId == null || !Readable(root) || !root.TryGetProperty("spawns", out var spawns) || spawns.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var spawn in spawns.EnumerateArray())
        {
            if (spawn.TryGetProperty("id", out var id) && string.Equals(id.GetString(), spawnId, StringComparison.Ordinal)
                && TryVec3(spawn, "pos", out x, out y, out z))
            {
                x = -x;
                return true;
            }
        }

        return false;
    }

    /// <summary>A three-number array property.</summary>
    private static bool TryVec3(JsonElement element, string name, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (!element.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 3)
        {
            return false;
        }

        x = v[0].GetSingle();
        y = v[1].GetSingle();
        z = v[2].GetSingle();
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
    }

    /// <summary>
    /// The direction through a doorway, in the asset's own frame: its local forward axis turned by its own rotation.
    /// </summary>
    /// <param name="portal">The portal element.</param>
    /// <returns>The ground-plane components of the forward vector.</returns>
    /// <remarks>
    /// <b>The axis is LOCAL and the rotation is not decoration.</b> Every portal in this pack declares its forward as
    /// "-Z", which reads like a constant and is not one: an interior's door back out carries a rot of a half turn
    /// [0, -1, 0, 0], so its forward in the asset's frame is +Z. Taking the string at face value points the crossing
    /// the wrong way, and because the exterior's rot happens to be the identity it would be right everywhere a body
    /// goes IN and wrong everywhere it tries to come OUT — nobody would ever leave a room, and nothing would throw.
    /// </remarks>
    private static (float X, float Z) ForwardOf(JsonElement portal, out bool known)
    {
        // <b>Rule 3: an axis this build does not know is not -Z.</b> Every portal in the pack today declares "-Z", which
        // is exactly what makes a silent default dangerous: it would be right for every asset that exists and wrong,
        // without a word, for the first one that does not. A door whose direction cannot be read is no door.
        var axis = portal.TryGetProperty("forward", out var f) ? f.GetString() : null;
        known = true;
        var (ax, ay, az) = axis switch
        {
            "+X" => (1f, 0f, 0f),
            "-X" => (-1f, 0f, 0f),
            "+Y" => (0f, 1f, 0f),
            "-Y" => (0f, -1f, 0f),
            "+Z" => (0f, 0f, 1f),
            "-Z" => (0f, 0f, -1f),
            _ => Unknown(out known),
        };

        if (!portal.TryGetProperty("rot", out var rot) || rot.ValueKind != JsonValueKind.Array || rot.GetArrayLength() < 4)
        {
            return (ax, az);
        }

        // v + 2q_v x (q_v x v + w v), the usual quaternion rotation written out: no vector type is worth a dependency here.
        var qx = rot[0].GetSingle();
        var qy = rot[1].GetSingle();
        var qz = rot[2].GetSingle();
        var qw = rot[3].GetSingle();
        var tx = ((qy * az) - (qz * ay)) + (qw * ax);
        var ty = ((qz * ax) - (qx * az)) + (qw * ay);
        var tz = ((qx * ay) - (qy * ax)) + (qw * az);
        return (ax + (2f * ((qy * tz) - (qz * ty))), az + (2f * ((qx * ty) - (qy * tx))));
    }

    /// <summary>
    /// The blocking primitives of one descriptor, as plan boxes in the WORLD's frame convention.
    /// </summary>
    /// <param name="json">A descriptor document.</param>
    /// <returns>Its blocking boxes; empty when it declares no collision.</returns>
    /// <remarks>
    /// <b>The X mirror is applied here, once.</b> A descriptor is in the glTF frame, which is right-handed, and the
    /// world is the renderer's left-handed one — the loader puts every model under a root that mirrors X, so anything
    /// read from a descriptor has to take the same mirror or it lands on the other side of the building from the wall
    /// it describes. Doing it at load means the per-contact path is a translate and a quarter turn.
    /// </remarks>
    internal static PlanBox[] ReadBlocking(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!Readable(doc.RootElement))
        {
            return [];
        }

        if (!doc.RootElement.TryGetProperty("collision", out var collision) || collision.ValueKind != JsonValueKind.Object
            || !collision.TryGetProperty("shapes", out var shapes) || shapes.ValueKind != JsonValueKind.Array
            || !collision.TryGetProperty("colliders", out var colliders) || colliders.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var boxes = new List<PlanBox>();
        foreach (var collider in colliders.EnumerateArray())
        {
            if (!collider.TryGetProperty("shape", out var shapeIndex) || !collider.TryGetProperty("pos", out var pos))
            {
                continue;
            }

            var index = shapeIndex.GetInt32();
            if (shapes.ValueKind != JsonValueKind.Array || index < 0 || index >= shapes.GetArrayLength()
                || pos.ValueKind != JsonValueKind.Array || pos.GetArrayLength() < 3)
            {
                // A descriptor this build cannot read is a published asset to fix, not a server to stop: the structure
                // keeps its footprint box, which is what every structure had before the asset pipeline.
                continue;
            }

            var shape = shapes[index];
            var (hx, hy, hz, radius) = HalfExtents(shape);
            if (!float.IsFinite(hx) || hx < 0f)
            {
                // Rule 3: an unknown `shapes[].type` is skipped rather than guessed. It used to fall through to zeros,
                // which is not "no shape" but a degenerate box AT A POSITION -- something a body can be exactly inside
                // and never be pushed out of, published by a pipeline this build is simply older than.
                continue;
            }

            var rot = collider.TryGetProperty("rot", out var r) ? Quat(r) : (0f, 0f, 0f, 1f);

            // The primitive's own extent along each world axis, which is |R| applied to its half extents: the same
            // conservative box the testbench builds for its broad phase, and exact for the axis-aligned majority.
            var (ex, ey, ez) = RotatedExtent(rot, hx, hy, hz);
            // <b>Every collider is kept, with its vertical span.</b> Whether one is a floor, a step or a wall depends
            // on the height of the feet that meet it, and that is not known here — see PlanBox and PushOutOfShapes.
            var y = (float)pos[1].GetDouble();
            var x = (float)pos[0].GetDouble();
            var z = (float)pos[2].GetDouble();

            // The top face's own plane, so a ramp stays a ramp instead of becoming a wall its own height.
            var (slopeX, slopeZ, lift) = TopPlane(rot, hx, hy, hz);

            // Mirrored in X, which turns [min, max] into [-max, -min] and flips the sign of any slope along it.
            // Y is not mirrored: up is up in both frames.
            //
            // <b>The AABB's top and the plane's height at the centre are two different numbers</b>, and keeping only the
            // second is what made a body fly: `TopAt` then had nothing to clamp its extrapolation against.
            // <b>Round in plan only while it still stands up.</b> A cylinder tilted off vertical has an ellipse for a
            // plan and a circle is then the wrong answer in the other direction — too small — so it keeps the bounding
            // box it always had. Every round collider in this pack stands upright; the test is for the pack that does not.
            var upright = radius > 0f && MathF.Abs(Rotate(rot, 0f, 1f, 0f).Y) > 0.999f;
            var box = new PlanBox(-(x + ex), z - ez, -(x - ex), z + ez, y - ey, y + ey, y + lift, -slopeX, slopeZ,
                upright ? radius : 0f);
            if (!float.IsFinite(box.MinX) || !float.IsFinite(box.MinZ) || !float.IsFinite(box.MaxX) || !float.IsFinite(box.MaxZ)
                || !float.IsFinite(box.MinY) || !float.IsFinite(box.MaxY) || !float.IsFinite(box.PlaneY)
                || !float.IsFinite(box.SlopeX) || !float.IsFinite(box.SlopeZ))
            {
                // A non-finite box is "inside" every test it takes part in, on both sides of the wire: it would make a
                // body collide with everything and never leave. Dropped here, where it is a file to fix.
                continue;
            }

            boxes.Add(box);
        }

        return boxes.ToArray();
    }

    /// <summary>The half extents of a shape, and the radius of its plan when that plan is a circle.</summary>
    /// <param name="shape">The shape element.</param>
    /// <returns>Its half extents along its own axes, and its plan radius — zero for a box.</returns>
    private static (float X, float Y, float Z, float Radius) HalfExtents(JsonElement shape)
    {
        var type = shape.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "box":
                var half = shape.GetProperty("half");
                return ((float)half[0].GetDouble(), (float)half[1].GetDouble(), (float)half[2].GetDouble(), 0f);
            case "sphere":
                var radius = (float)shape.GetProperty("radius").GetDouble();
                return (radius, radius, radius, radius);
            case "cylinder":
            case "capsule":
                var r = (float)shape.GetProperty("radius").GetDouble();
                var hh = (float)shape.GetProperty("half_height").GetDouble();

                // Both are along their local +Y, as the contract states. A capsule's caps add its radius to the height;
                // a cylinder's do not.
                return (r, type == "capsule" ? hh + r : hh, r, r);
            default:
                // NOT zeros: a zero box is a real box at a real position. NaN is the one value every caller's finiteness
                // test already rejects, and the caller skips the collider. See rule 3.
                return (float.NaN, float.NaN, float.NaN, 0f);
        }
    }

    private static (float X, float Y, float Z, float W) Quat(JsonElement r) =>
        r.GetArrayLength() < 4
            ? (0f, 0f, 0f, 1f)
            : ((float)r[0].GetDouble(), (float)r[1].GetDouble(), (float)r[2].GetDouble(), (float)r[3].GetDouble());

    /// <summary>
    /// The top face of a rotated box, as a gradient and the height of that face above the box's centre.
    /// </summary>
    /// <param name="q">The collider's rotation.</param>
    /// <param name="hx">Half extent along its own X.</param>
    /// <param name="hy">Half extent along its own Y.</param>
    /// <param name="hz">Half extent along its own Z.</param>
    /// <returns>How the top rises per metre east and north, and how far it sits above the centre at the centre.</returns>
    /// <remarks>
    /// Of the box's six faces the one that faces most nearly upwards is the one a body would stand on, so its outward
    /// normal is the one worth keeping. A plane through a point with normal <c>n</c> has height
    /// <c>y = p.y - (n.x dx + n.z dz) / n.y</c>, which is where the two gradients come from; a face normal pointing
    /// straight up gives zero for both and the flat case costs nothing.
    /// </remarks>
    private static (float SlopeX, float SlopeZ, float Lift) TopPlane((float X, float Y, float Z, float W) q, float hx, float hy, float hz)
    {
        Span<(float X, float Y, float Z, float H)> axes =
        [
            Axis(Rotate(q, 1f, 0f, 0f), hx),
            Axis(Rotate(q, 0f, 1f, 0f), hy),
            Axis(Rotate(q, 0f, 0f, 1f), hz),
        ];

        var best = 0;
        for (var i = 1; i < axes.Length; i++)
        {
            if (MathF.Abs(axes[i].Y) > MathF.Abs(axes[best].Y))
            {
                best = i;
            }
        }

        var (nx, ny, nz, h) = axes[best];
        if (ny < 0f)
        {
            // Take the face that points UP, not the opposite one.
            (nx, ny, nz) = (-nx, -ny, -nz);
        }

        if (ny <= 1e-4f)
        {
            // Edge-on: no top face worth speaking of, so the bounding box's own top is the honest answer.
            return (0f, 0f, 0f);
        }

        // The face centre sits h along the normal from the box centre, so the plane's height AT the box centre is h/1
        // projected back — which for a unit normal is simply h / ny... no: the face centre is offset along n, and the
        // plane through it has height (n . (centre + n h) - n . centre) / ny = h / ny above the centre.
        return (-nx / ny, -nz / ny, h / ny);
    }

    /// <summary>An axis this build does not know, for <see cref="ForwardOf"/>'s switch.</summary>
    /// <param name="known">Set to false.</param>
    /// <returns>Zero, which the caller discards.</returns>
    private static (float X, float Y, float Z) Unknown(out bool known)
    {
        known = false;
        return (0f, 0f, 0f);
    }

    /// <summary>One of a box's axes as a unit normal plus the half extent along it.</summary>
    private static (float X, float Y, float Z, float H) Axis((float X, float Y, float Z) v, float h) => (v.X, v.Y, v.Z, h);

    /// <summary>The axis-aligned extent of a box of these half extents under this rotation.</summary>
    private static (float X, float Y, float Z) RotatedExtent((float X, float Y, float Z, float W) q, float hx, float hy, float hz)
    {
        var (ax, ay, az) = Rotate(q, hx, 0f, 0f);
        var (bx, by, bz) = Rotate(q, 0f, hy, 0f);
        var (cx, cy, cz) = Rotate(q, 0f, 0f, hz);
        return (
            MathF.Abs(ax) + MathF.Abs(bx) + MathF.Abs(cx),
            MathF.Abs(ay) + MathF.Abs(by) + MathF.Abs(cy),
            MathF.Abs(az) + MathF.Abs(bz) + MathF.Abs(cz));
    }

    /// <summary><c>q v q*</c>, expanded.</summary>
    private static (float X, float Y, float Z) Rotate((float X, float Y, float Z, float W) q, float vx, float vy, float vz)
    {
        var tx = 2f * ((q.Y * vz) - (q.Z * vy));
        var ty = 2f * ((q.Z * vx) - (q.X * vz));
        var tz = 2f * ((q.X * vy) - (q.Y * vx));
        return (
            vx + (q.W * tx) + ((q.Y * tz) - (q.Z * ty)),
            vy + (q.W * ty) + ((q.Z * tx) - (q.X * tz)),
            vz + (q.W * tz) + ((q.X * ty) - (q.Y * tx)));
    }
}
