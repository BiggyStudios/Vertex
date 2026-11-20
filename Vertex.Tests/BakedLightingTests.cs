using System.Numerics;

using Vertex.Rendering;
using Vertex.Rendering.Baking;

namespace Vertex.Tests
{
    /// <summary>Baked lighting: laying meshes out for the lightmap, tracing rays, baking, saving, and drawing with it.</summary>
    public class BakedLightingTests : EngineTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexBakeTests", Guid.NewGuid().ToString("N"));
        private readonly string[] _assetFolders = Application.AssetFolders;
        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;

        public BakedLightingTests()
        {
            Directory.CreateDirectory(_folder);
            Application.AssetFolders = [_folder];
            _renderer = new Renderer(_backend);
            _renderer.Resize(640, 360);
        }

        public override void Dispose()
        {
            Application.AssetFolders = _assetFolders;
            base.Dispose();
            Directory.Delete(_folder, recursive: true);
        }

        private static GameObject StaticBox(string name, Vector3 at, Vector3 size)
        {
            GameObject box = GameObject.CreateCube(at, Color.White, name);
            box.Transform.Scale = size;
            box.GetComponent<MeshRenderer>()!.Static = true;
            return box;
        }

        private static Light Lamp(Vector3 at, LightMode mode, float range = 8f)
        {
            var lamp = new GameObject("Lamp");
            lamp.Transform.Position = at;
            Light light = lamp.AddComponent<Light>();
            light.Type = LightType.Point;
            light.Range = range;
            light.Mode = mode;
            light.Size = 0f;
            return light;
        }

        /// <summary>A floor with a box on it and a baked lamp over the box, baked quickly and given to the scene.</summary>
        private (GameObject Floor, GameObject Box, LightBakeResult Result) BakeRoom()
        {
            Scene.Active.Lighting.AmbientIntensity = 0f;
            Scene.Active.Lighting.BakeQuality = BakeQuality.Preview;
            Scene.Active.Lighting.BakeDetail = 4f;
            Scene.Active.Lighting.BakeBounces = 1;
            GameObject floor = StaticBox("Floor", new Vector3(0f, -0.5f, 0f), new Vector3(10f, 1f, 10f));
            GameObject box = StaticBox("Box", new Vector3(0f, 0.5f, 0f), Vector3.One);
            Lamp(new Vector3(0f, 4f, 0f), LightMode.Baked);
            Scene.Active.ApplyChanges();

            LightBakeResult result = LightBaker.Prepare(Scene.Active).Run();
            result.Apply(Scene.Active, Path.Combine(_folder, "Room.bake"));
            return (floor, box, result);
        }

        /// <summary>The baked light on the top of <paramref name="floor"/> at <paramref name="point"/>, read back from the lightmap.</summary>
        private static Vector3 LightOnFloor(BakedLighting baked, GameObject floor, Vector2 point)
        {
            MeshRenderer renderer = floor.GetComponent<MeshRenderer>()!;
            Mesh mesh = baked.MeshFor(renderer)!;
            Matrix4x4 world = floor.Transform.WorldMatrix;
            Texture lightmap = baked.Lightmap!;

            // The triangle on the top of the floor that the point's over, and where on it.
            for (int t = 0; t < mesh.Indices.Length; t += 3)
            {
                MeshVertex a = mesh.Vertices[mesh.Indices[t]], b = mesh.Vertices[mesh.Indices[t + 1]], c = mesh.Vertices[mesh.Indices[t + 2]];
                if (a.Normal.Y < 0.9f) continue;
                Vector3 pa = Vector3.Transform(a.Position, world), pb = Vector3.Transform(b.Position, world), pc = Vector3.Transform(c.Position, world);
                Vector2 p = point, v0 = new(pb.X - pa.X, pb.Z - pa.Z), v1 = new(pc.X - pa.X, pc.Z - pa.Z), v2 = new(p.X - pa.X, p.Y - pa.Z);
                float d = v0.X * v1.Y - v1.X * v0.Y;
                float u = (v2.X * v1.Y - v1.X * v2.Y) / d, v = (v0.X * v2.Y - v2.X * v0.Y) / d;
                if (u < 0f || v < 0f || u + v > 1f) continue;

                Vector2 uv = a.LightmapUV * (1f - u - v) + b.LightmapUV * u + c.LightmapUV * v;
                int x = (int)(uv.X * lightmap.Width), y = (int)(uv.Y * lightmap.Height);
                int i = (y * lightmap.Width + x) * 4;
                return BakedLighting.Decode(lightmap.Pixels[i], lightmap.Pixels[i + 1], lightmap.Pixels[i + 2], lightmap.Pixels[i + 3]);
            }

            throw new InvalidOperationException("Not over the floor.");
        }

        [Fact]
        public void A_baked_lamp_lights_the_floor_round_a_box_and_leaves_its_shadow_dark()
        {
            (GameObject floor, GameObject _, LightBakeResult result) = BakeRoom();
            BakedLighting baked = result.Lighting;
            Assert.Equal(2, result.Renderers.Count);

            // Near the lamp, its light; further off, less, as it fades out over its range.
            Vector3 open = LightOnFloor(baked, floor, new Vector2(2f, 2f));
            Vector3 far = LightOnFloor(baked, floor, new Vector2(4.5f, 4.5f));
            Assert.True(open.X > 0.05f, $"open {open}");
            Assert.True(open.X > far.X, $"open {open}, far {far}");
        }

        [Fact]
        public void The_box_casts_a_soft_shadow_from_a_lamp_off_to_one_side()
        {
            Scene.Active.Lighting.AmbientIntensity = 0f;
            Scene.Active.Lighting.BakeQuality = BakeQuality.Preview;
            Scene.Active.Lighting.BakeBounces = 0;
            GameObject floor = StaticBox("Floor", new Vector3(0f, -0.5f, 0f), new Vector3(10f, 1f, 10f));
            StaticBox("Wall", new Vector3(0f, 1f, 0f), new Vector3(0.5f, 2f, 4f));
            Lamp(new Vector3(-2f, 1.5f, 0f), LightMode.Baked);
            Scene.Active.ApplyChanges();

            LightBakeResult result = LightBaker.Prepare(Scene.Active).Run();
            result.Apply(Scene.Active, Path.Combine(_folder, "Wall.bake"));
            Vector3 lit = LightOnFloor(result.Lighting, floor, new Vector2(-1f, 0f));
            Vector3 shadowed = LightOnFloor(result.Lighting, floor, new Vector2(1f, 0f));
            Assert.True(lit.X > 0.1f, $"lit {lit}");
            Assert.True(shadowed.X < lit.X * 0.1f, $"lit {lit}, shadowed {shadowed}");
        }

        [Fact]
        public void Once_baked_a_baked_lamp_costs_nothing_static_meshes_draw_with_the_lightmap_and_moving_ones_with_probes()
        {
            Light realtime = Lamp(new Vector3(3f, 1f, 3f), LightMode.Realtime);
            (GameObject floor, GameObject box, LightBakeResult _) = BakeRoom();
            GameObject ball = GameObject.CreateSphere(new Vector3(2f, 1f, -2f), Color.White, "Ball");
            var camera = new GameObject("Camera");
            camera.Transform.Position = new Vector3(0f, 3f, 8f);
            camera.Transform.LookAt(Vector3.Zero);
            camera.AddComponent<Camera>();
            Scene.Active.Update();
            _renderer.Render(Scene.Active);

            // The baked lamp's gone from the lights the GPU works out; the realtime one's still there.
            Assert.Equal([realtime.Transform.WorldPosition], _backend.LastPointLights.Select(l => l.Position));

            DrawCall floorDraw = _backend.LastDraws.Single(d => d.WorldMatrix == floor.Transform.WorldMatrix);
            Assert.True(floorDraw.Baked.Lightmapped);
            Assert.NotSame(floor.GetComponent<MeshRenderer>()!.Mesh, floorDraw.Mesh);
            Assert.Contains(floorDraw.Mesh.Vertices, v => v.LightmapUV != Vector2.Zero);
            Assert.True(_backend.LastDraws.Single(d => d.WorldMatrix == box.Transform.WorldMatrix).Baked.Lightmapped);

            DrawCall ballDraw = _backend.LastDraws.Single(d => d.WorldMatrix == ball.Transform.WorldMatrix);
            Assert.False(ballDraw.Baked.Lightmapped);
            Assert.True(ballDraw.Baked.FromProbes);
            Assert.True(ballDraw.Baked.Probe.Toward(Vector3.UnitY).X > ballDraw.Baked.Probe.Toward(-Vector3.UnitY).X);
        }

        [Fact]
        public void A_baked_scene_comes_back_from_its_file_and_a_mesh_changed_since_isnt_drawn_with_it()
        {
            (GameObject floor, GameObject _, LightBakeResult result) = BakeRoom();
            Assert.Equal("Room.bake", Scene.Active.Lighting.BakedLightingFile);

            BakedLighting loaded = BakedLighting.Load(Path.Combine(_folder, "Room.bake"));
            Assert.Equal(result.Lighting.Lightmap!.Pixels, loaded.Lightmap!.Pixels);
            Assert.Equal(result.Lighting.Meshes.Count, loaded.Meshes.Count);
            Assert.Equal(result.Lighting.Probes!.Light, loaded.Probes!.Light);
            Assert.Equal(result.Lighting.Probes.Reach, loaded.Probes.Reach);
            Assert.NotNull(loaded.MeshFor(floor.GetComponent<MeshRenderer>()!));

            // Given another mesh, it isn't the one that was baked.
            floor.GetComponent<MeshRenderer>()!.Mesh = Primitives.Sphere;
            Assert.Null(loaded.MeshFor(floor.GetComponent<MeshRenderer>()!));
        }

        [Fact]
        public void Lightmap_pixels_keep_dim_light_close_and_bright_light_up_to_their_range()
        {
            foreach (Vector3 light in new[] { new Vector3(0.002f, 0.004f, 0.001f), new Vector3(0.3f, 0.5f, 0.2f), new Vector3(9f, 3f, 1f) })
            {
                (byte r, byte g, byte b, byte a) = BakedLighting.Encode(light);
                Vector3 back = BakedLighting.Decode(r, g, b, a);
                Assert.True(Vector3.Distance(back, light) <= light.Length() * 0.08f + 1e-4f, $"{light} came back {back}");
            }
        }

        [Fact]
        public void A_cube_is_laid_out_in_six_flat_pieces_as_big_as_its_sides()
        {
            Mesh cube = Primitives.Cube;
            LightmapUnwrap unwrap = LightmapUnwrap.Of(cube, Matrix4x4.CreateScale(2f, 1f, 3f));
            Assert.Equal(6, unwrap.Charts.Count);
            Assert.Equal(cube.Indices.Length, unwrap.Indices.Length);

            float[] areas = unwrap.Charts.Select(c => c.Size.X * c.Size.Y).Order().ToArray();
            Assert.Equal([2f, 2f, 3f, 3f, 6f, 6f], areas.Select(a => MathF.Round(a, 3)));

            // Corners stay where they were in the world; each points back to the mesh's own.
            for (int i = 0; i < unwrap.SourceVertices.Length; i++)
                Assert.Equal(Vector3.Transform(cube.Vertices[unwrap.SourceVertices[i]].Position, Matrix4x4.CreateScale(2f, 1f, 3f)), unwrap.Positions[i]);
        }

        [Fact]
        public void A_ray_hits_the_nearest_triangle_and_knows_if_it_hit_its_back()
        {
            // Two squares facing +Z, at z = 0 and z = -2, each two triangles.
            var triangles = new List<(Vector3, Vector3, Vector3)>();
            var facing = new List<Vector3>();
            foreach (float z in new[] { 0f, -2f })
            {
                triangles.Add((new Vector3(-1f, -1f, z), new Vector3(1f, -1f, z), new Vector3(1f, 1f, z)));
                triangles.Add((new Vector3(-1f, -1f, z), new Vector3(1f, 1f, z), new Vector3(-1f, 1f, z)));
                facing.Add(Vector3.UnitZ);
                facing.Add(Vector3.UnitZ);
            }

            var tree = new TriangleTree(triangles, facing);
            Assert.True(tree.Intersect(new Vector3(0.2f, 0.1f, 5f), -Vector3.UnitZ, 100f, out RayHit hit));
            Assert.Equal(5f, hit.Distance, 3);
            Assert.False(hit.Backface);

            Assert.True(tree.Intersect(new Vector3(0.2f, 0.1f, -1f), Vector3.UnitZ, 100f, out RayHit back));
            Assert.True(back.Backface);
            Assert.Equal(1f, back.Distance, 3);

            Assert.False(tree.Intersect(new Vector3(3f, 0f, 5f), -Vector3.UnitZ, 100f, out _));
            Assert.False(tree.Occluded(new Vector3(0f, 0f, 5f), -Vector3.UnitZ, 4f));
            Assert.True(tree.Occluded(new Vector3(0f, 0f, 5f), -Vector3.UnitZ, 6f));
        }

        [Fact]
        public void A_probe_with_a_floor_between_it_and_a_point_doesnt_light_it()
        {
            var grid = new LightProbeGrid(Vector3.Zero, 2f, 2, 2, 2);
            Array.Fill(grid.Valid, true);
            for (int i = 0; i < grid.Light.Length; i++)
            {
                // The bottom four dark, the top four bright.
                float bright = i / 2 % 2 == 1 ? 1f : 0f;
                grid.Light[i] = new ProbeLight(new Vector4(bright, 0f, 0f, 0f), new Vector4(bright, 0f, 0f, 0f), new Vector4(bright, 0f, 0f, 0f));
            }

            Assert.True(grid.Sample(new Vector3(1f, 0.5f, 1f), out ProbeLight blended));
            Assert.Equal(0.25f, blended.Red.X, 3);

            // A floor just above the bottom probes: they can't see up past it.
            foreach (int i in new[] { 0, 1, 4, 5 }) grid.Reach[i * 6 + 2] = 0.2f;
            Assert.True(grid.Sample(new Vector3(1f, 0.5f, 1f), out ProbeLight above));
            Assert.Equal(1f, above.Red.X, 3);
        }
    }
}
