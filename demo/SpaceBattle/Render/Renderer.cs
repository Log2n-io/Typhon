using System;
using System.Collections.Generic;
using SFML.Graphics;
using SFML.System;

namespace SpaceBattle;

/// <summary>
/// Draws the world and, more importantly, the engine's spatial state on top of it.
/// </summary>
/// <remarks>
/// <para>
/// Everything is batched into <see cref="VertexArray"/>s rather than per-entity <c>Shape</c> objects — at a few
/// thousand ships a per-entity <c>CircleShape</c> costs more than the whole simulation tick.
/// </para>
/// <para>
/// <b>The renderer is an independent reader of Typhon, not a consumer of simulation state.</b> It opens its own
/// transaction, runs at frame rate rather than tick rate, and asks the database what is in front of the camera. It
/// shares no buffers with <see cref="Simulation"/> and would keep working if the simulation were paused, replaced,
/// or moved to another thread. That separation is the whole reason this demo can scale the two independently.
/// </para>
/// <para>
/// <b>Two passes over clusters, deliberately.</b> The diagnostics pass visits EVERY cluster to collect AABB geometry
/// for the HUD and the selectivity probe — those must keep describing the whole world regardless of where the
/// camera is pointing, or the numbers would silently become "the part of the world you happen to be looking at".
/// The draw pass visits only what is visible, resolved through the engine's own two-level index (see
/// <see cref="VisibleClusters"/>). The first is O(clusters) and cheap; the second is what stops the renderer being
/// O(entities-in-the-world).
/// </para>
/// </remarks>
internal sealed class Renderer
{
    private readonly Config _cfg;
    private readonly TyphonHost _host;
    private readonly Simulation _sim;

    private readonly VertexArray _ships = new(PrimitiveType.Triangles);
    private readonly VertexArray _shots = new(PrimitiveType.Points);
    private readonly VertexArray _lines = new(PrimitiveType.Lines);
    private readonly VertexArray _heat = new(PrimitiveType.Triangles);
    /// <summary>Translucent fills for the cluster AABBs — overlap reads as accumulating brightness.</summary>
    private readonly VertexArray _aabbFill = new(PrimitiveType.Triangles);
    private readonly VertexArray _rocks = new(PrimitiveType.Triangles);
    private readonly VertexArray _pickups = new(PrimitiveType.Triangles);
    private readonly VertexArray _boostShots = new(PrimitiveType.Triangles);
    private readonly VertexArray _shields = new(PrimitiveType.Lines);
    private readonly VertexArray _stations = new(PrimitiveType.Triangles);

    /// <summary>
    /// LOD 1 entity markers. Quads rather than <see cref="PrimitiveType.Points"/> because an SFML point is exactly
    /// one pixel at every zoom, which makes the minimum-size clamp — the knob the whole LOD rule is built on —
    /// impossible to express. Six vertices per entity is irrelevant at these counts.
    /// </summary>
    private readonly VertexArray _points = new(PrimitiveType.Triangles);

    /// <summary>The LOD 2 aggregate, drawn in world space so it lines up with everything else.</summary>
    private readonly VertexArray _densityQuads = new(PrimitiveType.Triangles);

    /// <summary>Per-archetype cluster boxes, kept for the HUD and the selectivity probe.</summary>
    public readonly List<ClusterBox> ClusterBoxes = new();

    /// <summary>
    /// Stations and asteroids, gathered every frame. Shared with the minimap so both views mark the same places
    /// from the same data — a navigation aid that disagrees with the view it serves is worse than none.
    /// </summary>
    public readonly List<Landmark> Landmarks = new();

    /// <summary>The far-zoom aggregate. Public because the minimap draws the same data at a different transform.</summary>
    public readonly DensityField Density = new();

    public readonly ViewLod Lod = new();

    private readonly VisibleClusters _cull = new();

    public int VisibleShips { get; private set; }
    public int VisibleShots { get; private set; }
    public int DrawnClusters { get; private set; }

    /// <summary>Entities that reached a vertex buffer this frame. The number culling is meant to shrink.</summary>
    public int EntitiesDrawn { get; private set; }

    /// <summary>
    /// Laden miners that actually got a cargo cue this frame, split by tier. Exists because the cue was written,
    /// looked correct in the source, and rendered nothing at all for its entire life — it was queued into a vertex
    /// array submitted before the hull that covers it. "The code emits it" and "you can see it" are separate claims
    /// and only the second one matters; this counter is what makes the first one checkable without a screenshot.
    /// </summary>
    public int CargoCuesDrawn { get; private set; }

    /// <summary>Estimated entities inside the camera rect, from level-1 occupancy. Drives the LOD saturation rule.</summary>
    public int EntitiesInView { get; private set; }

    // Culling telemetry, summed across archetypes for the frame.
    public int CullCells { get; private set; }
    public int CullCandidates { get; private set; }
    public int CullPassed { get; private set; }
    public bool CullActive { get; private set; }

    /// <summary>Current selection, stashed for AddClusterBox — it is called from five builders and threading the
    /// selection through all of them would be noise.</summary>
    private Selection _sel;
    private Camera _cam;
    private WorldRect _visible;
    private int _densityAge = int.MaxValue;

    /// <summary>Clusters holding exactly one entity — their AABB is a point.</summary>
    public int SingletonClusters { get; private set; }

    /// <summary>Clusters whose AABB is too large to fill — i.e. spatially degenerate ones. A direct symptom count.</summary>
    public int OversizedClusters { get; private set; }

    internal static readonly Color FactionA = new(90, 170, 255);
    // Orange, not the salmon-red it used to be. The damage flash is red (255,60,50), and at (255,120,90) faction B
    // sat close enough to it in hue that a flashing BLUE station was indistinguishable from a healthy faction-B one
    // — the single most confusing thing on the map, because it inverted who was winning. Hue now carries team and
    // only team; red is reserved for "taking hull damage" and belongs to nobody.
    internal static readonly Color FactionB = new(255, 150, 30);
    private static readonly Color CellLine = new(60, 70, 90);
    private static readonly Color AabbShip = new(80, 230, 160, 200);
    private static readonly Color AabbShot = new(240, 220, 90, 150);
    private static readonly Color AabbStation = new(200, 120, 255, 200);
    private static readonly Color AabbRock = new(160, 150, 120, 190);

    // Deliberately dark and saturated, not the asteroids' pale tan. A laden miner's cue has to read against BOTH
    // miner colours, and MinerB (255,200,140) is close enough to the rock tan that the old cue would have been
    // invisible on half the fleet even had it been drawn on top.
    private static readonly Color CargoColor = new(120, 74, 32);

    private static readonly Color AabbLoot = new(255, 255, 255, 220);
    private static readonly Color PowerColor = new(255, 70, 50);
    private static readonly Color ShieldColor = new(90, 225, 255);

    /// <summary>
    /// Blends a miner's marker toward the ore colour by how full its hold is — the LOD 1 cargo cue. The floor of
    /// 0.20 matters: the caller only invokes this when <c>Cargo &gt; 0</c>, so even a miner one tick into its haul
    /// must shift enough to be seen. Both endpoints are valid bytes and t is clamped, so the result cannot leave
    /// the channel range.
    /// </summary>
    /// <remarks>
    /// Capped at 0.45, down from 0.85. At 0.85 the blend was strong enough to erase the team colour entirely: a
    /// full faction-A miner came out (125,96,65) and a full faction-B one (140,93,48) — the same brown dot at the
    /// 2 px size this tier draws, and since most miners on screen are commuting home full, that was most of them.
    /// A cue that identifies cargo must not cost the identification of who owns it. At 0.45 laden reads as a
    /// darker, muddier version of the team's own hue, which keeps both facts legible at once.
    /// </remarks>
    private static Color LadenTint(Color baseCol, float full)
    {
        var t = 0.20f + 0.25f * Math.Clamp(full, 0f, 1f);
        return new Color(
            (byte)(baseCol.R + (CargoColor.R - baseCol.R) * t),
            (byte)(baseCol.G + (CargoColor.G - baseCol.G) * t),
            (byte)(baseCol.B + (CargoColor.B - baseCol.B) * t));
    }

    /// <summary>
    /// A stable, well-separated colour per (archetype, cluster). Hues are walked by the golden ratio so that
    /// consecutive chunk ids land far apart on the wheel rather than in a gradient — neighbouring clusters must be
    /// told apart at a glance, which a linear hue ramp does not give you.
    /// </summary>
    private static Color ClusterColor(int archetypeId, int chunkId)
    {
        unchecked
        {
            var h = (uint)(chunkId * 2654435761u + (uint)archetypeId * 40503u);
            var hue = (h % 10007u) / 10007f;
            hue = (hue + 0.618033988f) % 1f;
            return HsvToRgb(hue, 0.62f, 1f);
        }
    }

    private static Color HsvToRgb(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6f) % 6;
        var f = h * 6f - MathF.Floor(h * 6f);
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);
        float r, g, b;
        switch (i)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return new Color((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f));
    }

    public Renderer(Config cfg, TyphonHost host, Simulation sim)
    {
        _cfg = cfg;
        _host = host;
        _sim = sim;
    }

    public void Draw(IRenderTarget target, Camera cam, Selection sel, uint winW, uint winH)
    {
        _ships.Clear();
        _shots.Clear();
        _lines.Clear();
        _heat.Clear();
        _aabbFill.Clear();
        _rocks.Clear();
        _pickups.Clear();
        _boostShots.Clear();
        _shields.Clear();
        _stations.Clear();
        _points.Clear();
        _densityQuads.Clear();
        ClusterBoxes.Clear();
        Landmarks.Clear();

        // Emitted here rather than from the ship pass, and deliberately so: that pass only walks VISIBLE clusters, so a
        // marker built there would vanish from the minimap the moment the camera looked away — which is the one time a
        // minimap marker is worth having. The simulation records the position every tick regardless of what is on
        // screen, so this is the source that answers "where is it" rather than "where is it, if you can already see it".
        if (_sim != null)
        {
            for (var f = 0; f < _sim.TheOneAlive.Length; f++)
            {
                if (!_sim.TheOneAlive[f])
                {
                    continue;
                }
                Landmarks.Add(new Landmark
                {
                    X = _sim.TheOnePos[f].X,
                    Y = _sim.TheOnePos[f].Y,
                    Kind = LandmarkKind.TheOne,
                    Faction = (byte)f,
                });
            }
        }
        _sel = sel;
        _cam = cam;
        SingletonClusters = 0;
        VisibleShips = 0;
        VisibleShots = 0;
        DrawnClusters = 0;
        OversizedClusters = 0;
        EntitiesDrawn = 0;
        CargoCuesDrawn = 0;
        CullCells = 0;
        CullCandidates = 0;
        CullPassed = 0;
        CullActive = false;

        _visible = cam.VisibleRect(_cfg.CullMargin);

        // The population estimate feeding the LOD rule comes from level-1 occupancy, NOT from last frame's draw
        // count. Using the draw count would be circular: the density tier draws no entities, so the estimate would
        // collapse to zero and immediately bounce the tier back — a two-frame oscillation with no stable state.
        EntitiesInView = _host.CountEntitiesInRect(_visible);
        Lod.Update(_cfg, cam, EntitiesInView, winW, winH);

        if (_cfg.ShowCells)
        {
            BuildGrid();
        }

        // Rebuilt on a cadence, for BOTH consumers — the LOD 2 overlay as much as the minimap. Building at frame
        // rate is what makes the entity-binning source expensive at scale, and it buys nothing visible: a bin is
        // ~780 m across and a ship covers 800 m/s, so in four frames an entity crosses a tenth of one. The
        // crossfade still animates every frame, because the blend weight is applied at draw time, not at build.
        //
        // Saturating increment. Seeded at the maximum so the first frame always builds — but a plain ++ on that
        // seed wraps to int.MinValue, which parks the counter below the threshold effectively forever. The symptom
        // was an empty minimap and a density field reporting zero bins, with no error anywhere.
        if (_densityAge < int.MaxValue)
        {
            _densityAge++;
        }
        var wantForLod = Lod.DensityWeight > 0f;
        if ((wantForLod || _cfg.ShowMinimap) && _densityAge >= Math.Max(1, _cfg.DensityRefreshFrames))
        {
            Density.Build(_cfg, _host);
            _densityAge = 0;
        }
        if (wantForLod)
        {
            Density.AppendWorldQuads(_densityQuads, _cfg, in _visible, Lod.DensityWeight);
        }

        using var tx = _host.DBE.CreateQuickTransaction();

        // Pass 1 — diagnostics. Every cluster, every archetype, regardless of the camera.
        CollectClusterBoxes(tx);

        // Pass 2a — LANDMARKS. Always drawn, at every tier. Their count is fixed by the scenario rather than by the
        // population, so the cost argument that collapses ships into the density field does not reach them, and a
        // far view with no stations or ore fields on it is an abstract heat map rather than a map.
        if (_cfg.ShowStations)
        {
            BuildStations(tx);
        }
        BuildAsteroids(tx);
        // A pickup is a landmark too — at most one is alive, and it is the single most important thing on the map
        // while it lasts, so it must not vanish at the zoom where you would want to see who is winning the race.
        BuildPickups(tx);

        // Pass 2b — the population. Culled, and skipped entirely once it has collapsed into the aggregate.
        if (Lod.Tier != LodTier.Density)
        {
            BuildShips(tx, sel);
            if (_cfg.ShowShots)
            {
                BuildShots(tx);
            }
        }

        if (_heat.VertexCount > 0)
        {
            target.Draw(_heat);
        }
        if (_densityQuads.VertexCount > 0)
        {
            target.Draw(_densityQuads);
        }
        if (_aabbFill.VertexCount > 0)
        {
            target.Draw(_aabbFill);
        }
        if (_rocks.VertexCount > 0)
        {
            target.Draw(_rocks);
        }
        if (_lines.VertexCount > 0)
        {
            target.Draw(_lines);
        }
        if (_stations.VertexCount > 0)
        {
            target.Draw(_stations);
        }
        if (_ships.VertexCount > 0)
        {
            target.Draw(_ships);
        }
        if (_points.VertexCount > 0)
        {
            target.Draw(_points);
        }
        if (_shots.VertexCount > 0)
        {
            target.Draw(_shots);
        }
        if (_boostShots.VertexCount > 0)
        {
            target.Draw(_boostShots);
        }
        if (_shields.VertexCount > 0)
        {
            target.Draw(_shields);
        }
        if (_pickups.VertexCount > 0)
        {
            target.Draw(_pickups);
        }
    }

    // ─── Level 1: the grid ────────────────────────────────────────────────────────────────────────────────────────

    private void BuildGrid()
    {
        var g = _host.GridConfig;
        var cs = g.CellSize;
        var w = g.GridWidth;
        var h = g.GridHeight;

        // Only the cells on screen. Zoomed in over a 50x50 grid this is a handful instead of 2,500.
        var inv = 1f / cs;
        var vx0 = Math.Clamp((int)MathF.Floor((_visible.MinX - g.WorldMin.X) * inv), 0, w - 1);
        var vy0 = Math.Clamp((int)MathF.Floor((_visible.MinY - g.WorldMin.Y) * inv), 0, h - 1);
        var vx1 = Math.Clamp((int)MathF.Floor((_visible.MaxX - g.WorldMin.X) * inv), 0, w - 1);
        var vy1 = Math.Clamp((int)MathF.Floor((_visible.MaxY - g.WorldMin.Y) * inv), 0, h - 1);

        if (_cfg.ShowCellHeat)
        {
            var maxN = 1;
            for (var cy = vy0; cy <= vy1; cy++)
            {
                for (var cx = vx0; cx <= vx1; cx++)
                {
                    var n = _host.CellEntityCount(CellKey(cx, cy));
                    if (n > maxN)
                    {
                        maxN = n;
                    }
                }
            }
            for (var cy = vy0; cy <= vy1; cy++)
            {
                for (var cx = vx0; cx <= vx1; cx++)
                {
                    var n = _host.CellEntityCount(CellKey(cx, cy));
                    if (n == 0)
                    {
                        continue;
                    }
                    // sqrt keeps sparse cells visible next to one very hot cell
                    var t = MathF.Sqrt(n / (float)maxN);
                    var c = new Color((byte)(20 + 60 * t), (byte)(30 + 40 * t), (byte)(70 + 90 * t), (byte)(40 + 110 * t));
                    var x0 = g.WorldMin.X + cx * cs;
                    var y0 = g.WorldMin.Y + cy * cs;
                    Quad(_heat, x0, y0, x0 + cs, y0 + cs, c);
                }
            }
        }

        for (var cy = vy0; cy <= vy1 + 1; cy++)
        {
            var y = g.WorldMin.Y + cy * cs;
            Line(_lines, g.WorldMin.X + vx0 * cs, y, g.WorldMin.X + (vx1 + 1) * cs, y, CellLine);
        }
        for (var cx = vx0; cx <= vx1 + 1; cx++)
        {
            var x = g.WorldMin.X + cx * cs;
            Line(_lines, x, g.WorldMin.Y + vy0 * cs, x, g.WorldMin.Y + (vy1 + 1) * cs, CellLine);
        }
    }

    private int CellKey(int cx, int cy) => _host.Grid.ComputeCellKey(cx, cy);

    // ─── Level 2: clusters ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The diagnostics pass. Uncculled on purpose — see the class remarks.
    /// </summary>
    private void CollectClusterBoxes(Transaction tx)
    {
        {
            using var acc = tx.For<Station>();
            using var e = acc.GetClusterEnumerator();
            foreach (var c in e)
            {
                AddClusterBox(_host.StationArchetypeId, c.ChunkId, in c.SpatialBounds, c.LiveCount, AabbStation,
                              spriteClearance: _cfg.ShipRadius * 8f);
            }
        }
        {
            using var acc = tx.For<Rock>();
            using var e = acc.GetClusterEnumerator();
            foreach (var c in e)
            {
                AddClusterBox(_host.RockArchetypeId, c.ChunkId, in c.SpatialBounds, c.LiveCount, AabbRock,
                              spriteClearance: _cfg.AsteroidRadius * 1.3f);
            }
        }
        {
            using var acc = tx.For<Loot>();
            using var e = acc.GetClusterEnumerator();
            foreach (var c in e)
            {
                AddClusterBox(_host.LootArchetypeId, c.ChunkId, in c.SpatialBounds, c.LiveCount, AabbLoot,
                              spriteClearance: _cfg.PickupRadius * 1.6f);
            }
        }
        {
            using var acc = tx.For<Ship>();
            using var e = acc.GetClusterEnumerator();
            foreach (var c in e)
            {
                AddClusterBox(_host.ShipArchetypeId, c.ChunkId, in c.SpatialBounds, c.LiveCount, AabbShip,
                              spriteClearance: _cfg.ShipRadius * 2.4f);
            }
        }
        if (_cfg.ShowShots)
        {
            using var acc = tx.For<Shot>();
            using var e = acc.GetClusterEnumerator();
            foreach (var c in e)
            {
                AddClusterBox(_host.ShotArchetypeId, c.ChunkId, in c.SpatialBounds, c.LiveCount, AabbShot);
            }
        }
    }

    private void AddClusterBox(int archetypeId, int chunkId, in ClusterSpatialAabb a, int liveCount, Color col,
                               float spriteClearance = 0f)
    {
        // The empty sentinel is +inf/-inf — a cluster with no spatial index, or none live.
        // All four components must be finite — the original check omitted Y, and a single non-finite vertex in a
        // Triangles array renders as undefined geometry across the whole viewport.
        if (!(a.MinX <= a.MaxX) || !(a.MinY <= a.MaxY) ||
            !float.IsFinite(a.MinX) || !float.IsFinite(a.MaxX) || !float.IsFinite(a.MinY) || !float.IsFinite(a.MaxY))
        {
            return;
        }
        var homeCell = _host.ClusterHomeCell(archetypeId, chunkId);
        var centreCell = _host.Grid.WorldToCellKey(0.5f * (a.MinX + a.MaxX), 0.5f * (a.MinY + a.MaxY));
        ClusterBoxes.Add(new ClusterBox
        {
            ArchetypeId = archetypeId,
            ChunkId = chunkId,
            MinX = a.MinX, MinY = a.MinY, MaxX = a.MaxX, MaxY = a.MaxY,
            LiveCount = liveCount,
            HomeCellKey = homeCell,
            CentreCellKey = centreCell,
        });
        DrawnClusters++;

        if (liveCount <= 1)
        {
            SingletonClusters++;
        }

        var isSelected = _sel.HasSelection && _sel.ArchetypeId == archetypeId && _sel.ChunkId == chunkId;

        // The selected entity's cluster is always drawn, even with the AABB overlay switched off — selecting a ship
        // is a request to see where it lives.
        if (!_cfg.ShowClusterAabb && !isSelected)
        {
            return;
        }
        // Off-screen boxes are still COLLECTED (the stats above) but not emitted as geometry.
        if (!isSelected && !_visible.Overlaps(a.MinX, a.MinY, a.MaxX, a.MaxY))
        {
            return;
        }

        // A cluster whose AABB centre no longer agrees with its recorded home cell is inside the hysteresis dead
        // zone (or waiting on the outlier guard). That disagreement is one of the things this tool exists to show,
        // so it gets its own colour rather than being averaged away.
        // In cluster-colour mode the box takes its cluster's identity colour, so a box and the entities inside it
        // match. Drift is not colour-coded here — colour is spoken for.
        var drifted = homeCell >= 0 && centreCell != homeCell;
        var c = _cfg.ClusterColorMode
            ? ClusterColor(archetypeId, chunkId)
            : drifted ? new Color(255, 80, 80, 230) : col;

        // Inflate for DISPLAY only. Two floors: a pixel floor so a degenerate box is visible at any zoom, and a
        // sprite-clearance floor so the box is never drawn underneath the very entities it contains.
        float bx0 = a.MinX, by0 = a.MinY, bx1 = a.MaxX, by1 = a.MaxY;
        if (_cfg.MinClusterBoxPixels > 0f)
        {
            var scale = _cam?.Scale ?? 1f;
            var minWorld = scale > 1e-6f ? _cfg.MinClusterBoxPixels / scale : 0f;
            minWorld = MathF.Max(minWorld, spriteClearance);
            Inflate(ref bx0, ref bx1, minWorld);
            Inflate(ref by0, ref by1, minWorld);
        }

        var borderA = (byte)Math.Clamp((int)((isSelected ? _cfg.SelectedBorderAlpha : _cfg.ClusterBorderAlpha) * 255f), 0, 255);
        // Floor a non-zero request at 1: 8-bit alpha cannot represent anything smaller, and truncating to 0 would
        // silently turn the fill off rather than making it faint.
        var fillRaw = (isSelected ? _cfg.SelectedFillAlpha : _cfg.ClusterFillAlpha) * 255f;
        var fillA = (byte)(fillRaw <= 0f ? 0 : Math.Clamp((int)MathF.Ceiling(fillRaw), 1, 255));

        var cellArea = _host.GridConfig.CellSize * _host.GridConfig.CellSize;
        var area = (a.MaxX - a.MinX) * (a.MaxY - a.MinY);   // TRUE area — the inflation must not make a box look oversized
        if (cellArea > 0 && area > _cfg.FillMaxCellArea * cellArea)
        {
            // Degenerate: filling it would paint over the whole view. Outline it in a warning colour instead.
            OversizedClusters++;
            Box(_lines, bx0, by0, bx1, by1, new Color(255, 160, 40, borderA));
            if (isSelected)
            {
                // Deliberately no fill even when selected: at this size a 0.2 quad covers the viewport.
                Box(_lines, bx0 - 4, by0 - 4, bx1 + 4, by1 + 4, new Color(255, 255, 255, borderA));
            }
            return;
        }

        Quad(_aabbFill, bx0, by0, bx1, by1, new Color(c.R, c.G, c.B, fillA));
        Box(_lines, bx0, by0, bx1, by1, new Color(c.R, c.G, c.B, borderA));
        if (isSelected)
        {
            // A second, slightly inset outline: SFML lines are one pixel wide whatever the zoom, so a single
            // opaque border is easy to lose among the others.
            var inset = MathF.Max(1f, (bx1 - bx0) * 0.006f);
            Box(_lines, bx0 + inset, by0 + inset, bx1 - inset, by1 - inset, new Color(255, 255, 255, borderA));
        }
    }

    // ─── Culling ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An enumerator over the clusters of <typeparamref name="T"/> that the camera can see, resolved through the
    /// engine's per-cell cluster index. Falls back to the full walk when the index is not available (an archetype
    /// with no clusters yet) or when culling is switched off for an A/B.
    /// </summary>
    private ClusterEnumerator<T> Visible<T>(ArchetypeAccessor<T> acc, int archetypeId)
        where T : Archetype<T>, new()
    {
        if (!_cfg.CullingEnabled || !_cull.Resolve(_host, archetypeId, in _visible))
        {
            return acc.GetClusterEnumerator();
        }
        CullActive = true;
        CullCells += _cull.CellsVisited;
        CullCandidates += _cull.ClustersInCells;
        CullPassed += _cull.ClustersPassed;
        return acc.GetClusterEnumerator(_cull.Ids, 0, _cull.Count);
    }

    /// <summary>
    /// Minimum on-screen half-size for an entity marker, in world units. This clamp is what keeps a distant ship
    /// visible at all — and, being a clamp, it is also what eventually smears a dense scene into a solid block,
    /// which is why <see cref="ViewLod"/> watches its consequences rather than a zoom threshold.
    /// </summary>
    private float MarkerHalfSize(float naturalRadius) =>
        MathF.Max(naturalRadius, _cfg.LodPointPixels * 0.5f * Lod.UnitsPerPixel);

    /// <summary>
    /// Half-size for a landmark, in world units: its true size until that would fall below
    /// <see cref="Config.LandmarkPixels"/> on screen, then a constant pixel size.
    /// </summary>
    /// <remarks>
    /// Zoomed in this returns the real dimensions and nothing is exaggerated — a station is 300 m and is drawn
    /// 300 m wide. The clamp only engages once the true size would be a couple of pixels, which is exactly where
    /// the choice is between a visible marker and nothing at all.
    /// </remarks>
    private float LandmarkHalfSize(float naturalRadius) =>
        MathF.Max(naturalRadius, _cfg.LandmarkPixels * 0.5f * Lod.UnitsPerPixel);

    /// <summary>True when the landmark is being drawn at its pixel floor rather than at its true size.</summary>
    private bool IsLandmarkClamped(float naturalRadius) =>
        naturalRadius < _cfg.LandmarkPixels * 0.5f * Lod.UnitsPerPixel;

    // ─── Entities ─────────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildShips(Transaction tx, Selection sel)
    {
        if (!_cfg.ShowShips)
        {
            return;
        }
        using var acc = tx.For<Ship>();
        using var e = Visible(acc, _host.ShipArchetypeId);
        var r = _cfg.ShipRadius;
        var detail = Lod.Tier == LodTier.Detail;

        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Ship.Position);
            var mot = cluster.GetReadOnlySpan(Ship.Motion);
            var com = cluster.GetReadOnlySpan(Ship.Combat);
            var mnr = cluster.GetReadOnlySpan(Ship.Miner);

            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref readonly var c = ref com[i];
                if (c.Dead != 0)
                {
                    continue;
                }
                var x = pos[i].Bounds.MinX;
                var y = pos[i].Bounds.MinY;
                // Per-entity rejection. The cluster passed the broadphase, but a cluster is not a point — with
                // membership as loose as it currently is, most of a passing cluster's entities can be elsewhere.
                if (!_visible.Contains(x, y))
                {
                    continue;
                }
                Color col;
                if (_cfg.ClusterColorMode)
                {
                    col = ClusterColor(_host.ShipArchetypeId, cluster.ChunkId);
                }
                else
                {
                    // Miners take the plain faction colour, same as fighters. Role is carried by SHAPE (octagon vs
                    // dart) and by the cargo nugget, not by hue — hue is for team. The washed-out MinerA/MinerB
                    // variants they used to get made faction harder to read for no gain, since the silhouette
                    // already says "miner" wherever the silhouette is visible at all.
                    col = c.Faction == 0 ? FactionA : FactionB;
                    if (c.Kind == Simulation.KindHeavy)
                    {
                        col = new Color((byte)Math.Min(255, col.R + 60), (byte)Math.Min(255, col.G + 60), (byte)Math.Min(255, col.B + 60));
                    }
                }
                if (c.HitFlash > 0 && !_cfg.ClusterColorMode)
                {
                    // Colour says which pool took it — cyan while the shield holds, red once rounds reach the
                    // hull. Same idiom as the stations, so "this one is actually in trouble" reads the same way
                    // everywhere on the map.
                    var t = Math.Clamp(c.HitFlash / MathF.Max(1f, _cfg.HitFlashTicks), 0f, 1f);
                    var flash = c.Shield > 0 ? ShieldColor : new Color(255, 60, 50);
                    col = new Color(
                        (byte)(col.R + (flash.R - col.R) * t),
                        (byte)(col.G + (flash.G - col.G) * t),
                        (byte)(col.B + (flash.B - col.B) * t));
                }

                VisibleShips++;
                EntitiesDrawn++;

                if (_cfg.ShowMotionVectors)
                {
                    MotionVector(x, y, mot[i].VX, mot[i].VY, mot[i].MaxSpeed, col);
                }

                if (!detail)
                {
                    // LOD 1: position only. Heading, hull shape, shield ring and selection box are all sub-pixel
                    // here, so drawing them costs vertices to render information nobody can read.
                    //
                    // Cargo is the one exception worth carrying down to this tier, and a nugget inside a 2 px marker
                    // would be exactly the sub-pixel detail this branch exists to avoid. So the marker itself is
                    // tinted toward the ore colour instead: no extra vertices, and at fleet scale "the brown dots
                    // are the ones hauling" is readable in a way a glyph never would be. Skipped in cluster-colour
                    // mode, where the colour is the cluster's identity and tinting it would corrupt the reading —
                    // same reason the hit flash is gated on it.
                    if (c.Kind == Simulation.KindMiner && mnr[i].Cargo > 0 && !_cfg.ClusterColorMode)
                    {
                        col = LadenTint(col, mnr[i].Cargo / MathF.Max(1f, mnr[i].CargoMax));
                        CargoCuesDrawn++;
                    }
                    var hs = MarkerHalfSize(r);
                    Quad(_points, x - hs, y - hs, x + hs, y + hs, col);
                    continue;
                }

                var vx = mot[i].VX;
                var vy = mot[i].VY;
                var len = MathF.Sqrt(vx * vx + vy * vy);
                if (len < 1e-3f)
                {
                    vx = 1;
                    vy = 0;
                }
                else
                {
                    vx /= len;
                    vy /= len;
                }

                // Shield protects the whole faction — miners included — so the ring is drawn per ship, not per kind.
                if (_sim != null && _sim.ShieldTicks[c.Faction & 3] > 0)
                {
                    Ring(_shields, x, y, r * _cfg.ShieldRingScale, ShieldColor, 12);
                }

                var size = c.Kind == Simulation.KindTheOne ? r * _cfg.TheOneSizeScale
                    : c.Kind == Simulation.KindDestroyer ? r * 3.2f
                    : c.Kind == Simulation.KindHeavy ? r * 1.8f
                    : c.Kind == Simulation.KindMiner ? r * 1.4f
                    : r;
                size = MarkerHalfSize(size);
                if (c.Kind == Simulation.KindTheOne)
                {
                    TheOne(_ships, _shields, x, y, vx, vy, size, col);
                }
                else if (c.Kind == Simulation.KindMiner)
                {
                    // Miners are octagons, not darts — instantly distinguishable at any zoom, and distinct from
                    // the asteroids' squares.
                    Octagon(_ships, x, y, size, col);

                    // A laden miner carries a nugget of ore, sized by how full the hold is. This is what separates
                    // "commuting home with cargo" from "wandering, unable to find ore" — two behaviours that
                    // otherwise look identical on screen.
                    //
                    // Drawn into _points, NOT _rocks. There is no depth buffer here: draw order IS z-order, and
                    // Draw() submits _rocks before _ships. A nugget queued into _rocks is painted over by the very
                    // octagon it is supposed to sit inside — which is why this cue was invisible from the day it
                    // was written. _points is the array submitted immediately AFTER _ships, so it is the decal layer.
                    if (mnr[i].Cargo > 0)
                    {
                        var full = Math.Clamp(mnr[i].Cargo / MathF.Max(1f, mnr[i].CargoMax), 0.4f, 1f);
                        var os = size * 0.35f * full;
                        Quad(_points, x - os, y - os, x + os, y + os, CargoColor);
                        CargoCuesDrawn++;
                    }
                }
                else if (c.Kind == Simulation.KindDestroyer)
                {
                    // Broad and blunt — the opposite silhouette to the interceptor's needle, and the largest thing on
                    // the map after a station. A capital ship the eye cannot pick out immediately defeats the point of
                    // having built one.
                    Tri(_ships, x, y, vx, vy, size, col, lengthScale: 0.9f, widthScale: 1.5f);
                }
                else if (c.Kind == Simulation.KindFast)
                {
                    // Long and thin: 2.2x the reach along its heading, a third of the width across it.
                    Tri(_ships, x, y, vx, vy, size, col, lengthScale: 2.2f, widthScale: 0.33f);
                }
                else
                {
                    Tri(_ships, x, y, vx, vy, size, col);
                }

                if (sel.HasSelection && sel.ArchetypeId == _host.ShipArchetypeId && sel.ChunkId == cluster.ChunkId && sel.Slot == i)
                {
                    Box(_lines, x - size * 2, y - size * 2, x + size * 2, y + size * 2, Color.White);
                    if (c.HasTarget != 0)
                    {
                        Line(_lines, x, y, c.TargetX, c.TargetY, new Color(255, 255, 255, 120));
                    }
                }
                else if (_cfg.ShowTargetLines && c.HasTarget != 0)
                {
                    Line(_lines, x, y, c.TargetX, c.TargetY, new Color(col.R, col.G, col.B, 40));
                }
            }
        }
    }

    /// <summary>Asteroids: size tracks remaining capacity, so a field visibly depletes as miners work it.</summary>
    private void BuildAsteroids(Transaction tx)
    {
        if (!_cfg.ShowAsteroids)
        {
            return;
        }
        using var acc = tx.For<Rock>();
        using var e = Visible(acc, _host.RockArchetypeId);
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Rock.Position);
            var ast = cluster.GetReadOnlySpan(Rock.Asteroid);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref readonly var a = ref ast[i];
                if (a.Dead != 0 || a.MaxCapacity <= 0)
                {
                    continue;
                }
                // Clamp BOTH ends. An unclamped fraction here painted the entire viewport grey: one bad quad in a
                // Triangles array is enough to cover the screen, and a lower-bound-only clamp does not stop it.
                var frac = Math.Clamp(a.Capacity / (float)a.MaxCapacity, 0.18f, 1f);
                var natural = _cfg.AsteroidRadius * frac;
                var s = LandmarkHalfSize(natural);
                var x = pos[i].Bounds.MinX;
                var y = pos[i].Bounds.MinY;
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(s))
                {
                    continue;
                }
                if (!_visible.Overlaps(x - s, y - s, x + s, y + s))
                {
                    continue;
                }
                Color rc;
                if (_cfg.ClusterColorMode)
                {
                    var cc = ClusterColor(_host.RockArchetypeId, cluster.ChunkId);
                    // Keep the depletion cue: scale brightness by remaining capacity, keep the cluster's hue.
                    rc = new Color((byte)(cc.R * frac), (byte)(cc.G * frac), (byte)(cc.B * frac), 235);
                }
                else
                {
                    var shade = (byte)(90 + 90 * frac);
                    rc = new Color(shade, (byte)(shade * 0.92f), (byte)(shade * 0.8f), 235);
                }
                RockPoly(_rocks, x, y, s, rc, cluster.GetEntityId(i).EntityKey);
                if (_cfg.ShowMotionVectors)
                {
                    MotionVector(x, y, a.VX, a.VY, _cfg.AsteroidSpeed, new Color(235, 215, 160));
                }
                if (IsLandmarkClamped(natural))
                {
                    // Marker mode: a halo ring so an ore field reads as a place, not as one more square. Ships are
                    // gone by this zoom, so there is nothing left for it to be confused with — except the stations,
                    // which get a box instead of a ring.
                    Ring(_shields, x, y, s * 1.7f, new Color(235, 215, 160, 235), 14);
                }
                Landmarks.Add(new Landmark { X = x, Y = y, Kind = LandmarkKind.Asteroid, Faction = 255 });
                EntitiesDrawn++;
            }
        }
    }

    /// <summary>Colour of a pickup by the effect at stake — the same colour is used everywhere it appears.</summary>
    internal static Color PickupColor(byte kind) => kind switch
    {
        Simulation.PickupPower => new Color(255, 70, 50),
        Simulation.PickupShield => new Color(90, 225, 255),
        Simulation.PickupSpeed => new Color(140, 255, 120),
        Simulation.PickupMining => new Color(255, 190, 60),
        // PRODUCTION — magenta, deliberately the one hue no faction, no damage flash and no other pickup uses, because
        // it is the only boost that changes the shape of the run rather than a stat.
        _ => new Color(235, 90, 235),
    };

    /// <summary>
    /// Pickups: a pulsing diamond in the effect's colour, with each faction's share of the 200-hit race drawn as a
    /// bar beneath it.
    /// </summary>
    /// <remarks>
    /// The bars are the point. A contested objective that takes hundreds of hits to win is, without a readout,
    /// indistinguishable from a delay — you cannot see who is ahead, whether it is close, or whether your side is
    /// even trying. Drawn at every tier including the density view, and clamped to a landmark's minimum size, so
    /// the state of the race is legible from any zoom.
    /// </remarks>
    private void BuildPickups(Transaction tx)
    {
        using var acc = tx.For<Loot>();
        using var e = Visible(acc, _host.LootArchetypeId);
        var need = MathF.Max(1f, _cfg.PickupHitsToWin);
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Loot.Position);
            var inf = cluster.GetReadOnlySpan(Loot.Info);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref readonly var pk = ref inf[i];
                if (pk.Dead != 0)
                {
                    continue;
                }
                var x = pos[i].Bounds.MinX;
                var y = pos[i].Bounds.MinY;
                var r = LandmarkHalfSize(_cfg.PickupRadius);
                if (!_visible.Overlaps(x - r * 3f, y - r * 3f, x + r * 3f, y + r * 3f))
                {
                    continue;
                }
                var pa = Math.Clamp(pk.Progress(0) / need, 0f, 1f);
                var pb = Math.Clamp(pk.Progress(1) / need, 0f, 1f);
                var col = _cfg.ClusterColorMode
                    ? ClusterColor(_host.LootArchetypeId, cluster.ChunkId)
                    : PickupColor(pk.Kind);

                // Pulse from the tick so it draws the eye without needing per-entity animation state.
                var pulse = 1f + 0.25f * MathF.Sin(_host.Tick * 0.15f);
                Diamond(_pickups, x, y, r * pulse, col);
                Ring(_shields, x, y, r * 1.9f * pulse, new Color(col.R, col.G, col.B, 200), 16);
                ProgressBar(x, y - r * 2.1f, r * 2.4f, r * 0.34f, pa, FactionA);
                ProgressBar(x, y - r * 2.7f, r * 2.4f, r * 0.34f, pb, FactionB);

                Landmarks.Add(new Landmark
                {
                    X = x, Y = y,
                    Kind = LandmarkKind.Pickup,
                    Faction = 255,
                    PickupKind = pk.Kind,
                    ProgressA = pa,
                    ProgressB = pb,
                });
                EntitiesDrawn++;
            }
        }
    }

    /// <summary>A world-space filled bar with an outline, centred on x. Used for the pickup race.</summary>
    private void ProgressBar(float cx, float y, float halfWidth, float halfHeight, float t, Color col)
    {
        var x0 = cx - halfWidth;
        var x1 = cx + halfWidth;
        Box(_lines, x0, y - halfHeight, x1, y + halfHeight, new Color(col.R, col.G, col.B, 140));
        if (t <= 0f)
        {
            return;
        }
        Quad(_pickups, x0, y - halfHeight, x0 + (x1 - x0) * t, y + halfHeight, new Color(col.R, col.G, col.B, 225));
    }

    private void BuildShots(Transaction tx)
    {
        using var acc = tx.For<Shot>();
        using var e = Visible(acc, _host.ShotArchetypeId);
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Shot.Position);
            var bul = cluster.GetReadOnlySpan(Shot.Bullet);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (bul[i].Dead != 0)
                {
                    continue;
                }
                var sx = pos[i].Bounds.MinX;
                var sy = pos[i].Bounds.MinY;
                if (!_visible.Contains(sx, sy))
                {
                    continue;
                }
                var boosted = bul[i].Boosted != 0;
                var col = _cfg.ClusterColorMode
                    ? ClusterColor(_host.ShotArchetypeId, cluster.ChunkId)
                    : boosted ? PowerColor
                    : bul[i].Faction == 0 ? new Color(180, 230, 255) : new Color(255, 220, 180);
                if (boosted)
                {
                    var bs = MarkerHalfSize(_cfg.ShipRadius * 0.55f);
                    Quad(_boostShots, sx - bs, sy - bs, sx + bs, sy + bs, col);
                }
                else
                {
                    _shots.Append(new Vertex(new Vector2f(sx, sy), col));
                }
                // Deliberately NO motion vector for projectiles. Shots outnumber ships several times over and all
                // travel at ShotSpeed, so every arrow was the same length pointing the same way as its neighbours —
                // it buried the ship vectors the overlay exists to show under a solid mat of lines.
                VisibleShots++;
                EntitiesDrawn++;
            }
        }
    }

    private void BuildStations(Transaction tx)
    {
        using var acc = tx.For<Station>();
        using var e = Visible(acc, _host.StationArchetypeId);
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Station.Position);
            var inf = cluster.GetReadOnlySpan(Station.Info);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var x = pos[i].Bounds.MinX;
                var y = pos[i].Bounds.MinY;
                var s = LandmarkHalfSize(_cfg.StationRadius);
                if (!_visible.Overlaps(x - s * 1.6f, y - s * 1.6f, x + s * 1.6f, y + s * 1.6f))
                {
                    continue;
                }
                ref readonly var si = ref inf[i];
                var col = _cfg.ClusterColorMode
                    ? ClusterColor(_host.StationArchetypeId, cluster.ChunkId)
                    : si.Faction == 0 ? FactionA : FactionB;

                var shieldFrac = _cfg.StationShieldMax > 0 ? Math.Clamp(si.Shield / (float)_cfg.StationShieldMax, 0f, 1f) : 0f;
                var hpFrac = _cfg.StationHpMax > 0 ? Math.Clamp(si.Hp / (float)_cfg.StationHpMax, 0f, 1f) : 0f;
                var down = si.Disabled != 0;

                // A recent hit flashes the station. Colour says WHERE it landed: cyan while the shield is still
                // holding, red once rounds are reaching the hull — which is the moment a siege stops being
                // harassment, and the one thing you want to spot from across the map.
                if (si.CalmTicks < _cfg.HitFlashTicks && !_cfg.ClusterColorMode)
                {
                    var t = 1f - si.CalmTicks / MathF.Max(1f, _cfg.HitFlashTicks);
                    var flash = si.Shield > 0 ? ShieldColor : new Color(255, 60, 50);
                    col = new Color(
                        (byte)(col.R + (flash.R - col.R) * t),
                        (byte)(col.G + (flash.G - col.G) * t),
                        (byte)(col.B + (flash.B - col.B) * t));
                }
                if (down)
                {
                    // A wreck: hollow, dim, unmistakably not producing anything.
                    col = new Color((byte)(col.R / 3), (byte)(col.G / 3), (byte)(col.B / 3));
                }

                Quad(_stations, x - s, y - s, x + s, y + s, new Color(col.R, col.G, col.B, down ? (byte)120 : (byte)235));
                Box(_lines, x - s * 1.6f, y - s * 1.6f, x + s * 1.6f, y + s * 1.6f, col);
                if (_sel.HasSelection && _sel.ArchetypeId == _host.StationArchetypeId
                    && _sel.ChunkId == cluster.ChunkId && _sel.Slot == i)
                {
                    Box(_lines, x - s * 2.9f, y - s * 2.9f, x + s * 2.9f, y + s * 2.9f, Color.White);
                }
                if (IsLandmarkClamped(_cfg.StationRadius))
                {
                    // Marker mode: a second, wider box so a station still reads as a base at a zoom where its true
                    // 300 m footprint is under two pixels.
                    Box(_lines, x - s * 2.3f, y - s * 2.3f, x + s * 2.3f, y + s * 2.3f,
                        new Color(col.R, col.G, col.B, 170));
                }

                // Shield above hull, same idiom as the pickup race — drawn at every tier so a station under siege
                // is visible from the density view.
                ProgressBar(x, y - s * 2.0f, s * 1.7f, s * 0.26f, shieldFrac, ShieldColor);
                ProgressBar(x, y - s * 2.6f, s * 1.7f, s * 0.26f, hpFrac,
                            down ? new Color(150, 60, 60) : new Color(120, 230, 150));

                // The shield itself, as a ring, so "shielded" reads without looking at the bar.
                if (shieldFrac > 0f && !down)
                {
                    Ring(_shields, x, y, s * (1.9f + 0.25f * shieldFrac),
                         new Color(ShieldColor.R, ShieldColor.G, ShieldColor.B, (byte)(60 + 140 * shieldFrac)), 18);
                }

                Landmarks.Add(new Landmark
                {
                    X = x, Y = y,
                    Kind = LandmarkKind.Station,
                    Faction = si.Faction,
                    ProgressA = shieldFrac,
                    ProgressB = hpFrac,
                    PickupKind = down ? (byte)1 : (byte)0,
                });
                EntitiesDrawn++;
            }
        }
    }

    // ─── Primitive helpers ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One entity's velocity, as a line whose length is the distance it covers in
    /// <see cref="Config.MotionVectorSeconds"/> and whose colour ramps with the fraction of top speed used.
    /// </summary>
    /// <remarks>
    /// Length is a real world distance rather than an arbitrary scale, so the vector can be read against the scene:
    /// a line that reaches an asteroid means the entity gets there in that many seconds. That is what makes it
    /// useful for diagnosing steering — a ship pointed at nothing, or drifting while it believes it is arriving,
    /// is obvious at a glance and invisible otherwise.
    /// </remarks>
    private void MotionVector(float x, float y, float vx, float vy, float maxSpeed, Color baseCol)
    {
        var sp = MathF.Sqrt(vx * vx + vy * vy);
        if (sp < 1e-3f || !float.IsFinite(sp))
        {
            // Stationary is information too: a cross marks something that has stopped.
            var d = _cfg.LodPointPixels * Lod.UnitsPerPixel;
            Line(_lines, x - d, y, x + d, y, new Color(255, 255, 255, 90));
            Line(_lines, x, y - d, x, y + d, new Color(255, 255, 255, 90));
            return;
        }
        var t = maxSpeed > 1e-3f ? Math.Clamp(sp / maxSpeed, 0f, 1f) : 1f;
        var len = sp * _cfg.MotionVectorSeconds;
        var ex = x + vx / sp * len;
        var ey = y + vy / sp * len;

        // Dim at the tail, bright at the head, so direction reads without drawing an arrowhead.
        var tail = new Color(baseCol.R, baseCol.G, baseCol.B, (byte)(40 + 60 * t));
        var head = new Color(
            (byte)Math.Min(255, baseCol.R + 60),
            (byte)Math.Min(255, baseCol.G + 60),
            (byte)Math.Min(255, baseCol.B + 60),
            (byte)(120 + 135 * t));
        _lines.Append(new Vertex(new Vector2f(x, y), tail));
        _lines.Append(new Vertex(new Vector2f(ex, ey), head));
    }

    /// <summary>Grows a 1-D interval symmetrically so it spans at least <paramref name="min"/>.</summary>
    private static void Inflate(ref float lo, ref float hi, float min)
    {
        var extra = min - (hi - lo);
        if (extra <= 0f)
        {
            return;
        }
        var half = extra * 0.5f;
        lo -= half;
        hi += half;
    }

    /// <summary>Open polygon outline — used for shield rings and pickup halos.</summary>
    private static void Ring(VertexArray va, float x, float y, float r, Color c, int sides)
    {
        var prev = new Vector2f(x + r, y);
        for (var i = 1; i <= sides; i++)
        {
            var a = i * (MathF.PI * 2f / sides);
            var next = new Vector2f(x + r * MathF.Cos(a), y + r * MathF.Sin(a));
            va.Append(new Vertex(prev, c));
            va.Append(new Vertex(next, c));
            prev = next;
        }
    }

    private static void Diamond(VertexArray va, float x, float y, float r, Color c)
    {
        var n = new Vector2f(x, y - r);
        var e2 = new Vector2f(x + r, y);
        var s2 = new Vector2f(x, y + r);
        var w = new Vector2f(x - r, y);
        va.Append(new Vertex(n, c)); va.Append(new Vertex(e2, c)); va.Append(new Vertex(s2, c));
        va.Append(new Vertex(n, c)); va.Append(new Vertex(s2, c)); va.Append(new Vertex(w, c));
    }

    private static void Line(VertexArray va, float x0, float y0, float x1, float y1, Color c)
    {
        va.Append(new Vertex(new Vector2f(x0, y0), c));
        va.Append(new Vertex(new Vector2f(x1, y1), c));
    }

    private static void Box(VertexArray va, float x0, float y0, float x1, float y1, Color c)
    {
        Line(va, x0, y0, x1, y0, c);
        Line(va, x1, y0, x1, y1, c);
        Line(va, x1, y1, x0, y1, c);
        Line(va, x0, y1, x0, y0, c);
    }

    private static void Quad(VertexArray va, float x0, float y0, float x1, float y1, Color c)
    {
        va.Append(new Vertex(new Vector2f(x0, y0), c));
        va.Append(new Vertex(new Vector2f(x1, y0), c));
        va.Append(new Vertex(new Vector2f(x1, y1), c));
        va.Append(new Vertex(new Vector2f(x0, y0), c));
        va.Append(new Vertex(new Vector2f(x1, y1), c));
        va.Append(new Vertex(new Vector2f(x0, y1), c));
    }

    /// <summary>
    /// A craggy rock: a polygon whose vertex radii vary, so it reads as a lump of ore rather than as scenery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shape is the fastest channel for "is this a thing I interact with". A plain axis-aligned square reads as
    /// backdrop — it is the shape of a UI panel and of the cell grid — whereas an irregular outline reads as an
    /// object. Ships are triangles, miners regular octagons and stations squares, so an irregular polygon is also
    /// the one silhouette not already spoken for.
    /// </para>
    /// <para>
    /// The radii are derived from the entity's own id, not from a random number generator: the shape has to be
    /// stable across frames or the rock boils. Same id, same rock, every frame and every run.
    /// </para>
    /// </remarks>
    private static void RockPoly(VertexArray va, float x, float y, float r, Color c, long id)
    {
        const int Sides = 11;
        var centre = new Vertex(new Vector2f(x, y), c);
        var prev = default(Vector2f);
        for (var i = 0; i <= Sides; i++)
        {
            var k = i % Sides;
            // Cheap deterministic hash of (id, vertex) into [0.62, 1.0] — enough variation to look broken off
            // without any vertex collapsing toward the centre and making the polygon self-intersect.
            unchecked
            {
                var h = (ulong)id * 0x9E3779B97F4A7C15UL + (ulong)k * 0xBF58476D1CE4E5B9UL;
                h ^= h >> 29;
                h *= 0x94D049BB133111EBUL;
                h ^= h >> 32;
                var jitter = 0.62f + (h % 1000UL) / 1000f * 0.38f;
                // Angular jitter too, so the vertices are not evenly spaced — an irregular radius on a regular
                // angular step still reads as a wheel.
                var wobble = ((h >> 20) % 1000UL) / 1000f - 0.5f;
                var a = (k + wobble * 0.45f) * (MathF.PI * 2f / Sides);
                var p = new Vector2f(x + r * jitter * MathF.Cos(a), y + r * jitter * MathF.Sin(a));
                if (i > 0)
                {
                    va.Append(centre);
                    va.Append(new Vertex(prev, c));
                    va.Append(new Vertex(p, c));
                }
                prev = p;
            }
        }
    }

    /// <summary>Regular octagon as a triangle fan — 8 triangles, no index buffer needed.</summary>
    private static void Octagon(VertexArray va, float x, float y, float r, Color c)
    {
        const int Sides = 8;
        // Offset by half a step so the octagon sits flat-topped rather than pointy-topped.
        const float Offset = MathF.PI / Sides;
        var centre = new Vertex(new Vector2f(x, y), c);
        var prev = new Vector2f(x + r * MathF.Cos(Offset), y + r * MathF.Sin(Offset));
        for (var i = 1; i <= Sides; i++)
        {
            var a = Offset + i * (MathF.PI * 2f / Sides);
            var next = new Vector2f(x + r * MathF.Cos(a), y + r * MathF.Sin(a));
            va.Append(centre);
            va.Append(new Vertex(prev, c));
            va.Append(new Vertex(next, c));
            prev = next;
        }
    }

    private static void Tri(VertexArray va, float x, float y, float dx, float dy, float s, Color c)
        => Tri(va, x, y, dx, dy, s, c, lengthScale: 1f, widthScale: 1f);

    /// <summary>
    /// "The one": three overlapping triangles — a long central fuselage flanked by two shorter reactor darts — drawn
    /// white, over a faction-tinted halo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// White is the brief, and white alone would cost the one thing every other hull gets for free: whose side it is
    /// on. The halo goes into the SHIELD array rather than the ship array, because that array is submitted before
    /// <c>_ships</c> and there is no depth buffer here — draw order is z-order, so anything queued into <c>_ships</c>
    /// alongside the body would paint over it depending on nothing more than loop order.
    /// </para>
    /// <para>
    /// The reactors are offset across the heading and set BACK along it, so they overlap the fuselage's rear third
    /// rather than sitting beside it as three separate darts. Overlap is what makes the silhouette read as one object
    /// with engines instead of a tight formation, which at this size it otherwise does.
    /// </para>
    /// </remarks>
    private static void TheOne(VertexArray ships, VertexArray halo, float x, float y, float dx, float dy, float s, Color faction)
    {
        var px = -dy;
        var py = dx;

        // Faction halo first (submitted earlier ⇒ drawn under), dimmed so the white body stays the brightest thing.
        Ring(halo, x, y, s * 1.35f, new Color(faction.R, faction.G, faction.B, 150), 16);

        // Reactors, set back and out. Drawn BEFORE the fuselage so the body reads as the front-most surface.
        var back = s * 0.45f;
        var lateral = s * 0.62f;
        for (var side = -1; side <= 1; side += 2)
        {
            var rx = x - dx * back + px * lateral * side;
            var ry = y - dy * back + py * lateral * side;
            Tri(ships, rx, ry, dx, dy, s * 0.62f, ReactorColor, lengthScale: 1.15f, widthScale: 0.62f);
        }

        // Fuselage: long and narrow, overlapping both reactors along its rear third.
        Tri(ships, x, y, dx, dy, s, TheOneColor, lengthScale: 1.45f, widthScale: 0.58f);
    }

    /// <summary>The one's hull: pure white, the only thing on the map drawn at full white.</summary>
    private static readonly Color TheOneColor = new(255, 255, 255);

    /// <summary>Its reactors, a shade down so the three triangles remain separable at a glance.</summary>
    private static readonly Color ReactorColor = new(200, 214, 235);

    /// <summary>
    /// The ship dart, with independent scaling along and across its heading.
    /// </summary>
    /// <remarks>
    /// The two scales exist for the interceptor, which is drawn as a long thin needle. Silhouette is the only channel
    /// left to tell hulls apart: colour already carries faction and size already carries the heavy, so a third hull
    /// needed a SHAPE or it would read as a light fighter that happens to be moving quickly — and "moving quickly" is
    /// precisely the thing you are trying to see.
    /// </remarks>
    private static void Tri(VertexArray va, float x, float y, float dx, float dy, float s, Color c, float lengthScale, float widthScale)
    {
        var px = -dy;
        var py = dx;
        var nose = s * 1.8f * lengthScale;
        var tail = s * lengthScale;
        var half = s * widthScale;
        va.Append(new Vertex(new Vector2f(x + dx * nose, y + dy * nose), c));
        va.Append(new Vertex(new Vector2f(x - dx * tail + px * half, y - dy * tail + py * half), c));
        va.Append(new Vertex(new Vector2f(x - dx * tail - px * half, y - dy * tail - py * half), c));
    }
}

internal struct ClusterBox
{
    public int ArchetypeId;
    public int ChunkId;
    public float MinX, MinY, MaxX, MaxY;
    public int LiveCount;
    public int HomeCellKey;
    public int CentreCellKey;

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;
    public float Area => Math.Max(0, Width) * Math.Max(0, Height);
    public bool Drifted => HomeCellKey >= 0 && CentreCellKey != HomeCellKey;

    public bool Overlaps(float qx0, float qy0, float qx1, float qy1) =>
        MaxX >= qx0 && MinX <= qx1 && MaxY >= qy0 && MinY <= qy1;
}

internal enum LandmarkKind : byte
{
    Station,
    Asteroid,
    Pickup,

    /// <summary>"The one". Emitted from the simulation's own record, not from the visible set — see below.</summary>
    TheOne,
}

/// <summary>A point of interest — station, ore field or contested pickup. Drawn at every zoom, in both views.</summary>
internal struct Landmark
{
    public float X;
    public float Y;
    public LandmarkKind Kind;

    /// <summary>Owning faction for a station, or 255 for a neutral landmark.</summary>
    public byte Faction;

    /// <summary>For a pickup: which effect is at stake.</summary>
    public byte PickupKind;

    /// <summary>For a pickup: each faction's share of the hits needed to win, 0..1.</summary>
    public float ProgressA;
    public float ProgressB;
}

internal struct Selection
{
    public bool HasSelection;
    public int ArchetypeId;
    public int ChunkId;
    public int Slot;
    public long EntityKey;
}
