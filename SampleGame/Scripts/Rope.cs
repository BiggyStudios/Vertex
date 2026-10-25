using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>A glowing grapple rope: a thin line between two points, moved each frame. Hidden until shown.</summary>
    public sealed class Rope
    {
        private const float _thickness = 0.045f;

        private readonly GameObject _line;

        /// <param name="parent">What it belongs to, if anything, so it goes when that does. It's still placed in world space.</param>
        public Rope(string name, Color color, Transform? parent = null)
        {
            _line = new GameObject(name) { Active = false };
            _line.Transform.Parent = parent;
            MeshRenderer renderer = _line.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Cube;
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = color;
            renderer.Material.Emission = color;
            renderer.Material.EmissionStrength = 1.5f;
            renderer.CastShadows = false;
        }

        /// <summary>Stretches the rope from <paramref name="from"/> to <paramref name="to"/> and shows it.</summary>
        public void Show(Vector3 from, Vector3 to)
        {
            Vector3 along = to - from;
            float length = along.Length();
            if (length < 0.01f)
            {
                Hide();
                return;
            }

            // A cube stretched along its Z axis, turned to point down the rope.
            Vector3 direction = along / length;
            Vector3 up = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            _line.Transform.WorldPosition = (from + to) * 0.5f;
            _line.Transform.WorldRotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, direction, up));
            _line.Transform.Scale = new Vector3(_thickness, _thickness, length);
            _line.Active = true;
        }

        public void Hide() => _line.Active = false;

        public void Destroy() => _line.Destroy();
    }
}
