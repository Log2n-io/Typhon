using Godot;

namespace AntHill;

public enum LodBand { Loupe = 0, Foot = 1, Patch = 2 }

/// <summary>
/// Free-cam (FPS / spectator style):
///   • <see cref="_position"/> — actual camera world position.
///   • <see cref="_yaw"/>      — rotation around world Y. 0 = looking along −Z. Positive = clockwise-from-above (look right).
///   • <see cref="_pitch"/>    — rotation around camera local X. 0 = horizon. Positive = looking up.
///
/// Controls (Phase 1 free-cam revision):
///   WASD                — forward / back / strafe along the camera's own basis (full 3D — flies in the look direction)
///   Space / Ctrl        — world-up / world-down (altitude independent of look)
///   Right-mouse drag    — mouselook (yaw + pitch). Mouse cursor captured for the duration of the drag.
///   Shift               — sprint (× <see cref="SprintFactor"/>)
///   Mouse wheel         — adjust base move speed (multiplicative)
///   T / Ctrl+T          — tilt presets (sets pitch to top-down / iso / cinematic)
///
/// LOD-band semantics: with no orbit target, the band is derived from <see cref="VisibleWidthAtFocus"/> —
/// the effective visible width at the camera's forward-ray ground intersection, capped at 100 m.
/// </summary>
public partial class GameCamera : Camera3D
{
    public const float FovDegrees   = 50f;
    public const float BaseSpeedDef = 6f;       // metres / second baseline
    public const float SprintFactor = 4.0f;
    public const float SpeedStep    = 1.15f;    // mouse-wheel multiplier per notch

    public const float MinSpeed = 0.1f;
    public const float MaxSpeed = 200f;

    // LOD band boundaries (effective visible width, metres).
    public const float LoupeFootBoundary = 5f;
    public const float FootPatchBoundary = 30f;
    private const float FootPatchHalf    = 4.5f;

    // Tilt presets used by SetTilt / settings dropdown. They set pitch *as if looking at the ground point ahead*:
    //   tilt 0  → pitch −90° (straight down)
    //   tilt 45° iso → pitch −45°
    //   tilt 85° cinematic → pitch −5°
    private const float TiltTopDown   = 0.0f;
    private const float TiltIso       = Mathf.Pi / 4f;
    private const float TiltCinematic = Mathf.Pi * (85f / 180f);

    private Vector3 _position = new(50f, 30f, 50f);  // start above world centre
    private float _yaw;
    private float _pitch = -Mathf.Pi / 2f;            // start looking straight down
    private float _baseSpeed = BaseSpeedDef;

    private bool _looking;

    // Minimum vertical clearance above the heightmap. Enforced at the end of _Process so any
    // altitude source (WASD/Space/Ctrl, fly-to, future tools) gets clamped uniformly. Sampled at
    // the camera's own XZ — when flying near a peak the camera rides over the top.
    private HeightmapResource _heightmap;
    private const float MinGroundClearance = 2f;    // metres above the terrain surface

    // Eased fly-to (minimap click) — moves both position and pitch.
    private Vector3? _flyToPos;
    private float _flyToPitchTarget;
    private float _flyToTimer;
    private const float FlyToDuration = 1.0f;

    public override void _Ready()
    {
        Projection = ProjectionType.Perspective;
        Fov = FovDegrees;
        ApplyTransform();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp)
            {
                _baseSpeed = Mathf.Clamp(_baseSpeed * SpeedStep, MinSpeed, MaxSpeed);
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                _baseSpeed = Mathf.Clamp(_baseSpeed / SpeedStep, MinSpeed, MaxSpeed);
            }
        }

        if (@event is InputEventMouseButton mmb && mmb.ButtonIndex == MouseButton.Right)
        {
            _looking = mmb.Pressed;
            Input.MouseMode = _looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        }

        if (@event is InputEventMouseMotion motion && _looking)
        {
            // FPS mouselook: 1 pixel ≈ 0.0035 rad (~0.2°/px). Pitch clamped to avoid gimbal flip.
            _yaw   += motion.Relative.X * 0.0035f;
            _pitch -= motion.Relative.Y * 0.0035f;
            _pitch  = Mathf.Clamp(_pitch, -Mathf.Pi * 0.49f, Mathf.Pi * 0.49f);
        }

        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.T:
                    if (Input.IsKeyPressed(Key.Ctrl)) SetTilt(TiltCinematic);
                    else                              SetTilt(Mathf.Abs(_pitch + Mathf.Pi / 2f) < 0.01f ? TiltIso : TiltTopDown);
                    break;
            }
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Movement — true free-cam (Pattern A noclip): WASD along the camera's local axes.
        //   W/S = forward / back along the full 3D look direction (so pitching down + W flies you down toward the ground)
        //   A/D = strafe left / right along the camera's right vector (horizontal because we don't roll)
        //   Space / Ctrl = world up / world down (altitude shortcut, independent of look)
        Vector3 forward = ForwardWorld;
        Vector3 right   = RightWorld;
        Vector3 move = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) move += forward;
        if (Input.IsKeyPressed(Key.S)) move -= forward;
        if (Input.IsKeyPressed(Key.D)) move += right;
        if (Input.IsKeyPressed(Key.A)) move -= right;
        if (Input.IsKeyPressed(Key.Space)) move += Vector3.Up;
        if (Input.IsKeyPressed(Key.Ctrl))  move += Vector3.Down;

        if (move.LengthSquared() > 0f)
        {
            float speed = _baseSpeed * (Input.IsKeyPressed(Key.Shift) ? SprintFactor : 1f);
            _position += move.Normalized() * speed * dt;
        }

        // Eased fly-to
        if (_flyToPos.HasValue)
        {
            _flyToTimer += dt;
            float t = Mathf.Clamp(_flyToTimer / FlyToDuration, 0f, 1f);
            float ease = t * t * (3f - 2f * t);
            _position = _position.Lerp(_flyToPos.Value, ease);
            _pitch    = Mathf.Lerp(_pitch, _flyToPitchTarget, ease);
            if (t >= 1f)
            {
                _position = _flyToPos.Value;
                _pitch    = _flyToPitchTarget;
                _flyToPos = null;
            }
        }

        // Ground clearance — sample the heightmap directly below the camera. If we'd dip below
        // groundY + MinGroundClearance, push the camera up. Works for every altitude source
        // (manual descent with Ctrl, fly-to into a hilltop, etc.) because it runs last.
        if (_heightmap != null)
        {
            float groundY = _heightmap.Sample(_position.X, _position.Z);
            float minY = groundY + MinGroundClearance;
            if (_position.Y < minY) _position.Y = minY;
        }

        ApplyTransform();
    }

    public void SetHeightmap(HeightmapResource heightmap) => _heightmap = heightmap;

    /// <summary>Builds the Transform from <see cref="_position"/>, <see cref="_yaw"/>, <see cref="_pitch"/>.</summary>
    private void ApplyTransform()
    {
        // Build the basis directly from yaw/pitch — no LookingAt() call. The orbital version used LookingAt with
        // (0,1,0) as up, which is degenerate when forward is vertical (pitch=±π/2). Computing the basis avoids that.
        //
        // FPS convention: forward = -Z, right = +X, up = +Y in the camera's local frame.
        // At yaw=0, pitch=0: forward=(0,0,-1), right=(1,0,0), up=(0,1,0).
        // Yaw rotates around world Y; pitch rotates around the camera's right axis.
        float cp = Mathf.Cos(_pitch);
        float sp = Mathf.Sin(_pitch);
        float cy = Mathf.Cos(_yaw);
        float sy = Mathf.Sin(_yaw);

        var forward = new Vector3(sy * cp, sp, -cy * cp);
        var right   = new Vector3(cy, 0f, sy);
        var up      = new Vector3(-sy * sp, cp, cy * sp);

        // Godot Basis(x, y, z) takes the basis columns. Camera local Z = -forward (camera looks along -Z).
        var basis = new Basis(right, up, -forward);
        Transform = new Transform3D(basis, _position);
    }

    private Vector3 ForwardWorld
    {
        get
        {
            float cp = Mathf.Cos(_pitch);
            float sp = Mathf.Sin(_pitch);
            float cy = Mathf.Cos(_yaw);
            float sy = Mathf.Sin(_yaw);
            return new Vector3(sy * cp, sp, -cy * cp).Normalized();
        }
    }

    private Vector3 RightWorld
    {
        get
        {
            // Right is the world-horizontal vector 90° clockwise from the yaw-only forward.
            float cy = Mathf.Cos(_yaw);
            float sy = Mathf.Sin(_yaw);
            return new Vector3(cy, 0f, sy).Normalized();
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Returns the world AABB visible through the camera frustum at the ground plane (Y=0), via 4-corner unprojection.</summary>
    public Rect2 GetGroundAabb(Viewport viewport)
    {
        var size = viewport.GetVisibleRect().Size;
        if (size.X <= 0 || size.Y <= 0)
        {
            return new Rect2(_position.X - 50, _position.Z - 50, 100, 100);
        }

        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        Vector2[] corners = [Vector2.Zero, new(size.X, 0), new(0, size.Y), size];
        foreach (var c in corners)
        {
            if (!TryProjectToGround(c, out var p)) continue;
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Z < minZ) minZ = p.Z;
            if (p.Z > maxZ) maxZ = p.Z;
        }

        if (float.IsInfinity(minX) || float.IsInfinity(minZ))
        {
            return new Rect2(_position.X - 50, _position.Z - 50, 100, 100);
        }
        return new Rect2(minX, minZ, maxX - minX, maxZ - minZ);
    }

    /// <summary>
    /// World-space AABB enclosing every world point currently in the camera frustum, clamped to [0, WorldSize]² in XZ.
    /// Robust to any camera pose, including near-horizontal pitch (where corner rays go above the horizon and miss the ground).
    /// For corners whose ray *doesn't* hit Y=0 in front of the camera, we extend the ray to its farthest intersection with
    /// the world's XZ bounding box (treating Y as unbounded) and use that point's XZ. Result is always a non-empty rect
    /// inside the world; if the camera can't see any part of the world, falls back to the full world rect.
    /// </summary>
    public Rect2 ComputeWorldVisibleAabb(Viewport viewport)
    {
        var size = viewport.GetVisibleRect().Size;
        if (size.X <= 0 || size.Y <= 0) return WorldRect;

        const float worldMax = AntRenderer.WorldSizeM;
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;

        Vector2[] corners = [Vector2.Zero, new(size.X, 0), new(0, size.Y), size];
        foreach (var c in corners)
        {
            var origin    = ProjectRayOrigin(c);
            var direction = ProjectRayNormal(c);

            float t = -1f;

            // First try: intersection with Y=0 in front of the camera (the common case for cameras looking down).
            if (Mathf.Abs(direction.Y) > 1e-6f)
            {
                float tGround = -origin.Y / direction.Y;
                if (tGround > 0) t = tGround;
            }

            // Fallback: ray misses Y=0 in front (looking up). Extend to the farthest intersection with the world XZ box.
            // For each of the 4 XZ boundary planes, find the t where the ray crosses it; take the farthest positive t,
            // capped at 500 m so a ray almost parallel to the ground doesn't extrapolate to infinity.
            if (t <= 0)
            {
                float tFar = 0f;
                if (direction.X >  1e-6f) tFar = Mathf.Max(tFar, (worldMax  - origin.X) / direction.X);
                if (direction.X < -1e-6f) tFar = Mathf.Max(tFar, (0f       - origin.X) / direction.X);
                if (direction.Z >  1e-6f) tFar = Mathf.Max(tFar, (worldMax - origin.Z) / direction.Z);
                if (direction.Z < -1e-6f) tFar = Mathf.Max(tFar, (0f       - origin.Z) / direction.Z);
                t = Mathf.Min(tFar, 500f);
            }

            if (t <= 0) continue;   // ray points away from the world entirely — corner contributes nothing

            var p = origin + direction * t;
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Z < minZ) minZ = p.Z;
            if (p.Z > maxZ) maxZ = p.Z;
        }

        if (float.IsInfinity(minX) || float.IsInfinity(minZ)) return WorldRect;

        // Clamp to world bounds — no point telling the sim about ants beyond the world.
        minX = Mathf.Clamp(minX, 0f, worldMax);
        maxX = Mathf.Clamp(maxX, 0f, worldMax);
        minZ = Mathf.Clamp(minZ, 0f, worldMax);
        maxZ = Mathf.Clamp(maxZ, 0f, worldMax);

        if (maxX <= minX + 0.001f || maxZ <= minZ + 0.001f) return WorldRect;  // degenerate after clamp

        return new Rect2(minX, minZ, maxX - minX, maxZ - minZ);
    }

    private static Rect2 WorldRect => new(0f, 0f, AntRenderer.WorldSizeM, AntRenderer.WorldSizeM);

    /// <summary>Project a viewport-space pixel coord to its intersection with the ground plane Y=0. Returns false if the ray is parallel.</summary>
    public bool TryProjectToGround(Vector2 screenPos, out Vector3 ground)
    {
        var origin = ProjectRayOrigin(screenPos);
        var direction = ProjectRayNormal(screenPos);
        if (Mathf.Abs(direction.Y) < 1e-6f) { ground = default; return false; }
        float t = -origin.Y / direction.Y;
        if (t < 0) { ground = default; return false; }  // ground is behind the camera
        ground = origin + direction * t;
        return true;
    }

    /// <summary>Distance from camera to where its forward ray meets the ground. Falls back to camera altitude if looking up.</summary>
    public float DistanceToFocus
    {
        get
        {
            var fwd = ForwardWorld;
            if (Mathf.Abs(fwd.Y) < 1e-3f) return Mathf.Max(_position.Y, 1f) * 30f;  // looking near-horizontal → huge effective distance
            float t = -_position.Y / fwd.Y;
            return t > 0 ? Mathf.Min(t, 200f) : Mathf.Max(_position.Y, 1f) * 5f;
        }
    }

    /// <summary>Effective visible width at the focus point (metres). 2 · dist · tan(fov/2).</summary>
    public float VisibleWidthAtFocus => 2f * DistanceToFocus * Mathf.Tan(Mathf.DegToRad(FovDegrees) * 0.5f);

    /// <summary>Legacy HUD label — same as <see cref="VisibleWidthAtFocus"/>.</summary>
    public float OrthoZoom => VisibleWidthAtFocus;

    /// <summary>Approximate metres-per-screen-pixel at the focus depth — used by ant shader sub-pixel guard.</summary>
    public float MetresPerPixelAtTarget(Viewport vp)
    {
        var size = vp.GetVisibleRect().Size;
        if (size.Y <= 0) return 0.01f;
        return VisibleWidthAtFocus / size.Y;
    }

    /// <summary>Sets pitch from the legacy "tilt" semantic (0 = top-down → pitch −90°, π/2 ≈ horizon → pitch 0).</summary>
    public void SetTilt(float tiltRadians)
    {
        tiltRadians = Mathf.Clamp(tiltRadians, 0f, Mathf.Pi * 0.49f);
        _pitch = tiltRadians - Mathf.Pi / 2f;
    }

    /// <summary>Teleport the camera to a vantage point that looks at the given world XZ position from above. Used by minimap click.</summary>
    public void SetFollowTarget(Vector3 worldPos, bool ease = true)
    {
        // Camera target = clickPoint at Y=0; vantage = clickPoint + (0, 30, 0) at top-down pitch.
        var vantage = new Vector3(worldPos.X, 30f, worldPos.Z);
        if (ease)
        {
            _flyToPos         = vantage;
            _flyToPitchTarget = -Mathf.Pi / 2f;
            _flyToTimer       = 0f;
            // Also align yaw to 0 (north) for a clean overhead — leave yaw if user wants, comment out:
            _yaw = 0f;
        }
        else
        {
            _position = vantage;
            _pitch    = -Mathf.Pi / 2f;
            _yaw      = 0f;
            _flyToPos = null;
        }
    }

    public Vector3 FollowTarget => _position;     // legacy alias for HUD code that wants "what we're looking at"

    /// <summary>Current LOD band based on effective visible width at the focus point.</summary>
    public LodBand CurrentBand
    {
        get
        {
            float w = VisibleWidthAtFocus;
            if (w < LoupeFootBoundary) return LodBand.Loupe;
            if (w < FootPatchBoundary) return LodBand.Foot;
            return LodBand.Patch;
        }
    }

    public float FadeIndividuals
    {
        get
        {
            float w = VisibleWidthAtFocus;
            float lo = FootPatchBoundary - FootPatchHalf;
            float hi = FootPatchBoundary + FootPatchHalf;
            float t = (w - lo) / (hi - lo);
            return Mathf.Clamp(1f - t, 0f, 1f);
        }
    }

    public float FadeDensity => 1f - FadeIndividuals;
}
