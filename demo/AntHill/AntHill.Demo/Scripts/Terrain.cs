using Godot;

namespace AntHill;

/// <summary>
/// Phase 1 unified ground: a subdivided 100 m × 100 m PlaneMesh whose vertex shader displaces Y from the heightmap,
/// and whose fragment shader composites three layers:
///   • dirt-brown base + Lambertian fake-shading from heightmap gradient (always visible)
///   • pheromone overlay — 200×200 RGBA heatmap (toggle via <see cref="ShowPheromone"/>)
///   • density overlay — 100×100 R8 per-cell ant density (weight = <c>fade_density</c> uniform from camera LOD)
///
/// This single MeshInstance3D replaces the inline brown ground plane that lived in Main.cs in Phase 0 and the
/// separate Sprite/Mesh in the old PheromoneOverlay.
/// </summary>
public partial class Terrain : Node3D
{
    private const float WorldSize = AntRenderer.WorldSizeM;
    private const int Subdivisions = 1023; // 1024 segments → 1025² ≈ 1.05 M verts, ≈2.1 M tris. Matches 1024² heightmap.

    private MeshInstance3D _mesh;
    private ShaderMaterial _material;
    private Image _pheroImage;
    private ImageTexture _pheroTexture;
    private Image _densityImage;
    private ImageTexture _densityTexture;
    // Phase 6B — fire CA overlay. R8 (1 byte/cell): 0=Empty (darken), 1=Fuel (pass-through),
    // 2=Burning (additive orange). 200×200 = 40 KB, uploaded each Godot frame.
    private Image _fireImage;
    private ImageTexture _fireTexture;
    private RenderBridge _bridge;
    private TyphonBridge _typhonBridge;
    private HeightmapResource _heightmap;
    private bool _showPheromone;

    public Texture2D DensityTexture => _densityTexture;
    public Texture2D PheromoneTexture => _pheroTexture;
    public Texture2D HeightmapTexture { get; private set; }
    public Texture2D FireTexture => _fireTexture;     // Phase 6B — shared with FireParticleRenderer (Phase 6 polish)

    public void Initialize(RenderBridge bridge, TyphonBridge typhonBridge, HeightmapResource heightmap)
    {
        _bridge = bridge;
        _typhonBridge = typhonBridge;
        _heightmap = heightmap;
    }

    public override void _Ready()
    {
        // Heightmap texture (FORMAT_RF, float displacement in metres)
        var heightImage = HeightmapFactory.ToImage(_heightmap);
        HeightmapTexture = ImageTexture.CreateFromImage(heightImage);

        // Pheromone CPU image (sized to RenderFrame.HeatmapSize)
        const int hs = RenderFrame.HeatmapSize;
        _pheroImage = Image.CreateEmpty(hs, hs, false, Image.Format.Rgba8);
        _pheroTexture = ImageTexture.CreateFromImage(_pheroImage);

        // Density CPU image — 100×100 R8 (one byte per 1 m cell, saturating count/255)
        _densityImage = Image.CreateEmpty(DensityResolution, DensityResolution, false, Image.Format.R8);
        _densityTexture = ImageTexture.CreateFromImage(_densityImage);

        // Phase 6B — fire CA CPU image (200×200 R8).
        _fireImage = Image.CreateEmpty(FireGrid.Size, FireGrid.Size, false, Image.Format.R8);
        _fireTexture = ImageTexture.CreateFromImage(_fireImage);

        var plane = new PlaneMesh
        {
            Size = new Vector2(WorldSize, WorldSize),
            SubdivideWidth = Subdivisions,
            SubdivideDepth = Subdivisions,
        };

        var shader = new Shader { Code = ShaderSource };
        _material = new ShaderMaterial { Shader = shader };
        _material.SetShaderParameter("heightmap_tex", HeightmapTexture);
        _material.SetShaderParameter("phero_tex", _pheroTexture);
        _material.SetShaderParameter("density_tex", _densityTexture);
        _material.SetShaderParameter("world_size", WorldSize);
        _material.SetShaderParameter("heightmap_res", (float)_heightmap.Resolution);
        _material.SetShaderParameter("dirt_color", new Color(0.32f, 0.24f, 0.18f));
        _material.SetShaderParameter("show_pheromone", _showPheromone);
        _material.SetShaderParameter("fade_density", 0f);
        _material.SetShaderParameter("density_norm", 32f);  // ants per cell that maps to "full red"
        // Sun direction (world space). Low-angle (≈25° above horizon) so even gentle slopes shade legibly.
        _material.SetShaderParameter("sun_dir", new Vector3(0.55f, 0.45f, 0.30f).Normalized());
        _material.SetShaderParameter("slope_boost", 1.5f);   // mild visualization boost — the noise now carries real amplitude
        _material.SetShaderParameter("ambient", 0.35f);
        _material.SetShaderParameter("u_brightness", 1f);    // Phase 6A — Daisyworld × day/night multiplier on final ALBEDO
        _material.SetShaderParameter("fire_tex", _fireTexture); // Phase 6B — fire CA overlay (R8 200×200)
        plane.SurfaceSetMaterial(0, _material);

        _mesh = new MeshInstance3D
        {
            Mesh = plane,
            // PlaneMesh is centred at the node origin. Place it so the world spans [0..WorldSize] on X and Z.
            Position = new Vector3(WorldSize * 0.5f, 0f, WorldSize * 0.5f),
            // PlaneMesh's AABB is flat (Y=0). Vertex shader displaces by ±1 m; this margin keeps the displaced
            // geometry inside the cull AABB.
            ExtraCullMargin = 5f,
        };
        AddChild(_mesh);
    }

    public const int DensityResolution = 100;

    public void TogglePheromone()
    {
        _showPheromone = !_showPheromone;
        _material?.SetShaderParameter("show_pheromone", _showPheromone);
        _typhonBridge?.SetHeatmapEnabled(_showPheromone);
    }

    public bool ShowPheromone
    {
        get => _showPheromone;
        set
        {
            if (_showPheromone == value) return;
            _showPheromone = value;
            _material?.SetShaderParameter("show_pheromone", _showPheromone);
            _typhonBridge?.SetHeatmapEnabled(_showPheromone);
        }
    }

    public void SetFadeDensity(float fade) => _material?.SetShaderParameter("fade_density", Mathf.Clamp(fade, 0f, 1f));

    public void SetBrightness(float brightness) => _material?.SetShaderParameter("u_brightness", Mathf.Clamp(brightness, 0f, 2f));

    public void UpdateFromFrame()
    {
        if (_bridge == null) return;
        var frame = _bridge.GetLatest();

        if (_showPheromone && frame?.HeatmapRGBA != null)
        {
            const int hs = RenderFrame.HeatmapSize;
            _pheroImage.SetData(hs, hs, false, Image.Format.Rgba8, frame.HeatmapRGBA);
            _pheroTexture.Update(_pheroImage);
        }
    }

    /// <summary>Replace the density texture's CPU image bytes with a caller-prepared R8 buffer. AntRenderer authors this each frame.</summary>
    public void UpdateDensity(byte[] r8Data)
    {
        if (_densityImage == null) return;
        _densityImage.SetData(DensityResolution, DensityResolution, false, Image.Format.R8, r8Data);
        _densityTexture.Update(_densityImage);
    }

    /// <summary>Phase 6B — replace the fire texture's CPU image bytes with a caller-prepared R8 buffer (200×200). TyphonBridge.PrepareRender authors this each tick.</summary>
    public void UpdateFireTex(byte[] r8Data)
    {
        if (_fireImage == null || r8Data == null) return;
        _fireImage.SetData(FireGrid.Size, FireGrid.Size, false, Image.Format.R8, r8Data);
        _fireTexture.Update(_fireImage);
    }

    /// <summary>Phase 6B — convenience wrapper that pulls the latest <see cref="RenderFrame.FireR8"/> from the RenderBridge and uploads it. Mirrors <see cref="UpdateFromFrame"/>.</summary>
    public void UpdateFireFromFrame()
    {
        if (_bridge == null) return;
        var frame = _bridge.GetLatest();
        if (frame?.FireR8 != null) UpdateFireTex(frame.FireR8);
    }

    // ── Shader source ──────────────────────────────────────────────────────────
    // Vertex stage: read displacement from heightmap_tex, lift the vertex along world Y.
    // Fragment stage: dirt-brown base + simple gradient shading (computed from neighbour samples for cheap normals);
    //   then add pheromone (gated by show_pheromone) and density (weighted by fade_density).

    private const string ShaderSource = @"
shader_type spatial;
// cull_disabled because heightmap displacement can put the camera below part of the terrain (in a valley or just inside
// a hill at grazing pitch), and cull_back would make those fragments vanish. Single-mesh opaque, no perf impact worth caring about.
render_mode unshaded, cull_disabled, depth_draw_opaque;

uniform sampler2D heightmap_tex : filter_linear, repeat_disable;
uniform sampler2D phero_tex     : filter_linear, repeat_disable;
uniform sampler2D density_tex   : filter_linear, repeat_disable;
uniform sampler2D fire_tex      : filter_nearest, repeat_disable;   // Phase 6B — crisp cell edges

uniform float world_size;
uniform float heightmap_res;
uniform vec4  dirt_color : source_color;
uniform bool  show_pheromone;
uniform float fade_density;
uniform float density_norm;
uniform vec3  sun_dir;
uniform float slope_boost;   // exaggerates slope for visual readability (real relief is ±50 cm / 100 m, too subtle without)
uniform float ambient;       // baseline lighting floor in [0, 1]
uniform float u_brightness;  // Phase 6A — Daisyworld × day/night multiplier (0=midnight, 1=noon, can exceed 1 if luminosity slider pushed past default)

varying vec2 v_world_xz;
varying float v_shade;

void vertex() {
    // VERTEX is in model space; the PlaneMesh is centred at origin with size (world_size, world_size), so
    // local XZ ∈ [-world_size/2, +world_size/2]. The node's own Position offset shifts to [0, world_size].
    vec2 local_xz = VERTEX.xz;
    vec2 uv = local_xz / world_size + 0.5;     // 0..1
    float h = texture(heightmap_tex, uv).r;
    VERTEX.y = h;

    // Real-normal computation: sample two world-space neighbours (one heightmap cell apart) and build the surface normal.
    // dh/dx_world is the per-metre slope; the normal of a height field h(x,z) is N = normalize((-dh/dx, 1, -dh/dz)).
    float pixel        = 1.0 / heightmap_res;
    float world_per_px = world_size / heightmap_res;
    float hx = texture(heightmap_tex, uv + vec2(pixel, 0.0)).r;
    float hz = texture(heightmap_tex, uv + vec2(0.0, pixel)).r;
    float dhx = ((hx - h) / world_per_px) * slope_boost;
    float dhz = ((hz - h) / world_per_px) * slope_boost;
    vec3 N = normalize(vec3(-dhx, 1.0, -dhz));

    // Lambertian against a low-angle sun. Ambient floor prevents valleys from going black at extreme slopes.
    vec3 L = normalize(sun_dir);
    float lambert = max(dot(N, L), 0.0);
    v_shade = ambient + (1.0 - ambient) * lambert;

    v_world_xz = uv;
}

void fragment() {
    vec3 col = dirt_color.rgb * v_shade;

    // Pheromone overlay: additive, gated by toggle. Sample uses [0,1] UV. Intensity = max channel.
    if (show_pheromone) {
        vec4 ph = texture(phero_tex, v_world_xz);
        float intensity = max(max(ph.r, ph.g), ph.b);
        col = mix(col, col + ph.rgb, intensity);
    }

    // Density overlay: red wash whose strength grows with cell population and current LOD fade.
    if (fade_density > 0.001) {
        float d = texture(density_tex, v_world_xz).r;   // [0,1] (saturating /density_norm — bake on CPU side)
        vec3 hot = mix(vec3(0.1, 0.05, 0.05), vec3(1.0, 0.2, 0.1), d);
        col = mix(col, hot, fade_density * smoothstep(0.0, 0.05, d));
    }

    // Phase 6B — fire CA overlay. Cell state in R channel as a byte: 0=Empty, 1=Fuel,
    // 2..11=Burning (countdown — 11 = just ignited, 2 = about to die). Modulate the orange
    // tint by freshness so fresh fires read brighter/yellower than dying embers.
    float fireR = texture(fire_tex, v_world_xz).r;
    float fireByte = fireR * 255.0;
    if (fireByte > 1.5) {
        float freshness = clamp((fireByte - 2.0) / 9.0, 0.0, 1.0);
        vec3 fireColor = mix(vec3(0.7, 0.15, 0.02), vec3(1.0, 0.6, 0.10), freshness);
        col += fireColor * 1.5;
    } else if (fireByte < 0.5) {
        // Empty (ash) — darken the dirt so the burn trail reads as scorched ground.
        col *= vec3(0.55, 0.50, 0.45);
    }
    // Fuel (state = 1) → no change. Default terrain shows through. Vegetation in 6C will be
    // the visible fuel layer.

    ALBEDO = col * u_brightness;
}
";
}
