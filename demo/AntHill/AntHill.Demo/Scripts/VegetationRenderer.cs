using AntHill.Core;
using Godot;

namespace AntHill.Demo;

/// <summary>
/// Phase 6C — renders the 100 k plant carpet as four <see cref="MultiMeshInstance3D"/> nodes
/// (one per kind: grass / moss / lichen / leaf). Each plant is an X-cross billboard (two quads
/// at 90°) whose top vertices sway in the current wind direction via a shared vertex shader.
///
/// Static-by-default model: per-instance transforms are written once in <see cref="_Ready"/>
/// (positions baked from <see cref="PlantGrid.Y"/> at sim startup), and the per-frame update path
/// only consumes the dirty-index list from <see cref="RenderFrame.PlantDirty"/> to apply colour
/// updates (Alive → tint, Burnt → charred grey, Despawned → alpha-0). Steady-state per-frame cost
/// is just two uniform writes (wind + time); fire-affected frames add up to a few thousand
/// <c>SetInstanceColor</c> calls.
/// </summary>
public partial class VegetationRenderer : Node3D
{
    private TyphonBridge _bridge;

    private readonly MultiMeshInstance3D[] _byKind = new MultiMeshInstance3D[PlantGrid.KindCount];
    private int[] _indexInKind;                                             // plant index → instance slot within its kind's MultiMesh
    private ShaderMaterial _sharedMaterial;

    // Tint per kind. Chosen for at-a-glance kind separation against the dirt-brown terrain.
    private static readonly Color[] AliveTints =
    [
        new(0.35f, 0.55f, 0.18f),                                     // grass — warm green
        new(0.18f, 0.40f, 0.20f),                                     // moss  — deep mossy
        new(0.65f, 0.65f, 0.50f),                                     // lichen — pale yellow-grey
        new(0.20f, 0.45f, 0.10f),                                     // leaf  — darker leafy
    ];
    private static readonly Color CharredColor = new(0.18f, 0.13f, 0.10f);
    private static readonly Color DespawnedColor = new(0f, 0f, 0f, 0f);     // alpha 0 → fragment discard

    // Mesh sizes (width × height in metres) per kind. Tweaked per kind so the world reads layered:
    // grass / leaf are taller; moss / lichen hug the ground.
    private static readonly (float W, float H)[] KindMeshSize =
    [
        (0.25f, 0.15f),                                                     // grass
        (0.18f, 0.10f),                                                     // moss
        (0.20f, 0.08f),                                                     // lichen
        (0.22f, 0.20f),                                                     // leaf
    ];

    public void Initialize(TyphonBridge bridge)
    {
        _bridge = bridge;
    }

    public override void _Ready()
    {
        if (_bridge?.PlantGrid == null)
        {
            GD.PrintErr("VegetationRenderer: PlantGrid not ready — was SetHeightmap called before AddChild?");
            return;
        }

        var pg = _bridge.PlantGrid;

        // Shared shader material — single Shader compile, one set of uniforms drives all four
        // MultiMesh instances. Wind sway is per-vertex; tint per-instance.
        _sharedMaterial = new ShaderMaterial { Shader = new Shader { Code = ShaderSource } };
        _sharedMaterial.SetShaderParameter("wind", new Vector2(_bridge.Wind.X, _bridge.Wind.Y));
        _sharedMaterial.SetShaderParameter("u_time", 0f);

        // World-spanning cull AABB so plants near the camera edge never pop out. 100×100 m world
        // with up to ~25 cm plant height → generous box.
        var worldSize = AntRenderer.WorldSizeM;
        var worldAabb = new Aabb(new Vector3(-1, -1, -1), new Vector3(worldSize + 2, 3, worldSize + 2));

        for (var k = 0; k < PlantGrid.KindCount; k++)
        {
            var (w, h) = KindMeshSize[k];
            var mm = new MultiMesh
            {
                // UseColors must be set before InstanceCount per Godot 4 docs.
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = BuildXCrossMesh(w, h, _sharedMaterial),
                InstanceCount = pg.CountByKind[k],
                VisibleInstanceCount = pg.CountByKind[k],
            };
            _byKind[k] = new MultiMeshInstance3D
            {
                Multimesh = mm,
                CustomAabb = worldAabb,
            };
            AddChild(_byKind[k]);
        }

        // Assign each plant a slot in its kind's MultiMesh, then write initial transform + colour.
        _indexInKind = new int[PlantGrid.Count];
        var cursor = new int[PlantGrid.KindCount];
        for (var i = 0; i < PlantGrid.Count; i++)
        {
            var kind = pg.Kind[i];
            var slot = cursor[kind]++;
            _indexInKind[i] = slot;

            // Sim → world: AntRenderer.SimToWorld = 100 / WorldSize = 0.005 m/unit.
            var wx = pg.X[i] * AntRenderer.SimToWorld;
            var wz = pg.Z[i] * AntRenderer.SimToWorld;
            var wy = pg.Y[i];

            // Identity basis — the mesh itself defines plant orientation (X-cross is rotationally
            // ambiguous from top-down ortho). Per-plant yaw randomness is a polish item.
            var xform = new Transform3D(Basis.Identity, new Vector3(wx, wy, wz));
            _byKind[kind].Multimesh.SetInstanceTransform(slot, xform);
            _byKind[kind].Multimesh.SetInstanceColor(slot, AliveTints[kind]);
        }
    }

    /// <summary>
    /// Per-frame hook from <see cref="Main"/>. Drains <see cref="RenderFrame.PlantDirty"/> for
    /// colour updates, then pushes the latest wind + time to the shared shader.
    /// </summary>
    public void UpdateFromFrame()
    {
        if (_bridge == null || _indexInKind == null) return;
        var frame = _bridge.RenderBridge?.GetLatest();
        var pg = _bridge.PlantGrid;
        if (frame != null && pg != null && frame.PlantDirty != null)
        {
            var dirty = frame.PlantDirty;
            var n = frame.PlantDirtyCount;
            for (var i = 0; i < n; i++)
            {
                var idx = dirty[i];
                if (idx < 0 || idx >= PlantGrid.Count) continue;
                var kind = pg.Kind[idx];
                var s = pg.State[idx];
                var col = s == PlantGrid.Despawned ? DespawnedColor
                        : s == PlantGrid.Alive     ? AliveTints[kind]
                        :                            CharredColor;
                _byKind[kind].Multimesh.SetInstanceColor(_indexInKind[idx], col);
            }
        }

        var w = _bridge.Wind;
        _sharedMaterial.SetShaderParameter("wind", new Vector2(w.X, w.Y));
        _sharedMaterial.SetShaderParameter("u_time", Time.GetTicksMsec() * 0.001f);
    }

    /// <summary>
    /// Build a two-quad X-cross mesh with the given footprint. Vertex colour omitted so the
    /// per-instance MultiMesh colour propagates to <c>COLOR</c> in the shader unchanged.
    /// </summary>
    private static ArrayMesh BuildXCrossMesh(float widthM, float heightM, Material material)
    {
        var w = widthM * 0.5f;
        var h = heightM;
        // 8 vertices: two quads, one along X-axis and one along Z-axis. Both span Y ∈ [0, h].
        var verts = new Vector3[]
        {
            new(-w, 0,  0),  new( w, 0,  0),  new(-w, h,  0),  new( w, h,  0),
            new( 0, 0, -w),  new( 0, 0,  w),  new( 0, h, -w),  new( 0, h,  w),
        };
        // CCW front-faces; render_mode cull_disabled on the shader takes care of back side.
        var indices = new[]
        {
            0, 2, 1,   1, 2, 3,
            4, 6, 5,   5, 6, 7,
        };
        var uvs = new Vector2[]
        {
            new(0, 1),  new(1, 1),  new(0, 0),  new(1, 0),
            new(0, 1),  new(1, 1),  new(0, 0),  new(1, 0),
        };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    // ── Shader source ──────────────────────────────────────────────────────────
    // Vertex stage: deterministic per-plant sway phase derived from world-space origin of each
    //   MultiMesh instance. Top vertices (Y > 0) translate proportionally to (wind × sin(t)).
    // Fragment stage: instance COLOR.rgb drives ALBEDO; COLOR.a is an alpha-discard mask that hides
    //   Despawned plants (a=0). Surviving fragments render at a constant 50% ALPHA so the terrain and
    //   ants read through the vegetation. unshaded → no per-fragment lighting cost; the tint already
    //   bakes the "vegetation under day/night" feel via the renderer's u_time.
    private const string ShaderSource = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

uniform vec2 wind = vec2(0.0, 0.0);
uniform float u_time = 0.0;

void vertex() {
    vec3 wp = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    float phase = wp.x * 1.7 + wp.z * 2.3;
    float sway = sin(u_time * 2.0 + phase) * 0.06;
    VERTEX.x += wind.x * sway * VERTEX.y;
    VERTEX.z += wind.y * sway * VERTEX.y;
}

void fragment() {
    if (COLOR.a < 0.5) discard;
    ALBEDO = COLOR.rgb;
    ALPHA = 0.5;
}
";
}
