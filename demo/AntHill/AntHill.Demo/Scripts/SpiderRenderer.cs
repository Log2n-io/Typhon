using AntHill.Core;
using Godot;

namespace AntHill.Demo;

/// <summary>
/// Renders the (small, ~8) Spider archetype as low-poly black spheres. Reads
/// <see cref="TyphonBridge.SpiderPositions"/> each frame and rebuilds per-instance transforms
/// — the count is fixed at <see cref="TyphonBridge.SpiderCount"/> so the MultiMesh capacity
/// is allocated up-front and never resized.
///
/// Per-instance colour driven by state — black (wander) / orange (chase) / red flash (kill).
/// </summary>
public partial class SpiderRenderer : Node3D
{
    private TyphonBridge _bridge;
    private HeightmapResource _heightmap;
    private MultiMeshInstance3D _mmi;

    public void Initialize(TyphonBridge bridge, HeightmapResource heightmap)
    {
        _bridge = bridge;
        _heightmap = heightmap;
    }

    private const int KillFlashDuration = 30;   // ticks (~500 ms at 60 Hz) of red post-kill

    public override void _Ready()
    {
        var sphereMesh = new SphereMesh
        {
            Radius = 0.35f,
            Height = 0.5f,
            RadialSegments = 10,
            Rings = 5,
        };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = Colors.White,
            VertexColorUseAsAlbedo = true,
            Roughness = 0.9f,
            Metallic = 0f,
        };
        sphereMesh.Material = mat;

        var mm = new MultiMesh
        {
            Mesh = sphereMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = TyphonBridge.SpiderCount,
            VisibleInstanceCount = 0,
        };
        _mmi = new MultiMeshInstance3D
        {
            Multimesh = mm,
            CustomAabb = new Aabb(new Vector3(-1, -1, -1), new Vector3(AntRenderer.WorldSizeM + 2, 4, AntRenderer.WorldSizeM + 2)),
        };
        AddChild(_mmi);
    }

    public override void _Process(double delta)
    {
        if (_bridge == null) return;
        var spiders = _bridge.SpiderPositions;
        var ticksSinceKill = _bridge.SpiderTicksSinceKill;
        var chasing = _bridge.SpiderChasing;
        if (spiders.Length == 0) return;

        // Compact live spiders into the leading instance slots. A killed spider is parked off-map
        // by TyphonBridge (_spiderPositions = (-1000, -1000)) for its respawn window; skipping it
        // here is what that park relies on — otherwise it renders as a sphere outside the map.
        var instanceCount = _mmi.Multimesh.InstanceCount;
        var liveCount = 0;
        for (var i = 0; i < spiders.Length && liveCount < instanceCount; i++)
        {
            var (sx, sy) = spiders[i];
            if (sx < 0f || sy < 0f) continue;   // parked off-screen (dead, awaiting respawn)

            var rx = sx * AntRenderer.SimToWorld;
            var rz = sy * AntRenderer.SimToWorld;
            var ry = _heightmap?.Sample(rx, rz) ?? 0f;
            var xform = new Transform3D(Basis.Identity, new Vector3(rx, ry + 0.2f, rz));
            _mmi.Multimesh.SetInstanceTransform(liveCount, xform);

            // Body colour priority: red flash (recent kill) > orange (chase) > black (wander).
            var tsk = i < ticksSinceKill.Length ? ticksSinceKill[i] : KillFlashDuration;
            var isChasing = i < chasing.Length && chasing[i];
            Color col;
            if (tsk < KillFlashDuration)
            {
                var flashT = 1.0f - tsk / (float)KillFlashDuration;
                col = new Color(0.02f + 0.98f * flashT, 0.02f + 0.03f * flashT, 0.02f + 0.03f * flashT);
            }
            else if (isChasing)
            {
                col = new Color(0.95f, 0.45f, 0.05f);    // hunting orange
            }
            else
            {
                col = new Color(0.02f, 0.02f, 0.02f);    // wander black
            }
            _mmi.Multimesh.SetInstanceColor(liveCount, col);
            liveCount++;
        }
        _mmi.Multimesh.VisibleInstanceCount = liveCount;
    }
}
