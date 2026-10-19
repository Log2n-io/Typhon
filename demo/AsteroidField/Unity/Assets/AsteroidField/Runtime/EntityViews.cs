using System.Collections.Generic;
using AsteroidField.Client;
using UnityEngine;

namespace AsteroidField.Unity
{
    /// <summary>
    /// One GameObject per entity the snapshot holds: created when an entity enters this pilot's view, moved along its motion segment every frame,
    /// destroyed when it leaves. The belt's (X, Y) plane is Unity's (x, z).
    /// </summary>
    public sealed class EntityViews
    {
        // How fast the drawn position catches up with the evaluated one after a correction: it eases in rather than snaps.
        private const float Smoothing = 12f;


        private readonly Transform _root;
        private readonly Dictionary<uint, View> _asteroids = new Dictionary<uint, View>();
        private readonly Dictionary<uint, View> _ships = new Dictionary<uint, View>();
        private readonly Dictionary<GameObject, uint> _netIdOf = new Dictionary<GameObject, uint>();
        private readonly HashSet<uint> _seen = new HashSet<uint>();
        private readonly List<uint> _gone = new List<uint>();
        private uint _selfNetId;

        public EntityViews(Transform root) => _root = root;

        public static Vector3 ToWorld(double x, double y) => new Vector3((float)x, 0f, (float)y);

        // The belt is a plane on the server; a client may give it some thickness. Seeded by the netId, so every client lifts a rock alike.
        private static float HeightOf(uint netId) => ((netId * 2654435761u) % 4001u / 4000f * 40f) - 20f;

        public bool TryGetShip(uint netId, out Transform ship)
        {
            ship = _ships.TryGetValue(netId, out var v) ? v.Transform : null;
            return ship != null;
        }

        public bool TryGetAsteroid(GameObject hit, out uint netId) => _netIdOf.TryGetValue(hit, out netId);

        public void Sync(BeltSnapshot world, long renderTick, double frac, float dt)
        {
            _selfNetId = world.SelfNetId;
            var ease = 1f - Mathf.Exp(-Smoothing * dt);
            SyncAsteroids(world, renderTick, frac, dt, ease);
            SyncShips(world, renderTick, frac, ease);
        }

        private void SyncAsteroids(BeltSnapshot world, long renderTick, double frac, float dt, float ease)
        {
            _seen.Clear();
            foreach (var a in world.Asteroids)
            {
                _seen.Add(a.NetId);
                world.PositionAt(in a, renderTick, frac, out var x, out var y);
                if (!_asteroids.TryGetValue(a.NetId, out var view))
                {
                    view = NewAsteroid(a, x, y);
                    _asteroids.Add(a.NetId, view);
                    _netIdOf[view.Transform.gameObject] = a.NetId;
                }

                view.MoveTowards(ToWorld(x, y) + (Vector3.up * view.Height), ease);
                var full = Mathf.Clamp01(a.Ore / (40f * a.Size * a.Size));
                view.Resize(full, dt);

                // Spin is the client's own flourish (the server does not simulate it), seeded by the netId so every client spins a rock alike.
                view.Transform.Rotate(view.Spin * dt, Space.Self);

                // Brighter while ore remains; dark once nearly mined out. Re-tinted only when the ore changes.
                if (a.Ore != view.TintedOre)
                {
                    view.TintedOre = a.Ore;
                    Materials.Tint(view.Renderer.sharedMaterial, Color.Lerp(new Color(0.25f, 0.22f, 0.2f), new Color(0.95f, 0.6f, 0.25f), full));
                }
            }

            Remove(_asteroids, true);
        }

        private void SyncShips(BeltSnapshot world, long renderTick, double frac, float ease)
        {
            _seen.Clear();
            foreach (var s in world.Ships)
            {
                _seen.Add(s.NetId);
                world.PositionAt(in s, renderTick, frac, out var x, out var y);
                if (!_ships.TryGetValue(s.NetId, out var view))
                {
                    view = NewShip(s, x, y);
                    _ships.Add(s.NetId, view);
                }

                view.MoveTowards(ToWorld(x, y), ease);
                view.Transform.rotation = Quaternion.Euler(0f, (float)(world.HeadingAt(in s, renderTick) * Mathf.Rad2Deg), 0f);

                // The beam: drawn to the target's view, which is in this pilot's view too or the server would not have resolved it. It is on while a
                // target is set; the server mines only within 60 m.
                if (s.Target != 0 && _asteroids.TryGetValue(s.Target, out var target))
                {
                    view.Beam.enabled = true;
                    view.Beam.SetPosition(0, view.Transform.position);
                    view.Beam.SetPosition(1, target.Transform.position);
                }
                else
                {
                    view.Beam.enabled = false;
                }
            }

            Remove(_ships, false);
        }

        private void Remove(Dictionary<uint, View> views, bool asteroids)
        {
            _gone.Clear();
            foreach (var pair in views)
            {
                if (!_seen.Contains(pair.Key))
                {
                    _gone.Add(pair.Key);
                }
            }

            foreach (var netId in _gone)
            {
                var view = views[netId];
                if (asteroids)
                {
                    _netIdOf.Remove(view.Transform.gameObject);
                }

                // Each view owns its materials: destroying the object does not free them.
                Object.Destroy(view.Renderer.sharedMaterial);
                if (view.Beam != null)
                {
                    Object.Destroy(view.Beam.sharedMaterial);
                }

                Object.Destroy(view.Transform.gameObject);
                views.Remove(netId);
            }
        }

        private View NewAsteroid(EntityView a, double x, double y)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"asteroid {a.NetId}";
            go.transform.SetParent(_root, false);
            var random = new System.Random((int)a.NetId);
            var size = 3f + (a.Size * 3.5f);
            var scale = new Vector3(size * Range(random, 0.7f, 1.3f), size * Range(random, 0.6f, 1.0f), size * Range(random, 0.7f, 1.3f));
            go.transform.rotation = Random.rotationUniform;
            var view = new View(go.transform, go.GetComponent<Renderer>(), null)
            {
                Spin = new Vector3(Range(random, -30, 30), Range(random, -30, 30), Range(random, -30, 30)),
                Height = HeightOf(a.NetId),
                Scale = scale,
                TintedOre = -1,
            };
            view.Renderer.sharedMaterial = Materials.Make(new Color(0.6f, 0.5f, 0.4f));
            view.Transform.position = ToWorld(x, y) + (Vector3.up * view.Height);
            view.Resize(Mathf.Clamp01(a.Ore / (40f * a.Size * a.Size)), 0f);
            return view;
        }

        private View NewShip(EntityView s, double x, double y)
        {
            var go = new GameObject($"ship {s.NetId}");
            go.transform.SetParent(_root, false);
            go.AddComponent<MeshFilter>().sharedMesh = ShipMesh.Get();
            var renderer = go.AddComponent<MeshRenderer>();
            var color = s.NetId == _selfNetId ? new Color(0.3f, 0.9f, 1f) : s.Drone ? new Color(1f, 0.85f, 0.3f) : new Color(0.4f, 1f, 0.45f);
            renderer.sharedMaterial = Materials.Make(color, glow: 0.4f);

            var beam = new GameObject("beam").AddComponent<LineRenderer>();
            beam.transform.SetParent(go.transform, false);
            beam.useWorldSpace = true;
            beam.positionCount = 2;
            beam.startWidth = 0.6f;
            beam.endWidth = 0.2f;
            beam.sharedMaterial = Materials.Make(new Color(1f, 0.35f, 0.2f), glow: 2f);
            beam.enabled = false;

            var view = new View(go.transform, renderer, beam);
            view.Transform.position = ToWorld(x, y);
            return view;
        }

        private static float Range(System.Random random, float min, float max) => min + ((float)random.NextDouble() * (max - min));

        private sealed class View
        {
            public View(Transform transform, Renderer renderer, LineRenderer beam)
            {
                Transform = transform;
                Renderer = renderer;
                Beam = beam;
            }

            public Transform Transform { get; }

            public Renderer Renderer { get; }

            public LineRenderer Beam { get; }

            public Vector3 Spin { get; set; }

            public float Height { get; set; }

            public Vector3 Scale { get; set; }

            public int TintedOre { get; set; }

            private const float GrowSeconds = 0.4f;
            private const float ShrinkSharpness = 6f;
            private const float SmallestFraction = 0.25f;

            private float _age;
            private float _fullness = -1f;

            // A rock's size follows its ore: its volume is what is left of it, so its size goes as the cube root, down to a quarter before it bursts.
            // One entering the view also grows in over a fraction of a second, so the edge of the view does not pop.
            public void Resize(float fullness, float dt)
            {
                _age += dt;
                _fullness = _fullness < 0f ? fullness : Mathf.Lerp(_fullness, fullness, 1f - Mathf.Exp(-ShrinkSharpness * dt));
                var grown = _age >= GrowSeconds ? 1f : Mathf.SmoothStep(0f, 1f, _age / GrowSeconds);
                Transform.localScale = Scale * (grown * Mathf.Max(SmallestFraction, Mathf.Pow(_fullness, 1f / 3f)));
            }

            // Eases toward the target; a jump of more than a few metres (a teleport, a wrap at the belt's edge) is taken at once.
            public void MoveTowards(Vector3 target, float t)
            {
                var current = Transform.position;
                Transform.position = (target - current).sqrMagnitude > 25f * 25f ? target : Vector3.Lerp(current, target, Mathf.Clamp01(t));
            }
        }
    }
}
