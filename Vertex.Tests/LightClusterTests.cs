using System.Numerics;

using Vertex.Rendering;
using Vertex.Rendering.Shaders;

namespace Vertex.Tests
{
    /// <summary>Sorting point and spot lights into the parts of the view they reach, so each pixel only works out those.</summary>
    public class LightClusterTests : EngineTest
    {
        // Looking along -Z from the origin, 16:9.
        private static readonly Matrix4x4 _view = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        private static readonly Matrix4x4 _projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 500f);

        private static LightClusters Build(params PointLightData[] lights)
        {
            var clusters = new LightClusters();
            clusters.Build(lights, _view, _projection);
            return clusters;
        }

        /// <summary>The cluster a point is in, as the shaders work it out (lightCluster).</summary>
        private static (int X, int Y, int Slice) ClusterOf(LightClusters clusters, Vector3 point)
        {
            Vector4 clip = Vector4.Transform(new Vector4(point, 1f), _view * _projection);
            float depth = -Vector3.Transform(point, _view).Z;
            int x = Math.Clamp((int)((clip.X / clip.W * 0.5f + 0.5f) * LightClusters.TilesX), 0, LightClusters.TilesX - 1);
            int y = Math.Clamp((int)((clip.Y / clip.W * 0.5f + 0.5f) * LightClusters.TilesY), 0, LightClusters.TilesY - 1);
            int slice = depth <= LightClusters.Near ? 0 : Math.Clamp((int)(MathF.Log(depth) * clusters.Slicing.X + clusters.Slicing.Y), 0, LightClusters.Slices - 1);
            return (x, y, slice);
        }

        [Fact]
        public void Every_point_a_light_reaches_is_in_a_cluster_that_lists_it_and_far_off_parts_of_the_view_dont()
        {
            var lamp = new PointLightData(new Vector3(3f, 1f, -20f), Color.White, 4f);
            var other = new PointLightData(new Vector3(-8f, -2f, -40f), Color.White, 3f);
            LightClusters clusters = Build(lamp, other);

            var random = new Random(4);
            for (int i = 0; i < 500; i++)
            {
                Vector3 offset = new Vector3(random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f) * 4f;
                if (offset.Length() >= 4f) continue;
                (int x, int y, int slice) = ClusterOf(clusters, lamp.Position + offset);
                Assert.Contains(0, clusters.LightIndices(x, y, slice));
            }

            // The other side of the view, and close to the camera, have neither.
            (int fx, int fy, int fs) = ClusterOf(clusters, new Vector3(-3f, -1f, -20f));
            Assert.DoesNotContain(0, clusters.LightIndices(fx, fy, fs));
            (int nx, int ny, int ns) = ClusterOf(clusters, new Vector3(3f, 1f, -2f));
            Assert.Equal(0, clusters.LightsIn(nx, ny, ns));
            (int ox, int oy, int os) = ClusterOf(clusters, other.Position);
            Assert.Equal([1], clusters.LightIndices(ox, oy, os));
        }

        [Fact]
        public void A_light_behind_the_camera_is_in_no_cluster_and_one_round_it_is_in_all_the_nearest()
        {
            LightClusters behind = Build(new PointLightData(new Vector3(0f, 0f, 10f), Color.White, 3f));
            Assert.Equal(0f, behind.Slicing.Z);

            LightClusters around = Build(new PointLightData(new Vector3(0f, 0f, 0.5f), Color.White, 3f));
            for (int y = 0; y < LightClusters.TilesY; y++)
            for (int x = 0; x < LightClusters.TilesX; x++)
                Assert.Equal(1, around.LightsIn(x, y, 0));
        }

        [Fact]
        public void A_narrow_spot_lights_only_the_clusters_along_its_beam()
        {
            // Shining straight away from the camera, a narrow beam: nothing to the side of it.
            var spot = new PointLightData(new Vector3(0f, 0f, -5f), Color.White, 30f)
            {
                Direction = -Vector3.UnitZ,
                SpotOuter = MathF.Cos(10f * MathF.PI / 180f),
                SpotInner = MathF.Cos(5f * MathF.PI / 180f)
            };
            // The spheres it's held in cover all its cone reaches, and none is much wider than the beam.
            Span<(Vector3 Centre, float Radius)> pieces = stackalloc (Vector3, float)[LightClusters.MaxPieces];
            int count = LightClusters.Pieces(spot, pieces);
            var random = new Random(2);
            for (int i = 0; i < 2000; i++)
            {
                Vector3 point = spot.Position + new Vector3(random.NextSingle() * 12f - 6f, random.NextSingle() * 12f - 6f, -random.NextSingle() * 30f);
                Vector3 to = point - spot.Position;
                if (to.Length() > spot.Range || Vector3.Dot(Vector3.Normalize(to), spot.Direction) < spot.SpotOuter) continue;
                Assert.Contains(true, pieces[..count].ToArray().Select(p => Vector3.Distance(p.Centre, point) <= p.Radius + 1e-3f));
            }
            foreach ((Vector3 _, float radius) in pieces[..count]) Assert.True(radius < 7f);

            LightClusters clusters = Build(spot);
            (int x, int y, int slice) = ClusterOf(clusters, new Vector3(0f, 0f, -30f));
            Assert.Equal(1, clusters.LightsIn(x, y, slice));
            (int sx, int sy, int ss) = ClusterOf(clusters, new Vector3(8f, 0f, -12f));
            Assert.Equal(0, clusters.LightsIn(sx, sy, ss));
        }

        [Fact]
        public void Many_lights_each_get_their_own_place_in_the_lists()
        {
            // More lights than fit in a word, all reaching the same spot.
            var lights = new PointLightData[ShaderLibrary.MaxPointLights];
            for (int i = 0; i < lights.Length; i++) lights[i] = new PointLightData(new Vector3(0f, 0f, -10f - i * 0.01f), Color.White, 5f);
            LightClusters clusters = Build(lights);

            (int x, int y, int slice) = ClusterOf(clusters, new Vector3(0f, 0f, -10f));
            Assert.Equal(Enumerable.Range(0, lights.Length), clusters.LightIndices(x, y, slice).Order());
        }

        [Fact]
        public void The_shaders_cut_the_view_up_as_the_clusters_are_built()
        {
            Assert.Contains($"CLUSTER_TILES_X = {LightClusters.TilesX};", ShaderLibrary.Frame);
            Assert.Contains($"CLUSTER_TILES_Y = {LightClusters.TilesY};", ShaderLibrary.Frame);
            Assert.Contains($"CLUSTER_SLICES = {LightClusters.Slices};", ShaderLibrary.Frame);
            Assert.Contains($"depth <= {LightClusters.Near:0.0#} ? 0", ShaderLibrary.Frame);
            Assert.Contains($"pointColor[{ShaderLibrary.MaxPointLights}]", ShaderLibrary.Frame);

            // The frame's data fits the smallest a uniform block can be in OpenGL.
            Assert.True(System.Runtime.CompilerServices.Unsafe.SizeOf<FrameUniforms>() <= 16384);
        }
    }
}
