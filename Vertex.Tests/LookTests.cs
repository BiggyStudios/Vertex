using System.Numerics;

using Vertex.Rendering;
using Vertex.Rendering.Shaders;

namespace Vertex.Tests
{
    /// <summary>The CPU side of how things look: the sky, the shadow cascades, and textures made in code.</summary>
    public class LookTests
    {
        private static readonly Vector3 _white = Vector3.One;

        [Fact]
        public void The_sky_is_blue_overhead_by_day_and_paler_at_the_horizon()
        {
            Vector3 sun = Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f));
            Vector3 overhead = Atmosphere.Radiance(Vector3.UnitY, sun, _white);
            Vector3 horizon = Atmosphere.Radiance(Vector3.Normalize(new Vector3(-1f, 0.02f, 0f)), sun, _white);

            Assert.True(overhead.Z > overhead.Y && overhead.Y > overhead.X, $"Overhead should be blue, was {overhead}.");
            Assert.True(horizon.X / horizon.Z > overhead.X / overhead.Z, "The horizon should be whiter than overhead.");
        }

        [Fact]
        public void Sunlight_is_unchanged_overhead_golden_when_low_and_gone_once_set()
        {
            Vector3 overhead = Atmosphere.SunlightTint(1f);
            Vector3 low = Atmosphere.SunlightTint(0.08f);
            Vector3 set = Atmosphere.SunlightTint(-0.2f);

            Assert.Equal(1f, overhead.X, 3);
            Assert.Equal(1f, overhead.Z, 3);
            Assert.True(low.X > low.Y && low.Y > low.Z, $"A low sun should be golden, was {low}.");
            Assert.Equal(Vector3.Zero, set);
        }

        [Fact]
        public void Cascades_cover_the_view_out_to_the_shadow_distance_with_the_nearest_smallest()
        {
            var frame = new RenderFrame
            {
                Width = 1280,
                Height = 720,
                ShadowDistance = 100f,
                CameraPosition = new Vector3(3f, 2f, 1f),
                CameraForward = -Vector3.UnitZ,
                ProjectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 1000f)
            };
            var cascades = new ShadowCascades();
            cascades.Place(frame, Vector3.Normalize(new Vector3(0.3f, -1f, 0.2f)));

            for (int i = 1; i < ShadowCascades.Count; i++)
            {
                Assert.True(cascades.Splits[i] > cascades.Splits[i - 1]);
                Assert.True(cascades.TexelSizes[i] > cascades.TexelSizes[i - 1]);
            }
            Assert.Equal(100f, cascades.Splits[^1], 2);

            // A point just ahead of the camera lands inside the first cascade's map.
            Vector4 inMap = Vector4.Transform(new Vector4(frame.CameraPosition - Vector3.UnitZ * 2f, 1f), cascades.Matrices[0]);
            Assert.InRange(inMap.X / inMap.W, -1f, 1f);
            Assert.InRange(inMap.Y / inMap.W, -1f, 1f);
            Assert.InRange(inMap.Z / inMap.W, 0f, 1f);
        }

        [Fact]
        public void Outlines_get_thicker_with_their_width_and_with_the_screen()
        {
            var lighting = new Lighting { Outlines = 1f, OutlineWidth = 3f };
            var frame = new RenderFrame { Width = 1280, Height = 720, Environment = EnvironmentData.From(lighting) };
            Vector2 step = PostUniforms.ForView(frame).OutlineStep;
            Assert.Equal(3f / 1280f, step.X, 6);
            Assert.Equal(3f / 720f, step.Y, 6);

            // Twice the lines, twice as many pixels, so it looks the same.
            Vector2 big = PostUniforms.OutlineStepFor(2560, 1440, 3f);
            Assert.Equal(6f / 2560f, big.X, 6);

            // The default is the fine line it always was, and nothing goes under a pixel.
            Assert.Equal(1f / 720f, PostUniforms.OutlineStepFor(1280, 720, new Lighting().OutlineWidth).Y, 6);
            Assert.Equal(1f / 360f, PostUniforms.OutlineStepFor(640, 360, 1f).Y, 6);
        }

        [Fact]
        public void A_flat_height_map_makes_a_normal_map_facing_straight_out()
        {
            var flat = new Texture(4, 4, Enumerable.Repeat((byte)128, 4 * 4 * 4).ToArray());
            Texture normals = Texture.CreateNormalMap(flat);

            Assert.Equal(128, normals.Pixels[0]);
            Assert.Equal(128, normals.Pixels[1]);
            Assert.Equal(255, normals.Pixels[2]);
        }

        [Fact]
        public void A_slope_tilts_the_normals_away_from_the_rise()
        {
            // Rising to the right (u): the surface faces left, so x points negative.
            byte[] pixels = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++) pixels[(y * 8 + x) * 4] = (byte)(x * 20);
            }

            Texture normals = Texture.CreateNormalMap(new Texture(8, 8, pixels));
            Assert.True(normals.Pixels[(3 * 8 + 3) * 4] < 128);
            Assert.Equal(128, normals.Pixels[(3 * 8 + 3) * 4 + 1]);
        }

        [Fact]
        public void Lighting_settings_are_kept_in_range_for_the_renderer()
        {
            var lighting = new Lighting { CloudCover = 3f, AmbientOcclusion = -1f, Vignette = 2f, FogDensity = -0.5f };
            EnvironmentData environment = EnvironmentData.From(lighting);

            Assert.Equal(1f, environment.CloudCover);
            Assert.Equal(0f, environment.AmbientOcclusion);
            Assert.Equal(1f, environment.Vignette);
            Assert.Equal(0f, environment.FogDensity);
        }
    }
}
