using System;

namespace SpaceBattle;

/// <summary>Which representation the renderer is using for entities this frame.</summary>
internal enum LodTier
{
    /// <summary>Real sprites: orientation, shield rings, hit flash. One entity is at least a few pixels.</summary>
    Detail = 0,

    /// <summary>One clamped point per entity. Position survives; shape, heading and size do not.</summary>
    Point = 1,

    /// <summary>No entities at all — a binned density field. The only tier whose cost is independent of population.</summary>
    Density = 2,
}

/// <summary>
/// Picks the level of detail from the camera, the window and how much is actually on screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trigger is pixels per entity, not camera distance.</b> Distance thresholds are silently wrong the moment
/// the window is resized, because the same distance covers a different number of pixels. Everything here derives
/// from <see cref="Camera.UnitsPerPixel"/>, so a rule tuned on one window holds on every other.
/// </para>
/// <para>
/// <b>The Point-to-Density boundary is self-tuning.</b> Points are clamped to a minimum pixel size or they would
/// vanish entirely — and that clamp is precisely what turns a dense scene into a featureless smear, because the
/// sprites stop shrinking while the gaps between them keep shrinking. The zoom at which that happens depends on how
/// many entities are on screen, so instead of a magic zoom number we estimate coverage
/// (<c>visible x pointPixels² / viewportPixels</c>) and switch when it crosses
/// <see cref="Config.LodSaturationFraction"/>. Change the population and the boundary moves on its own.
/// </para>
/// <para>
/// Both boundaries are hysteretic. Without that, parking the wheel on a threshold strobes the entire scene between
/// two representations once per frame.
/// </para>
/// </remarks>
internal sealed class ViewLod
{
    public LodTier Tier { get; private set; } = LodTier.Detail;

    /// <summary>Blend weight for sprite/point drawing, 0..1. Below 1 during a crossfade out of the entity tiers.</summary>
    public float EntityWeight { get; private set; } = 1f;

    /// <summary>Blend weight for the density field, 0..1.</summary>
    public float DensityWeight { get; private set; }

    public float UnitsPerPixel { get; private set; } = 1f;

    /// <summary>Screen size of one entity, in pixels, before any minimum-size clamp.</summary>
    public float EntityPixels { get; private set; }

    /// <summary>Estimated fraction of the viewport that clamped entity sprites would cover.</summary>
    public float Saturation { get; private set; }

    /// <summary>Why the current tier was chosen — surfaced in the HUD so the rule is never a black box.</summary>
    public string Reason { get; private set; } = "";

    public void Update(Config cfg, Camera cam, int visibleEntities, uint winW, uint winH)
    {
        UnitsPerPixel = cam.UnitsPerPixel;
        EntityPixels = UnitsPerPixel > 1e-9f ? cfg.ShipRadius * 2f / UnitsPerPixel : float.MaxValue;

        var viewportPixels = (float)winW * winH;
        var pointArea = cfg.LodPointPixels * cfg.LodPointPixels;
        Saturation = viewportPixels > 0 ? visibleEntities * pointArea / viewportPixels : 0f;

        if (!cfg.LodEnabled)
        {
            Set(LodTier.Detail, "LOD disabled");
            return;
        }
        if (cfg.ForceLod >= 0)
        {
            Set((LodTier)Math.Clamp(cfg.ForceLod, 0, 2), "forced");
            return;
        }

        // Hysteresis: a boundary is easier to cross in the direction that ADDS detail than to fall back over, so a
        // frame sitting on a threshold settles instead of oscillating.
        var h = MathF.Max(1f, cfg.LodHysteresis);
        var detailIn = cfg.LodDetailPixels * h;      // must be clearly big enough to gain sprites
        var detailOut = cfg.LodDetailPixels / h;     // must be clearly too small to lose them
        var satIn = cfg.LodSaturationFraction * h;   // must be clearly saturated to collapse to density
        var satOut = cfg.LodSaturationFraction / h;  // must be clearly sparse to get entities back
        var tinyIn = cfg.LodDensityPixels / h;       // must be clearly sub-pixel to collapse to density
        var tinyOut = cfg.LodDensityPixels * h;      // must be clearly resolvable to get entities back

        // Two independent reasons to abandon per-entity drawing: too many to separate, or each too small to depict
        // honestly. Either is sufficient.
        var saturated = Saturation > satIn;
        var tiny = EntityPixels < tinyIn;
        var resolvable = EntityPixels > tinyOut && Saturation < satOut;

        var next = Tier switch
        {
            LodTier.Detail => EntityPixels >= detailOut ? LodTier.Detail
                : saturated || tiny ? LodTier.Density
                : LodTier.Point,
            LodTier.Point => EntityPixels > detailIn ? LodTier.Detail
                : saturated || tiny ? LodTier.Density
                : LodTier.Point,
            _ => !resolvable ? LodTier.Density
                : EntityPixels > detailIn ? LodTier.Detail
                : LodTier.Point,
        };

        Set(next, next switch
        {
            LodTier.Detail => $"{EntityPixels:F1} px/entity >= {cfg.LodDetailPixels:F0}",
            LodTier.Point => $"{EntityPixels:F2} px/entity, {Saturation * 100:F1}% coverage",
            _ when tiny => $"{EntityPixels:F2} px/entity < {cfg.LodDensityPixels:F2} (sub-pixel)",
            _ => $"{Saturation * 100:F0}% coverage >= {cfg.LodSaturationFraction * 100:F0}% (saturated)",
        });

        // Crossfade across the saturation boundary only, and never in the tactical view — a density wash over a
        // 3 km tactical picture would obscure exactly the detail you zoomed in to see.
        //
        // The blend runs the density field UP while points are still being drawn, rather than swapping one for the
        // other on a single frame. That ordering matters: the aggregate establishes itself underneath the entities
        // it is about to replace, so the switch reads as the picture resolving rather than as a cut.
        if (cfg.LodCrossfadeOctaves > 0f && next != LodTier.Detail)
        {
            // One blend parameter for two thresholds: "pressure" is how far past the nearer boundary we are, and it
            // equals 1 at whichever boundary is about to fire. Blending on the raw saturation would leave the
            // sub-pixel transition — the one that actually fires in this world — with no crossfade at all.
            var pressure = MathF.Max(
                Saturation / MathF.Max(satIn, 1e-6f),
                tinyIn / MathF.Max(EntityPixels, 1e-6f));
            var octaves = MathF.Log2(MathF.Max(pressure, 1e-6f)) / cfg.LodCrossfadeOctaves;
            var t = Math.Clamp(octaves * 0.5f + 0.5f, 0f, 1f);   // 0 well below the boundary, 1 well above
            DensityWeight = Tier == LodTier.Density ? MathF.Max(t, 0.35f) : t;
            EntityWeight = Tier == LodTier.Density ? 0f : 1f;
        }
    }

    private void Set(LodTier tier, string reason)
    {
        Tier = tier;
        Reason = reason;
        EntityWeight = tier == LodTier.Density ? 0f : 1f;
        DensityWeight = tier == LodTier.Density ? 1f : 0f;
    }
}
