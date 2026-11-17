using System.Numerics;

using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Tests
{
    /// <summary>What the engine offers an editor: editing a scene without running it, rotations as angles, and the 2D layer's shapes and clipping.</summary>
    public class EditorSupportTests : EngineTest
    {
        public override void Dispose()
        {
            Canvas.FixedScale = 0f;
            base.Dispose();
        }

        [Theory]
        [InlineData(0f, 0f, 0f)]
        [InlineData(30f, 45f, 10f)]
        [InlineData(-80f, 170f, -60f)]
        [InlineData(10f, -120f, 175f)]
        public void Euler_angles_read_back_as_set(float pitch, float yaw, float roll)
        {
            var gameObject = new GameObject();
            gameObject.Transform.EulerAngles = new Vector3(pitch, yaw, roll);

            Vector3 read = gameObject.Transform.EulerAngles;
            Assert.True(Vector3.Distance(new Vector3(pitch, yaw, roll), read) < 0.01f, $"Got {read}");
        }

        [Fact]
        public void Angles_read_straight_up_still_turn_the_same_way()
        {
            Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0.7f, MathF.PI / 2f, 0.3f);
            Vector3 angles = Transform.ToEuler(rotation);
            Quaternion again = Quaternion.CreateFromYawPitchRoll(angles.Y * MathF.PI / 180f, angles.X * MathF.PI / 180f, angles.Z * MathF.PI / 180f);

            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, rotation), up = Vector3.Transform(Vector3.UnitY, rotation);
            Assert.True(Vector3.Distance(forward, Vector3.Transform(-Vector3.UnitZ, again)) < 1e-3f);
            Assert.True(Vector3.Distance(up, Vector3.Transform(Vector3.UnitY, again)) < 1e-3f);
        }

        [Fact]
        public void Changes_can_show_without_running_anything()
        {
            Recorder recorder = new GameObject("New").AddComponent<Recorder>();
            Assert.Empty(Scene.Active.GameObjects);

            Scene.Active.ApplyChanges();

            Assert.Single(Scene.Active.GameObjects);
            Assert.Empty(recorder.Calls);
        }

        [Fact]
        public void A_detached_object_and_its_children_go_and_come_back_in_place()
        {
            var first = new GameObject("First");
            var parent = new GameObject("Parent");
            var child = new GameObject("Child");
            child.Transform.Parent = parent.Transform;
            var last = new GameObject("Last");
            Scene.Active.ApplyChanges();

            int index = Scene.Active.Detach(parent);
            Assert.Equal(["First", "Last"], Scene.Active.GameObjects.Select(o => o.Name));
            Assert.False(parent.IsDestroyed);

            Scene.Active.Attach(parent, index);
            Assert.Equal(["First", "Parent", "Child", "Last"], Scene.Active.GameObjects.Select(o => o.Name));
            Assert.Same(parent.Transform, child.Transform.Parent);
        }

        [Fact]
        public void A_component_can_be_taken_off_and_put_back_without_being_destroyed()
        {
            var gameObject = new GameObject();
            Recorder first = gameObject.AddComponent<Recorder>();
            Light light = gameObject.AddComponent<Light>();

            int index = gameObject.DetachComponent(first);
            Assert.Equal([light], gameObject.Components);
            Assert.Empty(first.Calls);

            gameObject.AttachComponent(first, index);
            Assert.Equal<Component>([first, light], gameObject.Components);
        }

        [Fact]
        public void Clipping_leaves_out_what_is_outside_and_cuts_what_crosses_the_edge()
        {
            Canvas.FixedScale = 1f;
            Canvas.BeginFrame(800, 600);
            Canvas.PushClip(new Vector2(100f, 100f), new Vector2(200f, 100f));

            Canvas.DrawRect(new Vector2(500f, 500f), new Vector2(10f), Color.White);
            Canvas.DrawRect(new Vector2(150f, 120f), new Vector2(20f), Color.White);
            Canvas.DrawRect(new Vector2(280f, 150f), new Vector2(50f), Color.White);
            Canvas.PopClip();
            Canvas.DrawRect(new Vector2(500f, 500f), new Vector2(10f), Color.White);
            Canvas.EndFrame();

            IReadOnlyList<CanvasQuad> quads = Canvas.ReadyQuads;
            Assert.Equal(3, quads.Count);
            Assert.Equal(Vector4.Zero, quads[0].Clip);
            Assert.Equal(new Vector4(100f, 100f, 300f, 200f), quads[1].Clip);
            Assert.Equal(Vector4.Zero, quads[2].Clip);
        }

        [Fact]
        public void Rounded_rectangles_are_cut_to_shape_on_the_gpu_and_plain_ones_are_not()
        {
            Canvas.FixedScale = 2f;
            Canvas.BeginFrame(800, 600);
            Canvas.DrawRect(new Vector2(10f, 20f), new Vector2(30f, 40f), Color.White, radius: 4f);
            Canvas.DrawRect(new Vector2(10f, 20f), new Vector2(30f, 40f), Color.White);
            Canvas.EndFrame();

            CanvasQuad rounded = Canvas.ReadyQuads[0];
            Assert.Equal(new Vector4(50f, 80f, 30f, 40f), rounded.Shape);
            Assert.Equal(8f, rounded.ShapeStyle.X);
            Assert.Equal(Vector4.Zero, Canvas.ReadyQuads[1].Shape);
        }

        [Fact]
        public void A_fixed_scale_keeps_the_ui_the_same_size_in_any_window()
        {
            Canvas.FixedScale = 1.25f;
            Canvas.BeginFrame(1920, 1080);
            Assert.Equal(1536f, Canvas.Width, 0.01f);

            Canvas.BeginFrame(1280, 720);
            Assert.Equal(1.25f, Canvas.Scale);
        }

        [Fact]
        public void Extra_views_draw_after_the_scenes_own_cameras_and_before_the_screen()
        {
            var backend = new RendererTests.RecordingBackend();
            var renderer = new Renderer(backend);
            renderer.Resize(1280, 720);
            new GameObject("Cube").AddComponent<MeshRenderer>().Mesh = Primitives.Cube;
            var monitor = new GameObject("Monitor").AddComponent<Camera>();
            monitor.Target = new RenderTexture(64, 64);
            Scene.Active.Update();

            // The editor's camera, in a scene of its own.
            var editorCamera = new GameObject("Editor camera", new Scene()).AddComponent<Camera>();
            editorCamera.Transform.Position = new Vector3(0f, 0f, 5f);
            var view = new RenderTexture(320, 200);

            renderer.Render(Scene.Active, screenCamera: null, [new RenderView(editorCamera, view)]);

            Assert.Equal(3, backend.Frames.Count);
            Assert.Same(monitor.Target, backend.Frames[0].Target);
            Assert.Equal((view, true, 320, 200, 1, 0), backend.Frames[1]);
            Assert.Equal((null, false), (backend.Frames[2].Target, backend.Frames[2].HasCamera));
        }
    }
}
