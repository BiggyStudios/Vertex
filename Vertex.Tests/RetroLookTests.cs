using System.Numerics;

using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.Tests
{
    /// <summary>The look of early 3D consoles: corners snapped to the screen, textures bent across surfaces, crisp texels.</summary>
    public class RetroLookTests : EngineTest
    {
        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;
        private readonly Camera _camera;

        public RetroLookTests()
        {
            _renderer = new Renderer(_backend);
            _renderer.Resize(1280, 720);
            _camera = new GameObject("Camera").AddComponent<Camera>();
        }

        private void Render()
        {
            Scene.Active.Update();
            _renderer.Render(Scene.Active);
        }

        [Fact]
        public void The_scenes_retro_settings_reach_the_shaders()
        {
            Render();
            Assert.Equal(Vector4.Zero, _backend.LastUniforms.Retro);

            Lighting lighting = Scene.Active.Lighting;
            lighting.VertexSnapping = 240f;
            lighting.AffineTextures = 3f;
            lighting.PixelatedTextures = true;
            Render();

            Assert.Equal(new Vector4(240f, 1f, 1f, 0f), _backend.LastUniforms.Retro);
        }

        [Fact]
        public void An_orthographic_camera_doesnt_snap()
        {
            // Its grid would be the same size whatever the distance, and 2D sprites and text would jump about.
            Scene.Active.Lighting.VertexSnapping = 240f;
            _camera.Projection = CameraProjection.Orthographic;
            Render();

            Assert.Equal(0f, _backend.LastUniforms.Retro.X);
        }

        [Fact]
        public void They_are_saved_with_the_scene()
        {
            Lighting lighting = Scene.Active.Lighting;
            lighting.VertexSnapping = 180f;
            lighting.AffineTextures = 0.6f;
            lighting.PixelatedTextures = true;

            string json = SceneFile.Save(Scene.Active);
            var loaded = new Scene();
            SceneFile.LoadJson(json, loaded);

            Assert.Equal(180f, loaded.Lighting.VertexSnapping);
            Assert.Equal(0.6f, loaded.Lighting.AffineTextures);
            Assert.True(loaded.Lighting.PixelatedTextures);
        }
    }
}
