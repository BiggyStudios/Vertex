using System.Numerics;

using Vertex.Rendering;
using Vertex.Rendering.Shaders;

namespace Vertex.Tests
{
    /// <summary>Point and spot lights' shadows: which lights get them, and only drawing them again when something's changed.</summary>
    public class LocalShadowTests : EngineTest
    {
        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;

        public LocalShadowTests()
        {
            _renderer = new Renderer(_backend);
            _renderer.Resize(640, 360);
            var camera = new GameObject("Camera");
            camera.Transform.Position = new Vector3(0f, 3f, 10f);
            camera.Transform.LookAt(Vector3.Zero);
            camera.AddComponent<Camera>();
        }

        private static Light AddLight(LightType type, Vector3 at, LightMode mode = LightMode.Realtime)
        {
            var gameObject = new GameObject("Light");
            gameObject.Transform.Position = at;
            gameObject.Transform.LookAt(Vector3.Zero);
            Light light = gameObject.AddComponent<Light>();
            light.Type = type;
            light.Range = 10f;
            light.Mode = mode;
            return light;
        }

        private RenderStats Render()
        {
            Scene.Active.Update();
            _renderer.Render(Scene.Active);
            return _renderer.Stats;
        }

        [Fact]
        public void A_lights_shadow_is_drawn_once_and_again_only_when_something_near_it_moves()
        {
            GameObject.CreateCube(Vector3.Zero, Color.White, "Box");
            AddLight(LightType.Spot, new Vector3(0f, 4f, 0f));
            AddLight(LightType.Point, new Vector3(3f, 2f, 0f));

            // The spot light one layer, the point light six.
            Assert.Equal(7, Render().LightShadowLayers);
            Assert.Equal(0, Render().LightShadowLayers);
            Assert.Equal(0, Render().LightShadowLayers);

            // The box moves: both see it, and draw theirs again.
            Scene.Active.Find("Box")!.Transform.Position = new Vector3(0.5f, 0f, 0f);
            Assert.Equal(7, Render().LightShadowLayers);
            Assert.Equal(0, Render().LightShadowLayers);

            // Something far from both doesn't matter to them.
            GameObject.CreateCube(new Vector3(40f, 0f, 0f), Color.White, "Far");
            Assert.Equal(0, Render().LightShadowLayers);
        }

        [Fact]
        public void Baked_lights_and_those_without_shadows_dont_get_any_and_the_shaders_know_which_light_has_which()
        {
            GameObject.CreateCube(Vector3.Zero, Color.White, "Box");
            AddLight(LightType.Point, new Vector3(3f, 2f, 0f)).CastShadows = false;
            AddLight(LightType.Spot, new Vector3(0f, 4f, 0f));
            Render();

            Assert.Single(_backend.LastLocalShadows);
            Assert.False(_backend.LastLocalShadows[0].Point);
            int spot = _backend.LastPointLights.FindIndex(l => l.IsSpot);
            uint word = _backend.LastUniforms.LightShadows[spot / 4];
            Assert.Equal(1u, (word >> (spot % 4 * 8)) & 255u);
            uint other = _backend.LastUniforms.LightShadows[(1 - spot) / 4];
            Assert.Equal(0u, (other >> ((1 - spot) % 4 * 8)) & 255u);
        }

        [Fact]
        public void The_nearest_lights_get_shadows_when_there_are_more_than_a_view_takes()
        {
            GameObject.CreateCube(Vector3.Zero, Color.White, "Box");
            for (int i = 0; i < LocalShadows.MaxPerView + 4; i++) AddLight(LightType.Spot, new Vector3(i * 0.5f - 5f, 4f, -i * 2f));
            Render();
            Assert.Equal(LocalShadows.MaxPerView, _backend.LastLocalShadows.Count);
        }

        [Fact]
        public void A_point_lights_faces_are_found_in_the_shaders_as_they_were_drawn()
        {
            // The shaders don't keep a point light's six matrices: they work out where a point is on its face themselves.
            Vector3 light = new(1f, 2f, 3f);
            const float near = 0.1f, far = 10f;
            var random = new Random(3);
            for (int i = 0; i < 200; i++)
            {
                Vector3 offset = new Vector3(random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f) * 5f;
                Vector3 a = Vector3.Abs(offset);
                int face = a.X >= a.Y && a.X >= a.Z ? (offset.X > 0 ? 0 : 1) : a.Y >= a.Z ? (offset.Y > 0 ? 2 : 3) : (offset.Z > 0 ? 4 : 5);

                Vector4 clip = Vector4.Transform(new Vector4(light + offset, 1f), LocalShadows.FaceMatrix(light, face, near, far));
                Vector3 expected = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;

                // As localShadow in the shaders works it out.
                (Vector3 forward, Vector3 up) = LocalShadows.Faces[face];
                Vector3 across = Vector3.Normalize(Vector3.Cross(up, -forward));
                Vector3 above = Vector3.Cross(-forward, across);
                float d = Vector3.Dot(offset, forward);
                var worked = new Vector3(Vector3.Dot(offset, across) / d / LocalShadows.FaceTangent, Vector3.Dot(offset, above) / d / LocalShadows.FaceTangent,
                    far * (d - near) / ((far - near) * d));

                Assert.True(Vector3.Distance(expected, worked) < 1e-3f, $"{offset}: {expected} vs {worked}");
                Assert.True(MathF.Abs(worked.X) <= 1f && MathF.Abs(worked.Y) <= 1f);
            }

            Assert.Contains($"LOCAL_FACE_SCALE = {1f / LocalShadows.FaceTangent:0.00000};", ShaderLibrary.Frame);
            Assert.Contains($"LOCAL_SHADOW_SIZE = {LocalShadows.Resolution}.0;", ShaderLibrary.Frame);
            Assert.Contains($"localShadowParams[{LocalShadows.MaxPerView}]", ShaderLibrary.Frame);
            Assert.True(System.Runtime.CompilerServices.Unsafe.SizeOf<FrameUniforms>() <= 16384);
        }
    }
}
