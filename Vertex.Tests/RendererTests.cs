using System.Numerics;

using Silk.NET.Windowing;

using Vertex.Rendering;

namespace Vertex.Tests
{
    public class RendererTests : EngineTest
    {
        /// <summary>A backend that draws nothing, only notes what each frame it was given held.</summary>
        internal sealed class RecordingBackend : IRenderBackend
        {
            public readonly List<(RenderTexture? Target, bool HasCamera, int Width, int Height, int Draws, int CanvasQuads)> Frames = new();

            public readonly List<GpuParticleUpdate> ParticleUpdates = new();
            public int Begins, Ends;
            public bool GpuParticles;

            /// <summary>Particles in the last frame drawn.</summary>
            public List<ParticleData> LastParticles = new();

            /// <summary>Sprites and world text in the last frame drawn, in the order drawn.</summary>
            public List<WorldQuad> LastWorldQuads = new();

            /// <summary>What the last frame drew, and what it drew into each shadow cascade (empty without shadows).</summary>
            public List<DrawCall> LastDraws = new();
            public List<List<DrawCall>> LastShadowCasters = new();

            /// <summary>The point and spot lights and fog volumes in the last frame, and the shaders' data made from it.</summary>
            public List<PointLightData> LastPointLights = new();
            public List<FogVolumeData> LastFogVolumes = new();
            internal Rendering.Shaders.FrameUniforms LastUniforms;

            public string Name => "Recording";
            public bool SupportsGpuParticles => GpuParticles;
            public WindowOptions ConfigureWindow(WindowOptions options) => options;
            public void Initialise(IWindow window) {}
            public void Resize(int width, int height) {}
            public void Dispose() {}
            public void BeginFrame() => Begins++;
            public void EndFrame() => Ends++;
            public void UpdateParticles(GpuParticleUpdate update) => ParticleUpdates.Add(update);

            // The frame is reused, so copy out what's needed now.
            public void Render(RenderFrame frame)
            {
                Frames.Add((frame.Target, frame.HasCamera, frame.Width, frame.Height, frame.DrawCalls.Count, frame.CanvasQuads.Count));
                LastParticles = frame.Particles.ToList();
                LastWorldQuads = frame.WorldQuads.ToList();
                LastDraws = frame.DrawCalls.ToList();
                LastPointLights = frame.PointLights.ToList();
                LastFogVolumes = frame.FogVolumes.ToList();
                if (frame.HasCamera) LastUniforms = Rendering.Shaders.FrameUniforms.Create(frame, frame.HasShadows ? frame.Cascades : null);
                LastShadowCasters = frame.HasShadows
                    ? Enumerable.Range(0, Rendering.Shaders.ShadowCascades.Count).Select(i => frame.ShadowCasters(i).ToList()).ToList()
                    : new List<List<DrawCall>>();
            }
        }

        private static Camera AddCamera(string name, Vector3 position)
        {
            var gameObject = new GameObject(name);
            gameObject.Transform.Position = position;
            return gameObject.AddComponent<Camera>();
        }

        private static void AddCube()
        {
            var cube = new GameObject("Cube");
            cube.AddComponent<MeshRenderer>().Mesh = Primitives.Cube;
        }

        [Fact]
        public void Cameras_with_targets_draw_first_and_without_the_canvas()
        {
            var backend = new RecordingBackend();
            var renderer = new Renderer(backend);
            renderer.Resize(1280, 720);

            AddCube();
            AddCamera("Player", new Vector3(0f, 0f, 5f));
            var minimap = new RenderTexture(256, 128);
            Camera map = AddCamera("Minimap", new Vector3(0f, 20f, 0f));
            map.Target = minimap;
            map.Transform.EulerAngles = new Vector3(-90f, 0f, 0f);
            Scene.Active.Update();

            // Something on the 2D layer, as a HUD would have.
            UI.Canvas.BeginFrame(1280, 720);
            UI.Canvas.DrawRect(Vector2.Zero, new Vector2(10f), Color.White);
            UI.Canvas.EndFrame();

            renderer.Render(Scene.Active);

            Assert.Equal(2, backend.Frames.Count);
            Assert.Equal((minimap, true, 256, 128, 1, 0), backend.Frames[0]);
            Assert.Equal((null, true, 1280, 720, 1, 1), backend.Frames[1]);
        }

        [Fact]
        public void The_main_camera_is_one_that_draws_to_the_screen()
        {
            AddCamera("Monitor", Vector3.Zero).Target = new RenderTexture(64, 64);
            Camera player = AddCamera("Player", Vector3.Zero);
            Scene.Active.Update();

            Assert.Same(player, Camera.Main);
        }

        [Fact]
        public void The_main_camera_belongs_to_the_active_scene()
        {
            Camera first = AddCamera("First", Vector3.Zero);
            Camera.Main = first;
            Scene.Active.Update();

            var next = new Scene();
            Camera second = new GameObject("Second", next).AddComponent<Camera>();
            Scene.Active = next;
            Scene.Active.Update();

            Assert.Same(second, Camera.Main);
        }

        [Fact]
        public void Without_a_camera_the_screen_is_only_cleared()
        {
            var backend = new RecordingBackend();
            var renderer = new Renderer(backend);
            renderer.Resize(1280, 720);
            AddCube();
            Scene.Active.Update();

            renderer.Render(Scene.Active);

            Assert.Single(backend.Frames);
            Assert.False(backend.Frames[0].HasCamera);
            Assert.Equal(0, backend.Frames[0].Draws);
        }

        [Fact]
        public void Players_can_choose_opengl()
        {
            string? before = Environment.GetEnvironmentVariable("VERTEX_RENDERER");
            Environment.SetEnvironmentVariable("VERTEX_RENDERER", "opengl");
            try
            {
                using IRenderBackend backend = RenderBackends.CreateDefault();
                Assert.Equal("OpenGL", backend.Name);
                Assert.False(backend.SupportsGpuParticles);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VERTEX_RENDERER", before);
            }
        }

        [Fact]
        public void Render_textures_have_no_pixels_to_set()
        {
            var texture = new RenderTexture(4, 4);
            int version = texture.Version;

            Assert.Throws<InvalidOperationException>(() => texture.SetPixels(0, 0, 1, 1, new byte[4]));

            texture.Resize(8, 2);
            Assert.Equal((8, 2), (texture.Width, texture.Height));
            Assert.NotEqual(version, texture.Version);
        }
    }
}
