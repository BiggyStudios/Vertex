using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Editor.SceneEditing;
using Vertex.Rendering;

namespace Vertex.Editor.Tests
{
    /// <summary>Getting around the scene view, picking things in it, and the handles' maths.</summary>
    public class SceneViewTests : EditorTest
    {
        private static GameObject Cube(string name, Vector3 position)
        {
            var cube = new GameObject(name);
            cube.Transform.Position = position;
            cube.AddComponent<MeshRenderer>().Mesh = Primitives.Cube;
            return cube;
        }

        [Fact]
        public void Clicking_picks_the_nearest_thing_the_mouse_is_over()
        {
            Cube("Near", new Vector3(0f, 0f, -5f));
            Cube("Far", new Vector3(0f, 0f, -10f));
            Cube("Aside", new Vector3(4f, 0f, -5f));
            Scene.Active.ApplyChanges();

            var hit = Picking.Pick(Scene.Active, Vector3.Zero, -Vector3.UnitZ);

            Assert.Equal("Near", hit?.GameObject.Name);
            Assert.Equal(4.5f, hit!.Value.Distance, 0.001f);
            Assert.Null(Picking.Pick(Scene.Active, Vector3.Zero, Vector3.UnitY));
        }

        [Fact]
        public void Picking_goes_by_the_shape_not_the_box_round_it()
        {
            var sphere = new GameObject("Ball");
            sphere.Transform.Scale = new Vector3(2f);
            sphere.AddComponent<MeshRenderer>().Mesh = Primitives.Sphere;
            Scene.Active.ApplyChanges();

            // Through the corner of its box, which the round ball doesn't fill.
            Assert.Null(Picking.Pick(Scene.Active, new Vector3(0.9f, 0.9f, 5f), -Vector3.UnitZ));
            Assert.NotNull(Picking.Pick(Scene.Active, new Vector3(0f, 0f, 5f), -Vector3.UnitZ));
        }

        [Fact]
        public void Zooming_eases_in_rather_than_jumping()
        {
            var camera = new EditorCamera(new Scene());
            float before = camera.Distance;

            camera.Zoom(3f);
            camera.Update(1f / 60f);
            float afterOneFrame = camera.Distance;
            for (int i = 0; i < 120; i++) camera.Update(1f / 60f);

            Assert.True(afterOneFrame < before && afterOneFrame > before * 0.82f * 0.82f * 0.82f + 0.01f, $"{before} -> {afterOneFrame}");
            Assert.Equal(before * MathF.Pow(0.82f, 3f), camera.Distance, 0.01f);
        }

        [Fact]
        public void Easing_gets_to_the_same_place_at_any_frame_rate()
        {
            float slow = 0f, fast = 0f;
            for (int i = 0; i < 6; i++) slow = Ui.Approach(slow, 10f, 12f, 1f / 30f);
            for (int i = 0; i < 24; i++) fast = Ui.Approach(fast, 10f, 12f, 1f / 120f);

            Assert.Equal(slow, fast, 0.01f);
        }

        [Fact]
        public void Framing_flies_to_look_at_the_thing_from_the_same_angle()
        {
            var camera = new EditorCamera(new Scene());
            Vector3 forward = camera.Forward;

            camera.Frame(new Vector3(10f, 2f, -3f), 1f);
            camera.Settle();

            Assert.Equal(new Vector3(10f, 2f, -3f), camera.Pivot);
            Assert.True(Vector3.Distance(forward, camera.Forward) < 1e-4f);
            Assert.True(Vector3.Distance(camera.Position + camera.Forward * camera.Distance, camera.Pivot) < 1e-3f);
        }

        [Fact]
        public void Looking_around_turns_the_camera_where_it_stands()
        {
            var camera = new EditorCamera(new Scene());
            camera.Settle();
            Vector3 position = camera.Position;

            camera.Look(40f, 10f);
            for (int i = 0; i < 90; i++) camera.Update(1f / 60f);
            camera.EndLook();

            Assert.True(Vector3.Distance(position, camera.Position) < 1e-3f);
        }

        [Fact]
        public void Looking_along_an_axis_from_above_looks_straight_down()
        {
            var camera = new EditorCamera(new Scene());
            camera.LookAlong(-Vector3.UnitY);
            camera.Settle();

            Assert.True(camera.Forward.Y < -0.999f);
        }

        [Fact]
        public void A_ray_through_the_middle_of_the_view_goes_straight_ahead()
        {
            var camera = new EditorCamera(new Scene());
            camera.Settle();

            (Vector3 origin, Vector3 direction) = camera.Ray(new Vector2(0.5f), 16f / 9f);

            Assert.True(Vector3.Distance(camera.Position, origin) < 1e-4f);
            Assert.True(Vector3.Distance(camera.Forward, direction) < 1e-3f);
        }

        [Fact]
        public void Dragging_along_an_axis_follows_the_point_on_it_nearest_the_mouse()
        {
            // Looking down -Z from above the X axis, the mouse ray straight down meets the X axis at x = 3.
            Vector3? hit = TransformHandles.ClosestOnLine(Vector3.Zero, Vector3.UnitX, new Vector3(3f, 5f, 2f), -Vector3.UnitY);
            Assert.True(Vector3.Distance(new Vector3(3f, 0f, 0f), hit!.Value) < 1e-4f);

            Vector3? plane = TransformHandles.PlaneHit(Vector3.Zero, Vector3.UnitY, new Vector3(1f, 4f, 2f), Vector3.Normalize(new Vector3(0f, -1f, 1f)));
            Assert.True(Vector3.Distance(new Vector3(1f, 0f, 6f), plane!.Value) < 1e-4f);
        }
    }
}
