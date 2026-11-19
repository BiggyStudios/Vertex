using System.Numerics;

using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Tests
{
    /// <summary>Sprites, world text and orthographic cameras: what reaches the renderer, in what order, and where.</summary>
    public class FlatRenderingTests : EngineTest
    {
        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;

        public FlatRenderingTests()
        {
            _renderer = new Renderer(_backend);
            _renderer.Resize(1280, 720);
        }

        /// <summary>A camera at <paramref name="position"/> looking along -Z, the way 2D games look at their world.</summary>
        private static Camera Camera2D(Vector3 position, bool orthographic = true)
        {
            var gameObject = new GameObject("Camera");
            gameObject.Transform.Position = position;
            Camera camera = gameObject.AddComponent<Camera>();
            if (orthographic) camera.Projection = CameraProjection.Orthographic;
            return camera;
        }

        private static SpriteRenderer Sprite(string name, Vector3 position, int order = 0, Texture? texture = null)
        {
            var gameObject = new GameObject(name);
            gameObject.Transform.Position = position;
            SpriteRenderer sprite = gameObject.AddComponent<SpriteRenderer>();
            sprite.SortingOrder = order;
            sprite.Texture = texture;
            return sprite;
        }

        private List<WorldQuad> Draw()
        {
            Scene.Active.Update();
            _renderer.Render(Scene.Active);
            return _backend.LastWorldQuads;
        }

        private static Vector3 Middle(WorldQuad quad) => (quad.TopLeft + quad.TopRight + quad.BottomRight + quad.BottomLeft) / 4f;

        [Fact]
        public void Sprites_draw_by_sorting_order_then_furthest_first_and_ones_out_of_view_are_left_out()
        {
            Camera2D(new Vector3(0f, 0f, 10f));
            Sprite("Near", new Vector3(0f, 0f, 2f));
            Sprite("Far", new Vector3(1f, 0f, -2f));
            Sprite("On top", new Vector3(-1f, 0f, -5f), order: 1);
            Sprite("Behind", new Vector3(2f, 0f, 5f), order: -1);
            Sprite("Off screen", new Vector3(500f, 0f, 0f));

            List<WorldQuad> quads = Draw();

            Assert.Equal([2f, 1f, 0f, -1f], quads.Select(q => Middle(q).X));
        }

        [Fact]
        public void A_sprite_is_as_big_as_its_frames_pixels_and_shows_its_frame_of_the_sheet()
        {
            Camera2D(new Vector3(0f, 0f, 10f));
            var sheet = new Texture(64, 32, new byte[64 * 32 * 4]);
            SpriteRenderer sprite = Sprite("Coin", Vector3.Zero, texture: sheet);
            sprite.Columns = 4;
            sprite.Rows = 2;
            sprite.Frame = 5;
            sprite.PixelsPerUnit = 16f;
            sprite.Pivot = new Vector2(0.5f, 0f);
            sprite.Transform.Scale = new Vector3(2f);

            WorldQuad quad = Assert.Single(Draw());

            // Frames are 16 × 16 pixels: one unit, doubled by the scale; standing on the object's position.
            Assert.Equal(new Vector3(-1f, 2f, 0f), quad.TopLeft);
            Assert.Equal(new Vector3(1f, 0f, 0f), quad.BottomRight);

            // Frame 5 is the second in the second row.
            Assert.Equal(new Vector2(0.25f, 0.5f), quad.UvMin);
            Assert.Equal(new Vector2(0.5f, 1f), quad.UvMax);

            sprite.FlipX = true;
            quad = Assert.Single(Draw());
            Assert.Equal(0.5f, quad.UvMin.X);
            Assert.Equal(0.25f, quad.UvMax.X);
        }

        [Fact]
        public void A_billboard_faces_the_camera_wherever_it_is()
        {
            Camera camera = Camera2D(new Vector3(10f, 0f, 0f), orthographic: false);
            camera.Transform.LookAt(Vector3.Zero);
            SpriteRenderer sprite = Sprite("Tree", Vector3.Zero);
            sprite.Billboard = Billboard.FaceCamera;

            WorldQuad quad = Assert.Single(Draw());

            Vector3 normal = Vector3.Normalize(Vector3.Cross(quad.TopRight - quad.TopLeft, quad.BottomLeft - quad.TopLeft));
            Assert.True(MathF.Abs(Vector3.Dot(normal, Vector3.UnitX)) > 0.999f, $"It faces {normal}.");
            // Upright, as seen: its top is up.
            Assert.True(quad.TopLeft.Y > quad.BottomLeft.Y);
        }

        [Fact]
        public void World_text_is_a_quad_per_letter_from_the_font_lined_up_on_its_object()
        {
            Camera2D(new Vector3(0f, 0f, 10f));
            var gameObject = new GameObject("Sign");
            gameObject.Transform.Position = new Vector3(3f, 1f, 0f);
            WorldText text = gameObject.AddComponent<WorldText>();
            text.Text = "Hi there";
            text.Size = 1f;

            List<WorldQuad> quads = Draw();

            Assert.Equal(7, quads.Count);
            Assert.All(quads, q => Assert.True(q.PremultipliedTexture));

            // Centred on the object, both ways, and a line tall.
            Vector3 min = quads.Aggregate(new Vector3(float.MaxValue), (m, q) => Vector3.Min(m, Vector3.Min(q.TopLeft, q.BottomRight)));
            Vector3 max = quads.Aggregate(new Vector3(float.MinValue), (m, q) => Vector3.Max(m, Vector3.Max(q.TopLeft, q.BottomRight)));
            Assert.Equal(3f, (min.X + max.X) * 0.5f, 0.15f);
            Assert.InRange(max.Y - min.Y, 0.5f, 1.05f);
            Assert.InRange((min.Y + max.Y) * 0.5f, 0.8f, 1.2f);

            // The first letter is on the left, as read.
            Assert.True(Middle(quads[0]).X < Middle(quads[^1]).X);

            // A second line goes below, and a shadow doubles the letters.
            text.Text = "Hi\nthere";
            text.ShadowColor = new Color(0f, 0f, 0f, 0.5f);
            quads = Draw();
            Assert.Equal(14, quads.Count);
            Assert.True(quads.Skip(9).Select(Middle).Average(m => m.Y) < quads.Skip(7).Take(2).Select(Middle).Average(m => m.Y));
        }

        [Fact]
        public void An_orthographic_camera_sees_a_fixed_height_of_the_world_whatever_the_distance()
        {
            Camera camera = Camera2D(new Vector3(0f, 0f, 10f));
            camera.OrthographicSize = 5f;
            var screen = new Vector2(1280f, 720f);

            // Five units up is the top of the screen, near or far.
            Assert.Equal(0f, camera.WorldToScreen(new Vector3(0f, 5f, 0f), screen)!.Value.Y, 0.01f);
            Assert.Equal(0f, camera.WorldToScreen(new Vector3(0f, 5f, -50f), screen)!.Value.Y, 0.01f);
            Assert.Null(camera.WorldToScreen(new Vector3(0f, 0f, 20f), screen));

            // And back: the screen's top-right corner, on the plane through the origin.
            Vector3 corner = camera.ScreenToWorld(new Vector2(1280f, 0f), screen, depth: 10f);
            Assert.Equal(5f * 1280f / 720f, corner.X, 0.01f);
            Assert.Equal(5f, corner.Y, 0.01f);
            Assert.Equal(0f, corner.Z, 0.01f);

            (Vector3 origin, Vector3 direction) = camera.ScreenToRay(new Vector2(640f, 360f), screen);
            Assert.Equal(-Vector3.UnitZ, direction);
            Assert.Equal(0f, origin.X, 0.001f);
        }

        [Fact]
        public void A_perspective_cameras_ray_goes_through_the_point_on_the_screen()
        {
            Camera camera = Camera2D(new Vector3(0f, 2f, 10f), orthographic: false);
            var screen = new Vector2(1280f, 720f);
            var point = new Vector3(3f, 1f, -4f);

            Vector2 onScreen = camera.WorldToScreen(point, screen)!.Value;
            (Vector3 origin, Vector3 direction) = camera.ScreenToRay(onScreen, screen);

            Vector3 toPoint = Vector3.Normalize(point - origin);
            Assert.True(Vector3.Dot(toPoint, direction) > 0.9999f);
        }

        [Fact]
        public void A_sprite_animator_steps_through_its_frames_and_loops_or_stops()
        {
            SpriteRenderer sprite = Sprite("Coin", Vector3.Zero, texture: new Texture(80, 16, new byte[80 * 16 * 4]));
            sprite.Columns = 5;
            SpriteAnimator animator = sprite.GameObject.AddComponent<SpriteAnimator>();
            animator.FirstFrame = 1;
            animator.FrameCount = 3;
            animator.FramesPerSecond = 10f;

            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 1, 0.01);
            Assert.Equal(1, sprite.Frame);
            RunFrames(loop, 1, 0.1);
            Assert.Equal(2, sprite.Frame);
            RunFrames(loop, 2, 0.1);
            Assert.Equal(1, sprite.Frame);

            bool finished = false;
            animator.Finished += () => finished = true;
            animator.Loop = false;
            animator.Play();
            RunFrames(loop, 5, 0.1);
            Assert.True(finished);
            Assert.False(animator.IsPlaying);
            Assert.Equal(3, sprite.Frame);
        }
    }
}
