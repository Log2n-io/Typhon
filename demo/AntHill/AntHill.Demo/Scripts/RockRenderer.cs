using Godot;

namespace AntHill;

/// <summary>
/// One <see cref="MeshInstance3D"/> per rock placed by the player. Polls
/// <see cref="TyphonBridge.RockPositions"/> each frame and lazily spawns child boxes for
/// any new entries past the watermark. Phase 4 expects dozens, not thousands — per-instance
/// MeshInstance3D is simpler than a MultiMesh and fine at this scale.
/// </summary>
public partial class RockRenderer : Node3D
{
    private TyphonBridge _bridge;
    private HeightmapResource _heightmap;
    private BoxMesh _sharedMesh;
    private StandardMaterial3D _sharedMaterial;
    private int _renderedCount;

    public void Initialize(TyphonBridge bridge, HeightmapResource heightmap)
    {
        _bridge = bridge;
        _heightmap = heightmap;
    }

    public override void _Ready()
    {
        _sharedMesh = new BoxMesh { Size = new Vector3(0.4f, 0.4f, 0.4f) };
        _sharedMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.45f, 0.35f, 0.25f),
            Roughness = 0.95f,
            Metallic = 0f,
        };
    }

    public override void _Process(double delta)
    {
        if (_bridge == null) return;

        var positions = _bridge.RockPositions;
        if (positions.Length == _renderedCount) return;

        const float SimToWorld = AntRenderer.SimToWorld;

        for (var i = _renderedCount; i < positions.Length; i++)
        {
            var (sx, sy) = positions[i];
            var rx = sx * SimToWorld;
            var rz = sy * SimToWorld;
            var ry = (_heightmap?.Sample(rx, rz) ?? 0f) + 0.2f;   // sit on terrain (half box height)

            var inst = new MeshInstance3D
            {
                Mesh = _sharedMesh,
                MaterialOverride = _sharedMaterial,
                Position = new Vector3(rx, ry, rz),
            };
            AddChild(inst);
        }
        _renderedCount = positions.Length;
    }
}
