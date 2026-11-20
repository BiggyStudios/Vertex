using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Rendering;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// Where <see cref="Thumbnails"/> of materials and models are drawn: a scene of its own with a sun, and a material on a
    /// ball or a model turned three-quarters on, seen whole. One is drawn a frame, at twice the size it's shown (it isn't
    /// anti-aliased), with nothing behind it, and read back once the GPU has drawn it.
    /// </summary>
    internal sealed class ThumbnailStage
    {
        private readonly Scene _scene = new();
        private readonly Camera _camera;
        private readonly GameObject _ball;
        private readonly MeshRenderer _ballRenderer;
        private readonly RenderTexture _target = new(Thumbnails.Size * 2, Thumbnails.Size * 2);
        private GameObject? _model;

        // From the front, a little to the right and above, as things are usually shown.
        private static readonly Vector3 _angles = new(-28f, 30f, 0f);

        public ThumbnailStage()
        {
            Lighting lighting = _scene.Lighting;
            lighting.AmbientIntensity = 0.75f;
            lighting.CloudCover = 0.2f;
            lighting.FogDensity = 0f;
            lighting.Bloom = 0.5f;

            // From above and to the left of the camera, so a shine sits up on the left of a ball.
            var sun = new GameObject("Sun", _scene);
            sun.Transform.EulerAngles = new Vector3(-45f, -10f, 0f);
            Light light = sun.AddComponent<Light>();
            light.Intensity = 2.2f;
            light.CastShadows = false;

            _ball = new GameObject("Ball", _scene);
            _ballRenderer = _ball.AddComponent<MeshRenderer>();
            _ballRenderer.Mesh = Primitives.Sphere;
            _ballRenderer.CastShadows = false;

            var camera = new GameObject("Camera", _scene);
            _camera = camera.AddComponent<Camera>();
            _camera.FieldOfView = 30f;
            _camera.ClearColor = new Color(0f, 0f, 0f, 0f);
            _scene.ApplyChanges();
        }

        /// <summary>The next thumbnail for the renderer to draw this frame, or null if there's none waiting.</summary>
        public RenderView? Next()
        {
            ClearModel();
            while (Thumbnails.NextToDraw() is { } request)
            {
                try
                {
                    int settings = request.Kind == AssetKind.Material ? ShowMaterial(request.Path) : ShowModel(request.Path);
                    int width = _target.Width, height = _target.Height;
                    return new RenderView(_camera, _target, Scene: _scene, Transparent: true,
                        ReadBack: pixels => Thumbnails.Drawn(request, pixels, width, height, settings));
                }
                catch (Exception)
                {
                    ClearModel();
                    Thumbnails.Failed(request);
                }
            }

            return null;
        }

        private int ShowMaterial(string path)
        {
            Material material = Assets.LoadMaterial(path);
            _ball.Active = true;
            _ballRenderer.Material = material;
            _scene.ApplyChanges();
            Frame(_ballRenderer.Mesh!.BoundsCentre, _ballRenderer.Mesh.BoundsExtents.X, fill: 0.92f);
            return Thumbnails.SettingsOf(material);
        }

        private int ShowModel(string path)
        {
            _ball.Active = false;

            // Loaded into this scene, not the one being edited.
            Scene active = Scene.Active;
            Scene.Active = _scene;
            try
            {
                _model = Model.Load(path);
            }
            finally
            {
                Scene.Active = active;
            }

            _scene.ApplyChanges();

            // Seen whole: everything it draws, in a ball the camera's view fits round.
            bool any = false;
            Vector3 low = default, high = default;
            foreach (MeshRenderer renderer in ModelRenderers())
            {
                if (renderer.Mesh is not { } mesh) continue;
                Culling.WorldBounds(mesh, renderer.Transform.WorldMatrix, out Vector3 centre, out Vector3 extents);
                low = any ? Vector3.Min(low, centre - extents) : centre - extents;
                high = any ? Vector3.Max(high, centre + extents) : centre + extents;
                any = true;
            }

            if (!any) throw new InvalidDataException("The model has nothing to draw.");
            Frame((low + high) * 0.5f, MathF.Max((high - low).Length() * 0.5f, 1e-3f), fill: 0.95f);
            return 0;
        }

        // Everything the model draws: the stage has nothing else but the ball.
        private IEnumerable<MeshRenderer> ModelRenderers() => _scene.FindComponents<MeshRenderer>().Where(r => r != _ballRenderer).ToList();

        /// <summary>Puts the camera where a ball round <paramref name="centre"/> fills <paramref name="fill"/> of the picture.</summary>
        private void Frame(Vector3 centre, float radius, float fill)
        {
            Transform eye = _camera.Transform;
            eye.EulerAngles = _angles;
            float distance = radius / MathF.Sin(_camera.FieldOfView * 0.5f * MathF.PI / 180f) / fill;
            eye.Position = centre - eye.Forward * distance;
            _camera.NearPlane = MathF.Max(distance - radius * 1.5f, distance * 0.01f);
            _camera.FarPlane = distance + radius * 1.5f;
        }

        /// <summary>
        /// Takes away the last model drawn, and frees its meshes and textures, which were loaded for it alone. Freed once
        /// the GPU's done with them.
        /// </summary>
        private void ClearModel()
        {
            if (_model == null) return;

            var textures = new HashSet<Texture>();
            foreach (MeshRenderer renderer in ModelRenderers())
            {
                renderer.Mesh?.Dispose();
                if (renderer.Material.Texture is { } texture) textures.Add(texture);
            }

            foreach (Texture texture in textures) texture.Dispose();
            _model.Destroy();
            _scene.ApplyChanges();
            _model = null;
        }
    }
}
