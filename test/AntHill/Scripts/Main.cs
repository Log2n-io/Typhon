using System.Collections.Generic;
using Godot;
using Typhon.Engine.Profiler;
using Typhon.Engine.Profiler.Exporters;
using Typhon.Profiler;

namespace AntHill;

public partial class Main : Node2D
{
    private TyphonBridge _bridge;
    private AntRenderer _antRenderer;
    private Camera2D _camera;
    private PheromoneOverlay _pheromoneOverlay;

    // HUD
    private Label _hudLeft;
    private Label _hudRight;

    // Persisted window state — path is a user:// URI so Godot resolves it to the platform app-data dir (on Windows:
    // %APPDATA%/Godot/app_userdata/<project-name>/window_state.cfg), keeping the file out of the project folder and per-user.
    private const string WindowStatePath = "user://window_state.cfg";

    public override void _Ready()
    {
        // Restore last run's window position/size FIRST, before any scene-graph work. Godot's layout pass treats the window
        // dimensions as authoritative; changing them later would cascade into HUD anchor recalculations.
        LoadWindowState();

        GD.Print("AntHill: Initializing Typhon engine...");

        // Resolve profiler inputs from env vars + Godot cmdline user args (after the "++" separator).
        // CLI args override env vars (standard tooling convention). The whole conventions / parsing /
        // exporter-construction surface lives in the engine — see ProfilerLaunchConfig + ProfilerLauncher.
        _profilerConfig = ProfilerLaunchConfig
            .FromEnvironment()
            .MergedWith(ProfilerLaunchConfig.FromArgs(OS.GetCmdlineUserArgs()));

        // Step 1: flip the telemetry gate. Must happen BEFORE the bridge constructs the runtime so the JIT
        // gate (TelemetryConfig.ProfilerActive) is open when DagScheduler's hot methods compile.
        ProfilerLauncher.EnableTelemetryGateIfActive(_profilerConfig);

        _bridge = new TyphonBridge();
        _bridge.Initialize();

        // Step 2: exporters + TyphonProfiler.Start, now that DI exists so registry.Profiler is reachable.
        // Must happen BEFORE _bridge.Start() — attaching an exporter while TyphonProfiler is running throws,
        // and we want the first tick's events captured.
        if (_profilerConfig.IsActive)
        {
            try
            {
                // Dual-attach when both --trace and --live are supplied: records the session to disk AND streams live to the viewer. The engine's
                // consumer thread fans each batch to every attached exporter, so the CPU/bandwidth cost of having both is near-zero.
                _exporters = ProfilerLauncher.CreateExporters(_profilerConfig, _bridge.ProfilerParent);
                foreach (var exp in _exporters) TyphonProfiler.AttachExporter(exp);
                var metadata = ProfilerSetup.BuildSessionMetadata(
                    _bridge.Systems, workerCount: 16, baseTickRate: 60f,
                    phases: _bridge.PhaseNames,
                    currentEngineTickProvider: () => _bridge?.CurrentTick ?? 0,
                    engine: _bridge.DatabaseEngine,
                    resourceGraphRoot: _bridge.ResourceGraphRoot,
                    runtime: _bridge.ActiveRuntime);

                if (_profilerConfig.LiveWaitMs > 0 && _profilerConfig.LivePort >= 0)
                {
                    GD.Print($"AntHill: Waiting up to {_profilerConfig.LiveWaitMs} ms for the workbench to attach on :{_profilerConfig.LivePort}…");
                }

                // TyphonProfiler.Start runs each exporter's Initialize. For a TcpExporter with LiveWaitMs > 0, that
                // call blocks until the first viewer connects (or the timeout elapses), giving the operator time
                // to start the workbench and click Attach before the simulation runs.
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
                // Most likely causes: port in use (someone else listening on 9100), firewall block, disposal race on quick relaunch, or a
                // non-writable trace path. Surface loudly so the user doesn't conclude "profiler silently broken."
                GD.PrintErr($"AntHill: profiler startup FAILED — {ex.GetType().Name}: {ex.Message}");
                GD.PrintErr($"  Likely cause: port {_profilerConfig.LivePort} already in use, firewall blocking, or trace path not writable. Continuing without profiling.");
                _exporters = null;
            }
        }

        GD.Print($"AntHill: Spawned {TyphonBridge.AntCount:N0} ants. Starting runtime...");

        _antRenderer = GetNode<AntRenderer>("AntRenderer");
        _antRenderer.SetBridge(_bridge.RenderBridge);

        _camera = GetNode<Camera2D>("Camera");

        // Pheromone heatmap overlay (H to toggle)
        _pheromoneOverlay = new PheromoneOverlay();
        _pheromoneOverlay.SetBridge(_bridge.RenderBridge, _bridge);
        AddChild(_pheromoneOverlay);

        // HUD
        var hud = new CanvasLayer();
        hud.Layer = 10;

        _hudLeft = new Label();
        _hudLeft.Position = new Vector2(10, 10);
        _hudLeft.AddThemeColorOverride("font_color", Colors.White);
        _hudLeft.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _hudLeft.AddThemeConstantOverride("shadow_offset_x", 1);
        _hudLeft.AddThemeConstantOverride("shadow_offset_y", 1);
        _hudLeft.AddThemeFontSizeOverride("font_size", 16);
        hud.AddChild(_hudLeft);

        _hudRight = new Label();
        _hudRight.HorizontalAlignment = HorizontalAlignment.Right;
        _hudRight.AnchorLeft = 0.5f;
        _hudRight.AnchorRight = 1.0f;
        _hudRight.OffsetRight = -10;
        _hudRight.OffsetTop = 10;
        _hudRight.AddThemeColorOverride("font_color", Colors.White);
        _hudRight.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _hudRight.AddThemeConstantOverride("shadow_offset_x", 1);
        _hudRight.AddThemeConstantOverride("shadow_offset_y", 1);
        _hudRight.AddThemeFontSizeOverride("font_size", 14);
        hud.AddChild(_hudRight);

        AddChild(hud);

        _bridge.Start();
        GD.Print("AntHill: Runtime started. WASD=pan, wheel=zoom, `=pause, 1-4=speed, H=pheromone overlay.");

        // Telemetry diagnostics — prints current TelemetryConfig state, exporter types, and whether
        // TyphonProfiler's consumer is running. Call AFTER _bridge.Start() so the scheduler has
        // emitted at least the session's SystemDefinition events.
        ProfilerLauncher.PrintDiagnostics(GD.Print, _exporters);
    }

    private static TcpExporter FindTcpExporter(System.Collections.Generic.List<IProfilerExporter> exporters)
    {
        if (exporters == null) return null;
        foreach (var e in exporters)
        {
            if (e is TcpExporter tcp) return tcp;
        }
        return null;
    }

    private System.Collections.Generic.List<IProfilerExporter> _exporters;
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
                case Key.Key1: SetSpeed(1f); break;
                case Key.Key2: SetSpeed(2f); break;
                case Key.Key3: SetSpeed(4f); break;
                case Key.Key4: SetSpeed(10f); break;
                case Key.H: _pheromoneOverlay?.Toggle(); break;
            }
        }
    }

    private float _lastTimeScale = 1f;

    private void SetSpeed(float speed)
    {
        if (_bridge == null) return;
        _lastTimeScale = speed;
        _bridge.TimeScale = speed;
    }

    public override void _Process(double delta)
    {
        _antRenderer?.UpdateFromBridge();
        _pheromoneOverlay?.UpdateFromFrame();

        if (_camera != null && _bridge != null)
        {
            var vpSize = GetViewportRect().Size;
            float halfW = vpSize.X / (2f * _camera.Zoom.X);
            float halfH = vpSize.Y / (2f * _camera.Zoom.Y);
            _bridge.UpdateCamera(
                _camera.Position.X - halfW, _camera.Position.Y - halfH,
                _camera.Position.X + halfW, _camera.Position.Y + halfH);
        }

        // HUD update
        if (Engine.GetFramesDrawn() % 10 == 0 && _bridge != null)
        {
            var tiers = _bridge.TierCounts;
            var states = _bridge.StateCounts;
            int foraging = states[0];
            int carrying = states[1];

            string speedLabel = _bridge.TimeScale == 0f ? "PAUSED" : $"{_bridge.TimeScale:G}x";
            _hudLeft.Text =
                $"[{speedLabel}]  Ants: {TyphonBridge.AntCount:N0}  ({TyphonBridge.NestCount} nests)\n" +
                $"Foraging: {foraging:N0}   Returning: {carrying:N0}\n" +
                $"Food: {_bridge.FoodSourcesRemaining}/{TyphonBridge.FoodCount} sources   Delivered: {_bridge.FoodDelivered:N0}\n" +
                $"Nest reserves: {_bridge.TotalNestFood:N0}   Deaths: {_bridge.DeathCount:N0}";

            int drawCalls = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            var timing = _bridge.GetTimingInfo() ?? "N/A";
            _hudRight.Text =
                $"{Engine.GetFramesPerSecond():F0} fps  |  Draw: {drawCalls}  Visible: {_bridge.VisibleAnts:N0}\n" +
                $"T0: {tiers[0]:N0}  T1: {tiers[1]:N0}  T2: {tiers[2]:N0}  T3: {tiers[3]:N0}\n" +
                $"{timing}";

            DisplayServer.WindowSetTitle(
                $"AntHill {Engine.GetFramesPerSecond():F0}fps | {TyphonBridge.AntCount:N0} ants | " +
                $"F:{foraging:N0} C:{carrying:N0}");
        }
    }

    public override void _ExitTree()
    {
        GD.Print("AntHill: Shutting down...");

        // Persist window state BEFORE disposing the bridge — the window is still realized here and DisplayServer queries are valid.
        // Wrapped in try so a disk/permissions failure here doesn't cascade into the bridge-cleanup path below.
        try
        {
            SaveWindowState();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"AntHill: failed to save window state — {ex.Message}");
        }

        // Tear down in reverse order: bridge first (so any final tick events are emitted), then TyphonProfiler.Stop (which flushes + disposes
        // every attached exporter — don't dispose them ourselves), then DetachExporter for each so the static list is empty if the process is
        // reused. Detach is idempotent per exporter but must happen after Stop since Stop rejects mutations while running.
        try
        {
            _bridge?.Dispose();
        }
        catch
        {
            // ignored
        }

        _bridge = null;
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

        // Producer-ring drop counters — read AFTER TyphonProfiler.Stop() so the values reflect the full run.
        // If TickStartDroppedRingFull > 0 we know the per-thread ring overflowed during emit; that matches the
        // workbench-side "malformed tick" warning (TickStart record never made it into the trace stream, decoder
        // misattributes the next tick's events). TickStartDroppedNoSlot fires when ThreadSlotRegistry refused to
        // assign a slot (registry full — 256 worker threads max). Both are monotonic since the first call to
        // EmitTickStart.
        var dropRingFull = TyphonEvent.TickStartDroppedRingFull;
        var dropNoSlot = TyphonEvent.TickStartDroppedNoSlot;
        var dropTotal = TyphonEvent.TotalDroppedEvents;
        var exporterDrops = TyphonProfiler.TotalDroppedExporterBatches;
        var spillAcquired = TyphonProfiler.SpilloverPoolAcquiredCount;
        var spillExhausted = TyphonProfiler.SpilloverPoolExhaustedCount;
        GD.Print(
            $"AntHill: profiler drop counters — TickStart ring-full={dropRingFull:N0}, " +
            $"TickStart no-slot={dropNoSlot:N0}, total events dropped={dropTotal:N0}, " +
            $"exporter batches dropped={exporterDrops:N0}");
        GD.Print(
            $"AntHill: spillover pool — acquired={spillAcquired:N0} (chain extensions absorbed bursts), " +
            $"exhausted={spillExhausted:N0} (overflows that drop because pool was empty — bump SpilloverBufferCount if non-zero)");

        // Per-kind breakdown of ring-overflow drops. Currently only span events (those that go through the
        // central PublishEvent path) are tracked per-kind — instants/markers (TickStart, GC, Memory, etc.) are
        // counted in the aggregate above but not broken out. The gap "totalDropped - sum(byKind)" is "instant
        // events dropped". A hot kind here points directly at the system whose emit rate exceeds ring drain.
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
            // Sort descending by drop count so the worst offender is first.
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
    /// that no longer exists doesn't spawn the window off-screen (a surprisingly common scenario when moving between laptop/desktop setups).
    /// </summary>
    private void LoadWindowState()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(WindowStatePath) != Error.Ok) return;

        var savedMode = (int)(long)cfg.GetValue("window", "mode", (long)(int)DisplayServer.WindowGetMode());
        var savedPos = (Vector2I)cfg.GetValue("window", "position", DisplayServer.WindowGetPosition());
        var savedSize = (Vector2I)cfg.GetValue("window", "size", DisplayServer.WindowGetSize());

        // Clamp position to the current screen's usable region. Leave 100 px headroom on the right/bottom edges so the titlebar stays
        // grabbable even if the user drags the window partially off-screen before closing.
        var screen = DisplayServer.WindowGetCurrentScreen();
        var screenRect = DisplayServer.ScreenGetUsableRect(screen);
        savedPos.X = System.Math.Clamp(savedPos.X, screenRect.Position.X, screenRect.Position.X + screenRect.Size.X - 100);
        savedPos.Y = System.Math.Clamp(savedPos.Y, screenRect.Position.Y, screenRect.Position.Y + screenRect.Size.Y - 100);

        // Restore size/position FIRST, then mode — Godot's maximized/fullscreen modes don't re-read position/size until the mode
        // transitions out, so if we applied mode first the saved geometry would just be lost on next minimize-unmaximize.
        DisplayServer.WindowSetSize(savedSize);
        DisplayServer.WindowSetPosition(savedPos);
        if (savedMode != (int)DisplayServer.WindowMode.Windowed)
        {
            DisplayServer.WindowSetMode((DisplayServer.WindowMode)savedMode);
        }
    }

    /// <summary>
    /// Snapshot current window geometry to the persisted ConfigFile. When the window is maximized/fullscreen, Godot's WindowGet(Position|Size)
    /// return the WINDOWED dimensions (i.e. what the window would restore to), which is exactly what we want — the next run will restore that
    /// geometry and re-apply the maximize/fullscreen mode on top.
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
