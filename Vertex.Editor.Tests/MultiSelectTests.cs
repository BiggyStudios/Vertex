using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Editor.Panels;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Selecting several objects, and changing them all at once.</summary>
    public class MultiSelectTests : EditorTest
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

        private static void Click(Vector2 at, Action draw, params Key[] holding)
        {
            InputSystem.MoveMouse(at);
            UiFrame(draw);
            foreach (Key key in holding) InputSystem.PressKey(key);
            InputSystem.PressButton(MouseButton.Left);
            UiFrame(draw);
            InputSystem.ReleaseButton(MouseButton.Left);
            UiFrame(draw);
            foreach (Key key in holding) InputSystem.ReleaseKey(key);
            UiFrame(draw);
        }

        [Fact]
        public void Ctrl_and_shift_clicks_in_the_outliner_select_several()
        {
            var context = new EditorContext();
            GameObject a = context.Create("A"), b = context.Create("B"), c = context.Create("C"), d = context.Create("D");
            var outliner = new OutlinerPanel(context);
            var rect = new Rect(0f, 0f, 300f, 600f);
            void Draw() => outliner.Draw(rect);
            UiFrame(Draw);
            UiFrame(Draw);

            // Each object's row, found by feeling down the list.
            var rows = new Dictionary<GameObject, Vector2>();
            for (float y = 0f; y < 400f && rows.Count < 4; y += 4f)
            {
                InputSystem.MoveMouse(new Vector2(150f, y));
                UiFrame(Draw);
                UiFrame(Draw);
                foreach (GameObject o in new[] { a, b, c, d })
                {
                    if (Ui.HotId == Ui.Id(RuntimeHelpers.GetHashCode(o), "row")) rows.TryAdd(o, new Vector2(150f, y + 6f));
                }
            }

            Assert.Equal(4, rows.Count);

            Click(rows[a], Draw);
            Assert.Equal([a], context.Selection.Objects);

            Click(rows[c], Draw, Key.ControlLeft);
            Assert.Equal([a, c], context.Selection.Objects);
            Assert.Same(c, context.Selection.Object);

            // Shift: everything from the last row clicked (with or without Ctrl) to this one.
            Click(rows[b], Draw);
            Click(rows[d], Draw, Key.ShiftLeft);
            Assert.Equal([b, c, d], context.Selection.Objects);
            Assert.Same(d, context.Selection.Object);

            // Ctrl-clicking one that's selected leaves it out.
            Click(rows[c], Draw, Key.ControlLeft);
            Assert.Equal([b, d], context.Selection.Objects);

            // A plain click on one of them selects just it.
            Click(rows[b], Draw);
            Assert.Equal([b], context.Selection.Objects);
        }

        [Fact]
        public void Typing_a_position_into_the_inspector_moves_every_selected_object_on_that_axis_only()
        {
            var app = new EditorApp();
            GameObject first = app.Context.Create("First", g => g.Transform.Position = new Vector3(1f, 2f, 3f));
            GameObject second = app.Context.Create("Second", g => g.Transform.Position = new Vector3(4f, 5f, 6f));
            app.Context.History.Clear();
            app.Context.Selection.Select([first, second]);
            Frame(app, 2);

            // The main one (picked last) is shown: its X box, typed in.
            int position = Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(second.Transform), "transform"), "position");
            int x = Ui.Id(position, "x");
            NumberField.BeginTyping(x);
            Frame(app);
            foreach (char ch in "7") InputSystem.TypeCharacter(ch);
            Frame(app);
            Press(Key.Enter);
            Frame(app);
            ReleaseAll();
            Frame(app);

            Assert.Equal(new Vector3(7f, 2f, 3f), first.Transform.Position);
            Assert.Equal(new Vector3(7f, 5f, 6f), second.Transform.Position);

            // One undo puts both back.
            app.Context.Undo();
            Assert.Equal(new Vector3(1f, 2f, 3f), first.Transform.Position);
            Assert.Equal(new Vector3(4f, 5f, 6f), second.Transform.Position);
            Assert.False(app.Context.History.CanUndo);
        }

        [Fact]
        public void A_value_changed_on_several_components_changes_them_all_and_a_drag_is_one_undo()
        {
            var context = new EditorContext();
            Light one = new GameObject("One").AddComponent<Light>();
            Light two = new GameObject("Two").AddComponent<Light>();
            one.Intensity = 1f;
            two.Intensity = 3f;
            var grid = new PropertyGrid(context);
            void Draw() => grid.Draw(one, 0f, 0f, 400f, others: [two]);

            int id = Ui.Id(RuntimeHelpers.GetHashCode(one), "Intensity");
            UiFrame(Draw);
            NumberField.BeginTyping(id);
            UiFrame(Draw);
            foreach (char c in "2.5") InputSystem.TypeCharacter(c);
            UiFrame(Draw);
            InputSystem.PressKey(Key.Enter);
            UiFrame(Draw);

            Assert.Equal(2.5f, one.Intensity);
            Assert.Equal(2.5f, two.Intensity);

            // Changes to the same value on the same objects, one after another (a drag), join into one.
            var history = new UndoHistory();
            PropertyInfo intensity = typeof(Light).GetProperty(nameof(Light.Intensity))!;
            foreach (float value in new[] { 4f, 5f, 6f })
            {
                history.Do(new ChangeGroup("Change", [
                    new SetValue("Change", one, intensity, one.Intensity, value),
                    new SetValue("Change", two, intensity, two.Intensity, value)
                ]), ("drag", one));
            }

            history.Undo();
            Assert.Equal(2.5f, one.Intensity);
            Assert.Equal(2.5f, two.Intensity);
            Assert.False(history.CanUndo);
        }

        [Fact]
        public void Changing_one_part_of_a_vector_on_several_leaves_their_other_parts_be()
        {
            Assert.Equal(new Vector3(9f, 5f, 6f), PropertyGrid.Blend(new Vector3(1f, 2f, 3f), new Vector3(9f, 2f, 3f), new Vector3(4f, 5f, 6f)));
            Assert.Equal(new Vector2(4f, 8f), PropertyGrid.Blend(new Vector2(1f, 2f), new Vector2(1f, 8f), new Vector2(4f, 5f)));
            Assert.Equal("new", PropertyGrid.Blend("old", "new", "theirs"));
        }

        [Fact]
        public void A_component_added_with_several_selected_goes_on_them_all_as_one_undo()
        {
            var app = new EditorApp();
            GameObject first = app.Context.Create("First"), second = app.Context.Create("Second");
            app.Context.History.Clear();

            InspectorPanel inspector = app.Inspector;
            ComponentType light = ComponentCatalog.All.First(t => t.Type == typeof(Light));
            typeof(InspectorPanel).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(inspector, [new List<GameObject> { first, second }, light]);

            Assert.NotNull(first.GetComponent<Light>());
            Assert.NotNull(second.GetComponent<Light>());
            app.Context.Undo();
            Assert.Null(first.GetComponent<Light>());
            Assert.Null(second.GetComponent<Light>());
        }

        [Fact]
        public void Dragging_the_handles_moves_every_selected_object_as_one_undo()
        {
            var context = new EditorContext();
            context.Snap.Enabled = false;
            GameObject main = context.Create("Main", g => g.Transform.Position = new Vector3(0f, 1f, 0f));
            GameObject other = context.Create("Other", g => g.Transform.Position = new Vector3(3f, 0f, -2f));
            context.History.Clear();

            var camera = new SceneEditing.EditorCamera(new Scene());
            camera.Settle();
            var view = new SceneEditing.ViewProjection(camera, new Rect(0f, 0f, ScreenWidth, ScreenHeight));
            var handles = new SceneEditing.TransformHandles();
            void Draw() => handles.Draw(main.Transform, TransformTool.Move, view, context.History, context.Snap, mouseFree: true, others: [other.Transform]);

            // The handles' middle, dragged across the view.
            Vector2 centre = view.ToScreen(main.Transform.WorldPosition)!.Value;
            InputSystem.MoveMouse(centre);
            UiFrame(Draw);
            InputSystem.PressButton(MouseButton.Left);
            UiFrame(Draw);
            for (int i = 1; i <= 5; i++)
            {
                InputSystem.MoveMouse(centre + new Vector2(i * 20f, 0f));
                UiFrame(Draw);
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            UiFrame(Draw);

            Vector3 moved = main.Transform.Position - new Vector3(0f, 1f, 0f);
            Assert.True(moved.Length() > 0.5f);
            Assert.Equal(new Vector3(3f, 0f, -2f) + moved, other.Transform.Position);

            context.Undo();
            Assert.Equal(new Vector3(0f, 1f, 0f), main.Transform.Position);
            Assert.Equal(new Vector3(3f, 0f, -2f), other.Transform.Position);
            Assert.False(context.History.CanUndo);
        }

        [Fact]
        public void Deleting_and_duplicating_act_on_everything_selected()
        {
            var context = new EditorContext();
            GameObject a = context.Create("A"), b = context.Create("B");
            GameObject child = context.Create("Child", parent: a.Transform);
            context.Create("Other");

            // The child goes with its parent, so isn't copied twice.
            context.Selection.Select([a, child, b]);
            context.DuplicateSelected();
            context.Scene.ApplyChanges();
            Assert.Equal(["A", "A", "B", "B", "Other"], context.Scene.GameObjects.Where(o => o.Transform.Parent == null).Select(o => o.Name));
            Assert.Equal(2, context.Selection.Count);
            context.Undo();
            context.Scene.ApplyChanges();
            Assert.Equal(["A", "B", "Other"], context.Scene.GameObjects.Where(o => o.Transform.Parent == null).Select(o => o.Name));

            context.Selection.Select([a, b]);
            context.DeleteSelected();
            context.Scene.ApplyChanges();
            Assert.Equal(["Other"], context.Scene.GameObjects.Select(o => o.Name));
            Assert.Equal(0, context.Selection.Count);

            context.Undo();
            context.Scene.ApplyChanges();
            Assert.Equal(["A", "Child", "B", "Other"], context.Scene.GameObjects.Select(o => o.Name));
        }
    }
}
