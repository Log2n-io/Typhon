using AntHill.Core;
using Godot;

namespace AntHill.Demo;

/// <summary>
/// 3D ground indicator following the mouse via <see cref="GameCamera.TryProjectToGround"/>.
/// Visible only when the selected tool is something other than Pointer. Scaled to reflect
/// the tool's effective footprint so the player can see where they're about to act.
/// </summary>
public partial class ToolCursor3D : Node3D
{
    private MeshInstance3D _mesh;
    private StandardMaterial3D _material;
    private GameCamera _camera;
    private ToolPalette _palette;
    private HeightmapResource _heightmap;

    public void Initialize(GameCamera camera, ToolPalette palette, HeightmapResource heightmap)
    {
        _camera = camera;
        _palette = palette;
        _heightmap = heightmap;
    }

    public override void _Ready()
    {
        var torus = new TorusMesh
        {
            InnerRadius = 0.42f,
            OuterRadius = 0.5f,
        };
        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.4f, 1.0f, 0.4f, 0.7f),
            EmissionEnabled = true,
            Emission = new Color(0.4f, 1.0f, 0.4f, 1.0f),
            EmissionEnergyMultiplier = 0.6f,
            NoDepthTest = true,
        };
        _mesh = new MeshInstance3D
        {
            Mesh = torus,
            MaterialOverride = _material,
        };
        AddChild(_mesh);
        _mesh.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_camera == null || _palette == null) return;
        if (_palette.Current == ToolKind.Pointer || _palette.Current == ToolKind.Pause)
        {
            _mesh.Visible = false;
            return;
        }

        var mouse = GetViewport().GetMousePosition();
        if (!_camera.TryProjectToGround(mouse, out var ground))
        {
            _mesh.Visible = false;
            return;
        }

        // Lift the cursor slightly above terrain so it's always visible.
        var hY = _heightmap?.Sample(ground.X, ground.Z) ?? 0f;
        _mesh.Position = new Vector3(ground.X, hY + 0.04f, ground.Z);

        var footprint = FootprintFor(_palette.Current);
        _mesh.Scale = new Vector3(footprint, 1f, footprint);

        var col = ColorFor(_palette.Current);
        _material.AlbedoColor = new Color(col.R, col.G, col.B, 0.7f);
        _material.Emission = new Color(col.R, col.G, col.B, 1.0f);

        _mesh.Visible = true;
    }

    public static float FootprintFor(ToolKind tool) => tool switch
    {
        ToolKind.Food => 0.6f,
        ToolKind.Rock => 1.0f,
        ToolKind.Cull => 2.0f,    // diameter 2 m ≈ 1 m radius
        ToolKind.Ignite => 0.8f,
        _ => 0.6f,
    };

    private static Color ColorFor(ToolKind tool) => tool switch
    {
        ToolKind.Food => new Color(0.4f, 1.0f, 0.4f),
        ToolKind.Rock => new Color(0.55f, 0.35f, 0.2f),
        ToolKind.Cull => new Color(1.0f, 0.25f, 0.25f),
        ToolKind.Ignite => new Color(1.0f, 0.55f, 0.1f),
        _ => Colors.White,
    };
}
