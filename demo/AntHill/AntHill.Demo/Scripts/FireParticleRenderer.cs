using AntHill.Core;
using Godot;

namespace AntHill.Demo;

/// <summary>
/// Phase 6 polish — volumetric flames + smoke for the fire CA. Two <see cref="GpuParticles3D"/>
/// emitters cover the whole world plane. Each particle's <c>start()</c> samples the shared
/// <c>fire_tex</c> at its random spawn position; if the cell isn't Burning the particle is
/// killed (<c>ACTIVE = false</c>) before it costs any GPU work beyond the spawn check.
///
/// Result: flame + smoke density follows the fire CA exactly — no CPU bookkeeping per burning
/// cell, no per-frame node churn, scales the same whether 1 cell or 200 are burning.
/// </summary>
public partial class FireParticleRenderer : Node3D
{
    // Pre-allocated GPU buffers. Uniform spawn over the 100×100 m world means a single 0.5 m cell
    // covers 0.0025 % of the area, so the rejection rate is ~99.997 % when only a few cells burn.
    // Counts are cranked accordingly — the kill-in-start path is one texture sample + branch, so
    // even ~100 k spawns/frame is well under 1 ms on a modern GPU. Visible-at-any-moment ≈
    // amount × (burning_cells × cell_area / world_area):
    //   • 1 cell burning  → ~25 flames, ~17 smoke visible
    //   • 49 cells (Ignite tool, radius 3) → ~735 flames, ~490 smoke
    //   • 200 cells (large spread) → ~3000 flames, ~2000 smoke
    private const int FlameCount = 100_000;
    private const int SmokeCount = 60_000;
    private const float FlameLifetime = 1.0f;
    private const float SmokeLifetime = 4.0f;

    private TyphonBridge _bridge;
    private Terrain _terrain;
    private GpuParticles3D _flames;
    private GpuParticles3D _smoke;
    private ShaderMaterial _flameProcess;
    private ShaderMaterial _smokeProcess;

    public void Initialize(TyphonBridge bridge, Terrain terrain)
    {
        _bridge = bridge;
        _terrain = terrain;
    }

    public override void _Ready()
    {
        if (_bridge == null || _terrain == null)
        {
            GD.PrintErr("FireParticleRenderer: bridge/terrain not initialized");
            return;
        }

        var fireTex = _terrain.FireTexture;
        var worldSize = AntRenderer.WorldSizeM;
        var worldAabb = new Aabb(new Vector3(-2, 0, -2), new Vector3(worldSize + 4, 15, worldSize + 4));

        // ── Flames ───────────────────────────────────────────────────────────────
        _flameProcess = new ShaderMaterial { Shader = new Shader { Code = FlameShader } };
        _flameProcess.SetShaderParameter("fire_tex", fireTex);
        _flameProcess.SetShaderParameter("world_size", worldSize);
        _flameProcess.SetShaderParameter("wind", new Vector2());

        var flameMesh = new QuadMesh { Size = new Vector2(0.30f, 0.50f) };
        flameMesh.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(1, 1, 1, 1),
            DisableReceiveShadows = true,
        };

        _flames = new GpuParticles3D
        {
            Amount = FlameCount,
            Lifetime = FlameLifetime,
            ProcessMaterial = _flameProcess,
            DrawPass1 = flameMesh,
            Emitting = true,
            Explosiveness = 0f,
            Randomness = 0.5f,
            VisibilityAabb = worldAabb,
        };
        AddChild(_flames);

        // ── Smoke ────────────────────────────────────────────────────────────────
        _smokeProcess = new ShaderMaterial { Shader = new Shader { Code = SmokeShader } };
        _smokeProcess.SetShaderParameter("fire_tex", fireTex);
        _smokeProcess.SetShaderParameter("world_size", worldSize);
        _smokeProcess.SetShaderParameter("wind", new Vector2());

        var smokeMesh = new QuadMesh { Size = new Vector2(0.80f, 0.80f) };
        smokeMesh.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(1, 1, 1, 1),
            DisableReceiveShadows = true,
        };

        _smoke = new GpuParticles3D
        {
            Amount = SmokeCount,
            Lifetime = SmokeLifetime,
            ProcessMaterial = _smokeProcess,
            DrawPass1 = smokeMesh,
            Emitting = true,
            Explosiveness = 0f,
            Randomness = 0.3f,
            VisibilityAabb = worldAabb,
        };
        AddChild(_smoke);
    }

    public override void _Process(double delta)
    {
        if (_bridge == null || _flameProcess == null) return;
        var w = _bridge.Wind;
        var v = new Vector2(w.X, w.Y);
        _flameProcess.SetShaderParameter("wind", v);
        _smokeProcess.SetShaderParameter("wind", v);
    }

    // ── Flame shader ─────────────────────────────────────────────────────────────
    // start(): pick a random spawn position in the world plane, sample fire_tex; kill the
    //   particle if the cell isn't Burning ([BurnMin=2, BurnStart=11] on the R8 byte).
    // process(): track normalized age in CUSTOM.x; ramp colour orange → red while fading alpha.
    private const string FlameShader = @"
shader_type particles;

uniform sampler2D fire_tex : filter_nearest, repeat_disable;
uniform float world_size = 100.0;
uniform vec2 wind = vec2(0.0, 0.0);

float rand_from_seed(in uint seed) {
    int s = int(seed);
    if (s == 0) s = 305420679;
    int k = s / 127773;
    s = 16807 * (s - k * 127773) - 2836 * k;
    if (s < 0) s += 2147483647;
    return float(uint(s) % uint(65536)) / 65535.0;
}

uint hash_u(uint x) {
    x = ((x >> 16u) ^ x) * 0x45d9f3bu;
    x = ((x >> 16u) ^ x) * 0x45d9f3bu;
    x = (x >> 16u) ^ x;
    return x;
}

void start() {
    uint s1 = hash_u(NUMBER + uint(1) + RANDOM_SEED);
    uint s2 = hash_u(NUMBER + uint(27) + RANDOM_SEED);
    uint s3 = hash_u(NUMBER + uint(43) + RANDOM_SEED);
    uint s4 = hash_u(NUMBER + uint(99) + RANDOM_SEED);
    uint s5 = hash_u(NUMBER + uint(131) + RANDOM_SEED);

    float rx = rand_from_seed(s1) * world_size;
    float rz = rand_from_seed(s2) * world_size;

    float state = textureLod(fire_tex, vec2(rx, rz) / world_size, 0.0).r * 255.0;
    if (state >= 1.5 && state <= 11.5) {
        TRANSFORM[3] = vec4(rx, 0.10, rz, 1.0);
        VELOCITY = vec3(
            wind.x * 0.4 + (rand_from_seed(s3) - 0.5) * 0.3,
            1.6 + rand_from_seed(s4) * 0.8,
            wind.y * 0.4 + (rand_from_seed(s5) - 0.5) * 0.3
        );
        COLOR = vec4(1.0, 0.65, 0.10, 1.0);
        CUSTOM.x = 0.0;
    } else {
        ACTIVE = false;
    }
}

void process() {
    VELOCITY.y += DELTA * 1.0;                                                  // hot air rises faster
    CUSTOM.x += DELTA / LIFETIME;                                               // normalized age 0..1
    float age = clamp(CUSTOM.x, 0.0, 1.0);
    COLOR.rgb = mix(vec3(1.0, 0.70, 0.12), vec3(0.45, 0.10, 0.0), age);
    COLOR.a = 1.0 - age;
}
";

    // ── Smoke shader ─────────────────────────────────────────────────────────────
    // Same spawn-gate as flames. Slower upward velocity, stronger wind drift, longer lifetime.
    // Colour ramps light-grey → dark-grey with alpha that fades in then out (smoke "puff" curve).
    private const string SmokeShader = @"
shader_type particles;

uniform sampler2D fire_tex : filter_nearest, repeat_disable;
uniform float world_size = 100.0;
uniform vec2 wind = vec2(0.0, 0.0);

float rand_from_seed(in uint seed) {
    int s = int(seed);
    if (s == 0) s = 305420679;
    int k = s / 127773;
    s = 16807 * (s - k * 127773) - 2836 * k;
    if (s < 0) s += 2147483647;
    return float(uint(s) % uint(65536)) / 65535.0;
}

uint hash_u(uint x) {
    x = ((x >> 16u) ^ x) * 0x45d9f3bu;
    x = ((x >> 16u) ^ x) * 0x45d9f3bu;
    x = (x >> 16u) ^ x;
    return x;
}

void start() {
    uint s1 = hash_u(NUMBER + uint(7) + RANDOM_SEED);
    uint s2 = hash_u(NUMBER + uint(31) + RANDOM_SEED);
    uint s3 = hash_u(NUMBER + uint(53) + RANDOM_SEED);

    float rx = rand_from_seed(s1) * world_size;
    float rz = rand_from_seed(s2) * world_size;

    float state = textureLod(fire_tex, vec2(rx, rz) / world_size, 0.0).r * 255.0;
    if (state >= 1.5 && state <= 11.5) {
        TRANSFORM[3] = vec4(rx, 0.25, rz, 1.0);
        VELOCITY = vec3(
            wind.x * 0.8,
            0.5 + rand_from_seed(s3) * 0.3,
            wind.y * 0.8
        );
        COLOR = vec4(0.30, 0.27, 0.24, 0.0);                                    // fade-in starts at 0 alpha
        CUSTOM.x = 0.0;
    } else {
        ACTIVE = false;
    }
}

void process() {
    VELOCITY.xz += wind * (DELTA * 0.5);                                        // wind keeps pushing smoke laterally
    VELOCITY.y += DELTA * 0.08;
    CUSTOM.x += DELTA / LIFETIME;
    float age = clamp(CUSTOM.x, 0.0, 1.0);

    // Fade-in over first 15%, fade-out over remaining 85%.
    float alpha = age < 0.15 ? age / 0.15 : (1.0 - age) / 0.85;

    COLOR.rgb = mix(vec3(0.40, 0.35, 0.30), vec3(0.18, 0.16, 0.14), age);
    COLOR.a = alpha * 0.55;
}
";
}
