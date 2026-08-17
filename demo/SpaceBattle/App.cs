using System;
using System.Collections.Generic;
using System.Diagnostics;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace SpaceBattle;

/// <summary>The interactive shell: window, input, main loop, and the two render targets.</summary>
internal sealed class App : IDisposable
{
    private readonly Config _cfg;
    private readonly TyphonHost _host;
    private readonly Simulation _sim;

    private RenderWindow _win;
    private RenderWindow _mapWin;
    private Camera _cam;
    private Renderer _renderer;
    private readonly Minimap _minimap = new();
    private Hud _hud;
    private FileMapView _fileMap;
    private readonly SpatialProbe _probe = new();
    private readonly WindowLayout _layout = new();

    private Selection _sel;
    private float _speed;
    private bool _paused;
    private bool _stepOnce;
    private double _tickAccumulator;
    private SimStats _lastStats;
    private double _simMsEma;
    private double _frameMsEma;
    private double _fps;
    private double _ticksPerFrameEma;

    /// <summary>
    /// Simulated time advanced per unit of real time, relative to the requested speed. 1.0 means the world is
    /// keeping up; 0.37 means it is in slow motion and every duration you observe on screen is being stretched by
    /// nearly 3x. This is the number the old count-based catch-up cap hid: an overloaded tick showed up only as a
    /// low frame rate, with nothing anywhere saying the world clock itself had fallen behind.
    /// </summary>
    private double SimSpeedRatio
    {
        get
        {
            if (_frameMsEma <= 0 || _speed <= 0 || _paused)
            {
                return 0;
            }
            var actualTicksPerSecond = _ticksPerFrameEma / (_frameMsEma / 1000.0);
            return actualTicksPerSecond / (_cfg.TickRate * _speed);
        }
    }

    private double _lastLayoutCapture;
    private Vector2f _probeCentre;
    private bool _probeActive;

    public App(Config cfg, TyphonHost host, Simulation sim)
    {
        _cfg = cfg;
        _host = host;
        _sim = sim;
        _speed = cfg.StartSpeed;
        _paused = cfg.StartPaused;
    }

    public void Run()
    {
        CreateWindows();

        var frameTimer = Stopwatch.StartNew();
        var last = frameTimer.Elapsed.TotalSeconds;

        while (_win.IsOpen)
        {
            var now = frameTimer.Elapsed.TotalSeconds;
            var frameDt = Math.Min(0.25, now - last);
            last = now;
            _frameMsEma = _frameMsEma * 0.9 + frameDt * 1000 * 0.1;
            _fps = _frameMsEma > 0 ? 1000.0 / _frameMsEma : 0;

            _eventSeq++;
            _win.DispatchEvents();
            _mapWin?.DispatchEvents();

            StepSimulation(frameDt);
            if (now - _lastLayoutCapture > 1.0)
            {
                _lastLayoutCapture = now;
                CaptureLayout();
            }
            RenderMain();
            if (_mapWin is { IsOpen: true })
            {
                _fileMap.Draw(_mapWin);
                _mapWin.Display();
            }
        }
    }

    private void CreateWindows()
    {
        _win = new RenderWindow(new VideoMode(new Vector2u((uint)_cfg.WindowW, (uint)_cfg.WindowH)),
                                "Typhon SpaceBattle — spatial partitioning observatory");
        _win.SetVerticalSyncEnabled(_cfg.VSync);
        if (_cfg.RememberWindowLayout)
        {
            _layout.Apply(_win, "main");
        }
        _win.Closed += (_, _) => _win.Close();
        // Only invalidate the cached HUD view; the world view is rebuilt from the camera every frame regardless.
        _win.Resized += (_, _) => _hudView = null;
        _win.MouseButtonPressed += OnMouseDown;
        _win.MouseButtonReleased += (_, e) =>
        {
            if (e.Button == Mouse.Button.Left)
            {
                _draggingMinimap = false;
            }
            _cam.OnMouseUp(e.Button);
        };
        _win.MouseMoved += OnMouseMove;
        _win.MouseWheelScrolled += (_, e) => _cam.OnWheel(e.Delta, e.Position);
        _win.KeyPressed += OnKey;
        _win.TextEntered += OnText;

        _cam = new Camera(_win, _cfg.WorldSize);
        _cam.FrameWorld(_cfg.WorldSize);
        _renderer = new Renderer(_cfg, _host, _sim);
        _hud = new Hud();
        _fileMap = new FileMapView(_cfg, _host);

        if (_cfg.FileMapWindow)
        {
            OpenFileMapWindow();
        }
    }

    private void OpenFileMapWindow()
    {
        _mapWin = new RenderWindow(new VideoMode(new Vector2u((uint)_cfg.FileMapW, (uint)_cfg.FileMapH)),
                                   "Database file map — page activity");
        _mapWin.SetVerticalSyncEnabled(false);
        if (_cfg.RememberWindowLayout)
        {
            _layout.Apply(_mapWin, "filemap");
        }
        // Closing via the title bar must also drop our reference, otherwise the toggle would find a dead window and
        // refuse to reopen it.
        _mapWin.Closed += (_, _) =>
        {
            CaptureLayout();
            _mapWin?.Close();
            _mapWin = null;
        };
        _fileMap.Refresh();
    }

    private void ToggleFileMapWindow()
    {
        if (_mapWin is { IsOpen: true })
        {
            CaptureLayout();
            _mapWin.Close();
            _mapWin = null;
            _cfg.FileMapWindow = false;
        }
        else
        {
            OpenFileMapWindow();
            _cfg.FileMapWindow = true;
        }
    }

    // ─── Simulation clock ─────────────────────────────────────────────────────────────────────────────────────────

    // ─── Score trend ──────────────────────────────────────────────────────────────────────────────────────────────
    // Sampled on a wall clock rather than per frame: a delta against the previous FRAME is noise at 60 Hz — every value
    // moves by a handful and the arrow flickers. A few seconds is long enough that the sign means something.

    private readonly long[] _scoreAtLastSample = new long[4];
    private readonly long[] _scoreDelta = new long[4];
    private long _nextScoreSampleAt;

    private void SampleScoreTrend()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextScoreSampleAt)
        {
            return;
        }

        var first = _nextScoreSampleAt == 0;
        _nextScoreSampleAt = now + (long)(_cfg.ScoreTrendIntervalSeconds * Stopwatch.Frequency);

        for (var f = 0; f < _cfg.Factions && f < 4; f++)
        {
            var score = _sim.Score(f);
            // The first sample has nothing to compare against, and reporting the whole score as a delta would paint a
            // huge spurious arrow on the opening frames.
            _scoreDelta[f] = first ? 0 : score - _scoreAtLastSample[f];
            _scoreAtLastSample[f] = score;
        }
    }

    /// <summary>Arrow + signed delta over the last sampling window, or a flat mark when it has not moved.</summary>
    private string TrendMark(int faction)
    {
        var d = _scoreDelta[faction & 3];
        return d switch
        {
            > 0 => $"▲+{d:N0}",
            < 0 => $"▼{d:N0}",
            _ => "= 0",
        };
    }

    private void StepSimulation(double frameDt)
    {
        var dt = 1f / _cfg.TickRate;
        var ticksThisFrame = 0;

        if (_stepOnce)
        {
            RunOneTick(dt);
            ticksThisFrame = 1;
            _stepOnce = false;
        }
        else if (!_paused && _speed > 0)
        {
            _tickAccumulator += frameDt * _speed * _cfg.TickRate;

            // Catch-up is bounded by MEASURED wall clock, not by a tick count. The count this replaced was
            // ceil(speed * 2), decided before the first tick ran and never revised, so it kept authorising two
            // 45 ms ticks into a frame that could not afford one. Because the frame was already fully sim-bound,
            // the second tick added no ticks-per-second whatsoever — it only doubled input latency.
            var deadline = Stopwatch.GetTimestamp() + (long)(_cfg.SimBudgetMs / 1000.0 * Stopwatch.Frequency);
            while (_tickAccumulator >= 1.0)
            {
                RunOneTick(dt);
                _tickAccumulator -= 1.0;
                ticksThisFrame++;

                // Tested AFTER the tick, deliberately. Testing before would let a frame that had already blown its
                // budget elsewhere run ZERO ticks, and if that persisted the world would freeze while the window
                // kept redrawing and taking input — a stall that looks exactly like a running app. Checking after
                // guarantees forward progress: always at least one step, and stop the moment the budget is spent.
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    break;
                }
            }

            // Forgive debt beyond one maximum frame's worth. Unlike the old count cap, retaining a payable backlog
            // is safe here: per-frame work is bounded by the deadline above, so a backlog cannot become a burst.
            var maxBacklog = _cfg.MaxBacklogSeconds * _speed * _cfg.TickRate;
            if (_tickAccumulator > maxBacklog)
            {
                _tickAccumulator = maxBacklog;
            }
        }

        _ticksPerFrameEma = _ticksPerFrameEma * 0.9 + ticksThisFrame * 0.1;
        SampleScoreTrend();
        WriteCensus();

        if (ticksThisFrame > 0 && _mapWin is { IsOpen: true } &&
            _host.Tick % Math.Max(1, _cfg.FileMapEveryNTicks) == 0)
        {
            _fileMap.Refresh();
        }
    }

    private System.IO.StreamWriter _census;
    private long _nextCensusTick;

    /// <summary>
    /// One CSV line every <see cref="Config.CensusEveryTicks"/> ticks, flushed immediately.
    /// </summary>
    /// <remarks>
    /// Flushed per line on purpose: the failure under study (#824) terminates the process, so anything buffered is
    /// anything lost. A run that dies at minute thirty must still yield the whole curve up to minute thirty —
    /// otherwise an hour of machine time produces one bit ("it crashed"), which is what the first long run produced.
    /// </remarks>
    private void WriteCensus()
    {
        if (_cfg.CensusEveryTicks <= 0 || _host.Tick < _nextCensusTick)
        {
            return;
        }
        _nextCensusTick = _host.Tick + _cfg.CensusEveryTicks;

        if (_census == null)
        {
            var path = System.IO.Path.IsPathRooted(_cfg.CensusFile)
                ? _cfg.CensusFile
                : System.IO.Path.Combine(AppContext.BaseDirectory, _cfg.CensusFile);
            _census = new System.IO.StreamWriter(path, append: false) { AutoFlush = true };
            _census.WriteLine("tick,ships,dirtyPages,acwPages,slotRefPages,epochHeldPages,unevictablePages,totalPages," +
                              "scoreB,scoreO,stationsB,stationsO,shipsB,shipsO,peakBpDebt,peakBpEpochHeld," +
                              "checkpoints,gatedCycles,segmentsRecycled,walBytes,walFiles,simMs,frameMs,worstFenceMs");
            Console.WriteLine($"census -> {path}");
        }

        var cp = _host.CheckpointStats();
        var pin = _host.CountPinnedPages();
        var peak = _host.PeakBackpressure();
        var (walBytes, walFiles) = _host.WalFootprint();
        _census.WriteLine($"{_host.Tick},{_sim.ShipsAlive[0] + _sim.ShipsAlive[1]}," +
                          $"{pin.Dirty},{pin.Acw},{pin.SlotRef},{pin.EpochHeld},{pin.Unevictable},{pin.Total}," +
                          $"{_sim.Score(0)},{_sim.Score(1)},{_sim.StationsAlive[0]},{_sim.StationsAlive[1]},{_sim.ShipsAlive[0]},{_sim.ShipsAlive[1]}," +
                          $"{peak.Debt},{peak.EpochHeld}," +
                          $"{cp.Checkpoints},{cp.GatedCycles},{cp.SegmentsRecycled}," +
                          $"{walBytes},{walFiles},{_simMsEma:F2},{_frameMsEma:F2},{_host.MaxFenceMs:F1}");
    }

    private void RunOneTick(float dt)
    {
        var t0 = Stopwatch.GetTimestamp();
        _lastStats = _sim.Step(dt);
        _simMsEma = _simMsEma * 0.9 + Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 0.1;
    }

    // ─── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.Position;

        // The minimap claims the click before anything else. It sits on top of the world, so a click that lands on
        // it must not also be interpreted as a click on whatever is underneath.
        if (e.Button == Mouse.Button.Left && _cfg.ShowMinimap && _minimap.TryWorldAt(p, _cfg, out var jump))
        {
            _cam.JumpTo(jump);
            _draggingMinimap = true;
            return;
        }

        _cam.OnMouseDown(e.Button, p);
        if (e.Button == Mouse.Button.Left)
        {
            SelectAt(_cam.ScreenToWorld(p));
        }
        else if (e.Button == Mouse.Button.Right)
        {
            _probeCentre = _cam.ScreenToWorld(p);
            _probeActive = true;
        }
    }

    private void OnMouseMove(object sender, MouseMoveEventArgs e)
    {
        var p = e.Position;
        if (_draggingMinimap)
        {
            if (!Mouse.IsButtonPressed(Mouse.Button.Left))
            {
                _draggingMinimap = false;
            }
            else if (_minimap.TryWorldAt(p, _cfg, out var jump))
            {
                _cam.JumpTo(jump);
            }
            return;
        }
        _cam.OnMouseMove(p);
        if (_probeActive && Mouse.IsButtonPressed(Mouse.Button.Right))
        {
            _probeCentre = _cam.ScreenToWorld(p);
        }
    }

    private bool _draggingMinimap;

    /// <summary>
    /// Speed control, driven by the CHARACTER typed rather than by a key code.
    /// </summary>
    /// <remarks>
    /// <c>Keyboard.Key</c> in SFML 3 is the <em>logical</em> key, and on a non-US layout there simply is no plain
    /// key that reports <c>LBracket</c> — on AZERTY '[' is AltGr+5 — so the key-code route silently never fires.
    /// <c>Scancode</c> would give the physical QWERTY position, which is the wrong key on that layout. Matching the
    /// typed character is the only route that means "the [ key" on every keyboard, including dead-key and AltGr
    /// combinations. Handled here exclusively, so a US keyboard cannot double-step by matching both routes.
    /// </remarks>
    private void OnText(object sender, TextEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Unicode))
        {
            return;
        }
        HandleChar(e.Unicode[0]);
    }

    /// <summary>The character route's logic, callable without an SFML event (SFML 3 event args cannot be built by hand).</summary>
    private void HandleChar(char ch)
    {
        switch (ch)
        {
            case '[': Slower(); break;
            case ']': Faster(); break;
        }
    }

    /// <summary>
    /// Guard so that a single physical press cannot step the speed twice. SFML delivers KeyPressed and TextEntered
    /// for the same keystroke; both routes are wired because neither is reliable on every layout, so exactly one of
    /// them must win per press.
    /// </summary>
    private int _lastSpeedStepSeq = -1;
    private int _eventSeq;

    private bool ClaimSpeedStep()
    {
        if (_lastSpeedStepSeq == _eventSeq)
        {
            return false;
        }
        _lastSpeedStepSeq = _eventSeq;
        return true;
    }

    private void Faster()
    {
        if (ClaimSpeedStep())
        {
            _speed = MathF.Min(32f, _speed <= 0.01f ? 0.05f : _speed * 1.5f);
        }
    }

    private void Slower()
    {
        if (ClaimSpeedStep())
        {
            _speed = MathF.Max(0.02f, _speed / 1.5f);
        }
    }

    private void OnKey(object sender, KeyEventArgs e) => HandleKey(e.Code);

    private void HandleKey(Keyboard.Key code)
    {
        switch (code)
        {
            case Keyboard.Key.Escape: _win.Close(); break;
            case Keyboard.Key.Space: _paused = !_paused; break;
            case Keyboard.Key.Period: _stepOnce = true; _paused = true; break;
            // Speed is handled in OnText, not here — see the note on the TextEntered wiring below.
            // PageUp/PageDown are offered as layout-proof alternates; they produce no character, so they cannot
            // collide with the TextEntered path.
            case Keyboard.Key.PageUp:
            case Keyboard.Key.RBracket: Faster(); break;
            case Keyboard.Key.PageDown:
            case Keyboard.Key.LBracket: Slower(); break;
            case Keyboard.Key.Num0: _speed = 1f; break;
            case Keyboard.Key.F: _cam.FrameWorld(_cfg.WorldSize); break;
            case Keyboard.Key.Num1: _cfg.ShowCells = !_cfg.ShowCells; break;
            case Keyboard.Key.Num2: _cfg.ShowCellHeat = !_cfg.ShowCellHeat; break;
            case Keyboard.Key.Num3: _cfg.ShowClusterAabb = !_cfg.ShowClusterAabb; break;
            case Keyboard.Key.Num4: _cfg.ShowShips = !_cfg.ShowShips; break;
            case Keyboard.Key.Num5: _cfg.ShowShots = !_cfg.ShowShots; break;
            case Keyboard.Key.Num6: _cfg.ShowTargetLines = !_cfg.ShowTargetLines; break;
            case Keyboard.Key.Num7: _cfg.ShowSelectivity = !_cfg.ShowSelectivity; break;
            case Keyboard.Key.Num8: _cfg.ShowAsteroids = !_cfg.ShowAsteroids; break;
            case Keyboard.Key.Num9: _cfg.ClusterColorMode = !_cfg.ClusterColorMode; break;
            case Keyboard.Key.N: _cfg.ShowMinimap = !_cfg.ShowMinimap; break;
            case Keyboard.Key.V: _cfg.ShowMotionVectors = !_cfg.ShowMotionVectors; break;
            case Keyboard.Key.C: _cfg.CullingEnabled = !_cfg.CullingEnabled; break;
            // -1 (auto) -> 0 -> 1 -> 2 -> back to auto. Forcing a tier is how you compare representations of the
            // same scene without having to hold the zoom steady between two runs.
            case Keyboard.Key.L: _cfg.ForceLod = _cfg.ForceLod >= 2 ? -1 : _cfg.ForceLod + 1; break;
            case Keyboard.Key.D:
                _cfg.DensitySource = DensityField.ParseSource(_cfg.DensitySource) == DensitySource.Entities ? "cells" : "entities";
                break;
            case Keyboard.Key.M: ToggleFileMapWindow(); break;
            case Keyboard.Key.H: _cfg.ShowHud = !_cfg.ShowHud; break;
            case Keyboard.Key.P: _probeActive = !_probeActive; break;
            case Keyboard.Key.F12: SaveScreenshot($"spacebattle-{DateTime.Now:HHmmss}.png", report: true); break;
        }
    }

    private void SelectAt(Vector2f world)
    {
        _sel = default;

        // Stations first, and they win outright when the click is on one. A station is 150 m to a ship's 5 m, so a
        // nearest-wins contest across both would hand the pick to whichever fighter happened to be drifting over
        // the base — you could never select the thing occupying most of the pixels under the cursor.
        if (TrySelectStation(world))
        {
            return;
        }

        var best = float.MaxValue;
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();
        var pickRadius = MathF.Max(_cfg.ShipRadius * 4f, _cam.ViewHeight * 0.02f);
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Ship.Position);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var dx = pos[i].Bounds.MinX - world.X;
                var dy = pos[i].Bounds.MinY - world.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 < best && d2 < pickRadius * pickRadius)
                {
                    best = d2;
                    _sel = new Selection
                    {
                        HasSelection = true,
                        ArchetypeId = _host.ShipArchetypeId,
                        ChunkId = cluster.ChunkId,
                        Slot = i,
                        EntityKey = cluster.GetEntityId(i).EntityKey,
                    };
                }
            }
        }
    }

    /// <summary>Picks a station under the cursor. Returns false if the click was not on one, so ships get their turn.</summary>
    private bool TrySelectStation(Vector2f world)
    {
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Station>();
        using var e = acc.GetClusterEnumerator();

        // Generous: the station's own footprint, or 3 % of the view when zoomed out far enough that the footprint
        // is sub-pixel. Matches the marker the renderer actually draws at that zoom, so what you click is what you see.
        var pick = MathF.Max(_cfg.StationRadius * 1.6f, _cam.ViewHeight * 0.03f);
        var best = pick * pick;
        var found = false;
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Station.Position);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var dx = pos[i].Bounds.MinX - world.X;
                var dy = pos[i].Bounds.MinY - world.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 < best)
                {
                    best = d2;
                    found = true;
                    _sel = new Selection
                    {
                        HasSelection = true,
                        ArchetypeId = _host.StationArchetypeId,
                        ChunkId = cluster.ChunkId,
                        Slot = i,
                        EntityKey = cluster.GetEntityId(i).EntityKey,
                    };
                }
            }
        }
        return found;
    }

    /// <summary>Reads the live component state of the selected station straight out of Typhon.</summary>
    private bool TryReadSelectedStation(out StationInfo info, out float x, out float y)
    {
        info = default;
        x = 0;
        y = 0;
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Station>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            if (cluster.ChunkId != _sel.ChunkId)
            {
                continue;
            }
            var bits = cluster.OccupancyBits;
            if ((bits & (1UL << _sel.Slot)) == 0)
            {
                return false;
            }
            var pos = cluster.GetReadOnlySpan(Station.Position);
            var inf = cluster.GetReadOnlySpan(Station.Info);
            info = inf[_sel.Slot];
            x = pos[_sel.Slot].Bounds.MinX;
            y = pos[_sel.Slot].Bounds.MinY;
            return true;
        }
        return false;
    }

    /// <summary>Reads the selected ship's combat + miner state and world position.</summary>
    private bool TryReadSelectedShip(out Combat com, out Miner min, out float x, out float y)
    {
        com = default;
        min = default;
        x = 0;
        y = 0;
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            if (cluster.ChunkId != _sel.ChunkId)
            {
                continue;
            }
            var bits = cluster.OccupancyBits;
            if ((bits & (1UL << _sel.Slot)) == 0)
            {
                return false;
            }
            var pos = cluster.GetReadOnlySpan(Ship.Position);
            var cb = cluster.GetReadOnlySpan(Ship.Combat);
            var mn = cluster.GetReadOnlySpan(Ship.Miner);
            com = cb[_sel.Slot];
            min = mn[_sel.Slot];
            x = pos[_sel.Slot].Bounds.MinX;
            y = pos[_sel.Slot].Bounds.MinY;
            return true;
        }
        return false;
    }

    /// <summary>Everything the simulation knows about one ship, in gameplay terms.</summary>
    private IEnumerable<string> DescribeSelectedShip(Combat c, Miner m, float sx, float sy)
    {
        var kindName = c.Kind switch
        {
            Simulation.KindHeavy => "HEAVY",
            Simulation.KindMiner => "MINER",
            Simulation.KindFast => "INTERCEPTOR",
            Simulation.KindDestroyer => "DESTROYER",
            _ => "LIGHT fighter",
        };
        var hpMax = c.Kind switch
        {
            Simulation.KindHeavy => _cfg.ShipHp * 3,
            Simulation.KindMiner => _cfg.ShipHp * 2,
            Simulation.KindFast => _cfg.FastHp,
            Simulation.KindDestroyer => _cfg.DestroyerHp,
            _ => _cfg.ShipHp,
        };
        var shMax = c.Kind switch
        {
            Simulation.KindHeavy => _cfg.ShipShieldMax * 3,
            Simulation.KindMiner => _cfg.ShipShieldMax * 2,
            Simulation.KindFast => _cfg.FastShield,
            Simulation.KindDestroyer => _cfg.DestroyerShield,
            _ => _cfg.ShipShieldMax,
        };
        var speed = c.Kind switch
        {
            Simulation.KindHeavy => _cfg.ShipMaxSpeed * 0.6f,
            Simulation.KindMiner => _cfg.ShipMaxSpeed * 0.7f,
            Simulation.KindFast => _cfg.FastMaxSpeed,
            Simulation.KindDestroyer => _cfg.DestroyerMaxSpeed,
            _ => _cfg.ShipMaxSpeed,
        };
        var cost = c.Kind switch
        {
            Simulation.KindHeavy => _cfg.HeavyCost,
            Simulation.KindMiner => _cfg.MinerCost,
            Simulation.KindFast => _cfg.FastCost,
            Simulation.KindDestroyer => _cfg.DestroyerCost,
            _ => _cfg.LightCost,
        };

        yield return $"  {kindName}   faction {Simulation.FactionTag(c.Faction)}   position ({sx:F0}, {sy:F0}) m   cost {cost} material";

        // The line this panel exists for. Everything else is a stat you could infer from the hull; the TASK is the
        // one thing that is pure state and invisible on screen — two ships flying the same direction may be racing
        // an objective and fleeing to defend a base, and nothing in the picture distinguishes them.
        yield return $"  TASK   {Simulation.TaskName(Simulation.TaskOf(c.SteerFlags))}";

        yield return $"  hull   {c.Hp}/{hpMax}   shield {c.Shield}/{shMax}   effective HP {c.Hp + c.Shield}/{hpMax + shMax}";
        yield return $"  damage {c.Damage}/shot   range {_sim.WeaponRangeOf(c.Kind):F0} m   cooldown {c.Cooldown}/{_cfg.WeaponCooldownTicks} ticks"
                   + (c.Cooldown == 0 ? "  (ready to fire)" : "");
        yield return $"  top speed {speed:F0} m/s   rooted {c.RootTicks} ticks   orbit {((c.SteerFlags & 1) == 0 ? "CW" : "CCW")}";

        if (c.HasTarget != 0)
        {
            var d = MathF.Sqrt((c.TargetX - sx) * (c.TargetX - sx) + (c.TargetY - sy) * (c.TargetY - sy));
            var inRange = d <= _sim.WeaponRangeOf(c.Kind);
            yield return $"  target ({c.TargetX:F0}, {c.TargetY:F0}) m   distance {d:F0} m   {(inRange ? "IN RANGE" : "closing")}";
        }
        else
        {
            yield return "  no target — steering, not shooting";
        }

        yield return c.ThreatTicks > 0
            ? $"  UNDER ATTACK — {c.ThreatTicks} ticks of threat left (defends instead of hunting)"
            : $"  not under attack   calm {c.CalmTicks} ticks (shield regen after {_cfg.ShipShieldRegenTicks})";

        if (c.Kind == Simulation.KindMiner)
        {
            var pct = m.CargoMax > 0 ? 100f * m.Cargo / m.CargoMax : 0f;
            yield return $"  cargo  {m.Cargo}/{m.CargoMax} ({pct:F0}%)   mine rate {_cfg.MineRate}/tick   home station ({m.HomeX:F0}, {m.HomeY:F0}) m";
            yield return m.HasOre != 0
                ? $"  working asteroid key {m.OreKey} at ({m.OreX:F0}, {m.OreY:F0}) m"
                : $"  no asteroid held   next search in {m.SearchCooldown} ticks";
        }

        var eff = Effect(c.Faction & 3);
        yield return $"  faction effects: {eff}";
        yield return "";
    }

    // ─── Rendering ────────────────────────────────────────────────────────────────────────────────────────────────

    private void RenderMain() => RenderMain(true);

    private void RenderMain(bool present)
    {
        _win.Clear(new Color(8, 9, 14));

        _win.SetView(_cam.BuildView());
        _renderer.Draw(_win, _cam, _sel, _win.Size.X, _win.Size.Y);
        if (_probeActive)
        {
            DrawProbe();
        }

        _win.SetView(HudView());
        if (_cfg.ShowMinimap)
        {
            // Drawn in the overlay view, and drawn even with the HUD hidden: it is navigation, not telemetry.
            _minimap.Draw(_win, _cfg, _renderer.Density, _cam, _renderer.Landmarks, _win.Size.X, _win.Size.Y);
        }
        if (_cfg.ShowHud)
        {
            DrawHud();
        }
        if (present)
        {
            _win.Display();
        }
    }

    /// <summary>
    /// A 1-unit-per-pixel view matching the window's CURRENT size, for screen-space overlays.
    /// </summary>
    /// <remarks>
    /// Not <c>RenderWindow.DefaultView</c>: SFML fixes that at the size the window was CREATED with, so once the
    /// window is resized — including by the saved-layout restore, which happens immediately after creation — a
    /// fixed-pixel HUD gets stretched by the ratio between the two. Rebuilding from <c>_win.Size</c> keeps the
    /// overlay at true pixel scale whatever the window does.
    /// </remarks>
    private View HudView()
    {
        var w = _win.Size.X;
        var h = _win.Size.Y;
        if (_hudView == null || MathF.Abs(_hudView.Size.X - w) > 0.5f || MathF.Abs(_hudView.Size.Y - h) > 0.5f)
        {
            _hudView = new View(new FloatRect(new Vector2f(0, 0), new Vector2f(w, h)));
        }
        return _hudView;
    }

    private View _hudView;

    private void DrawProbe()
    {
        var r = _cfg.ProbeRadius;
        var c = new CircleShape(r, 48u)
        {
            Position = new Vector2f(_probeCentre.X - r, _probeCentre.Y - r),
            FillColor = new Color(255, 255, 255, 12),
            OutlineColor = new Color(255, 255, 255, 160),
            OutlineThickness = MathF.Max(1f, r * 0.004f),
        };
        _win.Draw(c);

        if (_cfg.ShowSelectivity)
        {
            _probe.Measure(_host, _renderer.ClusterBoxes, _host.ShipArchetypeId,
                           _probeCentre.X - r, _probeCentre.Y - r, _probeCentre.X + r, _probeCentre.Y + r);
            _probe.MeasureMatches(_host, _probeCentre.X - r, _probeCentre.Y - r, _probeCentre.X + r, _probeCentre.Y + r);
        }
    }

    private void DrawHud()
    {
        var lines = new List<(string, Color)>();
        var white = new Color(220, 228, 240);
        var dim = new Color(140, 150, 170);
        var good = new Color(120, 230, 150);
        var warn = new Color(255, 200, 90);
        var bad = new Color(255, 120, 110);

        var mc = _host.ReadMigrationCounters();
        var g = _host.GridConfig;

        lines.Add(($"tick {_host.Tick,-9} {(_paused ? "PAUSED" : "running")} speed x{_speed:0.##}   " +
                   $"{_fps:F0} fps   sim {_simMsEma:F2} ms   frame {_frameMsEma:F1} ms",
                   _paused ? new Color(255, 200, 90) : white));
        // The world clock, stated outright. A sim that cannot hold its tick rate is running in slow motion, and
        // that used to be visible only as a low fps — indistinguishable from a rendering problem.
        var clockRatio = SimSpeedRatio;
        var keepingUp = _paused || clockRatio >= 0.95;
        lines.Add(($"world clock {(_paused ? "held" : $"{clockRatio:P0} of real time")}   " +
                   $"{_ticksPerFrameEma:F2} ticks/frame   budget {_cfg.SimBudgetMs:F0} ms   " +
                   $"backlog {_tickAccumulator:F1} ticks",
                   keepingUp ? new Color(150, 220, 170) : bad));
        lines.Add(($"ships {Simulation.FactionTag(0)} {_sim.ShipsAlive[0]} vs {Simulation.FactionTag(1)} {_sim.ShipsAlive[1]}   shots {_sim.ShotsAlive}   spawned {_sim.TotalSpawned}  killed {_sim.TotalKilled}", white));
        lines.Add(($"miners {Simulation.FactionTag(0)} {_sim.MinersAlive[0]} vs {Simulation.FactionTag(1)} {_sim.MinersAlive[1]}   " +
                   $"material {Simulation.FactionTag(0)} {_sim.Material[0]} vs {Simulation.FactionTag(1)} {_sim.Material[1]}   " +
                   $"asteroids {_sim.AsteroidsAlive}   mined {_sim.TotalMined}", new Color(190, 220, 170)));
        lines.Add(($"stations {Simulation.FactionTag(0)} {_sim.StationsAlive[0]} vs {Simulation.FactionTag(1)} {_sim.StationsAlive[1]}   " +
                   $"score {Simulation.FactionTag(0)} {_sim.Score(0):N0} {TrendMark(0)}  vs  {Simulation.FactionTag(1)} {_sim.Score(1):N0} {TrendMark(1)}",
                   _sim.Score(0) >= _sim.Score(1) ? Renderer.FactionA : Renderer.FactionB));
        lines.Add(($"pickups won {_sim.PickupsCollected}  hits landed {_sim.PickupHits}  shots absorbed {_sim.ShotsAbsorbed}   " +
                   $"{Simulation.FactionTag(0)}[{Effect(0)}]  {Simulation.FactionTag(1)}[{Effect(1)}]", new Color(255, 220, 140)));
        lines.Add((PickupLine(), _sim.LivePickupKind >= 0 ? new Color(255, 235, 150) : dim));
        lines.Add(("", dim));
        lines.Add((RenderLine(), _renderer.Lod.Tier == LodTier.Density ? new Color(255, 220, 140) : white));
        lines.Add((CullLine(), _renderer.CullActive ? dim : warn));
        lines.Add(("", dim));
        lines.Add(($"LEVEL 1  grid {g.GridWidth}x{g.GridHeight}  cell {g.CellSize:F0}m  cellCount {g.CellCount}  (Morton-padded)", dim));
        lines.Add(($"LEVEL 2  clusters {mc.ActiveClusters}  drawn {_renderer.DrawnClusters}   " +
                   $"singletons {_renderer.SingletonClusters} (zero-area AABB, drawn at minimum size)",
                   _renderer.SingletonClusters * 2 > _renderer.DrawnClusters ? warn : dim));
        lines.Add(($"         migrations/tick {mc.Migrations}   hysteresis-absorbed {mc.HysteresisAbsorbed}   exec {mc.ExecuteMs:F2} ms", mc.Migrations > 200 ? warn : dim));

        // Cluster-vs-cell geometry: the headline of the whole investigation.
        var (meanArea, drifted, maxArea) = SummariseClusters();
        var cellArea = g.CellSize * g.CellSize;
        var ratio = cellArea > 0 ? meanArea / cellArea : 0;
        var ratioColor = ratio > 0.5f ? bad : ratio > 0.2f ? warn : good;
        lines.Add(($"         mean cluster AABB = {ratio * 100:F1}% of a cell   (max {(cellArea > 0 ? maxArea / cellArea * 100 : 0):F0}%)   drifted {drifted}", ratioColor));
        if (_renderer.OversizedClusters > 0)
        {
            lines.Add(($"         {_renderer.OversizedClusters} clusters exceed {_cfg.FillMaxCellArea:F0} cell-areas (orange outline) — spatially degenerate", warn));
        }

        if (_cfg.ShowSelectivity && _probeActive)
        {
            lines.Add(("", dim));
            lines.Add(("QUERY PROBE (right-drag to move, P to toggle)", white));
            lines.Add(($"  cells touched      {_probe.CellsTouched}", dim));
            lines.Add(($"  clusters in cells  {_probe.ClustersInCells}", dim));
            lines.Add(($"  passed AABB test   {_probe.ClustersPassedAabb}   (rejected {_probe.ClusterRejectRate * 100:F1}%)",
                       _probe.ClusterRejectRate < 0.2f ? bad : _probe.ClusterRejectRate < 0.6f ? warn : good));
            lines.Add(($"  entities examined  {_probe.EntitiesExamined}", dim));
            lines.Add(($"  entities matched   {_probe.EntitiesMatched}", dim));
            lines.Add(($"  SELECTIVITY        {_probe.Selectivity * 100:F1}%   <- R1: 'percentage of useful data processed'",
                       _probe.Selectivity < 0.1f ? bad : _probe.Selectivity < 0.4f ? warn : good));
        }

        if (_sel.HasSelection)
        {
            lines.Add(("", dim));
            lines.Add((_sel.ArchetypeId == _host.StationArchetypeId ? "SELECTED STATION" : "SELECTED SHIP — Typhon internals", white));
            foreach (var l in DescribeSelection())
            {
                lines.Add((l, dim));
            }
        }

        lines.Add(("", dim));
        lines.Add(("MMB pan · wheel zoom · LMB select (or click/drag the minimap) · RMB probe · space pause · . step · [ ] speed · 0 reset · F frame", dim));
        lines.Add(("1 cells  2 heat  3 cluster AABB  4 ships  5 shots  6 target lines  7 selectivity  8 asteroids  9 CLUSTER COLOUR", dim));
        lines.Add(("N minimap   V motion vectors   L force LOD   C culling   D density source   M file map   H hud   F12 shot   Esc quit", dim));
        lines.Add(($"window layout: {_layout.Describe()}", dim));
        if (_cfg.ClusterColorMode)
        {
            lines.Add(("CLUSTER-COLOUR MODE — entity colour = its cluster. Interleaved colours mean interleaved membership.", new Color(255, 230, 140)));
        }
        if (_mapWin is { IsOpen: true })
        {
            lines.Add(($"file map: {_fileMap.ActivityStatus}   WAL {_fileMap.WalBytesPerSecond / 1024:F0} KiB/s   pages {_fileMap.PageCount}", dim));
        }

        var panelW = MathF.Min(_win.Size.X - 24f, MathF.Max(720f, _hud.MeasureMaxWidth(lines) + 24f));
        _hud.DrawPanel(_win, 8, 8, panelW, lines.Count * 16 + 14);
        _hud.DrawLines(_win, 16, 14, lines);
    }

    /// <summary>
    /// The level-of-detail readout. States the DECISION and the number behind it, so the rule can be checked against
    /// what is on screen rather than trusted.
    /// </summary>
    private string RenderLine()
    {
        var l = _renderer.Lod;
        var name = l.Tier switch
        {
            LodTier.Detail => "LOD0 sprites",
            LodTier.Point => "LOD1 points",
            _ => "LOD2 density",
        };
        var forced = _cfg.ForceLod >= 0 ? " (forced)" : "";
        var span = l.UnitsPerPixel >= 1000f ? $"{l.UnitsPerPixel / 1000f:F1} km/px" : $"{l.UnitsPerPixel:F1} m/px";
        return $"RENDER   {name}{forced}   {span}   {l.Reason}   drawn {_renderer.EntitiesDrawn}  " +
               $"in view ~{_renderer.EntitiesInView}";
    }

    /// <summary>
    /// The culling readout, which doubles as a live measurement of what level 2 is worth.
    /// </summary>
    /// <remarks>
    /// <c>candidates</c> is how many clusters the visible cells offered up; <c>passed</c> is how many survived the
    /// cluster-AABB test. The gap between them is the level-2 rejection rate, measured on the actual camera every
    /// frame instead of on a synthetic probe. A rate near zero means the second level rejected nothing and the
    /// renderer opened every cluster the cells contained.
    /// </remarks>
    private string CullLine()
    {
        var d = _renderer.Density;
        var src = d.Factionless ? "cells" : "entities";
        var density = $"density[{src}] {d.OccupiedBins} bins  max {d.Max:F0}  {d.BuildMs:F2} ms";

        string cull;
        if (!_cfg.CullingEnabled)
        {
            cull = "OFF — every cluster of every archetype walked (press C)";
        }
        else if (_renderer.Lod.Tier == LodTier.Density)
        {
            // At this tier the only entities read are the landmarks — a fixed handful of stations and asteroids
            // whose count does not grow with population, so the cull has nothing meaningful to reject.
            cull = $"landmarks only ({_renderer.EntitiesDrawn} drawn) — population not read";
        }
        else if (!_renderer.CullActive)
        {
            cull = "on, but no per-cell index yet — full walk";
        }
        else
        {
            var reject = _renderer.CullCandidates > 0
                ? (1f - _renderer.CullPassed / (float)_renderer.CullCandidates) * 100f
                : 0f;
            cull = $"cells {_renderer.CullCells}  candidates {_renderer.CullCandidates}  passed {_renderer.CullPassed}  " +
                   $"(L2 rejected {reject:F0}%)";
        }
        return $"CULLING  {cull}   {density}";
    }

    /// <summary>Compact per-faction effect readout for the HUD.</summary>
    private string Effect(int faction)
    {
        var parts = new List<string>();
        Add(_sim.PowerTicks[faction], "POWER");
        Add(_sim.ShieldTicks[faction], "SHIELD");
        Add(_sim.SpeedTicks[faction], "SPEED");
        Add(_sim.MiningTicks[faction], "MINING");
        Add(_sim.ProductionTicks[faction], "PRODUCTION");
        return parts.Count == 0 ? "-" : string.Join(" + ", parts);

        void Add(int ticks, string name)
        {
            if (ticks > 0)
            {
                parts.Add($"{name} {ticks / (float)_cfg.TickRate:F0}s");
            }
        }
    }

    /// <summary>
    /// The state of the contested-pickup race. Without this the 200-hit contest is invisible — you can see a
    /// crowd of ships around an object but not who is winning, or by how much.
    /// </summary>
    private string PickupLine()
    {
        if (_sim.LivePickupKind < 0)
        {
            var next = _sim.TicksToNextPickup;
            return next > 0
                ? $"CONTEST  none live — next in ~{next / (float)_cfg.TickRate:F0}s"
                : "CONTEST  none live";
        }
        var need = Math.Max(1, _cfg.PickupHitsToWin);
        var a = _sim.PickupProgress[0];
        var b = _sim.PickupProgress[1];
        var p = _sim.LivePickupPos;
        return $"CONTEST  {Simulation.PickupName((byte)_sim.LivePickupKind)} at ({p.X / 1000f:F0},{p.Y / 1000f:F0}) km   " +
               $"{Simulation.FactionTag(0)} {a}/{need} {Bar(a / (float)need)}   {Simulation.FactionTag(1)} {b}/{need} {Bar(b / (float)need)}";

        static string Bar(float t)
        {
            const int Width = 14;
            var n = (int)MathF.Round(Math.Clamp(t, 0f, 1f) * Width);
            return "[" + new string('=', n) + new string('.', Width - n) + "]";
        }
    }

    private (float meanArea, int drifted, float maxArea) SummariseClusters()
    {
        double sum = 0;
        var n = 0;
        var drifted = 0;
        float max = 0;
        foreach (var b in _renderer.ClusterBoxes)
        {
            if (b.ArchetypeId != _host.ShipArchetypeId)
            {
                continue;
            }
            sum += b.Area;
            n++;
            if (b.Area > max)
            {
                max = b.Area;
            }
            if (b.Drifted)
            {
                drifted++;
            }
        }
        return (n > 0 ? (float)(sum / n) : 0f, drifted, max);
    }

    private IEnumerable<string> DescribeSelection()
    {
        if (_sel.ArchetypeId == _host.StationArchetypeId && TryReadSelectedStation(out var si, out var sx, out var sy))
        {
            var hpPct = _cfg.StationHpMax > 0 ? 100f * si.Hp / _cfg.StationHpMax : 0f;
            var shPct = _cfg.StationShieldMax > 0 ? 100f * si.Shield / _cfg.StationShieldMax : 0f;
            yield return $"  faction {Simulation.FactionTag(si.Faction)}   position ({sx:F0}, {sy:F0}) m   spawned {si.SpawnedTotal} ships";
            yield return $"  hull   {si.Hp}/{_cfg.StationHpMax} ({hpPct:F0}%)   shield {si.Shield}/{_cfg.StationShieldMax} ({shPct:F0}%)";

            // A station is never destroyed, only DISABLED — which is why one sits at 0 hull and keeps drawing fire.
            // Spelling out the rebuild gate here is the whole point of the panel: the state is legible in the data
            // and was invisible on screen, so a wreck under permanent siege looked identical to a broken one.
            if (si.Disabled != 0)
            {
                var need = _cfg.StationRegenDelayTicks;
                yield return si.CalmTicks >= need
                    ? $"  DISABLED — rebuilding, +{_cfg.StationHpRegen} hull/tick, {(_cfg.StationHpRegen > 0 ? (_cfg.StationHpMax - si.Hp) / _cfg.StationHpRegen : 0)} ticks to go"
                    : $"  DISABLED — rebuild BLOCKED: under fire, calm {si.CalmTicks}/{need} ticks (a garrison parked on the wreck keeps it down)";
            }
            else
            {
                yield return si.CalmTicks < _cfg.StationRegenDelayTicks
                    ? $"  under fire — shield regen blocked, calm {si.CalmTicks}/{_cfg.StationRegenDelayTicks} ticks"
                    : $"  quiet — shield regenerating +{_cfg.StationShieldRegen}/tick";
            }
            yield return $"  gun cooldown {si.Cooldown}/{_cfg.StationCooldownTicks} ticks   range {_cfg.StationWeaponRange:F0} m   damage {_cfg.StationDamage}";
            yield return "";
        }

        if (_sel.ArchetypeId == _host.ShipArchetypeId && TryReadSelectedShip(out var sc, out var sm, out var shx, out var shy))
        {
            foreach (var l in DescribeSelectedShip(sc, sm, shx, shy))
            {
                yield return l;
            }
        }

        yield return $"  entityKey {_sel.EntityKey}   archetypeId {_sel.ArchetypeId}";
        yield return $"  cluster chunkId {_sel.ChunkId}   slot {_sel.Slot}";

        var home = _host.ClusterHomeCell(_sel.ArchetypeId, _sel.ChunkId);
        if (home >= 0)
        {
            var (cx, cy) = _host.Grid.CellKeyToCoords(home);
            yield return $"  home cell key {home} = ({cx},{cy})   entities in cell {_host.CellEntityCount(home)}   clusters in cell {_host.CellClusterCount(home)}";
        }
        else
        {
            yield return "  home cell: unmapped (-1)";
        }

        foreach (var b in _renderer.ClusterBoxes)
        {
            if (b.ArchetypeId == _sel.ArchetypeId && b.ChunkId == _sel.ChunkId)
            {
                var g = _host.GridConfig;
                var pct = g.CellSize > 0 ? b.Area / (g.CellSize * g.CellSize) * 100 : 0;
                yield return $"  cluster AABB [{b.MinX:F0},{b.MinY:F0}]-[{b.MaxX:F0},{b.MaxY:F0}]  {b.Width:F0}x{b.Height:F0}u = {pct:F1}% of a cell";
                yield return $"  live entities in cluster {b.LiveCount}" + (b.Drifted ? "   *** AABB centre is in a DIFFERENT cell than its home (hysteresis drift)" : "");
                break;
            }
        }
    }

    // ─── Screenshot / self-verification ───────────────────────────────────────────────────────────────────────────

    public void SaveScreenshot(string path, bool report, FrameProbe.Rect? rect = null)
    {
        var tex = new Texture(_win.Size);
        tex.Update(_win);
        var img = tex.CopyToImage();
        img.SaveToFile(path);
        Console.WriteLine($"screenshot -> {path}");
        if (report)
        {
            Console.Write(FrameProbe.Report(img, rect));
            foreach (var p in FrameProbe.Check(img, rect))
            {
                Console.Error.WriteLine($"[frame-check] {p}");
            }
        }
    }

    /// <summary>Headless-ish mode: open, run N ticks, screenshot, report, exit. Used to self-verify rendering.</summary>
    public void RunAuto()
    {
        CreateWindows();
        var dt = 1f / _cfg.TickRate;
        for (var i = 0; i < _cfg.AutoTicks; i++)
        {
            _win.DispatchEvents();
            RunOneTick(dt);
            // The census belongs here too, not only in the interactive frame loop: the counter leaks this demo exists to
            // expose take tens of minutes of wall clock to show up at 1x, and auto mode is the only way to run the same
            // tick count in a couple of minutes. Without it the one measurement that answers "is the page cache still
            // filling up?" is unavailable in precisely the mode built for unattended runs.
            WriteCensus();
            if (_mapWin is { IsOpen: true } && i % Math.Max(1, _cfg.FileMapEveryNTicks) == 0)
            {
                _fileMap.Refresh();
            }
        }
        _probeActive = true;
        _probeCentre = new Vector2f(_cfg.WorldSize * 0.5f, _cfg.WorldSize * 0.5f);

        if (_cfg.AutoViewHeight > 0f)
        {
            _cam.ViewHeight = _cfg.AutoViewHeight;
            _cam.Center = new Vector2f(
                _cfg.AutoCenterX >= 0f ? _cfg.AutoCenterX : _cfg.WorldSize * 0.5f,
                _cfg.AutoCenterY >= 0f ? _cfg.AutoCenterY : _cfg.WorldSize * 0.5f);
        }

        // Select whatever ship is nearest the centre, so the auto screenshot exercises the selection path — it is
        // otherwise only reachable by a mouse click and would ship untested.
        SelectNearestForAuto();

        // Warm the render path before the frame that gets measured. A single cold call reports JIT and first-touch
        // allocation as if they were per-frame cost — the density build came out at a flat ~2 ms whether it binned
        // 128 entities or 2,000, which is the signature of fixed overhead, not of work. It also lets the LOD
        // hysteresis settle, so the reported tier is the one the view converges to rather than the first guess.
        var warmup = Stopwatch.StartNew();
        for (var i = 0; i < 8; i++)
        {
            RenderMain(present: false);
        }
        Console.WriteLine($"render warm-up: 8 frames in {warmup.Elapsed.TotalMilliseconds:F1} ms " +
                          $"({warmup.Elapsed.TotalMilliseconds / 8:F2} ms/frame)");

        // Draw, capture, THEN present. Texture.Update reads the back buffer; after Display() it is undefined,
        // which produced a uniformly black screenshot until this was reordered.
        RenderMain(present: false);
        FrameProbe.Rect? rect = FrameProbe.Rect.TryParse(_cfg.AutoRect, out var r) ? r : null;
        SaveScreenshot(string.IsNullOrEmpty(_cfg.AutoShot) ? "spacebattle-auto.png" : _cfg.AutoShot, report: true, rect);
        _win.Display();

        if (_mapWin is { IsOpen: true })
        {
            _fileMap.Draw(_mapWin);
            _mapWin.Display();
        }

        PrintSelectivitySweep();
    }

    /// <summary>
    /// Sweeps the probe radius and reports selectivity at each scale. This is the money measurement: it shows
    /// broadphase rejection collapsing as the query gets smaller than a cell, which is exactly the regime real
    /// gameplay queries (a sensing radius, a projectile hit test) live in.
    /// </summary>
    public void PrintSelectivitySweep()
    {
        var g = _host.GridConfig;
        DumpAsteroids();
        DumpMissingAabbs();
        Console.WriteLine();
        Console.WriteLine($"ECONOMY  ships {Simulation.FactionTag(0)} {_sim.ShipsAlive[0]} vs {Simulation.FactionTag(1)} {_sim.ShipsAlive[1]}   " +
                          $"miners {Simulation.FactionTag(0)} {_sim.MinersAlive[0]} vs {Simulation.FactionTag(1)} {_sim.MinersAlive[1]}   " +
                          $"material {_sim.Material[0]} vs {_sim.Material[1]}   asteroids {_sim.AsteroidsAlive}   mined {_sim.TotalMined}   killed {_sim.TotalKilled}");
        Console.WriteLine($"PICKUPS  collected {_sim.PickupsCollected}  absorbed {_sim.ShotsAbsorbed}  " +
                          $"effect uptime {Simulation.FactionTag(0)} {(_sim.TicksElapsed > 0 ? 100.0 * _sim.EffectTicks[0] / _sim.TicksElapsed : 0):F1}%  " +
                          $"{Simulation.FactionTag(1)} {(_sim.TicksElapsed > 0 ? 100.0 * _sim.EffectTicks[1] / _sim.TicksElapsed : 0):F1}%");
        Console.WriteLine($"GEOMETRY oversized clusters (> {_cfg.FillMaxCellArea:F0} cell-areas): {_renderer.OversizedClusters} of {_renderer.DrawnClusters}");
        Console.WriteLine();
        var mm = _sim.MinerModeCount;
        var meanMine = _sim.MiningDistanceCount > 0 ? _sim.MiningDistanceSum / _sim.MiningDistanceCount : 0;
        Console.WriteLine($"MINERS   seeking {mm[0]}  mining {mm[1]}  returning {mm[2]}   no ore target {mm[3]}   " +
                          $"in-range {_sim.MiningDistanceCount} at {meanMine:F0} m mean / {_sim.MiningDistanceMax:F0} m max " +
                          $"(mine {_cfg.MineRange:F0} m, dock {_cfg.MineDockRange:F0} m)   " +
                          $"cargo {_sim.LadenMiners} laden, mean {_sim.MeanCargo:F0}/{_cfg.CargoMax}   retargets {_sim.OreRetargets}   " +
                          $"cargo cues drawn {_renderer.CargoCuesDrawn} (of the laden miners on screen)");
        Console.WriteLine($"         delivered {_sim.DropDistanceCount} loads at {_sim.MeanDropDistance:F0} m mean / " +
                          $"{_sim.DropDistanceMax:F0} m max from station centre (dock {_cfg.StationDockRange:F0} m, " +
                          $"station radius {_cfg.StationRadius:F0} m)");
        var fighters = Math.Max(1, _sim.StandoffSamples);
        var flipRate = _sim.TicksElapsed > 0
            ? _sim.StandoffFlips / (double)_sim.TicksElapsed * _cfg.TickRate / fighters * 1000.0
            : 0;
        Console.WriteLine($"STANDOFF mean engagement {_sim.MeanEngagementDistance:F0} m (target {_cfg.StandoffRange:F0} " +
                          $"+/- {_cfg.StandoffBand / 2:F0})   engaging {_sim.StandoffSamples}  orbiting {_sim.StandoffOrbiting}   " +
                          $"reversals {flipRate:F1} per 1000 fighters/s   nearest neighbour {_sim.MeanNearestNeighbour:F0} m (sep radius {_cfg.SeparationRadius:F0} m)");
        Console.WriteLine($"CLOCK    world running at {SimSpeedRatio:P0} of real time   {_ticksPerFrameEma:F2} ticks/frame   " +
                          $"sim budget {_cfg.SimBudgetMs:F0} ms/frame   backlog {_tickAccumulator:F1} ticks   " +
                          $"frame {_frameMsEma:F1} ms");
        Console.WriteLine($"PERF     sim {_simMsEma:F2} ms/tick   ships {_sim.ShipsAlive[0] + _sim.ShipsAlive[1]}   " +
                          $"reacquire every {_cfg.TargetReacquireTicks} ticks   acquireRadius {_cfg.AcquireRadius:F0} m");
        Console.WriteLine($"GUNNERY  fired {_sim.TotalShotsFired}  hits {_sim.TotalShotHits}  " +
                          $"HIT RATE {_sim.ShotHitRate * 100:F1}%   " +
                          $"aim staleness <= {_cfg.TargetReacquireTicks} ticks = {_cfg.TargetReacquireTicks * _cfg.ShipMaxSpeed / _cfg.TickRate:F0} m   " +
                          $"flight drift @{_cfg.WeaponRange:F0} m = " +
                          $"{_cfg.WeaponRange / _cfg.ShotSpeed * _cfg.ShipMaxSpeed:F0} m   hitRadius {_cfg.ShotHitRadius:F0} m");
        var defendCalls = _sim.DefendersOnTarget + _sim.DefendersBlind;
        Console.WriteLine($"DEFENCE  calls {defendCalls}   on target {_sim.DefendersOnTarget} " +
                          $"({(defendCalls > 0 ? 100.0 * _sim.DefendersOnTarget / defendCalls : 0):F1}%)   " +
                          $"blind {_sim.DefendersBlind}   at base (<2 km) {_sim.DefendersAtBase} " +
                          $"({(defendCalls > 0 ? 100.0 * _sim.DefendersAtBase / defendCalls : 0):F1}%)   " +
                          $"mean range to the point ordered {_sim.MeanDefendRange:F0} m   " +
                          $"scan {_cfg.StationThreatScanRadius:F0} m  garrison ring {_cfg.StationGarrisonRadius:F0} m   " +
                          $"stations lost {_sim.StationsDestroyed} destroyed / {_sim.StationsDisabled} disabled");
        var defTicks = _sim.DefenderTicksAtPost + _sim.DefenderTicksAway;
        Console.WriteLine($"GARRISON defender-ticks {defTicks}   at post {_sim.DefenderTicksAtPost} " +
                          $"({(defTicks > 0 ? 100.0 * _sim.DefenderTicksAtPost / defTicks : 0):F1}%)   " +
                          $"away {_sim.DefenderTicksAway} at {_sim.MeanDefenderAwayRange:F0} m mean   " +
                          $"OUTBOUND {_sim.DefenderTicksOutbound} " +
                          $"({(defTicks > 0 ? 100.0 * _sim.DefenderTicksOutbound / defTicks : 0):F1}% — the screen symptom)   " +
                          $"unreachable sieges declined {_sim.DefenceCallsUnreachable}   " +
                          $"leash {_cfg.StationDefendMaxTravelTicks:F0} ticks " +
                          $"(= {_cfg.StationDefendMaxTravelTicks * _cfg.ShipMaxSpeed / _cfg.TickRate / 1000f:F1} km for a light hull)");
        Console.WriteLine(RenderLine());
        Console.WriteLine(CullLine());
        var layout = $"world {_cfg.WorldSize / 1000f:F0} km  layout {_cfg.StationLayout}/{_cfg.AsteroidLayout}";
        if (_renderer.Density.Factionless)
        {
            // Not zero — unmeasurable. The cell-counter source has no faction lane, so "bins holding both sides"
            // cannot be computed from it. Printing 0 here would read as "no fighting anywhere" on a run with six
            // thousand kills, which is worse than printing nothing.
            Console.WriteLine($"CONFLICT n/a — densitySource=cells has no faction split; use --densitySource=entities.  {layout}");
        }
        else
        {
                // Build it a second time, back to back. If the repeat is fast the first was paying for cold state
            // (page-cache misses, first-touch) rather than for work; if both are slow it is real compute. This
            // distinction is otherwise unguessable from a single timing, and it is exactly the mistake that made
            // the very first density measurement read 2 ms when the steady-state cost was 0.05 ms.
            var firstMs = _renderer.Density.BuildMs;
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            _renderer.Density.Build(_cfg, _host);
            Console.WriteLine($"         density build: first {firstMs:F2} ms, immediate repeat {_renderer.Density.BuildMs:F2} ms   " +
                              $"GC during repeat: gen0 +{GC.CollectionCount(0) - g0} gen1 +{GC.CollectionCount(1) - g1} " +
                              $"gen2 +{GC.CollectionCount(2) - g2}");

            var (contested, spread, ccx, ccy) = _renderer.Density.ContestedSpread();
            Console.WriteLine($"CONFLICT contested bins {contested}  spread {spread / 1000f:F1} km (RMS about its own centroid)  " +
                              $"centre ({ccx:F0},{ccy:F0})  {layout}");
        }
        Console.WriteLine("         stations: " + _sim.DescribeStations());
        Console.WriteLine($"         station health: {_sim.DescribeStationHealth()}   disabled {_sim.StationsDisabled}  rebuilt {_sim.StationsRebuilt}");
        Console.WriteLine($"         stations alive: {Simulation.FactionTag(0)} {_sim.StationsAlive[0]}  {Simulation.FactionTag(1)} {_sim.StationsAlive[1]}   " +
                          $"destroyed {_sim.StationsDestroyed} (destructible {_cfg.StationsDestructible})   " +
                          $"miners rehomed {_sim.MinersRehomed}");

        // Migration churn is a headline metric of this demo — it is what the tick fence spends its time on — so it
        // belongs in the machine-readable report and not only in the on-screen HUD.
        var mcc = _host.ReadMigrationCounters();
        // Force a checkpoint and let it settle before reading the dirty count. Reading it mid-flight cannot tell a
        // page that is legitimately in use from one whose DirtyCounter never returns to zero.
        _host.DBE.ForceCheckpoint();
        System.Threading.Thread.Sleep(400);
        var dirty = _host.CheckpointStats0();

        // Convergence probe: does repeated checkpointing at quiesce drain the residue to zero, or plateau?
        // Draining means the residue is purely #824's per-cycle K-1 imbalance. A plateau means a second,
        // frequency-independent component exists underneath and #824 is scoped wrong.
        if (_cfg.QuiesceCheckpoints > 0)
        {
            var trail = new System.Text.StringBuilder().Append(dirty.Dirty);
            for (var i = 0; i < _cfg.QuiesceCheckpoints; i++)
            {
                _host.DBE.ForceCheckpoint();
                System.Threading.Thread.Sleep(300);
                trail.Append(" -> ").Append(_host.CheckpointStats0().Dirty);
            }
            Console.WriteLine($"CONVERGE dirty pages across {_cfg.QuiesceCheckpoints} extra quiescent checkpoints: {trail}");
            dirty = _host.CheckpointStats0();
        }
        if (_cfg.DirtyTraceAll)
        {
            // Marks and writeback debt are separate obligations with separate owners (PS-05 / PS-10), so report them
            // separately. At quiesce BOTH must be zero: a mark outstanding is one its owner never released, and a page
            // still owed is one the checkpoint never wrote. A single "dirty" number cannot tell those apart, which is
            // why this demo spent a long time watching the wrong one.
            Console.WriteLine($"MARKS    pages holding unreleased mutator marks at quiesce: {_host.CountMarkPages()} (must be 0)");
        }
        Console.WriteLine($"PAGECACHE dirty {dirty.Dirty} of {dirty.Total} pages after a forced checkpoint, quiescent   " +
                          $"({100.0 * dirty.Dirty / Math.Max(1, dirty.Total):F1}% dirty)   first dirty page {dirty.FirstDirtyPage}");
        var pinNow = _host.CountPinnedPages();
        Console.WriteLine($"PINS     dirty {pinNow.Dirty}  acw {pinNow.Acw}  slotRef {pinNow.SlotRef}  epochHeld {pinNow.EpochHeld}   " +
                          $"unevictable {pinNow.Unevictable} of {pinNow.Total} ({100.0 * pinNow.Unevictable / Math.Max(1, pinNow.Total):F1}%)");
        if (_cfg.DirtyTracePage >= 0)
        {
            Console.WriteLine($"DCLEAK   page {_cfg.DirtyTracePage}:{Typhon.Engine.Internals.PagedMMF.DescribeDirtyOutstanding()}");
        }
        var cp = _host.CheckpointStats();
        Console.WriteLine($"CHECKPT  running {cp.Running}   checkpoints {cp.Checkpoints}   segments recycled {cp.SegmentsRecycled}   " +
                          $"pages written {cp.PagesWritten}   consecutive gated cycles {cp.GatedCycles}");
        Console.WriteLine($"SKIPS    {_host.DescribeCheckpointSkips()}");
        if (_cfg.AcwTracePage >= 0)
        {
            Console.WriteLine($"ACWLEAK  page {_cfg.AcwTracePage}:{Typhon.Engine.Internals.PagedMMF.DescribeAcwOutstanding()}");
        }
        Console.WriteLine($"WAL      worst fence {_host.MaxFenceMs:F1} ms at tick {_host.MaxFenceTick}   " +
                          $"fences over {TyphonHost.LongFenceThresholdMs:F0} ms: {_host.LongFenceCount}   " +
                          $"fua {_cfg.WalUseFua}  segment {_cfg.WalSegmentSizeMB} MB  staging {_cfg.WalStagingBufferKB} KB  " +
                          $"prealloc {_cfg.WalPreAllocateSegments}");
        Console.WriteLine($"FENCE    migrations/tick {mcc.Migrations}  hysteresis-absorbed {mcc.HysteresisAbsorbed}  " +
                          $"exec {mcc.ExecuteMs:F2} ms  active clusters {mcc.ActiveClusters}  " +
                          $"shipSpeed {_cfg.ShipMaxSpeed:F0} m/s");
        Console.WriteLine($"         camera height {_cam.ViewHeight:F0}m  centre ({_cam.Center.X:F0},{_cam.Center.Y:F0})  " +
                          $"minimap {(_cfg.ShowMinimap ? "on" : "off")}");
        Console.WriteLine();
        Console.WriteLine($"SELECTIVITY SWEEP at world centre — cell {g.CellSize:F0}u, {_renderer.ClusterBoxes.Count} clusters drawn");
        Console.WriteLine("  radius   q/cell   cells  clusters  passed  reject%   examined  matched  selectivity");
        foreach (var radius in new[] { 25f, 50f, 100f, 200f, 400f, 800f, 1600f, 3200f })
        {
            _probe.Measure(_host, _renderer.ClusterBoxes, _host.ShipArchetypeId,
                           _probeCentre.X - radius, _probeCentre.Y - radius, _probeCentre.X + radius, _probeCentre.Y + radius);
            _probe.MeasureMatches(_host, _probeCentre.X - radius, _probeCentre.Y - radius,
                                  _probeCentre.X + radius, _probeCentre.Y + radius);
            var qOverCell = 2 * radius / g.CellSize;
            Console.WriteLine($"  {radius,6:F0}   {qOverCell,6:F3}   {_probe.CellsTouched,5}  {_probe.ClustersInCells,8}  " +
                              $"{_probe.ClustersPassedAabb,6}  {_probe.ClusterRejectRate * 100,6:F1}%   " +
                              $"{_probe.EntitiesExamined,8}  {_probe.EntitiesMatched,7}  {_probe.Selectivity * 100,10:F2}%");
        }
        Console.WriteLine($"  mean cluster AABB = {_probe.MeanClusterAreaVsCell * 100:F1}% of one cell's area");
    }

    /// <summary>Drives the speed handlers directly, so the wiring can be verified without pressing a key.</summary>
    public bool SelfTestSpeedKeys()
    {
        var ok = true;
        _speed = 1f;

        _eventSeq++; HandleChar(']');
        ok &= Report("] via TextEntered", 1.5f);

        _eventSeq++; HandleChar('[');
        ok &= Report("[ via TextEntered", 1.0f);

        _eventSeq++; HandleKey(Keyboard.Key.RBracket);
        ok &= Report("] via KeyPressed", 1.5f);

        _eventSeq++; HandleKey(Keyboard.Key.PageDown);
        ok &= Report("PageDown", 1.0f);

        // One physical press delivers BOTH events in the same batch: it must step exactly once.
        _eventSeq++; HandleKey(Keyboard.Key.RBracket); HandleChar(']');
        ok &= Report("one press, both routes (must step once)", 1.5f);

        _eventSeq++; HandleChar('[');
        _eventSeq++; HandleChar('[');
        _eventSeq++; HandleChar('[');
        ok &= Report("three [ presses compound", 1.5f / 1.5f / 1.5f / 1.5f);

        Console.WriteLine(ok ? "speed-key self-test: PASS" : "speed-key self-test: FAIL");
        return ok;

        bool Report(string what, float expected)
        {
            var good = MathF.Abs(_speed - expected) < 0.001f;
            Console.WriteLine($"  {(good ? "ok  " : "FAIL")} {what,-44} speed = {_speed:0.####} (expected {expected:0.####})");
            return good;
        }
    }

    /// <summary>Ground truth on the asteroid field: how many exist, and where they actually are.</summary>
    private void DumpAsteroids()
    {
        var n = 0;
        var nonFinite = 0;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        double sx = 0, sy = 0;
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Rock>();
        using var e = acc.GetClusterEnumerator();
        var clusters = 0;
        foreach (var cluster in e)
        {
            clusters++;
            var bits = cluster.OccupancyBits;
            var pos = cluster.GetReadOnlySpan(Rock.Position);
            var ast = cluster.GetReadOnlySpan(Rock.Asteroid);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (ast[i].Dead != 0)
                {
                    continue;
                }
                var x = pos[i].Bounds.MinX;
                var y = pos[i].Bounds.MinY;
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    nonFinite++;
                    continue;
                }
                n++;
                sx += x; sy += y;
                minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
                minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
            }
        }
        Console.WriteLine($"ASTEROIDS live={n} nonFinite={nonFinite} clusters={clusters} counterSaysAlive={_sim.AsteroidsAlive}");
        // Raw component contents: if VX/Capacity come back outside the values we wrote, the component is being
        // read at the wrong offsets — the packed-vs-padded layout trap that already bit StationInfo.
        {
            var shown = 0;
            using var tx2 = _host.DBE.CreateQuickTransaction();
            using var acc2 = tx2.For<Rock>();
            using var e2 = acc2.GetClusterEnumerator();
            foreach (var cl in e2)
            {
                var b2 = cl.OccupancyBits;
                var ps = cl.GetReadOnlySpan(Rock.Position);
                var az = cl.GetReadOnlySpan(Rock.Asteroid);
                while (b2 != 0 && shown < 6)
                {
                    var i2 = System.Numerics.BitOperations.TrailingZeroCount(b2);
                    b2 &= b2 - 1;
                    ref readonly var r = ref az[i2];
                    Console.WriteLine($"  raw[{shown}] chunk={cl.ChunkId} slot={i2} pos=({ps[i2].Bounds.MinX:F1},{ps[i2].Bounds.MinY:F1}) " +
                                      $"VX={r.VX:F3} VY={r.VY:F3} Cap={r.Capacity} Max={r.MaxCapacity} Dead={r.Dead}");
                    shown++;
                }
                if (shown >= 6)
                {
                    break;
                }
            }
        }
        if (n > 0)
        {
            Console.WriteLine($"  x [{minX:F0}..{maxX:F0}] y [{minY:F0}..{maxY:F0}]  centroid ({sx / n:F0},{sy / n:F0})  " +
                              $"world centre ({_cfg.WorldSize * 0.5f:F0},{_cfg.WorldSize * 0.5f:F0})  field radius {_cfg.WorldSize * _cfg.AsteroidFieldRadiusPct:F0}");
        }
    }

    /// <summary>Selects the ship nearest the world centre, ignoring the pick radius (auto mode has no cursor).</summary>
    private void SelectNearestForAuto()
    {
        var target = new Vector2f(_cfg.WorldSize * 0.5f, _cfg.WorldSize * 0.5f);
        var best = float.MaxValue;
        _sel = default;
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Ship.Position);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var dx = pos[i].Bounds.MinX - target.X;
                var dy = pos[i].Bounds.MinY - target.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 < best)
                {
                    best = d2;
                    _sel = new Selection
                    {
                        HasSelection = true,
                        ArchetypeId = _host.ShipArchetypeId,
                        ChunkId = cluster.ChunkId,
                        Slot = i,
                        EntityKey = cluster.GetEntityId(i).EntityKey,
                    };
                }
            }
        }
        if (_cfg.AutoSelectStation)
        {
            // Auto mode has no cursor, so the station panel is otherwise unreachable in a headless run — and an
            // info panel nobody can exercise is exactly the kind of feature that ships rendering nothing.
            var centre = new Vector2f(_cfg.WorldSize * 0.5f, _cfg.WorldSize * 0.5f);
            var nearest = _sim.NearestStationPosition(centre.X, centre.Y);
            if (TrySelectStation(new Vector2f(nearest.X, nearest.Y)))
            {
                Console.WriteLine($"SELECTED station chunk={_sel.ChunkId} slot={_sel.Slot} entityKey={_sel.EntityKey}");
                foreach (var l in DescribeSelection())
                {
                    Console.WriteLine(l);
                }
                return;
            }
            Console.WriteLine("SELECTED station: NONE — pick failed");
        }
        if (_sel.HasSelection)
        {
            Console.WriteLine($"SELECTED ship chunk={_sel.ChunkId} slot={_sel.Slot} entityKey={_sel.EntityKey}");
        }
    }

    /// <summary>Snapshots both windows' placement. Called on a timer and at shutdown, so a hard kill still keeps
    /// a layout from at most a second ago.</summary>
    private void CaptureLayout()
    {
        if (!_cfg.RememberWindowLayout || _cfg.AutoTicks > 0)
        {
            return;   // headless verification runs must not clobber the interactive layout
        }
        _layout.Capture(_win, "main");
        _layout.Capture(_mapWin, "filemap");
        _layout.Save();
    }

    /// <summary>
    /// Counts live clusters whose SpatialBounds is the empty sentinel — clusters holding entities that the renderer
    /// cannot draw a box for, and that a spatial query would therefore never return.
    /// </summary>
    private void DumpMissingAabbs()
    {
        Console.WriteLine();
        Console.WriteLine("CLUSTERS WITHOUT A VALID AABB (entities present, bounds = empty sentinel)");
        CheckContainment();
        Report("Ship", _host.ShipArchetypeId, () => CountShip());
        Report("Shot", _host.ShotArchetypeId, () => CountShot());
        Report("Rock", _host.RockArchetypeId, () => CountRock());
        Report("Station", _host.StationArchetypeId, () => CountStation());

        void Report(string name, int archId, Func<(int total, int bad, int badEntities)> f)
        {
            var (total, bad, badEntities) = f();
            var flag = bad > 0 ? "  <-- entities drawn with no box" : "";
            Console.WriteLine($"  {name,-8} clusters={total,4}  withoutAabb={bad,4}  entitiesAffected={badEntities,5}{flag}");
        }

        (int, int, int) CountShip()
        {
            using var tx = _host.DBE.CreateQuickTransaction();
            using var acc = tx.For<Ship>();
            using var e = acc.GetClusterEnumerator();
            int t = 0, b = 0, be = 0;
            foreach (var c in e)
            {
                t++;
                ref readonly var a2 = ref c.SpatialBounds;
                if (!(a2.MinX <= a2.MaxX) || !float.IsFinite(a2.MinX)) { b++; be += c.LiveCount; }
            }
            return (t, b, be);
        }

        (int, int, int) CountShot()
        {
            using var tx = _host.DBE.CreateQuickTransaction();
            using var acc = tx.For<Shot>();
            using var e = acc.GetClusterEnumerator();
            int t = 0, b = 0, be = 0;
            foreach (var c in e)
            {
                t++;
                ref readonly var a2 = ref c.SpatialBounds;
                if (!(a2.MinX <= a2.MaxX) || !float.IsFinite(a2.MinX)) { b++; be += c.LiveCount; }
            }
            return (t, b, be);
        }

        (int, int, int) CountRock()
        {
            using var tx = _host.DBE.CreateQuickTransaction();
            using var acc = tx.For<Rock>();
            using var e = acc.GetClusterEnumerator();
            int t = 0, b = 0, be = 0;
            foreach (var c in e)
            {
                t++;
                ref readonly var a2 = ref c.SpatialBounds;
                if (!(a2.MinX <= a2.MaxX) || !float.IsFinite(a2.MinX)) { b++; be += c.LiveCount; }
            }
            return (t, b, be);
        }

        (int, int, int) CountStation()
        {
            using var tx = _host.DBE.CreateQuickTransaction();
            using var acc = tx.For<Station>();
            using var e = acc.GetClusterEnumerator();
            int t = 0, b = 0, be = 0;
            foreach (var c in e)
            {
                t++;
                ref readonly var a2 = ref c.SpatialBounds;
                if (!(a2.MinX <= a2.MaxX) || !float.IsFinite(a2.MinX)) { b++; be += c.LiveCount; }
            }
            return (t, b, be);
        }
    }

    /// <summary>
    /// CA-01 containment check from the outside: is every live entity inside its own cluster's stored AABB?
    /// An entity outside its box is invisible to the broadphase — the query rejects the cluster and never looks
    /// inside — so this is a silent-false-negative detector, not a cosmetic one.
    /// </summary>
    private void CheckContainment()
    {
        var worst = 0f;
        var outside = 0;
        var total = 0;
        var oversized = 0;
        float biggest = 0;

        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();
        foreach (var c in e)
        {
            var bits = c.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            ref readonly var box = ref c.SpatialBounds;
            var w = box.MaxX - box.MinX;
            var h = box.MaxY - box.MinY;
            if (w * h > _cfg.FillMaxCellArea * _cfg.CellSize * _cfg.CellSize)
            {
                oversized++;
            }
            biggest = MathF.Max(biggest, MathF.Max(w, h));

            var pos = c.GetReadOnlySpan(Ship.Position);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                total++;
                var x = pos[i].Bounds.MinX;
                var y = pos[i].Bounds.MinY;
                var dx = MathF.Max(box.MinX - x, x - box.MaxX);
                var dy = MathF.Max(box.MinY - y, y - box.MaxY);
                var d = MathF.Max(dx, dy);
                if (d > 0.001f)
                {
                    outside++;
                    worst = MathF.Max(worst, d);
                }
            }
        }
        Console.WriteLine($"  CA-01 containment: {outside} of {total} ship entities OUTSIDE their cluster AABB (worst {worst:F1}u)");
        Console.WriteLine($"  largest ship cluster AABB edge {biggest:F0}u ({biggest / _cfg.CellSize:F2} cells); oversized clusters {oversized}");
    }

    public void Dispose()
    {
        CaptureLayout();
        _mapWin?.Close();
        _win?.Close();
    }
}
