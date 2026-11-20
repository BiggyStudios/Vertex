using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Rendering;

namespace Vertex.Editor.Graphs
{
    /// <summary>What a shader graph's preview shows a surface shader on.</summary>
    public enum PreviewShape
    {
        Sphere,
        Cube,
        Plane
    }

    /// <summary>
    /// A small picture of what a shader graph makes, kept up to date as the graph's edited: a surface shader on a ball
    /// (or a cube or a flat square) in the sun, which can be dragged round; a post effect on a little scene of its own.
    /// It's a scene of its own, drawn into a texture by the renderer along with the editor's other views.
    /// </summary>
    internal sealed class ShaderPreview
    {
        public const int Size = 256;

        private readonly Scene _scene = new();
        private readonly Camera _camera;
        private readonly GameObject _pivot;
        private readonly MeshRenderer _shape;
        private readonly Material _material = new();
        private readonly PostEffect _effect;
        private readonly GameObject _stage;
        private RenderTexture? _texture;
        private Vector2 _angles = new(-20f, 30f);

        public PreviewShape Shape { get; set; }

        public ShaderPreview()
        {
            Lighting lighting = _scene.Lighting;
            lighting.AmbientIntensity = 0.7f;
            lighting.CloudCover = 0.2f;

            var sun = new GameObject("Sun", _scene);
            sun.Transform.EulerAngles = new Vector3(-40f, 35f, 0f);
            sun.AddComponent<Light>().Intensity = 2.2f;

            _pivot = new GameObject("Shape", _scene);
            _shape = _pivot.AddComponent<MeshRenderer>();
            _shape.Material = _material;
            _shape.CastShadows = false;

            // What a post effect is seen on: a floor, a ball and a block, under the sky.
            _stage = new GameObject("Stage", _scene);
            var floor = new GameObject("Floor", _scene);
            floor.Transform.Parent = _stage.Transform;
            floor.Transform.Position = new Vector3(0f, -0.6f, 0f);
            floor.Transform.Scale = new Vector3(12f, 1f, 12f);
            floor.AddComponent<MeshRenderer>(r => { r.Mesh = Primitives.Plane; r.Material = new Material(new Color(0.45f, 0.45f, 0.48f)); });
            var ball = new GameObject("Ball", _scene);
            ball.Transform.Parent = _stage.Transform;
            ball.Transform.Position = new Vector3(-0.6f, -0.1f, 0f);
            ball.AddComponent<MeshRenderer>(r => { r.Mesh = Primitives.Sphere; r.Material = new Material(new Color(0.9f, 0.35f, 0.25f)) { Roughness = 0.3f }; });
            var block = new GameObject("Block", _scene);
            block.Transform.Parent = _stage.Transform;
            block.Transform.Position = new Vector3(0.7f, -0.1f, -0.4f);
            block.Transform.EulerAngles = new Vector3(0f, 30f, 0f);
            block.AddComponent<MeshRenderer>(r => { r.Mesh = Primitives.Cube; r.Material = new Material(new Color(0.3f, 0.55f, 0.9f)); });

            var camera = new GameObject("Camera", _scene);
            _camera = camera.AddComponent<Camera>();
            _camera.FieldOfView = 35f;
            _effect = camera.AddComponent<PostEffect>();
            _scene.ApplyChanges();
        }

        /// <summary>
        /// The view for the renderer to draw <paramref name="shader"/>'s preview into, this frame. Its properties are the
        /// graph's starting values.
        /// </summary>
        public RenderView View(Shader shader)
        {
            _texture ??= new RenderTexture(Size, Size);
            bool surface = !shader.IsPostEffect;

            _pivot.Active = surface;
            _stage.Active = !surface;
            _material.Shader = surface ? shader : null;
            _effect.Shader = surface ? null : shader;
            _effect.Enabled = !surface;

            _shape.Mesh = Shape switch { PreviewShape.Cube => Primitives.Cube, PreviewShape.Plane => Primitives.Plane, _ => Primitives.Sphere };
            if (Shape == PreviewShape.Plane)
            {
                // Flat on, facing the camera.
                _pivot.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
                _pivot.Transform.Scale = new Vector3(1.4f);
            }
            else
            {
                _pivot.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(_angles.Y * MathF.PI / 180f, _angles.X * MathF.PI / 180f, 0f);
                _pivot.Transform.Scale = new Vector3(Shape == PreviewShape.Cube ? 0.85f : 1f);
            }

            Transform eye = _camera.Transform;
            eye.Position = surface ? new Vector3(0f, 0f, 3f) : new Vector3(0f, 1.1f, 4.2f);
            eye.EulerAngles = surface ? Vector3.Zero : new Vector3(-14f, 0f, 0f);
            _scene.ApplyChanges();
            return new RenderView(_camera, _texture, Scene: _scene);
        }

        /// <summary>The picture, in <paramref name="rect"/>: dragging over it turns the shape round.</summary>
        public void Draw(int id, Rect rect, bool surface)
        {
            if (_texture == null) return;
            Interaction interaction = Ui.Interact(id, rect);
            if (interaction.Held && surface && Shape != PreviewShape.Plane)
            {
                _angles.Y += Ui.MouseDelta.X * 0.6f;
                _angles.X = Math.Clamp(_angles.X + Ui.MouseDelta.Y * 0.6f, -89f, 89f);
            }

            Vertex.UI.Canvas.DrawImage(_texture, rect.Position, rect.Size, Vector2.Zero, Vector2.One, Ui.Fade(Color.White), 6f);
            if (surface && Shape != PreviewShape.Plane) Ui.Tooltip(id, "Drag to turn it round");
        }
    }
}
