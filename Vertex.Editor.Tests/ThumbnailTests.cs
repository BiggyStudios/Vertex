using System.Diagnostics;
using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.Editor.Tests
{
    /// <summary>Thumbnails of materials and models: drawn by the renderer one at a time, kept on disk, and drawn again when they change.</summary>
    public class ThumbnailTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
        private readonly ThumbnailStage _stage = new();

        public ThumbnailTests()
        {
            Directory.CreateDirectory(_folder);
            Thumbnails.CacheFolder = Path.Combine(_folder, "Cache");
        }

        public override void Dispose()
        {
            // Thumbnails still being written to disk finish first.
            var waited = Stopwatch.StartNew();
            while (Thumbnails.Writing > 0 && waited.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);

            base.Dispose();
            Assets.Forget(Path.Combine(_folder, "Red.material"));
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        private string RedMaterial()
        {
            string path = Path.Combine(_folder, "Red.material");
            File.WriteAllText(path, SceneFile.SaveMaterial(new Material(new Color(1f, 0f, 0f))));
            return path;
        }

        /// <summary>Asks for the thumbnail until the stage has something to draw for it, as the editor does each frame.</summary>
        private RenderView WaitToDraw(string path)
        {
            var waited = Stopwatch.StartNew();
            while (true)
            {
                Thumbnails.Get(path);
                if (_stage.Next() is { } view) return view;
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "Nothing was given to draw.");
                Thread.Sleep(5);
            }
        }

        private static byte[] Grey(RenderView view) => Enumerable.Repeat((byte)200, view.Target.Width * view.Target.Height * 4).ToArray();

        [Fact]
        public void A_material_is_drawn_once_and_read_from_disk_next_time()
        {
            string path = RedMaterial();
            Assert.Null(Thumbnails.Get(path));

            RenderView view = WaitToDraw(path);
            Assert.True(view.Transparent);
            Assert.NotNull(view.Scene);
            Assert.NotSame(Scene.Active, view.Scene);
            Assert.Equal(Thumbnails.Size * 2, view.Target.Width);

            // Read back at twice the size, it's shrunk to the thumbnail's.
            view.ReadBack!(Grey(view));
            Texture? thumbnail = Thumbnails.Get(path);
            Assert.NotNull(thumbnail);
            Assert.Equal(Thumbnails.Size, thumbnail.Width);
            Assert.Equal(200, thumbnail.Pixels[0]);

            var waited = Stopwatch.StartNew();
            while (!Directory.Exists(Thumbnails.CacheFolder) || !Directory.EnumerateFiles(Thumbnails.CacheFolder, "*.thumb").Any())
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "It wasn't kept on disk.");
                Thread.Sleep(5);
            }

            // As if the editor had been opened again: it comes from disk, with nothing drawn.
            Thumbnails.Reset();
            waited.Restart();
            while (Thumbnails.Get(path) == null)
            {
                Assert.Null(_stage.Next());
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "It wasn't read from disk.");
                Thread.Sleep(5);
            }

            Assert.Equal(200, Thumbnails.Get(path)!.Pixels[0]);
        }

        [Fact]
        public void Changing_a_material_draws_it_again_while_the_old_picture_stays_shown()
        {
            string path = RedMaterial();
            RenderView view = WaitToDraw(path);
            view.ReadBack!(Grey(view));
            Texture shown = Thumbnails.Get(path)!;
            Assert.Null(_stage.Next());

            Assets.LoadMaterial(path).Color = new Color(0f, 0f, 1f);
            Assert.Same(shown, Thumbnails.Get(path));
            RenderView again = _stage.Next() ?? throw new Xunit.Sdk.XunitException("The changed material wasn't drawn again.");

            // Drawn into the same texture, so nothing showing it is left with one that's gone.
            int version = shown.Version;
            again.ReadBack!(Enumerable.Repeat((byte)90, again.Target.Width * again.Target.Height * 4).ToArray());
            Assert.Same(shown, Thumbnails.Get(path));
            Assert.Equal(90, shown.Pixels[0]);
            Assert.True(shown.Version > version);
            Assert.Null(_stage.Next());
        }

        [Fact]
        public void A_model_is_drawn_in_a_scene_of_its_own_and_its_meshes_freed_after()
        {
            string path = Path.Combine(_folder, "Ramp.obj");
            File.WriteAllText(path, "v 0 0 0\nv 2 0 0\nv 0 1 0\nv 2 1 -1\nf 1 2 3\nf 2 4 3\n");
            int objects = Scene.Active.GameObjects.Count;

            RenderView view = WaitToDraw(path);
            Assert.Equal(objects, Scene.Active.GameObjects.Count);
            MeshRenderer ramp = view.Scene!.FindComponents<MeshRenderer>().Single(r => r.Mesh != Primitives.Sphere);

            // Framed whole: the camera's looking at the middle of it.
            Vector3 middle = new(1f, 0.5f, -0.5f);
            Vector3 toMiddle = Vector3.Normalize(middle - view.Camera.Transform.WorldPosition);
            Assert.True(Vector3.Dot(toMiddle, view.Camera.Transform.Forward) > 0.999f);

            bool freed = false;
            ramp.Mesh!.Disposed += _ => freed = true;
            view.ReadBack!(Grey(view));
            Assert.NotNull(Thumbnails.Get(path));

            // Taken away when the stage is next used.
            Assert.Null(_stage.Next());
            Assert.True(freed);
            Assert.DoesNotContain(view.Scene.FindComponents<MeshRenderer>(), r => r.Mesh != Primitives.Sphere);
        }

        [Fact]
        public void One_scrolled_past_before_its_turn_is_left_until_its_asked_for_again()
        {
            string path = RedMaterial();
            Thumbnails.Get(path);
            Thread.Sleep(2200);

            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromMilliseconds(300))
            {
                Assert.Null(_stage.Next());
                Thread.Sleep(5);
            }

            // Asked for again, it's made.
            WaitToDraw(path);
        }
    }
}
