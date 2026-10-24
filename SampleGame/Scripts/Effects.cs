using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>Destroys its GameObject after <see cref="Seconds"/>.</summary>
    public class Lifetime : Component
    {
        public float Seconds { get; set; } = 1f;

        protected override void Update()
        {
            Seconds -= Time.DeltaTime;
            if (Seconds <= 0f) GameObject.Destroy();
        }
    }

    /// <summary>Quick visual effects for shots.</summary>
    public static class Effects
    {
        private static readonly Color _tracerColor = new(1f, 0.85f, 0.4f);
        private static readonly Color _sparkColor = new(1f, 0.6f, 0.2f);

        /// <summary>A bright streak along a bullet's path, gone after a couple of frames.</summary>
        public static void Tracer(Vector3 from, Vector3 to)
        {
            float length = Vector3.Distance(from, to);
            if (length < 0.05f) return;

            var tracer = new GameObject("Tracer");
            tracer.Transform.Position = (from + to) * 0.5f;
            tracer.Transform.LookAt(to);
            tracer.Transform.Scale = new Vector3(0.012f, 0.012f, length);
            AddGlow(tracer, _tracerColor);
            tracer.AddComponent<Lifetime>().Seconds = 0.05f;
        }

        /// <summary>A small spark where a bullet hit something.</summary>
        public static void Impact(Vector3 point)
        {
            var spark = new GameObject("Impact");
            spark.Transform.Position = point;
            spark.Transform.Scale = new Vector3(0.08f);
            AddGlow(spark, _sparkColor);
            spark.AddComponent<Lifetime>().Seconds = 0.08f;
        }

        private static void AddGlow(GameObject gameObject, Color color)
        {
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Cube;
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = color;
        }
    }
}
