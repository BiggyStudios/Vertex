using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Gui;
using Vertex.Input;
using Vertex.Rendering;

namespace Vertex.Editor.Tests
{
    /// <summary>A script that points at other things in the scene, as a gun points at its muzzle.</summary>
    public sealed class Aimer : Component
    {
        public GameObject? Target { get; set; }
        public Light? Lamp { get; set; }
    }

    /// <summary>Slots in the inspector for other objects and their components: picked from a list, or dragged in from the outliner.</summary>
    public class ReferenceTests : EditorTest
    {
        private static Vector2? FindInInspector(EditorApp app, int id)
        {
            for (float y = 60f; y < ScreenHeight; y += 5f)
            {
                InputSystem.MoveMouse(new Vector2(ScreenWidth - 120f, y));
                Frame(app, 2);
                if (Ui.HotId == id) return new Vector2(ScreenWidth - 120f, y);
            }

            return null;
        }

        private static Vector2? Find(EditorApp app, int id, float x, float fromY, float toY)
        {
            for (float y = fromY; y < toY; y += 4f)
            {
                InputSystem.MoveMouse(new Vector2(x, y));
                Frame(app, 2);
                if (Ui.HotId == id) return new Vector2(x, y);
            }

            return null;
        }

        private static void Click(EditorApp app, Vector2 at)
        {
            InputSystem.MoveMouse(at);
            Frame(app);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app, 3);
        }

        [Fact]
        public void An_object_slot_picks_from_the_scenes_objects_and_a_component_slot_only_from_those_that_have_one()
        {
            var app = new EditorApp();
            GameObject muzzle = app.Context.Create("Muzzle");
            GameObject lamp = app.Context.Create("Lamp", g => g.AddComponent<Light>());
            GameObject gun = app.Context.Create("Gun", g => g.AddComponent<Aimer>());
            Aimer aimer = gun.GetComponent<Aimer>()!;
            app.Context.Selection.Select(gun);
            Frame(app, 20);

            int target = Ui.Id(RuntimeHelpers.GetHashCode(aimer), nameof(Aimer.Target));
            Vector2? slot = FindInInspector(app, target);
            Assert.NotNull(slot);
            Click(app, slot!.Value);
            Assert.True(Popup.IsOpen(target));

            // Searching narrows the list to the muzzle: it's the first choice after None.
            foreach (char c in "Muz") InputSystem.TypeCharacter(c);
            Frame(app, 3);
            Vector2? choice = Find(app, Ui.Id(target, 1), slot.Value.X, slot.Value.Y, slot.Value.Y + 200f);
            Assert.NotNull(choice);
            Click(app, choice!.Value);

            Assert.Same(muzzle, aimer.Target);
            app.Context.Undo();
            Assert.Null(aimer.Target);

            // A slot for a light offers only the lamp, and holds its light.
            int lampSlot = Ui.Id(RuntimeHelpers.GetHashCode(aimer), nameof(Aimer.Lamp));
            slot = FindInInspector(app, lampSlot);
            Assert.NotNull(slot);
            Click(app, slot!.Value);
            choice = Find(app, Ui.Id(lampSlot, 1), slot.Value.X, slot.Value.Y, slot.Value.Y + 200f);
            Assert.NotNull(choice);
            Assert.Null(Find(app, Ui.Id(lampSlot, 2), slot.Value.X, slot.Value.Y, slot.Value.Y + 200f));
            Click(app, choice!.Value);

            Assert.Same(lamp.GetComponent<Light>(), aimer.Lamp);
        }

        [Fact]
        public void An_object_dragged_from_the_outliner_drops_into_a_slot_of_the_object_selected_before()
        {
            var app = new EditorApp();
            GameObject muzzle = app.Context.Create("Muzzle");
            GameObject gun = app.Context.Create("Gun", g => g.AddComponent<Aimer>());
            Aimer aimer = gun.GetComponent<Aimer>()!;
            app.Context.Selection.Select(gun);
            Frame(app, 20);

            int target = Ui.Id(RuntimeHelpers.GetHashCode(aimer), nameof(Aimer.Target));
            Vector2? slot = FindInInspector(app, target);
            Assert.NotNull(slot);

            Vector2? row = Find(app, Ui.Id(RuntimeHelpers.GetHashCode(muzzle), "row"), 120f, 60f, 400f);
            Assert.NotNull(row);

            // Pressing the muzzle's row selects it, but once it's dragged the gun is back in the inspector to drop it on.
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            Assert.Same(muzzle, app.Context.Selection.Object);
            for (int i = 1; i <= 30; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(row!.Value, slot!.Value, i / 30f));
                Frame(app);
            }

            Assert.Same(gun, app.Context.Selection.Object);
            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app, 3);

            Assert.Same(muzzle, aimer.Target);
            Assert.Same(gun, app.Context.Selection.Object);
        }

        [Fact]
        public void An_object_dragged_within_the_outliner_is_still_selected_when_let_go()
        {
            var app = new EditorApp();
            GameObject parent = app.Context.Create("Parent");
            GameObject child = app.Context.Create("Child");
            app.Context.Selection.Select(parent);
            Frame(app, 20);

            Vector2? from = Find(app, Ui.Id(RuntimeHelpers.GetHashCode(child), "row"), 120f, 60f, 400f);
            Assert.NotNull(from);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            for (int i = 1; i <= 10; i++)
            {
                InputSystem.MoveMouse(from!.Value + new Vector2(i * 2f, 0f));
                Frame(app);
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app, 3);
            Assert.Same(child, app.Context.Selection.Object);
        }
    }
}
