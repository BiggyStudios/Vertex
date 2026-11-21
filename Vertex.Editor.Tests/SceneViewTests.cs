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

        [Theory]
        [InlineData(-30f)]
        [InlineData(30f)]
        public void Letting_go_of_a_look_mid_turn_doesnt_bob_the_camera(float pitch)
        {
            var camera = new EditorCamera(new Scene());
            camera.Settle();
            Vector3 position = camera.Position;

            // A quick look up or down, let go before the turn has caught up.
            camera.Look(0f, pitch);
            camera.Update(1f / 60f);
            camera.EndLook();

            float furthest = 0f;
            for (int i = 0; i < 120; i++)
            {
                camera.Update(1f / 60f);
                furthest = MathF.Max(furthest, Vector3.Distance(position, camera.Position));
            }

            Assert.True(furthest < 1e-3f, $"the camera moved {furthest} m");
            Assert.True(MathF.Abs(MathF.Asin(camera.Forward.Y) * 180f / MathF.PI - (-22f + pitch)) < 0.05f);

            // Once it's settled, moves of its own work as before: zooming goes towards the pivot.
            float distance = camera.Distance;
            camera.Zoom(2f);
            for (int i = 0; i < 120; i++) camera.Update(1f / 60f);
            Assert.True(camera.Distance < distance * 0.8f);
        }

        [Fact]
        public void Zooming_straight_after_a_look_zooms_rather_than_staying_put()
        {
            var camera = new EditorCamera(new Scene());
            camera.Settle();
            Vector3 position = camera.Position;

            camera.Look(0f, -30f);
            camera.Update(1f / 60f);
            camera.EndLook();
            camera.Zoom(3f);
            for (int i = 0; i < 120; i++) camera.Update(1f / 60f);

            Assert.True(Vector3.Distance(position, camera.Position) > 1f);
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

        [Fact]
        public void The_2D_view_looks_flat_on_at_the_plane_and_back_to_3D_turned_as_it_was()
        {
            var camera = new EditorCamera(new Scene());
            camera.Settle();
            Vector3 forward3D = camera.Forward;

            camera.Set2D(true);
            Assert.True(camera.Is2D);
            Assert.Equal(CameraProjection.Orthographic, camera.Camera.Projection);
            Assert.True(Vector3.Distance(camera.Forward, -Vector3.UnitZ) < 1e-4f);
            Assert.Equal(0f, camera.Pivot.Z);

            // Rays go straight along -Z, from where they cross the screen.
            (Vector3 leftOrigin, Vector3 leftDirection) = camera.Ray(new Vector2(0f, 0.5f), 16f / 9f);
            (Vector3 rightOrigin, Vector3 rightDirection) = camera.Ray(new Vector2(1f, 0.5f), 16f / 9f);
            Assert.True(Vector3.Distance(leftDirection, -Vector3.UnitZ) < 1e-4f && Vector3.Distance(rightDirection, -Vector3.UnitZ) < 1e-4f);
            Assert.Equal(2f * camera.Camera.OrthographicSize * 16f / 9f, rightOrigin.X - leftOrigin.X, 0.01f);

            // Zooming in shows less; there's no turning.
            float size = camera.Camera.OrthographicSize;
            camera.Zoom(2f);
            camera.Look(30f, 30f);
            camera.Orbit(30f, 30f);
            for (int i = 0; i < 120; i++) camera.Update(1f / 60f);
            Assert.True(camera.Camera.OrthographicSize < size * 0.7f);
            Assert.True(Vector3.Distance(camera.Forward, -Vector3.UnitZ) < 1e-4f);

            // Remembered, and back to 3D turned as before.
            EditorCamera.Placement placement = camera.Remember();
            Assert.True(placement.Is2D);
            camera.Set2D(false);
            Assert.Equal(CameraProjection.Perspective, camera.Camera.Projection);
            Assert.True(Vector3.Distance(camera.Forward, forward3D) < 1e-3f);
            camera.Restore(placement);
            Assert.True(camera.Is2D);
        }

        [Fact]
        public void Clicking_picks_sprites_too_and_the_one_drawn_on_top_where_they_overlap()
        {
            GameObject Sprite(string name, Vector3 position, int order)
            {
                var gameObject = new GameObject(name);
                gameObject.Transform.Position = position;
                gameObject.AddComponent<SpriteRenderer>().SortingOrder = order;
                return gameObject;
            }

            Sprite("Back", Vector3.Zero, order: 0);
            Sprite("Front", new Vector3(0.3f, 0f, 0f), order: 2);
            Sprite("Middle", new Vector3(-0.3f, 0f, 0f), order: 1);
            Scene.Active.ApplyChanges();

            Assert.Equal("Front", Picking.Pick(Scene.Active, new Vector3(0f, 0f, 10f), -Vector3.UnitZ)?.GameObject.Name);
            Assert.Equal("Middle", Picking.Pick(Scene.Active, new Vector3(-0.6f, 0f, 10f), -Vector3.UnitZ)?.GameObject.Name);
            Assert.Null(Picking.Pick(Scene.Active, new Vector3(3f, 0f, 10f), -Vector3.UnitZ));
        }

        // The box round every end of a collider's drawn lines.
        private static (Vector3 Min, Vector3 Max, int Lines) Drawn(Physics.Collider collider)
        {
            var lines = new List<(Vector3 From, Vector3 To)>();
            ColliderShapes.Outline(collider, lines, new Vector3(0f, 0f, 1000f));
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach ((Vector3 from, Vector3 to) in lines)
            {
                min = Vector3.Min(min, Vector3.Min(from, to));
                max = Vector3.Max(max, Vector3.Max(from, to));
            }

            return (min, max, lines.Count);
        }

        private static void Near(Vector3 expected, Vector3 actual)
        {
            Assert.True(Vector3.Distance(expected, actual) < 0.01f, $"expected {expected}, got {actual}");
        }

        [Fact]
        public void A_box_collider_is_drawn_where_the_physics_world_puts_it_scaled_turned_and_offset()
        {
            var crate = new GameObject("Crate");
            crate.Transform.Position = new Vector3(5f, 0f, 0f);
            crate.Transform.Scale = new Vector3(1f, 2f, 3f);
            crate.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            var box = crate.AddComponent<Physics.BoxCollider>();
            box.Size = new Vector3(2f, 1f, 1f);
            box.Center = new Vector3(0f, 1f, 0f);

            // Half size (1, 1, 1.5) after scaling, raised 2 by the scaled centre; turned a quarter, its X runs along Z.
            (Vector3 min, Vector3 max, int count) = Drawn(box);
            Assert.Equal(12, count);
            Near(new Vector3(3.5f, 1f, -1f), min);
            Near(new Vector3(6.5f, 3f, 1f), max);
        }

        [Fact]
        public void Spheres_capsules_and_characters_are_drawn_the_size_the_physics_world_makes_them()
        {
            // A sphere stays round, so it takes the largest axis of the scale.
            var ball = new GameObject("Ball");
            ball.Transform.Scale = new Vector3(1f, 3f, 1f);
            (Vector3 min, Vector3 max, _) = Drawn(ball.AddComponent<Physics.SphereCollider>());
            Near(new Vector3(-1.5f), min);
            Near(new Vector3(1.5f), max);

            // A capsule 2 tall and 0.5 round stands along Y.
            var pill = new GameObject("Pill");
            (min, max, _) = Drawn(pill.AddComponent<Physics.CapsuleCollider>());
            Near(new Vector3(-0.5f, -1f, -0.5f), min);
            Near(new Vector3(0.5f, 1f, 0.5f), max);

            // A character stands on its feet, at the object's position.
            var walker = new GameObject("Walker");
            walker.Transform.Position = new Vector3(0f, 3f, 0f);
            (min, max, _) = Drawn(walker.AddComponent<Physics.CharacterController>());
            Near(new Vector3(-0.4f, 3f, -0.4f), min);
            Near(new Vector3(0.4f, 4.8f, 0.4f), max);
        }

        [Fact]
        public void Only_the_collider_the_physics_world_uses_is_drawn()
        {
            var crate = new GameObject("Crate");
            var off = crate.AddComponent<Physics.BoxCollider>();
            off.Enabled = false;
            var used = crate.AddComponent<Physics.SphereCollider>();
            crate.AddComponent<Physics.BoxCollider>();

            Assert.Equal([used], ColliderShapes.Used(crate));
        }

        [Fact]
        public void A_mesh_colliders_edges_are_drawn_once_each_however_its_corners_are_split()
        {
            // The cube's faces each have their own corners (for their normals): 12 sides and a diagonal across each face.
            Assert.Equal(18 * 2, ColliderShapes.EdgesOf(Primitives.Cube).Length);
        }

        [Fact]
        public void A_line_going_behind_the_camera_is_cut_short_there_not_left_out()
        {
            var camera = new EditorCamera(new Scene());
            var view = new ViewProjection(camera, new Rect(0f, 0f, ScreenWidth, ScreenHeight));

            // From in front of the camera to well behind it: the part in front still shows.
            Vector3 eye = view.CameraPosition, ahead = view.CameraForward;
            Vector3 side = Vector3.Normalize(Vector3.Cross(ahead, Vector3.UnitY));
            var line = view.Clipped(eye + ahead * 3f + side, eye - ahead * 3f + side);
            Assert.NotNull(line);
            Assert.True(float.IsFinite(line!.Value.From.X) && float.IsFinite(line.Value.To.X));
            Assert.Null(view.Clipped(eye - ahead * 2f, eye - ahead * 3f));
        }
    }
}
