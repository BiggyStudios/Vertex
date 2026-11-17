using System.Numerics;

using Vertex.Rendering;

namespace Vertex.Tests
{
    /// <summary>What's left out of drawing because it can't be seen, and the numbers kept about drawing.</summary>
    public class CullingTests : EngineTest
    {
        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;

        public CullingTests()
        {
            _renderer = new Renderer(_backend);
            _renderer.Resize(1280, 720);

            // Looking along -Z from just above the ground.
            var camera = new GameObject("Camera");
            camera.Transform.Position = new Vector3(0f, 2f, 0f);
            camera.AddComponent<Camera>();
        }

        private static GameObject AddBox(string name, Vector3 position, Vector3? scale = null)
        {
            var box = new GameObject(name);
            box.Transform.Position = position;
            box.Transform.Scale = scale ?? Vector3.One;
            box.AddComponent<MeshRenderer>().Mesh = Primitives.Cube;
            return box;
        }

        private static void AddSun()
        {
            var sun = new GameObject("Sun");
            sun.AddComponent<Light>();
            sun.Transform.EulerAngles = new Vector3(-90f, 0f, 0f);
        }

        private List<string> Drawn() => _backend.LastDraws.Select(draw => Scene.Active.FindComponents<MeshRenderer>()
            .First(renderer => renderer.Transform.WorldMatrix == draw.WorldMatrix).GameObject.Name).ToList();

        private void Render()
        {
            Scene.Active.Update();
            _renderer.Render(Scene.Active);
        }

        [Fact]
        public void A_mesh_knows_the_box_it_fits_in()
        {
            Assert.Equal(Vector3.Zero, Primitives.Cube.BoundsCentre);
            Assert.Equal(new Vector3(0.5f), Primitives.Cube.BoundsExtents);
        }

        [Fact]
        public void Only_what_the_camera_can_see_is_drawn()
        {
            AddBox("Ahead", new Vector3(0f, 1f, -10f));
            AddBox("Behind", new Vector3(0f, 1f, 10f));
            AddBox("Far to the side", new Vector3(200f, 1f, -10f));
            AddBox("Past the far plane", new Vector3(0f, 1f, -5000f));

            Render();

            Assert.Equal(["Ahead"], Drawn());
            Assert.Equal(1, _renderer.Stats.DrawCalls);
            Assert.Equal(3, _renderer.Stats.CulledDrawCalls);
        }

        [Fact]
        public void Something_only_partly_in_view_is_still_drawn()
        {
            // Its middle is out of view to the right, but it's long enough to reach in.
            AddBox("Long", new Vector3(30f, 1f, -10f), new Vector3(60f, 1f, 1f));

            Render();

            Assert.Equal(["Long"], Drawn());
        }

        [Fact]
        public void Things_out_of_view_still_cast_shadows_into_it_but_only_into_cascades_they_reach()
        {
            AddSun();
            AddBox("Ground", new Vector3(0f, -0.5f, -20f), new Vector3(40f, 1f, 40f));
            // Overhead, above the top of the view, with its shadow falling straight down onto the ground in view.
            AddBox("Overhead", new Vector3(0f, 40f, -10f));
            // Far off and out of view, too far to shadow anything seen.
            AddBox("Far away", new Vector3(0f, 1f, 3000f));

            Render();

            Assert.Equal(["Ground"], Drawn());

            List<List<DrawCall>> casters = _backend.LastShadowCasters;
            Assert.NotEmpty(casters);
            Assert.Contains(casters, cascade => cascade.Any(draw => draw.WorldMatrix.Translation == new Vector3(0f, 40f, -10f)));
            Assert.DoesNotContain(casters, cascade => cascade.Any(draw => draw.WorldMatrix.Translation == new Vector3(0f, 1f, 3000f)));
            Assert.Equal(casters.Sum(cascade => cascade.Count), _renderer.Stats.ShadowDrawCalls);
        }

        [Fact]
        public void Gpu_pass_times_are_the_gaps_between_timestamps_added_up_by_name()
        {
            var times = new List<GpuPassTime>();
            GpuTimestamps.Measure(times, ["Scene", "Bloom", "Scene", ""], [100, 110, 130, 135], 0.5);

            Assert.Equal([new GpuPassTime("Scene", 7.5), new GpuPassTime("Bloom", 10)], times);
        }

        [Fact]
        public void Gpu_times_that_go_backwards_are_thrown_away()
        {
            var times = new List<GpuPassTime> { new("Old", 1) };
            GpuTimestamps.Measure(times, ["Scene", "Bloom", ""], [100, 90, 120], 1);

            Assert.Empty(times);
        }
    }
}
