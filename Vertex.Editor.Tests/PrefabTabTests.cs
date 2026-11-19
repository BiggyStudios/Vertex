using System.Numerics;

using Vertex.Editor.Editing;

namespace Vertex.Editor.Tests
{
    /// <summary>Prefabs opened in a tab of their own, changed there, and their copies in the scene following.</summary>
    public class PrefabTabTests : EditorTest
    {
        [Fact]
        public void A_prefab_opens_in_a_tab_is_changed_on_its_own_and_its_copies_follow_when_the_scene_shows_again()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            try
            {
                var app = new EditorApp();
                EditorContext context = app.Context;
                Scene scene = context.Scene;

                // A crate with a lid, made a prefab, and a second copy of it somewhere else.
                GameObject crate = context.Create("Crate");
                context.Create("Lid", parent: crate.Transform);
                Prefab prefab = context.CreatePrefab(crate, folder)!;
                GameObject other = context.AddPrefab(prefab.Source, new Vector3(5f, 0f, 0f))!;
                context.Selection.Select(other);
                context.MarkSaved();
                Frame(app, 2);

                // Opened: its own scene and history, shown in a tab of the scene view.
                PrefabStage stage = context.OpenPrefab(prefab.Source)!;
                Frame(app, 2);
                Assert.Same(stage, context.Stage);
                Assert.Same(stage.Scene, context.Scene);
                Assert.NotSame(scene, context.Scene);
                Assert.Same(scene, context.MainScene);
                Assert.Same(stage, Assert.Single(context.OpenPrefabs));
                Assert.Single(app.SceneView.Views);
                Assert.Empty(context.Selection.Objects);

                // What's made goes inside it, and is saved to its file once done.
                GameObject handle = context.Create("Handle");
                Assert.Same(stage.Root.Transform, handle.Transform.Parent);
                Frame(app);
                Assert.Contains("\"Handle\"", File.ReadAllText(prefab.Source));

                // Its top object is the prefab itself: it stays.
                context.Selection.Select(stage.Root);
                context.DeleteSelected();
                Assert.False(stage.Root.IsDestroyed);

                // The scene's not changed by any of it.
                Assert.False(context.Dirty);

                // Back to the scene: both copies are made again with the handle, each where it was; one undo puts them back.
                context.ShowPrefab(null);
                Frame(app);
                Assert.Same(scene, context.Scene);
                List<GameObject> copies = scene.GameObjects.Where(o => o.Prefab == prefab && !o.IsDestroyed).ToList();
                Assert.Equal(2, copies.Count);
                Assert.All(copies, copy => Assert.Contains(copy.Transform.Children, child => child.GameObject.Name == "Handle"));
                Assert.Contains(copies, copy => copy.Transform.Position == new Vector3(5f, 0f, 0f));
                Assert.Equal(new Vector3(5f, 0f, 0f), context.Selection.Object!.Transform.Position);
                Assert.Contains(context.Selection.Object, copies);
                Assert.True(context.Dirty);

                context.Undo();
                Assert.All(scene.GameObjects.Where(o => o.Prefab == prefab && !o.IsDestroyed),
                    copy => Assert.DoesNotContain(copy.Transform.Children, child => child.GameObject.Name == "Handle"));

                // Back to the prefab's tab, then closed: the scene again.
                context.OpenPrefab(prefab.Source);
                Assert.Same(stage, context.Stage);
                context.ClosePrefab(stage);
                Frame(app);
                Assert.Null(context.Stage);
                Assert.Empty(context.OpenPrefabs);
                Assert.Same(scene, context.Scene);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void Playing_goes_back_to_the_scene_first()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            try
            {
                var app = new EditorApp();
                EditorContext context = app.Context;
                Scene scene = context.Scene;
                Prefab prefab = context.CreatePrefab(context.Create("Crate"), folder)!;
                context.OpenPrefab(prefab.Source);
                Frame(app);

                context.Play.Start();
                Frame(app);
                Assert.Null(context.Stage);
                Assert.True(context.Play.Playing);
                context.Play.Stop();
                Frame(app);
                Assert.Null(context.Stage);
                Assert.Contains(context.Scene.GameObjects, o => o.Name == "Crate" && o.Prefab == prefab);

                // And a prefab can't be opened while playing.
                context.Play.Start();
                Assert.Null(context.OpenPrefab(prefab.Source));
                context.Play.Stop();
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void A_value_changed_in_the_inspector_inside_a_prefab_goes_in_the_prefabs_history_and_is_saved()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            try
            {
                var context = new EditorContext();
                GameObject lamp = context.Create("Lamp");
                lamp.AddComponent<Vertex.Rendering.Light>().Type = Vertex.Rendering.LightType.Point;
                Prefab prefab = context.CreatePrefab(lamp, folder)!;
                context.MarkSaved();

                // Made while the scene showed, as the inspector's is.
                var grid = new Vertex.Editor.Inspection.PropertyGrid(context);
                PrefabStage stage = context.OpenPrefab(prefab.Source)!;
                var light = stage.Root.GetComponent<Vertex.Rendering.Light>()!;

                void Draw()
                {
                    Time.Advance(1.0 / 60.0);
                    Vertex.UI.Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
                    Gui.Ui.BeginFrame(1f / 60f);
                    grid.Draw(light, 0f, 0f, 400f);
                    Gui.Ui.EndFrame();
                    Vertex.UI.Canvas.EndFrame();
                    Input.InputSystem.EndFrame();
                }

                Draw();
                Gui.NumberField.BeginTyping(Gui.Ui.Id(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(light), "Range"));
                Draw();
                foreach (char c in "25") Input.InputSystem.TypeCharacter(c);
                Draw();
                Input.InputSystem.PressKey(Input.Key.Enter);
                Draw();
                Input.InputSystem.ReleaseKey(Input.Key.Enter);
                Draw();

                Assert.Equal(25f, light.Range);
                Assert.True(stage.History.CanUndo);
                Assert.False(context.Dirty);
                context.Update();
                Assert.Contains("25", File.ReadAllText(prefab.Source));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }
    }
}
