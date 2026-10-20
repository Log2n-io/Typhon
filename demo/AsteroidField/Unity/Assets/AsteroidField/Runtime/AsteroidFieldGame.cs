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
    /// <para>Built from code on scene load (<see cref="Boot"/>), so any scene — even an empty one — runs the demo. Controls: WASD flies relative to the camera, left
    /// click an asteroid to mine it, right click to stop, E to sell your hold.</para>
    /// </remarks>
    public sealed class AsteroidFieldGame : MonoBehaviour
    {
        public string Host = "127.0.0.1";
        public int Port = 9300;

        private const int HoldCapacity = 300;
        private const float SteerPeriod = 0.04f;
        private const float RadiusRefreshSeconds = 2f;
        private const float MinViewRadiusM = 300f;
        private const float MaxViewRadiusM = 1200f;
        private const float RadiusPeriod = 0.25f;
        private const float AimSettleSeconds = 1f;

        private BeltLink _link;
        private Clock _clock;
        private BeltSnapshot _world;
        private EntityViews _views;
        private ChaseCamera _camera;
        private readonly List<FrameArrival> _arrivals = new List<FrameArrival>();
        private readonly List<ReceivedEvent> _events = new List<ReceivedEvent>();
        private readonly List<string> _log = new List<string>();
        private float _steerTimer;
        private float _radiusTimer;
        private float _radiusAge;
        private double _farthestHeld;
        private float _viewRadius = MinViewRadiusM;
        private Vector2 _rightLastAt;
        private uint _wantedTarget;
        private float _aimedAt = float.NegativeInfinity;
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
            Lighting.Ensure();
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
                // The views copied what they draw out of the previous one: it goes back to the link to be refilled.
                _link.Recycle(_world);
                _world = snapshot;
            }

            if (_world == null)
            {
                return;
            }

            AdvanceClock();
            _views.Sync(_world, _clock.RenderTick, _clock.RenderFrac, Time.deltaTime);
            HandleEvents();
            HandleInput();
            if (_views.TryGetShip(_world.SelfNetId, out var ship))
            {
                _camera.Follow(ship, Time.deltaTime);
            }

            FollowZoomWithTheView();
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
            // Steer goes 25 times a second, above the server's 20 Hz tick and under the command's rate of 30: every tick finds one. The timer keeps its
            // remainder, so a frame rate that does not divide the period still sends at the period.
            _steerTimer += Time.deltaTime;
            if (_steerTimer >= SteerPeriod)
            {
                _steerTimer = _steerTimer >= 2f * SteerPeriod ? 0f : _steerTimer - SteerPeriod;
                SteerTowardsInput();
            }

            // Left click toggles the beam: off wherever you click while it is on; on, at the asteroid clicked, while it is off.
            if (Input.GetMouseButtonDown(0) && _camera.Camera != null)
            {
                if (CurrentTarget() != 0)
                {
                    Aim(0);
                    Log("beam off");
                }
                else if (Physics.Raycast(_camera.Camera.ScreenPointToRay(Input.mousePosition), out var hit, _camera.Camera.farClipPlane)
                         && _views.TryGetAsteroid(hit.collider.gameObject, out var netId))
                {
                    Aim(netId);
                    Log($"beam on asteroid {netId} — it mines within {60} m");
                }
            }

            // The right button orbits the camera.
            if (Input.GetMouseButtonDown(1))
            {
                _rightLastAt = Input.mousePosition;
            }
            else if (Input.GetMouseButton(1))
            {
                var at = (Vector2)Input.mousePosition;
                _camera.Orbit(at - _rightLastAt);
                _rightLastAt = at;
            }

            if (Input.GetKeyDown(KeyCode.E) && _world.Cargo > 0)
            {
                _link.Sell();
                Log($"sold {_world.Cargo} ore");
            }
        }

        // WASD is relative to the camera: the pressed direction on screen becomes a heading on the belt. The server flies a ship by thrust and turn, so
        // the ship turns toward that heading and thrusts once it faces it roughly — pressing S turns it around rather than reversing.
        private void SteerTowardsInput()
        {
            var input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            if (input.sqrMagnitude < 0.01f || !_world.TryGetShip(_world.SelfNetId, out var self))
            {
                _link.Steer(0f, 0f);
                return;
            }

            var wanted = (_camera.PlanarRight * input.x) + (_camera.PlanarForward * input.y);
            var desired = Mathf.Atan2(wanted.x, wanted.z);
            var error = Mathf.DeltaAngle((float)self.Heading * Mathf.Rad2Deg, desired * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            var turn = Mathf.Clamp(error * 2f, -1f, 1f);
            var facing = Mathf.Cos(error);
            var thrust = facing > 0.5f ? Mathf.Clamp01(input.magnitude) * facing : 0f;
            _link.Steer(thrust, turn);
        }

        // The server sends what is within the view's radius; the camera decides what is on screen. Zooming out asks for a wider radius, so the belt
        // does not end at the edge of the old one — in steps of 50 m, at most four times a second, which is what the command's rate allows. Sent again
        // every two seconds: a session that reconnected starts at the profile's own radius, and a command sent while disconnected is lost.
        private void FollowZoomWithTheView()
        {
            _radiusTimer -= Time.deltaTime;
            _radiusAge += Time.deltaTime;
            // Rounded up, so the radius covers the picture; a corner above the horizon asks for the most there is.
            var reach = _camera.GroundReachM;
            var wanted = float.IsInfinity(reach) ? MaxViewRadiusM : Mathf.Clamp(Mathf.Ceil(reach / 50f) * 50f, MinViewRadiusM, MaxViewRadiusM);
            if (_radiusTimer > 0f || (Mathf.Approximately(wanted, _viewRadius) && _radiusAge < RadiusRefreshSeconds))
            {
                return;
            }

            _radiusTimer = RadiusPeriod;
            _radiusAge = 0f;
            _viewRadius = wanted;
            _link.ViewRadius(wanted);
        }

        private void Aim(uint asteroid)
        {
            _wantedTarget = asteroid;
            _aimedAt = Time.time;
            _link.Mine(asteroid);
        }

        // The asteroid being mined: the one just clicked while the server's answer is on its way, then the server's own word — it clears the target
        // when the rock is mined out, the hold fills, or the rock leaves the view.
        private uint CurrentTarget()
        {
            if (Time.time - _aimedAt < AimSettleSeconds)
            {
                return _wantedTarget;
            }

            return _world.TryGetShip(_world.SelfNetId, out var self) ? self.Target : 0u;
        }

        private void MeasureBandwidth()
        {
            _bytesTimer += Time.deltaTime;
            if (_bytesTimer >= 1f)
            {
                _bytesPerSecond = (_world.BytesReceived - _lastBytes) / _bytesTimer;
                _lastBytes = _world.BytesReceived;
                _bytesTimer = 0;
                _farthestHeld = FarthestHeld();
            }
        }

        // What the server actually applied, seen from here: the farthest asteroid this client holds from its own ship.
        private double FarthestHeld()
        {
            if (!_world.TryGetShip(_world.SelfNetId, out var self))
            {
                return 0;
            }

            self.PositionAt(_world.Tick, out var sx, out var sy);
            var farthest = 0d;
            foreach (var a in _world.Asteroids)
            {
                a.PositionAt(_world.Tick, out var x, out var y);
                farthest = Math.Max(farthest, ((x - sx) * (x - sx)) + ((y - sy) * (y - sy)));
            }

            return Math.Sqrt(farthest);
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
                GUILayout.Label($"view radius {_viewRadius:F0} m asked, farthest held {_farthestHeld:F0} m   ·   {_bytesPerSecond / 1024:F1} KiB/s received");
                GUILayout.Label($"in view: {_world.Asteroids.Count} asteroids, {_world.Ships.Count} ships");
                GUILayout.Label($"hold {_world.Cargo} / {HoldCapacity}   credits {_world.Credits}" +
                                (CurrentTarget() != 0 ? $"   mining {CurrentTarget()}" : ""));
            }

            GUILayout.Space(6);
            GUILayout.Label("WASD: fly, relative to the camera · left click: beam on (at an asteroid) / off · right drag: orbit · wheel: zoom (and view radius) · E: sell");
            foreach (var line in _log)
            {
                GUILayout.Label(line);
            }

            GUILayout.EndArea();
        }

        private static GUIStyle _rich;

        // Built once: OnGUI runs several times a frame. GUI.skin is only readable inside OnGUI, so not in a field initializer.
        private static GUIStyle Rich() => _rich ??= new GUIStyle(GUI.skin.label) { richText = true };

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
