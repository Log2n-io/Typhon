using System.Collections.Generic;
using System.Threading;
using AntHill.Core;
using Godot;
using Typhon.Profiler;

namespace AntHill.Demo;

public partial class Main : Node3D
{
    private TyphonBridge _bridge;
    private AntRenderer _antRenderer;
    private GameCamera _camera;
    private Terrain _terrain;
    private HeightmapResource _heightmap;
    private SettingsPanel _settings;
    private TextureRect _minimapRect;
    private ShaderMaterial _minimapMaterial;
    private const int MinimapSizePx = 256;

    // Phase 4 — god-game UI
    private ToolPalette _toolPalette;
    private ToolCursor3D _toolCursor;
    private EventLogHud _eventLog;
    private RockRenderer _rockRenderer;
    private FoodNestRenderer _foodNestRenderer;
    private bool _snapToGrid;

    // Phase 5 — predator visuals
    private SpiderRenderer _spiderRenderer;

    // Phase 6C — vegetation. Built after SetHeightmap so PlantGrid is already populated when the
    // renderer's _Ready reads positions; UpdateFromFrame in _Process applies dirty-list colour
    // changes (Alive → tint, Burnt → charred, Despawned → alpha 0).
    private VegetationRenderer _vegetation;

    // Phase 6 polish — GPU-driven flame + smoke particles. Reads the shared fire_tex from
    // Terrain and the wind vector from the bridge; samples the texture per-spawn to gate
    // emission on Burning cells only.
    private FireParticleRenderer _fireParticles;

    // Phase 6A — Daisyworld / day-night plumbing. Refs retained so we can modulate per-frame.
    // _sun.LightEnergy and _worldEnv.AmbientLightEnergy carry brightness for the shaded materials
    // (rocks / food / nests / spiders / cursor); the two unshaded shaders pull it via u_brightness.
    // BackgroundColor is lerped between SkyNight and SkyDay each frame so the flat clear-color
    // tracks the rest of the world. Real skybox/sun-disk is Phase 8.
    private DirectionalLight3D _sun;
    private WorldEnvironment _worldEnv;
    private const float SunBaseEnergy = 1.0f;
    private const float AmbientBaseEnergy = 0.6f;
    private static readonly Color SkyDay   = new(0.40f, 0.55f, 0.70f);
    private static readonly Color SkyNight = new(0.04f, 0.05f, 0.12f);

    // HUD
    private Label _hudLeft;
    private Label _hudRight;

    // Persisted window state — path is a user:// URI so Godot resolves it to the platform app-data dir (on Windows:
    // %APPDATA%/Godot/app_userdata/<project-name>/window_state.cfg), keeping the file out of the project folder and per-user.
    private const string WindowStatePath = "user://window_state.cfg";

    public override void _Ready()
    {
        Thread.CurrentThread.Name = "Main Thread";
        
        // Restore last run's window position/size FIRST, before any scene-graph work. Godot's layout pass treats the window
        // dimensions as authoritative; changing them later would cascade into HUD anchor recalculations.
        LoadWindowState();

        GD.Print("AntHill: Initializing Typhon engine...");

        // Resolve profiler inputs from env vars + Godot cmdline user args (after the "++" separator).
        _profilerConfig = ProfilerLaunchConfig
            .FromEnvironment()
            .MergedWith(ProfilerLaunchConfig.FromArgs(OS.GetCmdlineUserArgs()));

        ProfilerLauncher.EnableTelemetryGateIfActive(_profilerConfig);

        _bridge = new TyphonBridge();
        _bridge.Initialize();

        if (_profilerConfig.IsActive)
        {
            try
            {
                _exporters = ProfilerLauncher.CreateExporters(_profilerConfig, _bridge.ProfilerParent);
                foreach (var exp in _exporters)
                {
                    TyphonProfiler.AttachExporter(exp);
                }
                // CPU sampler must start BEFORE BuildSessionMetadata so its QPC anchor lands in the trace header.
                // Gated internally on CpuSampling being enabled + a configured trace file — a no-op (returns 0) otherwise.
                var samplingQpc = ProfilerLauncher.StartCpuSampler(_profilerConfig);
                var metadata = ProfilerSetup.BuildSessionMetadata(_bridge.Systems, 16, 60f, () => _bridge?.CurrentTick ?? 0,
                    _bridge.DatabaseEngine, _bridge.ResourceGraphRoot, _bridge.ActiveRuntime, samplingQpc);

                if (_profilerConfig.LiveWaitMs > 0 && _profilerConfig.LivePort >= 0)
                {
                    GD.Print($"AntHill: Waiting up to {_profilerConfig.LiveWaitMs} ms for the workbench to attach on :{_profilerConfig.LivePort}…");
                }

                TyphonProfiler.Start(_bridge.ProfilerParent, metadata);

                if (_profilerConfig.TraceFilePath != null) GD.Print($"AntHill: Profiler enabled -> file: {_profilerConfig.TraceFilePath}");
                if (_profilerConfig.LivePort >= 0)
                {
                    var tcp = FindTcpExporter(_exporters);
                    if (tcp != null && _profilerConfig.LiveWaitMs > 0)
                    {
                        GD.Print(tcp.HasClientEverConnected
                            ? $"AntHill: Profiler enabled -> live TCP client attached on :{_profilerConfig.LivePort}."
                            : $"AntHill: Profiler enabled -> live TCP listener on :{_profilerConfig.LivePort} (live-wait timed out — viewer can still attach).");
                    }
                    else
                    {
                        GD.Print($"AntHill: Profiler enabled -> live TCP listener on :{_profilerConfig.LivePort}.");
                    }
                    GD.Print("  Viewer server must connect to this port (LiveStream:Port in its config, default 9100).");
                }
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"AntHill: profiler startup FAILED — {ex.GetType().Name}: {ex.Message}");
                GD.PrintErr($"  Likely cause: port {_profilerConfig.LivePort} already in use, firewall blocking, or trace path not writable. Continuing without profiling.");
                _exporters = null;
            }
        }

        GD.Print($"AntHill: Spawned {TyphonBridge.AntCount:N0} ants. Starting runtime...");

        _antRenderer = GetNode<AntRenderer>("AntRenderer");
        _antRenderer.SetBridge(_bridge.RenderBridge);

        _camera = GetNode<GameCamera>("Camera");

        BuildSceneLighting();

        // Procedural heightmap (Perlin, ±50 cm relief). Owned here; Phase 3 promotes to a Typhon shared resource.
        _heightmap = HeightmapFactory.GeneratePerlin();
        _bridge.SetHeightmap(_heightmap);   // slope-aware MoveAll samples this in AntUpdateTick Step 1
        _camera?.SetHeightmap(_heightmap);  // free-cam enforces min clearance above the terrain

        // Unified ground: heightmap displacement + pheromone overlay + density overlay, all one MeshInstance3D.
        _terrain = new Terrain();
        _terrain.Initialize(_bridge.RenderBridge, _bridge, _heightmap);
        AddChild(_terrain);

        // Bind heightmap to the ant shader so capsules sit on the terrain.
        _antRenderer.SetHeightmapTexture(_terrain.HeightmapTexture);

        BuildHud();
        BuildMinimap();
        BuildSettings();
        BuildToolPalette();
        BuildEventLog();
        BuildRockRenderer();
        BuildFoodNestRenderer();
        BuildSpiderRenderer();
        BuildVegetationRenderer();
        BuildFireParticleRenderer();
        BuildToolCursor();

        _bridge.Start();
        GD.Print("AntHill: Runtime started. WASD=pan, wheel=zoom, T=tilt, Ctrl+T=cinematic, mid-drag=yaw, `=pause, 1-4=speed, H=pheromone overlay, M=minimap toggle, Esc=settings, F1=debug HUD, tools 1/2/3/4/5/P.");

        ProfilerLauncher.PrintDiagnostics(GD.Print, _exporters);
    }

    private void BuildSettings()
    {
        _settings = new SettingsPanel();
        _settings.TimeScaleChanged += scale =>
        {
            if (_bridge != null) _bridge.TimeScale = scale;
            if (scale > 0f) _lastTimeScale = scale;
        };
        _settings.TiltChosen += tilt => _camera?.SetTilt(tilt);
        _settings.PheromoneToggled += enabled =>
        {
            if (_terrain != null) _terrain.ShowPheromone = enabled;
        };
        _settings.DebugToggled += enabled =>
        {
            if (_hudRight != null) _hudRight.Visible = enabled;
        };
        _settings.SnapToGridChanged += enabled => _snapToGrid = enabled;
        _settings.LuminosityChanged += val =>
        {
            if (_bridge != null) _bridge.Luminosity = val;
        };
        _settings.PauseDayNightToggled += paused =>
        {
            if (_bridge != null) _bridge.PauseDayNight = paused;
        };
        AddChild(_settings);
    }

    private void BuildToolPalette()
    {
        _toolPalette = new ToolPalette();
        _toolPalette.ToolSelected += OnToolSelected;
        AddChild(_toolPalette);
    }

    private void BuildToolCursor()
    {
        _toolCursor = new ToolCursor3D();
        _toolCursor.Initialize(_camera, _toolPalette, _heightmap);
        AddChild(_toolCursor);
    }

    private void BuildEventLog()
    {
        _eventLog = new EventLogHud();
        _eventLog.Initialize(_camera);
        AddChild(_eventLog);
    }

    private void BuildRockRenderer()
    {
        _rockRenderer = new RockRenderer();
        _rockRenderer.Initialize(_bridge, _heightmap);
        AddChild(_rockRenderer);
    }

    private void BuildFoodNestRenderer()
    {
        _foodNestRenderer = new FoodNestRenderer();
        _foodNestRenderer.Initialize(_bridge, _heightmap);
        AddChild(_foodNestRenderer);
    }

    private void BuildSpiderRenderer()
    {
        _spiderRenderer = new SpiderRenderer();
        _spiderRenderer.Initialize(_bridge, _heightmap);
        AddChild(_spiderRenderer);
    }

    private void BuildVegetationRenderer()
    {
        _vegetation = new VegetationRenderer();
        _vegetation.Initialize(_bridge);
        AddChild(_vegetation);
    }

    private void BuildFireParticleRenderer()
    {
        _fireParticles = new FireParticleRenderer();
        _fireParticles.Initialize(_bridge, _terrain);
        AddChild(_fireParticles);
    }

    private void BuildSceneLighting()
    {
        // No light existed before Phase 5 (Terrain + AntRenderer shaders are unshaded). The new
        // StandardMaterial3D-based renderers (Rock / Food / Nest / Spider / ToolCursor) need actual
        // light to be visible. Add one strong directional sun aligned with the Terrain shader's
        // sun_dir so shadowing direction matches across shaded and unshaded surfaces.
        // Phase 6A: keep refs so _Process can modulate LightEnergy + AmbientLightEnergy by the
        // bridge's EnvironmentBrightness scalar.
        _sun = new DirectionalLight3D
        {
            LightColor = new Color(1.0f, 0.96f, 0.88f),
            LightEnergy = SunBaseEnergy,
            ShadowEnabled = false,   // shadow on 100k+ moving instances is a perf trap — defer to Phase 9
        };
        _sun.Transform = new Transform3D(Basis.LookingAt(new Vector3(-0.55f, -0.45f, -0.30f), Vector3.Up), new Vector3(50f, 50f, 50f));
        AddChild(_sun);

        _worldEnv = new WorldEnvironment
        {
            Environment = new Environment
            {
                BackgroundMode = Environment.BGMode.Color,
                BackgroundColor = SkyDay,
                AmbientLightSource = Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.75f, 0.80f, 0.90f),
                AmbientLightEnergy = AmbientBaseEnergy,
            },
        };
        AddChild(_worldEnv);
    }

    private void OnToolSelected(ToolKind kind)
    {
        if (kind == ToolKind.Pause)
        {
            // Pause is a one-shot toggle, then the palette springs back to Pointer for click safety.
            var current = _bridge?.TimeScale ?? 1f;
            if (current > 0f)
            {
                if (_bridge != null) _bridge.TimeScale = 0f;
            }
            else
            {
                if (_bridge != null) _bridge.TimeScale = _lastTimeScale > 0f ? _lastTimeScale : 1f;
            }
            _toolPalette.Select(ToolKind.Pointer);
        }
    }

    private void BuildHud()
    {
        var hud = new CanvasLayer { Layer = 10 };

        _hudLeft = new Label
        {
            Position = new Vector2(10, 10),
        };
        StyleLabel(_hudLeft, 16);
        hud.AddChild(_hudLeft);

        // _hudRight historically anchored to the top-right, but the Phase 4 EventLog panel now
        // occupies the right column (EventLogHud.cs:32 anchors right, OffsetTop 80), so the debug
        // HUD lives on the left under _hudLeft to avoid overlap. Name kept for blame churn.
        _hudRight = new Label
        {
            Position = new Vector2(10, 110),
        };
        StyleLabel(_hudRight, 14);
        hud.AddChild(_hudRight);

        AddChild(hud);
    }

    private static void StyleLabel(Label label, int fontSize)
    {
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_shadow_color", Colors.Black);
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        label.AddThemeFontSizeOverride("font_size", fontSize);
    }

    private void BuildMinimap()
    {
        // 2D canvas-item minimap — draws density + heightmap + pheromone directly via shader, NO SubViewport / 3D camera.
        // Why: re-rendering the world through a second 3D camera with OwnWorld3D=false had inconsistent culling (terrain
        // partially or fully dropped depending on main camera pose). Drawing the same texture data as a 2D composite is
        // both reliable and a better minimap UX (density-as-map, not 200 k subpixel dots).
        var hudOverlay = new CanvasLayer { Layer = 11 };

        var shader = new Shader { Code = MinimapShaderSource };
        _minimapMaterial = new ShaderMaterial { Shader = shader };
        _minimapMaterial.SetShaderParameter("density_tex",   _terrain.DensityTexture);
        _minimapMaterial.SetShaderParameter("phero_tex",     _terrain.PheromoneTexture);
        _minimapMaterial.SetShaderParameter("heightmap_tex", _terrain.HeightmapTexture);
        _minimapMaterial.SetShaderParameter("heightmap_res", (float)HeightmapResource.DefaultResolution);
        _minimapMaterial.SetShaderParameter("dirt_color",    new Color(0.32f, 0.24f, 0.18f));
        _minimapMaterial.SetShaderParameter("sun_dir",       new Vector3(0.55f, 0.45f, 0.30f).Normalized());
        _minimapMaterial.SetShaderParameter("show_pheromone", false);

        _minimapRect = new TextureRect
        {
            // The shader uses its own uniform samplers; this primary texture exists only so the TextureRect renders.
            // Heightmap is a reasonable choice — same proportions as the minimap and we'd be sampling it anyway.
            Texture = _terrain.HeightmapTexture,
            Material = _minimapMaterial,
            Size = new Vector2(MinimapSizePx, MinimapSizePx),
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            AnchorTop = 1.0f,
            AnchorBottom = 1.0f,
            OffsetLeft = -MinimapSizePx - 10,
            OffsetTop = -MinimapSizePx - 10,
            OffsetRight = -10,
            OffsetBottom = -10,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            // ExpandMode=IgnoreSize: don't let the 1024×1024 heightmap force the rect to be 1024×1024.
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _minimapMaterial.SetShaderParameter("minimap_size_px", (float)MinimapSizePx);
        _minimapRect.GuiInput += OnMinimapClick;
        hudOverlay.AddChild(_minimapRect);
        AddChild(hudOverlay);
    }

    private const string MinimapShaderSource = @"
shader_type canvas_item;

uniform sampler2D heightmap_tex : filter_linear, repeat_disable;
uniform sampler2D phero_tex     : filter_linear, repeat_disable;
uniform sampler2D density_tex   : filter_linear, repeat_disable;
uniform float heightmap_res;
uniform float minimap_size_px;
uniform vec4  dirt_color : source_color;
uniform vec3  sun_dir;
uniform bool  show_pheromone;

varying vec2 v_uv;

void vertex() {
    // Force UV to span exactly 0..1 across the rect, independent of the assigned Texture's intrinsic UV.
    // (Default canvas_item UV is the assigned texture's UV — for a 1024×1024 heightmap displayed in a 256-px rect
    // some Godot/ExpandMode combinations give UV ranges other than 0..1, depending on Texture aspect.)
    v_uv = VERTEX / minimap_size_px;
}

void fragment() {
    vec2 uv = v_uv;

    // Cheap heightmap shading — Lambertian against a fixed sun. Visual relief identical to main terrain shader.
    float h  = texture(heightmap_tex, uv).r;
    float px = 1.0 / heightmap_res;
    float hx = texture(heightmap_tex, uv + vec2(px, 0.0)).r;
    float hz = texture(heightmap_tex, uv + vec2(0.0, px)).r;
    vec3 N = normalize(vec3(-(hx - h), 0.05, -(hz - h)));
    vec3 L = normalize(sun_dir);
    float shade = 0.45 + 0.55 * max(dot(N, L), 0.0);

    vec3 col = dirt_color.rgb * shade;

    // Density overlay — ALWAYS visible on the minimap. This is the map's primary information layer.
    float d = texture(density_tex, uv).r;
    vec3 hot = mix(vec3(0.35, 0.18, 0.10), vec3(1.0, 0.30, 0.10), d);
    col = mix(col, hot, smoothstep(0.0, 0.04, d) * 0.85);

    // Pheromone overlay (additive, gated by toggle).
    if (show_pheromone) {
        vec4 ph = texture(phero_tex, uv);
        float intensity = max(max(ph.r, ph.g), ph.b);
        col = mix(col, col + ph.rgb, intensity);
    }

    COLOR = vec4(col, 1.0);
}
";

    private void OnMinimapClick(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed || mb.ButtonIndex != MouseButton.Left) return;

        // Map click position in the TextureRect to world XZ. Minimap covers the full 100 m world.
        var localPos = mb.Position;  // relative to the TextureRect
        var u = Mathf.Clamp(localPos.X / MinimapSizePx, 0f, 1f);
        var v = Mathf.Clamp(localPos.Y / MinimapSizePx, 0f, 1f);
        var worldX = u * AntRenderer.WorldSizeM;
        var worldZ = v * AntRenderer.WorldSizeM;
        _camera?.SetFollowTarget(new Vector3(worldX, 0f, worldZ));
    }

    private static TcpExporter FindTcpExporter(List<IProfilerExporter> exporters)
    {
        if (exporters == null) return null;
        foreach (var e in exporters)
        {
            if (e is TcpExporter tcp) return tcp;
        }
        return null;
    }

    private List<IProfilerExporter> _exporters;
    private ProfilerLaunchConfig _profilerConfig;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Quoteleft:
                    if (_bridge != null)
                    {
                        _bridge.TimeScale = _bridge.TimeScale == 0f ? _lastTimeScale : 0f;
                    }
                    break;
                // [ / ] — step the settings slider down/up through the discrete speed table
                // (0, 0.5×, 1×, 2×, 4×, 10×). Slider drives the bridge, so the keyboard route
                // stays in sync with the on-screen control.
                case Key.Bracketleft:  _settings?.StepTimeScale(-1); break;
                case Key.Bracketright: _settings?.StepTimeScale(+1); break;
                // Phase 4 — number row + P select tools. Speed control lives in the settings panel slider.
                case Key.Key1: _toolPalette?.Select(ToolKind.Pointer); break;
                case Key.Key2: _toolPalette?.Select(ToolKind.Food); break;
                case Key.Key3: _toolPalette?.Select(ToolKind.Rock); break;
                case Key.Key4: _toolPalette?.Select(ToolKind.Cull); break;
                case Key.Key5: _toolPalette?.Select(ToolKind.Ignite); break;
                case Key.P: _toolPalette?.Select(ToolKind.Pause); break;
                case Key.H:
                    if (_terrain != null)
                    {
                        _terrain.TogglePheromone();
                        _settings?.SetPheromoneToggle(_terrain.ShowPheromone);
                    }
                    break;
                case Key.M: if (_minimapRect != null) _minimapRect.Visible = !_minimapRect.Visible; break;
                case Key.F1: if (_hudRight != null) _hudRight.Visible = !_hudRight.Visible; break;
            }
        }
        else if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            TryDispatchToolClick(mb.Position);
        }
    }

    private void TryDispatchToolClick(Vector2 screenPos)
    {
        if (_toolPalette == null || _bridge == null || _camera == null) return;
        var tool = _toolPalette.Current;
        if (tool == ToolKind.Pointer || tool == ToolKind.Pause) return;

        if (!_camera.TryProjectToGround(screenPos, out var ground)) return;

        // Render m → sim units. AntRenderer.SimToWorld converts sim→render, so we divide.
        const float simPerWorld = TyphonBridge.WorldSize / AntRenderer.WorldSizeM;
        var simX = ground.X * simPerWorld;
        var simY = ground.Z * simPerWorld;

        if (_snapToGrid)
        {
            // Snap to 1 m grid in render space = 200 sim units. Round to nearest.
            var renderX = Mathf.Round(ground.X);
            var renderZ = Mathf.Round(ground.Z);
            simX = renderX * simPerWorld;
            simY = renderZ * simPerWorld;
        }

        // Reject clicks that fall outside the world bounds (free-cam can look past it).
        if (simX < 0f || simX > TyphonBridge.WorldSize || simY < 0f || simY > TyphonBridge.WorldSize) return;

        var cmd = tool switch
        {
            ToolKind.Food => ToolCommand.PlaceFood(simX, simY, 8000f),
            ToolKind.Rock => ToolCommand.PlaceRock(simX, simY),
            ToolKind.Cull => ToolCommand.Cull(simX, simY, 1f * simPerWorld),   // 1 m in sim units
            ToolKind.Ignite => ToolCommand.Ignite(simX, simY),
            _ => default,
        };
        _bridge.EnqueueToolCommand(cmd);
        GetViewport().SetInputAsHandled();
    }

    private float _lastTimeScale = 1f;

    public override void _Process(double delta)
    {
        _antRenderer?.UpdateFromBridge();
        _terrain?.UpdateFromFrame();
        // Push the freshly-binned density texture from AntRenderer into the Terrain shader.
        if (_antRenderer != null && _terrain != null)
        {
            _terrain.UpdateDensity(_antRenderer.DensityBytes);
        }

        // Phase 6B — push the fire CA state texture to the terrain shader. Terrain pulls
        // RenderFrame.FireR8 from its own RenderBridge (mirrors the pheromone path in
        // UpdateFromFrame above).
        _terrain?.UpdateFireFromFrame();

        // Phase 6C — apply per-frame plant state-change colours (dirty list from RenderFrame) and
        // push wind + time uniforms to the shared vegetation shader.
        _vegetation?.UpdateFromFrame();

        // Drain sim-side event log entries (tool actions, milestones, depletions) into the HUD.
        if (_bridge != null && _eventLog != null)
        {
            while (_bridge.TryDequeueLogEntry(out var entry))
            {
                _eventLog.AddEntry(entry);
            }
        }

        if (_camera != null && _bridge != null)
        {
            // Full-world AABB hint. The minimap shares the RenderFrame with the main camera, so a tight viewport hint
            // would degrade the minimap. ComputeWorldVisibleAabb stays available (useful later if we give the minimap
            // its own data pipeline, decoupled from TyphonBridge.FillRender).
            _bridge.UpdateCamera(0f, 0f, TyphonBridge.WorldSize, TyphonBridge.WorldSize);

            // LOD fade — push to ant material and Terrain
            var fadeInd = _camera.FadeIndividuals;
            var fadeDen = _camera.FadeDensity;
            _antRenderer!.SetFadeIndividuals(fadeInd);
            _terrain?.SetFadeDensity(fadeDen);
            // Per-band split: at Patch (fade=0) skip state-texture upload + multimesh draw entirely.
            _antRenderer.SetDrawIndividuals(fadeInd > 0f);

            // Sub-pixel guard — single metres-per-pixel value; in perspective it varies across the screen but using the
            // value at the target's depth is a good enough approximation for the size clamp.
            _antRenderer.SetMetresPerPixel(_camera.MetresPerPixelAtTarget(GetViewport()));

            // Sync the pheromone toggle to the minimap material (the terrain shader reads its own copy).
            _minimapMaterial?.SetShaderParameter("show_pheromone", _terrain?.ShowPheromone ?? false);
        }

        // Phase 6A — push the bridge's combined Daisyworld × day/night brightness scalar to the
        // two unshaded shaders (terrain + ants) and to Godot's sun + ambient (which drive the
        // remaining shaded materials: rocks / food / nests / spiders / cursor). Minimap material is
        // intentionally NOT modulated — the user navigates by it, must stay readable at midnight.
        if (_bridge != null)
        {
            var brightness = _bridge.EnvironmentBrightness;
            _terrain?.SetBrightness(brightness);
            _antRenderer?.SetBrightness(brightness);
            if (_sun != null)
            {
                _sun.LightEnergy = SunBaseEnergy * brightness;
            }
            // Ambient mixes a 0.4 floor with 0.6 × brightness so shaded materials never go fully
            // black at night — pairs with the bridge's own 0.15 day-curve floor for redundancy.
            // BackgroundColor lerps SkyNight→SkyDay so the clear color tracks the rest of the world
            // (no real skybox in 6A — that's Phase 8).
            if (_worldEnv?.Environment != null)
            {
                _worldEnv.Environment.AmbientLightEnergy = AmbientBaseEnergy * (0.4f + 0.6f * brightness);
                _worldEnv.Environment.BackgroundColor = SkyNight.Lerp(SkyDay, Mathf.Clamp(brightness, 0f, 1f));
            }
        }

        // HUD update
        if (Engine.GetFramesDrawn() % 10 == 0 && _bridge != null)
        {
            var tiers = _bridge.TierCounts;
            var states = _bridge.StateCounts;
            var foraging = states[0];
            var carrying = states[1];

            var speedLabel = _bridge.TimeScale == 0f ? "PAUSED" : $"{_bridge.TimeScale:G}x";
            _hudLeft.Text =
                $"[{speedLabel}]  Ants: {TyphonBridge.AntCount:N0}  ({TyphonBridge.NestCount} nests)\n" +
                $"Foraging: {foraging:N0}   Returning: {carrying:N0}\n" +
                $"Food: {_bridge.FoodSourcesRemaining}/{TyphonBridge.FoodCount} sources   Delivered: {_bridge.FoodDelivered:N0}\n" +
                $"Nest reserves: {_bridge.TotalNestFood:N0}   Deaths: {_bridge.DeathCount:N0}";

            var drawCalls = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            var timing = _bridge.GetTimingInfo() ?? "N/A";
            var lodLine = _camera != null
                ? $"Band: {_camera.CurrentBand}  fade=ind:{_camera.FadeIndividuals:F2} dens:{_camera.FadeDensity:F2}  zoom: {_camera.OrthoZoom:F1}m"
                : "Band: ?";
            var fps = (float)Engine.GetFramesPerSecond();
            var frameMs = fps > 0f ? 1000f / fps : 0f;
            var uploadKb = (_antRenderer?.LastUploadBytes ?? 0) / 1024;
            var drawingInd = _antRenderer?.LastDrewIndividuals ?? false;
            var fadeDenForHud = _camera?.FadeDensity ?? 0f;
            var perfLine = $"frame: {frameMs:F1}ms  ant: {(drawingInd ? "ON " : "off")} state_tex_up: {uploadKb}KB  density: {(fadeDenForHud > 0.001f ? "ON " : "off")}";
            var spiderPerf = $"spider: t0={_bridge.SpiderTier0Count}/{TyphonBridge.SpiderCount}  hits={_bridge.SpiderTotalHits}  query={_bridge.SpiderQueryMs:F2}ms  foreach={_bridge.SpiderForeachMs:F2}ms  commit={_bridge.SpiderCommitMs:F2}ms  kills={_bridge.SpiderKills}";
            _hudRight.Text =
                $"{fps:F0} fps  |  Draw: {drawCalls}  Visible: {_bridge.VisibleAnts:N0}\n" +
                $"T0: {tiers[0]:N0}  T1: {tiers[1]:N0}  T2: {tiers[2]:N0}  T3: {tiers[3]:N0}\n" +
                $"{lodLine}\n" +
                $"{perfLine}\n" +
                $"{spiderPerf}\n" +
                $"{timing}";

            DisplayServer.WindowSetTitle(
                $"AntHill {Engine.GetFramesPerSecond():F0}fps | {TyphonBridge.AntCount:N0} ants | " +
                $"F:{foraging:N0} C:{carrying:N0}");
        }
    }

    public override void _ExitTree()
    {
        GD.Print("AntHill: Shutting down...");

        try
        {
            SaveWindowState();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"AntHill: failed to save window state — {ex.Message}");
        }

        // Begin stopping the CPU sampler BEFORE the bridge teardown so its (seconds-long) .nettrace transcode +
        // symbol resolution runs on a background thread, overlapping the engine-teardown dirty-page flush instead
        // of freezing the exit path after it.
        try
        {
            ProfilerLauncher.BeginCpuSamplerStop();
        }
        catch
        {
            // ignored
        }

        try
        {
            _bridge?.Dispose();
        }
        catch
        {
            // ignored
        }

        _bridge = null;

        // Finish the CPU sampler (awaits the background parse) and hand the samples to the FileExporter, which
        // Stop() then drains into the trace's CpuSampleSection — BEFORE TyphonProfiler.Stop(). Idempotent + best-effort.
        try
        {
            ProfilerLauncher.StopCpuSampler();
        }
        catch
        {
            // ignored
        }

        if (_exporters != null && _exporters.Count > 0)
        {
            try
            {
                TyphonProfiler.Stop();
            }
            catch
            {
                // ignored
            }

            foreach (var exp in _exporters)
            {
                try
                {
                    TyphonProfiler.DetachExporter(exp);
                }
                catch
                {
                    // ignored
                }
            }
            _exporters = null;
        }

        var dropTotal = TyphonEvent.TotalDroppedEvents;
        var exporterDrops = TyphonProfiler.TotalDroppedExporterBatches;
        var spillAcquired = TyphonProfiler.SpilloverPoolAcquiredCount;
        var spillExhausted = TyphonProfiler.SpilloverPoolExhaustedCount;
        GD.Print(
            $"AntHill: profiler drop counters — total events dropped={dropTotal:N0}, " +
            $"exporter batches dropped={exporterDrops:N0}");
        GD.Print(
            $"AntHill: spillover pool — acquired={spillAcquired:N0} (chain extensions absorbed bursts), " +
            $"exhausted={spillExhausted:N0} (overflows that drop because pool was empty — bump SpilloverBufferCount if non-zero)");

        var byKind = TyphonEvent.DroppedEventsByKind;
        if (byKind.Count == 0)
        {
            GD.Print("AntHill: per-kind drop breakdown — none (all dropped records are instants, see total above).");
        }
        else
        {
            long sum = 0;
            foreach (var kv in byKind)
            {
                sum += kv.Value;
            }
            GD.Print($"AntHill: per-kind drop breakdown ({sum:N0} of {dropTotal:N0} attributed) —");
            var sorted = new List<KeyValuePair<TraceEventKind, long>>(byKind);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in sorted)
            {
                GD.Print($"    {kv.Key}: {kv.Value:N0}");
            }
        }
    }

    /// <summary>
    /// Restore window position, size, and maximize-state from the persisted ConfigFile. No-op on first run (file doesn't exist) — Godot
    /// keeps its default placement in that case. The position is clamped to the current screen's usable rect so a config saved on a monitor
    /// that no longer exists doesn't spawn the window off-screen.
    /// </summary>
    private void LoadWindowState()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(WindowStatePath) != Error.Ok) return;

        var savedMode = (int)(long)cfg.GetValue("window", "mode", (long)(int)DisplayServer.WindowGetMode());
        var savedPos = (Vector2I)cfg.GetValue("window", "position", DisplayServer.WindowGetPosition());
        var savedSize = (Vector2I)cfg.GetValue("window", "size", DisplayServer.WindowGetSize());

        var screen = DisplayServer.WindowGetCurrentScreen();
        var screenRect = DisplayServer.ScreenGetUsableRect(screen);
        savedPos.X = System.Math.Clamp(savedPos.X, screenRect.Position.X, screenRect.Position.X + screenRect.Size.X - 100);
        savedPos.Y = System.Math.Clamp(savedPos.Y, screenRect.Position.Y, screenRect.Position.Y + screenRect.Size.Y - 100);

        DisplayServer.WindowSetSize(savedSize);
        DisplayServer.WindowSetPosition(savedPos);
        if (savedMode != (int)DisplayServer.WindowMode.Windowed)
        {
            DisplayServer.WindowSetMode((DisplayServer.WindowMode)savedMode);
        }
    }

    /// <summary>
    /// Snapshot current window geometry to the persisted ConfigFile.
    /// </summary>
    private void SaveWindowState()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("window", "mode", (long)(int)DisplayServer.WindowGetMode());
        cfg.SetValue("window", "position", DisplayServer.WindowGetPosition());
        cfg.SetValue("window", "size", DisplayServer.WindowGetSize());
        cfg.Save(WindowStatePath);
    }
}
