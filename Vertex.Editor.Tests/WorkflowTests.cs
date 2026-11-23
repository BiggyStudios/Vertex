using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Projects, scenes, play mode, and other things done with the editor as a whole.</summary>
    public class WorkflowTests : EditorTest
    {
        private static void UiFrame(Action draw)
        {
            Time.Advance(1.0 / 60.0);
            Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
            Ui.BeginFrame(1f / 60f);
            draw();
            Ui.EndFrame();
            Canvas.EndFrame();
            InputSystem.EndFrame();
        }

        [Fact]
        public void A_number_typed_into_the_inspector_sets_a_float()
        {
            var context = new EditorContext();
            Light light = new GameObject("Lamp").AddComponent<Light>();
            var grid = new PropertyGrid(context);
            var failures = new List<string>();
            context.Notified += failures.Add;
            void Draw() => grid.Draw(light, 0f, 0f, 400f);

            // The intensity box, typed in.
            int id = Ui.Id(RuntimeHelpers.GetHashCode(light), "Intensity");
            UiFrame(Draw);
            NumberField.BeginTyping(id);
            UiFrame(Draw);
            foreach (char c in "2.5") InputSystem.TypeCharacter(c);
            UiFrame(Draw);
            InputSystem.PressKey(Key.Enter);
            UiFrame(Draw);

            Assert.Empty(failures);
            Assert.Equal(2.5f, light.Intensity);
            Assert.True(context.History.CanUndo);
        }

        [Fact]
        public void A_number_typed_into_the_inspector_counts_when_clicked_away_from_without_Enter()
        {
            var app = new EditorApp();
            GameObject first = app.Context.Create("First");
            GameObject second = app.Context.Create("Second");
            app.Context.Selection.Select(first);
            Frame(app, 2);

            int x = Ui.Id(Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(first.Transform), "transform"), "position"), "x");
            void TypeX(string text)
            {
                NumberField.BeginTyping(x);
                Frame(app);
                foreach (char c in text) InputSystem.TypeCharacter(c);
                Frame(app);
            }

            void ClickAt(Vector2 at)
            {
                InputSystem.MoveMouse(at);
                Frame(app);
                InputSystem.PressButton(MouseButton.Left);
                Frame(app);
                InputSystem.ReleaseButton(MouseButton.Left);
                Frame(app, 2);
            }

            // Clicking empty space.
            TypeX("7");
            ClickAt(new Vector2(ScreenWidth - 150f, ScreenHeight - 60f));
            Assert.Equal(7f, first.Transform.Position.X);
            Assert.Equal(0, Ui.FocusId);

            // Clicking another object in the outliner: the number goes to the object it was typed for, then the other's selected.
            int secondRow = Ui.Id(RuntimeHelpers.GetHashCode(second), "row");
            Vector2? row = null;
            for (float y = 60f; y < 500f && row == null; y += 6f)
            {
                InputSystem.MoveMouse(new Vector2(150f, y));
                Frame(app, 2);
                if (Ui.HotId == secondRow) row = new Vector2(150f, y);
            }

            Assert.NotNull(row);
            TypeX("3");
            ClickAt(row!.Value);
            Assert.Equal(3f, first.Transform.Position.X);
            Assert.Equal(0f, second.Transform.Position.X);
            Assert.Same(second, app.Context.Selection.Object);
        }

        [Fact]
        public void Text_typed_into_the_inspector_changes_as_it_is_typed_and_undoes_in_one_go()
        {
            var context = new EditorContext();
            Vertex.UI.UIText label = new GameObject("Title").AddComponent<Vertex.UI.UIText>();
            label.Text = "";
            var grid = new PropertyGrid(context);
            void Draw() => grid.Draw(label, 0f, 0f, 400f);

            int id = Ui.Id(RuntimeHelpers.GetHashCode(label), "Text");
            UiFrame(Draw);
            TextInput.Focus(id);
            UiFrame(Draw);

            // Each letter shows in the scene straight away, without pressing Enter.
            foreach (string sofar in new[] { "P", "Pl", "Pla", "Play" })
            {
                InputSystem.TypeCharacter(sofar[^1]);
                UiFrame(Draw);
                Assert.Equal(sofar, label.Text);
            }

            InputSystem.PressKey(Key.Enter);
            UiFrame(Draw);
            InputSystem.ReleaseKey(Key.Enter);
            UiFrame(Draw);

            context.Undo();
            Assert.Equal("", label.Text);
            Assert.False(context.History.CanUndo);
        }

        [Fact]
        public void Stopping_play_goes_back_to_the_tab_that_was_showing()
        {
            var app = new EditorApp();
            Frame(app);

            // From the scene view: the game shows while playing, then the scene again.
            app.Context.Play.Start();
            Frame(app);
            Assert.True(app.SceneView.ShowingGame);
            app.Context.Play.Stop();
            Frame(app);
            Assert.False(app.SceneView.ShowingGame);

            // From the game's picture (arranging UI): it stays there.
            app.SceneView.ShowGame();
            Frame(app);
            app.Context.Play.Start();
            Frame(app);
            app.Context.Play.Stop();
            Frame(app);
            Assert.True(app.SceneView.ShowingGame);
        }

        /// <summary>Notes what the editor asked of its window.</summary>
        private sealed class FakeWindow : IWindowControls
        {
            public List<bool> Fullscreen { get; } = new();
            public bool Maximized => false;
            public void Minimize() { }
            public void ToggleMaximize() { }
            public void Close() { }
            public void BeginMove() { }
            public void SetFullscreen(bool fullscreen) => Fullscreen.Add(fullscreen);
        }

        [Fact]
        public void Fullscreen_on_play_fills_the_screen_with_the_game_until_F11_or_stopping()
        {
            var window = new FakeWindow();
            var app = new EditorApp(window: window);
            Frame(app);

            // Off: playing stays in the editor.
            app.Context.Play.Start();
            Frame(app);
            Assert.False(app.ShowingFullscreen);
            Assert.Empty(window.Fullscreen);
            app.Context.Play.Stop();
            Frame(app);

            // On: the window covers the screen, and the game's picture all of it.
            app.Context.Preferences.FullscreenOnPlay = true;
            app.Context.Play.Start();
            Frame(app);
            Assert.True(app.ShowingFullscreen);
            Assert.Equal([true], window.Fullscreen);
            Assert.Equal(new Rect(0f, 0f, ScreenWidth, ScreenHeight), app.SceneView.GameRect);

            // F11 brings the editor back while the game plays on, and again fills the screen.
            Press(Key.F11);
            Frame(app);
            ReleaseAll();
            Frame(app);
            Assert.True(app.Context.Play.Playing);
            Assert.False(app.ShowingFullscreen);
            Assert.True(app.SceneView.GameRect.Y > 0f);
            Press(Key.F11);
            Frame(app);
            ReleaseAll();
            Frame(app);
            Assert.True(app.ShowingFullscreen);

            // Ctrl+P stops, even with the game holding the keys, and the window goes back.
            Assert.True(app.Context.Play.GameFocused);
            Press(Key.P, Key.ControlLeft);
            Frame(app);
            ReleaseAll();
            Frame(app);
            Assert.False(app.Context.Play.Playing);
            Assert.False(app.ShowingFullscreen);
            Assert.Equal([true, false, true, false], window.Fullscreen);
        }

        /// <summary>Adds up the mouse movement the game sees, and the frames it sees the left button down.</summary>
        public sealed class MouseWatcher : Component
        {
            public static Vector2 Moved;
            public static int Clicks;

            protected internal override void Update()
            {
                Moved += InputSystem.MouseDelta;
                if (InputSystem.GetMouseButtonDown(MouseButton.Left) || InputSystem.GetMouseButton(MouseButton.Left)) Clicks++;
            }
        }

        [Fact]
        public void Escape_shows_the_cursor_and_the_game_still_turns_with_it_until_a_click_hides_it_again()
        {
            var app = new EditorApp();
            app.Context.Create("Watcher", g => g.AddComponent<MouseWatcher>());
            Frame(app);
            app.Context.Play.Start();
            Frame(app, 2);
            Assert.True(app.Context.Play.GameFocused);
            Rect game = app.SceneView.GameRect;
            var middle = new Vector2(game.X + game.Width * 0.5f, game.Y + game.Height * 0.5f);
            InputSystem.MoveMouse(middle);
            Frame(app);

            // The cursor isn't locked: Escape is the game's, for its own menus.
            InputSystem.PressKey(Key.Escape);
            Frame(app);
            InputSystem.ReleaseKey(Key.Escape);
            Frame(app);
            Assert.True(app.Context.Play.GameFocused);
            Assert.False(InputSystem.CursorFreed);

            // A camera has taken it: Escape shows it, but the game keeps it.
            InputSystem.CursorLocked = true;
            Frame(app);
            InputSystem.PressKey(Key.Escape);
            Frame(app);
            InputSystem.ReleaseKey(Key.Escape);
            Frame(app);
            Assert.True(InputSystem.CursorFreed);
            Assert.True(InputSystem.CursorLocked);
            Assert.True(app.Context.Play.GameFocused);

            // Moving over the game still steers it (from where the cursor showed up).
            InputSystem.MoveMouse(middle);
            Frame(app);
            MouseWatcher.Moved = Vector2.Zero;
            InputSystem.MoveMouse(middle + new Vector2(0f, 30f));
            Frame(app);
            Assert.Equal(30f, MouseWatcher.Moved.Y, 0.01f);

            // Off the game's picture, it doesn't.
            InputSystem.MoveMouse(new Vector2(game.Right + 40f, middle.Y));
            Frame(app);
            MouseWatcher.Moved = Vector2.Zero;
            InputSystem.MoveMouse(new Vector2(game.Right + 40f, middle.Y + 30f));
            Frame(app);
            Assert.Equal(Vector2.Zero, MouseWatcher.Moved);

            // Clicking the game hides the cursor again; the game doesn't take the click as a shot.
            InputSystem.MoveMouse(middle);
            Frame(app);
            MouseWatcher.Clicks = 0;
            InputSystem.PressButton(MouseButton.Left);
            Frame(app, 3);
            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app);
            Assert.False(InputSystem.CursorFreed);
            Assert.True(InputSystem.CursorLocked);
            Assert.True(app.Context.Play.GameFocused);
            Assert.Equal(0, MouseWatcher.Clicks);

            // Shift+F1 still gives the editor the mouse.
            InputSystem.PressKey(Key.ShiftLeft);
            InputSystem.PressKey(Key.F1);
            Frame(app);
            InputSystem.ReleaseKey(Key.F1);
            InputSystem.ReleaseKey(Key.ShiftLeft);
            Frame(app);
            Assert.False(InputSystem.CursorLocked);
            Assert.False(app.Context.Play.GameFocused);
            app.Context.Play.Stop();
        }

        [Fact]
        public void Panning_holds_the_cursor_like_looking_around_and_lets_it_go_after()
        {
            var app = new EditorApp();
            InputSystem.MoveMouse(new Vector2(ScreenWidth * 0.5f, ScreenHeight * 0.4f));
            Frame(app, 2);
            Vector3 pivot = app.SceneView.Camera.Pivot;

            InputSystem.PressButton(MouseButton.Middle);
            Frame(app);
            Assert.True(InputSystem.CursorLocked);

            for (int i = 1; i <= 5; i++)
            {
                InputSystem.MoveMouse(new Vector2(ScreenWidth * 0.5f + i * 20f, ScreenHeight * 0.4f));
                Frame(app);
                Assert.True(InputSystem.CursorLocked);
            }

            Assert.NotEqual(pivot, app.SceneView.Camera.Pivot);

            InputSystem.ReleaseButton(MouseButton.Middle);
            Frame(app);
            Assert.False(InputSystem.CursorLocked);
        }

        [Fact]
        public void Play_runs_the_scene_and_stop_puts_it_back()
        {
            var context = new EditorContext();
            GameObject mover = context.Create("Mover", g => g.AddComponent<Recorder>());
            mover.Transform.Position = new Vector3(1f, 2f, 3f);
            context.MarkSaved();

            context.Play.Start();
            Assert.True(context.Play.Playing);
            context.Play.Step();
            context.Play.Step();

            // Changes while playing: something moved, something made.
            context.Scene.Find("Mover")!.Transform.Position = new Vector3(9f);
            new GameObject("Made while playing");
            Assert.Equal(["Start", "Update", "Update"], context.Scene.Find("Mover")!.GetComponent<Recorder>()!.Calls);

            context.Play.Stop();
            context.Scene.ApplyChanges();
            Assert.False(context.Play.Playing);
            Assert.Equal(["Mover"], context.Scene.GameObjects.Select(o => o.Name));
            Assert.Equal(new Vector3(1f, 2f, 3f), context.Scene.Find("Mover")!.Transform.Position);
            Assert.Empty(context.Scene.Find("Mover")!.GetComponent<Recorder>()!.Calls);
            Assert.False(context.Dirty);
        }

        [Fact]
        public void Duplicating_copies_the_object_and_what_is_under_it_just_after_it()
        {
            var context = new EditorContext();
            GameObject first = context.Create("First");
            GameObject original = context.Create("Original", g => g.AddComponent<Light>().Intensity = 4f);
            context.Create("Child", parent: original.Transform);
            context.Create("Last");

            GameObject copy = context.Duplicate(original)!;
            context.Scene.ApplyChanges();

            Assert.Equal(["First", "Original", "Original", "Last"],
                context.Scene.GameObjects.Where(o => o.Transform.Parent == null).Select(o => o.Name));
            Assert.Equal(4f, copy.GetComponent<Light>()!.Intensity);
            Assert.Single(copy.Transform.Children);
            Assert.Same(copy, context.Selection.Object);

            context.Undo();
            Assert.False(context.InScene(copy));
            _ = first;
        }

        [Fact]
        public void Objects_can_be_moved_between_others_and_back()
        {
            var context = new EditorContext();
            GameObject a = context.Create("A"), b = context.Create("B"), c = context.Create("C");

            context.History.Do(new MoveInHierarchy(c, null, 0));
            Assert.Equal(["C", "A", "B"], context.Scene.GameObjects.Select(o => o.Name));

            context.History.Do(new MoveInHierarchy(b, c.Transform, 0));
            Assert.Same(c.Transform, b.Transform.Parent);

            context.Undo();
            context.Undo();
            Assert.Null(b.Transform.Parent);
            Assert.Equal(["A", "B", "C"], context.Scene.GameObjects.Where(o => o.Transform.Parent == null).Select(o => o.Name));
            _ = a;
        }

        [Fact]
        public void A_saved_scene_opens_again_and_is_not_marked_changed()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            try
            {
                var context = new EditorContext();
                context.NewScene();
                context.Create("Saved", g => g.Transform.Position = new Vector3(5f, 0f, 0f));
                Assert.True(context.Dirty);

                string path = Path.Combine(folder, "Level.scene");
                Directory.CreateDirectory(folder);
                Vertex.Serialization.SceneFile.Save(context.Scene, path);
                context.OpenScene(path);

                Assert.Equal("Level", context.SceneName);
                Assert.False(context.Dirty);
                Assert.Equal(new Vector3(5f, 0f, 0f), context.Scene.Find("Saved")!.Transform.Position);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void A_games_csproj_opens_as_a_project_with_its_code_set_up()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "Assets", "Textures"));
                File.WriteAllText(Path.Combine(folder, "MyGame.csproj"), "<Project />");
                File.WriteAllBytes(Path.Combine(folder, "Assets", "Textures", "Brick.png"), []);

                Project project = Project.Open(Path.Combine(folder, "MyGame.csproj"));

                Assert.Equal(Path.Combine(folder, "MyGame.vertexproject"), project.FilePath);
                Assert.Equal(Path.Combine(folder, "MyGame.csproj"), project.GameProjectFile);
                Assert.Equal(project.FilePath, Project.Open(folder).FilePath);

                using var assets = new AssetDatabase(project.AssetsDirectory);
                AssetEntry brick = Assert.Single(assets.All(AssetKind.Texture));
                Assert.Equal("Textures/Brick.png", assets.Relative(brick.Path));
                Assert.Equal(AssetKind.Folder, assets.List(project.AssetsDirectory)[0].Kind);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void A_minimised_window_does_not_break_the_layout()
        {
            var app = new EditorApp();
            foreach ((int width, int height) in new[] { (1, 1), (120, 80), (400, 300) })
            {
                Time.Advance(1.0 / 60.0);
                Canvas.BeginFrame(width, height, height);
                app.Update(1f / 60f);
                Canvas.EndFrame();
                InputSystem.EndFrame();
            }
        }

        [Fact]
        public void F2_renames_a_new_object_straight_away()
        {
            var app = new EditorApp();
            Frame(app);
            GameObject made = app.Context.Create("Cube");
            Frame(app);

            Press(Key.F2);
            Frame(app);
            ReleaseAll();
            Frame(app);

            Assert.NotEqual(0, Ui.FocusId);
            foreach (char c in "Crate") InputSystem.TypeCharacter(c);
            Frame(app);
            Press(Key.Enter);
            Frame(app);
            ReleaseAll();

            Assert.Equal("Crate", made.Name);
        }
    }
}
