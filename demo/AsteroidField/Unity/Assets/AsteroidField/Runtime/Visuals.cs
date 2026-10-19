using UnityEngine;

namespace AsteroidField.Unity
{
    /// <summary>Unlit materials, from whichever pipeline the project uses: built-in, or URP.</summary>
    public static class Materials
    {
        private static Shader _shader;

        public static Material Make(Color color)
        {
            if (_shader == null)
            {
                _shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            }

            var material = new Material(_shader) { color = color };
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            return material;
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

    /// <summary>The camera: behind and above this pilot's ship, easing after it.</summary>
    public sealed class ChaseCamera
    {
        private readonly Transform _transform;

        private ChaseCamera(Camera camera)
        {
            Camera = camera;
            _transform = camera.transform;
        }

        public Camera Camera { get; }

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
            camera.farClipPlane = 1500f;
            camera.transform.position = new Vector3(0, 120, -120);
            return new ChaseCamera(camera);
        }

        public void Follow(Transform ship, float dt)
        {
            var wanted = ship.position - (ship.forward * 45f) + (Vector3.up * 30f);
            _transform.position = Vector3.Lerp(_transform.position, wanted, Mathf.Clamp01(dt * 4f));
            _transform.rotation = Quaternion.Slerp(_transform.rotation, Quaternion.LookRotation(ship.position + (ship.forward * 40f) - _transform.position),
                Mathf.Clamp01(dt * 4f));
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
            go.GetComponent<Renderer>().material = Materials.Make(new Color(1f, 0.7f, 0.3f));
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
