using System;
using Godot;

namespace AntHill;

/// <summary>
/// Renders food piles + nests as two MultiMeshes (sphere for food, larger half-dome for nests).
///
/// History: <see cref="TyphonBridge.PrepareRender"/> packed both into <c>_overlayBuffer</c>
/// during the 2D era; the buffer is still produced and published in <c>RenderFrame.Overlay</c>
/// but no one consumed it after the 3D migration. This renderer skips that buffer and reads
/// the bridge's typed spans directly — same data, no marshalling, no layout coupling.
///
/// Position / color update every frame in <c>_Process</c>. Food piles shrink with their
/// remaining-ratio and hide entirely when depleted. Nest size + brightness reflect stockpile.
/// </summary>
public partial class FoodNestRenderer : Node3D
{
    private TyphonBridge _bridge;
    private HeightmapResource _heightmap;
    private MultiMeshInstance3D _foodMmi;
    private MultiMeshInstance3D _nestMmi;
    private const int InitialFoodCapacity = 128;
    private const int InitialNestCapacity = 16;

    public void Initialize(TyphonBridge bridge, HeightmapResource heightmap)
    {
        _bridge = bridge;
        _heightmap = heightmap;
    }

    public override void _Ready()
    {
        // Food pile mesh — slightly flattened sphere so it reads as a pile, not a marble.
        var foodMesh = new SphereMesh
        {
            Radius = 0.5f,
            Height = 0.7f,
            RadialSegments = 12,
            Rings = 6,
        };
        var foodMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.7f,
        };
        foodMesh.Material = foodMat;

        var foodMm = new MultiMesh
        {
            Mesh = foodMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = InitialFoodCapacity,
            VisibleInstanceCount = 0,
        };
        _foodMmi = new MultiMeshInstance3D
        {
            Multimesh = foodMm,
            CustomAabb = new Aabb(new Vector3(-1, -1, -1), new Vector3(AntRenderer.WorldSizeM + 2, 3, AntRenderer.WorldSizeM + 2)),
        };
        AddChild(_foodMmi);

        // Nest mesh — larger sphere (the demo doesn't have a proper dome primitive). Brown.
        var nestMesh = new SphereMesh
        {
            Radius = 1.5f,
            Height = 1.5f,
            RadialSegments = 16,
            Rings = 8,
        };
        var nestMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.85f,
        };
        nestMesh.Material = nestMat;

        var nestMm = new MultiMesh
        {
            Mesh = nestMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = InitialNestCapacity,
            VisibleInstanceCount = 0,
        };
        _nestMmi = new MultiMeshInstance3D
        {
            Multimesh = nestMm,
            CustomAabb = new Aabb(new Vector3(-2, -2, -2), new Vector3(AntRenderer.WorldSizeM + 4, 4, AntRenderer.WorldSizeM + 4)),
        };
        AddChild(_nestMmi);
    }

    public override void _Process(double delta)
    {
        if (_bridge == null) return;
        UpdateFood();
        UpdateNests();
    }

    private void UpdateFood()
    {
        var foods = _bridge.FoodSources;
        var remaining = _bridge.FoodRemainingInt;

        // Capacity grow — happens after a burst of tool-placed food.
        if (foods.Length > _foodMmi.Multimesh.InstanceCount)
        {
            _foodMmi.Multimesh.InstanceCount = Math.Max(_foodMmi.Multimesh.InstanceCount * 2, foods.Length);
        }

        int visible = 0;
        for (int i = 0; i < foods.Length; i++)
        {
            var rem = remaining[i];
            if (rem <= 0) continue;

            var (sx, sy, initial) = foods[i];
            var ratio = initial > 0f ? Math.Clamp(rem / initial, 0f, 1f) : 0f;
            float rx = sx * AntRenderer.SimToWorld;
            float rz = sy * AntRenderer.SimToWorld;
            float ry = _heightmap?.Sample(rx, rz) ?? 0f;

            // Pile scale grows with remaining (0.2 m depleted → 0.5 m brimming).
            float scale = 0.2f + 0.3f * ratio;
            var basis = new Basis(Vector3.Right * scale, Vector3.Up * scale, Vector3.Back * scale);
            var xform = new Transform3D(basis, new Vector3(rx, ry + scale * 0.35f, rz));
            _foodMmi.Multimesh.SetInstanceTransform(visible, xform);
            _foodMmi.Multimesh.SetInstanceColor(visible, new Color(0.25f, 0.35f + 0.55f * ratio, 0.15f));
            visible++;
        }
        _foodMmi.Multimesh.VisibleInstanceCount = visible;
    }

    private void UpdateNests()
    {
        var nests = _bridge.NestPositions;
        var stocks = _bridge.NestFoodStocks;
        int initial = _bridge.InitialNestFoodPerNest;
        if (initial <= 0) initial = 1;

        if (nests.Length > _nestMmi.Multimesh.InstanceCount)
        {
            _nestMmi.Multimesh.InstanceCount = Math.Max(_nestMmi.Multimesh.InstanceCount * 2, nests.Length);
        }

        for (int i = 0; i < nests.Length; i++)
        {
            var (sx, sy) = nests[i];
            float rx = sx * AntRenderer.SimToWorld;
            float rz = sy * AntRenderer.SimToWorld;
            float ry = _heightmap?.Sample(rx, rz) ?? 0f;

            float ratio = Math.Clamp(stocks[i] / (float)initial, 0f, 3f);
            float scale = 0.45f + 0.25f * Math.Min(ratio, 1f);   // 45-70 cm radius
            var basis = new Basis(Vector3.Right * scale, Vector3.Up * scale, Vector3.Back * scale);
            // Half-buried dome look — sphere centre at terrain level shows top hemisphere.
            var xform = new Transform3D(basis, new Vector3(rx, ry, rz));
            _nestMmi.Multimesh.SetInstanceTransform(i, xform);

            // Slate blue-gray rather than nest-brown — pops against the dirt terrain so the
            // player can spot nests at a glance. Stockpile drives brightness; cool tint stays constant.
            float fill = Math.Min(ratio, 1f);
            float b = 0.30f + 0.45f * fill;
            _nestMmi.Multimesh.SetInstanceColor(i, new Color(b * 0.55f, b * 0.70f, b));
        }
        _nestMmi.Multimesh.VisibleInstanceCount = nests.Length;
    }
}
