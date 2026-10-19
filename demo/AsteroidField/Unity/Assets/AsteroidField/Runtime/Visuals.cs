using UnityEngine;

namespace AsteroidField.Unity
{
    /// <summary>
    /// Lit materials, copied from the default material Unity gives a primitive: it is the pipeline's own (built-in Standard, or URP Lit), and a player
    /// build always includes it — a shader looked up by name may be stripped from one.
    /// </summary>
    public static class Materials
    {
        private static Material _template;

        /// <param name="color">The surface colour.</param>
        /// <param name="glow">How much it glows on its own, 0–1: a beam or a burst should read in the dark, a rock should not.</param>
        public static Material Make(Color color, float glow = 0f)
        {
            if (_template == null)
            {
                var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _template = probe.GetComponent<Renderer>().sharedMaterial;
                Object.Destroy(probe);
            }

            var material = new Material(_template);
            Tint(material, color);
            if (glow > 0f && material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * glow);
            }

            return material;
        }

        /// <summary>Sets the surface colour under both names: built-in shaders read <c>_Color</c>, URP's <c>_BaseColor</c>.</summary>
        public static void Tint(Material material, Color color)
        {
            material.color = color;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
        }
    }

    /// <summary>A sun for the belt, unless the scene brings its own light, and a dim fill so the shadowed sides are not black.</summary>
    public static class Lighting
    {
        public static void Ensure()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.18f, 0.2f, 0.26f);
            if (Object.FindAnyObjectByType<Light>() != null)
            {
                return;
            }

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.3f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
        }
    }

    /// <summary>A ship: a flat arrowhead pointing along +Z, built once.</summary>
    public static class ShipMesh
    {
        private static Mesh _mesh;

        public static Mesh Get()
        {
            if (_mesh != null)
            {
                return _mesh;
            }

            _mesh = new Mesh { name = "ship" };
            _mesh.vertices = new[]
            {
                new Vector3(0, 0, 6), new Vector3(-4, 0, -4), new Vector3(0, 1.5f, -2), new Vector3(4, 0, -4), new Vector3(0, -0.8f, -2),
            };
            _mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 4, 1, 0, 3, 4, 1, 4, 2, 2, 4, 3 };
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            return _mesh;
        }
    }

    /// <summary>
    /// The camera: above and behind this pilot's ship, looking down at it at a steep angle — the belt is a plane, and a grazing view piles it onto the
    /// horizon. The mouse wheel zooms from a close three-quarter view to a nearly top-down one over the belt; dragging with the right button
    /// orbits it around the ship. The orbit is fixed to the world, not to the ship's heading: WASD steers relative to the camera, and a camera that
    /// turned with the ship would turn "left" with it, for ever.
    /// </summary>
    public sealed class ChaseCamera
    {
        private const float MinDistance = 60f;
        private const float MaxDistance = 1500f;
        private const float NearPitch = 50f;
        private const float FarPitch = 80f;

        private const float OrbitDegreesPerPixel = 0.08f;
        private const float MinPitch = 8f;
        private const float MaxPitch = 85f;

        private readonly Transform _transform;
        private float _distance = 220f;
        private float _yaw;
        private bool _placed;
        private float _pitchOffset;
        private bool _orbiting;

        private ChaseCamera(Camera camera)
        {
            Camera = camera;
            _transform = camera.transform;
        }

        public Camera Camera { get; }

        /// <summary>The camera's forward and right on the belt's plane: what "up" and "right" on screen mean to a pilot.</summary>
        public Vector3 PlanarForward => Quaternion.AngleAxis(_yaw, Vector3.up) * Vector3.forward;

        public Vector3 PlanarRight => Quaternion.AngleAxis(_yaw, Vector3.up) * Vector3.right;

        /// <summary>How far from the ship the view reaches on the belt, in metres: the half-width of the picture at the ship's distance.</summary>
        public float GroundReachM => _distance * Mathf.Tan(Camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * Mathf.Max(Camera.aspect, 1f);

        public static ChaseCamera Create()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                camera = new GameObject("Main Camera").AddComponent<Camera>();
                camera.tag = "MainCamera";
            }

            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.02f, 0.05f);
            camera.fieldOfView = 50f;
            camera.nearClipPlane = 1f;
            camera.farClipPlane = 8000f;
            camera.transform.position = new Vector3(0, 200, -120);
            return new ChaseCamera(camera);
        }

        /// <summary>Turns the orbit by a mouse movement in pixels: right/left around the ship, up/down over it.</summary>
        public void Orbit(Vector2 pixels)
        {
            _orbiting = true;
            _yaw += pixels.x * OrbitDegreesPerPixel;
            _pitchOffset -= pixels.y * OrbitDegreesPerPixel;
        }

        public void Follow(Transform ship, float dt)
        {
            _distance = Mathf.Clamp(_distance * (1f - (Input.mouseScrollDelta.y * 0.1f)), MinDistance, MaxDistance);
            var basePitch = Mathf.Lerp(NearPitch, FarPitch, Mathf.InverseLerp(MinDistance, MaxDistance, _distance));
            _pitchOffset = Mathf.Clamp(basePitch + _pitchOffset, MinPitch, MaxPitch) - basePitch;
            var pitch = (basePitch + _pitchOffset) * Mathf.Deg2Rad;

            // The orbit starts behind the ship, then holds its world yaw until dragged.
            if (!_placed)
            {
                _yaw = ship.eulerAngles.y;
                _placed = true;
            }

            var back = Quaternion.AngleAxis(_yaw, Vector3.up) * Vector3.back;
            var wanted = ship.position + (back * (_distance * Mathf.Cos(pitch))) + (Vector3.up * (_distance * Mathf.Sin(pitch)));

            // A drag answers at once; following the ship eases.
            var ease = Mathf.Clamp01(dt * (_orbiting ? 20f : 4f));
            _orbiting = false;
            _transform.position = Vector3.Lerp(_transform.position, wanted, ease);
            // The camera's own up, exact at every tilt: world up blended toward the ship's side as the view turns downward. World up alone is nearly
            // parallel to the view near top-down, and the roll it then picks swings with every pixel of motion.
            var up = (Vector3.up * Mathf.Cos(pitch)) - (back * Mathf.Sin(pitch));
            _transform.rotation = Quaternion.Slerp(_transform.rotation, Quaternion.LookRotation(ship.position - _transform.position, up), ease);
        }
    }

    /// <summary>The burst an asteroid leaves when it is mined out: a sphere that swells and goes.</summary>
    public sealed class Effects : MonoBehaviour
    {
        private float _age;
        private float _size;

        public static void Burst(Vector3 at, int size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(go.GetComponent<Collider>());
            go.transform.position = at;
            go.GetComponent<Renderer>().material = Materials.Make(new Color(1f, 0.7f, 0.3f), glow: 1.5f);
            var effect = go.AddComponent<Effects>();
            effect._size = 6f + (size * 6f);
        }

        private void Update()
        {
            _age += Time.deltaTime;
            transform.localScale = Vector3.one * (_size * _age / 0.6f);
            if (_age >= 0.6f)
            {
                Destroy(gameObject);
            }
        }
    }
}
