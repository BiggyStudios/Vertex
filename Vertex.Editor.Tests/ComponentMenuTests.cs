using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Gui;
using Vertex.Input;
using Vertex.Physics;

namespace Vertex.Editor.Tests
{
    /// <summary>A component's menu in the inspector (move it, remove it): from its ⋯ button, or a right-click on its header.</summary>
    public class ComponentMenuTests : EditorTest
    {
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

        [Fact]
        public void Right_clicking_a_components_header_opens_its_menu_where_the_mouse_is()
        {
            var app = new EditorApp();
            GameObject crate = app.Context.Create("Crate", g => g.AddComponent<BoxCollider>());
            BoxCollider box = crate.GetComponent<BoxCollider>()!;
            app.Context.Selection.Select(crate);
            Frame(app, 20);

            int card = Ui.Id(RuntimeHelpers.GetHashCode(box), "card");
            Vector2? header = Find(app, card, ScreenWidth - 200f, 60f, ScreenHeight);
            Assert.NotNull(header);

            InputSystem.PressButton(MouseButton.Right);
            Frame(app);
            InputSystem.ReleaseButton(MouseButton.Right);
            Frame(app, 3);

            int menu = Ui.Id(card, "menu");
            Assert.True(Menu.IsOpen(menu));

            // Remove Component is the fourth row (after Move Up, Move Down and a line), just below the mouse.
            Vector2? remove = Find(app, Ui.Id(Ui.Id(menu, "menu"), 3), header!.Value.X + 30f, header.Value.Y, header.Value.Y + 140f);
            Assert.NotNull(remove);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app, 3);

            Assert.Null(crate.GetComponent<BoxCollider>());
            app.Context.Undo();
            Assert.NotNull(crate.GetComponent<BoxCollider>());
        }
    }
}
