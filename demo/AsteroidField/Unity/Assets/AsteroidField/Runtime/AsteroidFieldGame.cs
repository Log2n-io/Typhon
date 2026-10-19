using System;
using System.Collections.Generic;
using System.Globalization;
using AsteroidField.Client;
using Typhon.Client;
using UnityEngine;

namespace AsteroidField.Unity
{
    /// <summary>
    /// The whole client: connects to an Asteroid Field server, mirrors what it says this pilot can see, and sends the pilot's input back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Threads.</b> The Typhon SDK receives and applies frames on its own thread; <see cref="BeltLink"/> copies each one into a snapshot there. This
    /// component only ever reads snapshots, on Unity's main thread, so no Unity object is touched off it.
    /// </para>
    /// <para>
    /// <b>Time.</b> Positions are not interpolated between snapshots: each entity carries a motion segment (a start point, a velocity and a start tick), and
    /// the SDK's <see cref="Typhon.Client.Clock"/> says which server tick is "now" for rendering — slightly behind the newest frame, so the network's jitter
    /// never shows. A drifting asteroid is one segment for as long as it drifts straight: the HUD's bytes/s is what that costs.
    /// </para>
    /// <para>Built from code on scene load (<see cref="Boot"/>), so any scene — even an empty one — runs the demo. Controls: W/S thrust, A/D turn, left
    /// click an asteroid to mine it, right click to stop, E to sell your hold.</para>
    /// </remarks>
    public sealed class AsteroidFieldGame : MonoBehaviour
    {
        public string Host = "127.0.0.1";
        public int Port = 9300;

        private const int HoldCapacity = 300;
        private const float SteerPeriod = 0.05f;

        private BeltLink _link;
        private Clock _clock;
        private BeltSnapshot _world;
        private EntityViews _views;
        private ChaseCamera _camera;
        private readonly List<FrameArrival> _arrivals = new List<FrameArrival>();
        private readonly List<ReceivedEvent> _events = new List<ReceivedEvent>();
        private readonly List<string> _log = new List<string>();
        private float _steerTimer;
        private uint _wantedTarget;
        private string _status = "connecting…";
        private long _lastBytes;
        private float _bytesTimer;
        private double _bytesPerSecond;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindAnyObjectByType<AsteroidFieldGame>() == null)
            {
                new GameObject("Asteroid Field").AddComponent<AsteroidFieldGame>();
            }
        }

        private void Start()
        {
            ReadCommandLine();
            Application.targetFrameRate = 120;
            _views = new EntityViews(transform);
            _camera = ChaseCamera.Create();
            _link = new BeltLink(Host, Port);
            Connect();
        }

        private async void Connect()
        {
            try
            {
                await _link.ConnectAsync();
                _status = $"connected to {Host}:{Port}";
            }
            catch (Exception e)
            {
                _status = $"could not connect to {Host}:{Port}: {e.Message}";
            }
        }

        private void Update()
        {
            if (_link == null)
            {
                return;
            }

            if (_link.TryTakeLatest(out var snapshot))
            {
                _world = snapshot;
            }

            if (_world == null)
            {
                return;
            }

            AdvanceClock();
            var renderTime = _clock.RenderTick + _clock.RenderFrac;
            _views.Sync(_world, renderTime, Time.deltaTime);
            HandleEvents();
            HandleInput();
            if (_views.TryGetShip(_world.SelfNetId, out var ship))
            {
                _camera.Follow(ship, Time.deltaTime);
            }

            MeasureBandwidth();
        }

        private void AdvanceClock()
        {
            if (_clock == null)
            {
                _clock = new Clock(new ClockOptions { TickPeriodMs = _world.PeriodMs > 0 ? _world.PeriodMs : 50 });
            }

            _arrivals.Clear();
            _link.DrainArrivals(_arrivals);
            foreach (var a in _arrivals)
            {
                _clock.OnFrame(a.Tick, a.ReceivedMs);
            }

            _clock.Update(BeltLink.NowMs());
        }

        private void HandleEvents()
        {
            _events.Clear();
            _link.DrainEvents(_events);
            foreach (var e in _events)
            {
                if (e.Name == "Depleted")
                {
                    Effects.Burst(EntityViews.ToWorld(e.Number("x"), e.Number("y")), (int)e.Number("size"));
                    Log("an asteroid was mined out nearby");
                }
                else if (e.Name == "CargoFull")
                {
                    _wantedTarget = 0;
                    Log($"hold full ({(int)e.Number("cargo")} ore): press E to sell");
                }
            }
        }

        private void HandleInput()
        {
            // The server keeps only the newest Steer of a tick, and a tick without one is a released stick: send it at the server's rate.
            _steerTimer += Time.deltaTime;
            if (_steerTimer >= SteerPeriod)
            {
                _steerTimer = 0;
                _link.Steer(Input.GetAxisRaw("Vertical"), Input.GetAxisRaw("Horizontal"));
            }

            if (Input.GetMouseButtonDown(0) && _camera.Camera != null)
            {
                var ray = _camera.Camera.ScreenPointToRay(Input.mousePosition);
                if (Physics.Raycast(ray, out var hit, 2000f) && _views.TryGetAsteroid(hit.collider.gameObject, out var netId))
                {
                    _wantedTarget = netId;
                    _link.Mine(netId);
                    Log($"mining asteroid {netId} — get within {60} m");
                }
            }

            if (Input.GetMouseButtonDown(1) && _wantedTarget != 0)
            {
                _wantedTarget = 0;
                _link.Mine(0);
                Log("beam off");
            }

            if (Input.GetKeyDown(KeyCode.E) && _world.Cargo > 0)
            {
                _link.Sell();
                Log($"sold {_world.Cargo} ore");
            }
        }

        private void MeasureBandwidth()
        {
            _bytesTimer += Time.deltaTime;
            if (_bytesTimer >= 1f)
            {
                _bytesPerSecond = (_world.BytesReceived - _lastBytes) / _bytesTimer;
                _lastBytes = _world.BytesReceived;
                _bytesTimer = 0;
            }
        }

        private void Log(string line)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            if (_log.Count > 6)
            {
                _log.RemoveAt(0);
            }
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 460, 400), GUI.skin.box);
            GUILayout.Label("<b>Asteroid Field</b> — a Typhon server, mirrored in Unity", Rich());
            GUILayout.Label(_link?.LastError ?? _status);
            if (_world != null)
            {
                GUILayout.Label($"server tick {_world.Tick}   render delay {(_clock != null ? _clock.RenderDelayMs : 0):F0} ms   frames {_world.Frames}");
                GUILayout.Label($"in view: {_world.Asteroids.Count} asteroids, {_world.Ships.Count} ships   ·   {_bytesPerSecond / 1024:F1} KiB/s received");
                GUILayout.Label($"hold {_world.Cargo} / {HoldCapacity}   credits {_world.Credits}" +
                                (_wantedTarget != 0 ? $"   mining {_wantedTarget}" : ""));
            }

            GUILayout.Space(6);
            GUILayout.Label("W/S thrust · A/D turn · left click an asteroid: mine · right click: stop · E: sell");
            foreach (var line in _log)
            {
                GUILayout.Label(line);
            }

            GUILayout.EndArea();
        }

        private static GUIStyle Rich() => new GUIStyle(GUI.skin.label) { richText = true };

        // A player build reads `-host <name> -port <n>`; the Editor uses the inspector's values.
        private void ReadCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-host")
                {
                    Host = args[i + 1];
                }
                else if (args[i] == "-port" && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                {
                    Port = port;
                }
            }
        }

        private void OnDestroy()
        {
            var link = _link;
            _link = null;
            if (link != null)
            {
                // Waited for briefly: the SDK says goodbye to the server, then its threads end before the Editor reloads the domain.
                link.DisposeAsync().AsTask().Wait(1000);
            }
        }
    }
}
