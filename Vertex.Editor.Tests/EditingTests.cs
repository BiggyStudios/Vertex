using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Input;
using Vertex.Rendering;

namespace Vertex.Editor.Tests
{
    /// <summary>Changes made in the editor, and taking them back.</summary>
    public class EditingTests : EditorTest
    {
        private readonly EditorContext _context = new();

        private static readonly System.Reflection.PropertyInfo _intensity = typeof(Light).GetProperty(nameof(Light.Intensity))!;

        [Fact]
        public void A_drag_is_one_undo_and_the_next_drag_is_another()
        {
            Light light = new GameObject("Lamp").AddComponent<Light>();

            // Dragging a number: a change a frame, all the same value.
            SetValue.Apply(_context.History, "Change Intensity", light, _intensity, 1.5f);
            SetValue.Apply(_context.History, "Change Intensity", light, _intensity, 2f);
            SetValue.Apply(_context.History, "Change Intensity", light, _intensity, 3f);
            _context.History.EndMerge();
            SetValue.Apply(_context.History, "Change Intensity", light, _intensity, 5f);

            _context.Undo();
            Assert.Equal(3f, light.Intensity);
            _context.Undo();
            Assert.Equal(1f, light.Intensity);
            Assert.False(_context.History.CanUndo);

            _context.Redo();
            _context.Redo();
            Assert.Equal(5f, light.Intensity);
        }

        [Fact]
        public void A_new_change_forgets_what_could_have_been_redone()
        {
            Light light = new GameObject("Lamp").AddComponent<Light>();
            SetValue.Apply(_context.History, "Change Intensity", light, _intensity, 2f);
            _context.Undo();

            SetValue.Apply(_context.History, "Change Intensity", light, _intensity, 4f);

            Assert.False(_context.History.CanRedo);
        }

        [Fact]
        public void Deleting_can_be_undone_with_the_object_back_where_it_was()
        {
            GameObject parent = _context.Create("Parent");
            GameObject child = _context.Create("Child", parent: parent.Transform);
            GameObject other = _context.Create("Other");
            _context.History.Clear();
            _context.Selection.Select(child);

            _context.Delete(child);
            _context.Scene.ApplyChanges();
            Assert.False(_context.InScene(child));
            Assert.Empty(parent.Transform.Children);
            Assert.Null(_context.Selection.Object);

            _context.Undo();
            Assert.True(_context.InScene(child));
            Assert.Same(parent.Transform, child.Transform.Parent);
            Assert.Equal(["Parent", "Child", "Other"], _context.Scene.GameObjects.Select(o => o.Name));
            Assert.False(child.IsDestroyed);
            _ = other;
        }

        [Fact]
        public void Creating_selects_the_new_object_and_undo_takes_it_away()
        {
            GameObject made = _context.Create("Cube", gameObject => gameObject.AddComponent<MeshRenderer>());

            Assert.Same(made, _context.Selection.Object);
            Assert.True(_context.InScene(made));

            _context.Undo();
            Assert.False(_context.InScene(made));

            _context.Redo();
            Assert.True(_context.InScene(made));
        }

        [Fact]
        public void Adding_and_removing_components_can_be_undone_in_place()
        {
            GameObject gameObject = _context.Create("Thing", g => g.AddComponent<MeshRenderer>());
            MeshRenderer renderer = gameObject.GetComponent<MeshRenderer>()!;
            var light = new Light();

            _context.History.Do(new AddComponent(gameObject, light));
            Assert.Equal<Component>([renderer, light], gameObject.Components);

            _context.History.Do(new RemoveComponent(gameObject, renderer));
            Assert.Equal<Component>([light], gameObject.Components);

            _context.Undo();
            Assert.Equal<Component>([renderer, light], gameObject.Components);
            _context.Undo();
            Assert.Equal<Component>([renderer], gameObject.Components);
        }

        [Fact]
        public void Parenting_keeps_an_object_where_it_is_in_the_world()
        {
            GameObject parent = _context.Create("Parent", g =>
            {
                g.Transform.Position = new Vector3(5f, 0f, 0f);
                g.Transform.EulerAngles = new Vector3(0f, 90f, 0f);
                g.Transform.Scale = new Vector3(2f);
            });
            GameObject child = _context.Create("Child", g => g.Transform.Position = new Vector3(1f, 2f, 3f));

            _context.History.Do(new Reparent(child, parent.Transform));
            Assert.Same(parent.Transform, child.Transform.Parent);
            Assert.True(Vector3.Distance(new Vector3(1f, 2f, 3f), child.Transform.WorldPosition) < 1e-4f);
            Assert.True(Vector3.Distance(Vector3.One, child.Transform.WorldScale) < 1e-4f);

            _context.Undo();
            Assert.Null(child.Transform.Parent);
            Assert.Equal(new Vector3(1f, 2f, 3f), child.Transform.Position);
        }

        [Fact]
        public void The_editor_deletes_with_the_delete_key_and_undoes_with_ctrl_z()
        {
            var app = new EditorApp();
            GameObject cube = app.Context.Create("Cube");
            Frame(app);

            Press(Key.Delete);
            Frame(app);
            ReleaseAll();
            Frame(app);
            Assert.False(app.Context.InScene(cube));

            Press(Key.Z, Key.ControlLeft);
            Frame(app);
            ReleaseAll();
            Frame(app);
            Assert.True(app.Context.InScene(cube));
        }

        [Fact]
        public void Tool_keys_switch_tools_but_not_while_typing()
        {
            var app = new EditorApp();
            Frame(app);

            Press(Key.E);
            Frame(app);
            ReleaseAll();
            Assert.Equal(TransformTool.Rotate, app.Context.Tool);

            // A text box has the keyboard: R is a letter, not the scale tool.
            Gui.TextInput.Focus(Gui.Ui.Id("outliner.search"));
            Frame(app);
            Press(Key.R);
            InputSystem.TypeCharacter('r');
            Frame(app);
            ReleaseAll();
            Assert.Equal(TransformTool.Rotate, app.Context.Tool);
        }
    }
}
