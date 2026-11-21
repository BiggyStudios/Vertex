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

        [Fact]
        public void Right_clicking_the_transform_resets_it_on_every_object_selected_as_one_undo()
        {
            var app = new EditorApp();
            GameObject crate = app.Context.Create("Crate");
            GameObject barrel = app.Context.Create("Barrel");
            foreach (GameObject gameObject in (GameObject[])[crate, barrel])
            {
                gameObject.Transform.Position = new Vector3(3f, 1f, -2f);
                gameObject.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
                gameObject.Transform.Scale = new Vector3(2f, 0.5f, 1f);
            }

            app.Context.Selection.Select([crate, barrel]);
            Frame(app, 20);

            // The card's for whichever of them the inspector shows first.
            int[] cards = [Ui.Id(RuntimeHelpers.GetHashCode(crate.Transform), "card"), Ui.Id(RuntimeHelpers.GetHashCode(barrel.Transform), "card")];
            Vector2? header = null;
            int card = 0;
            for (float y = 60f; y < ScreenHeight && header == null; y += 4f)
            {
                InputSystem.MoveMouse(new Vector2(ScreenWidth - 200f, y));
                Frame(app, 2);
                if (cards.Contains(Ui.HotId)) (header, card) = (new Vector2(ScreenWidth - 200f, y), Ui.HotId);
            }

            Assert.NotNull(header);
            InputSystem.PressButton(MouseButton.Right);
            Frame(app);
            InputSystem.ReleaseButton(MouseButton.Right);
            Frame(app, 3);

            int menu = Ui.Id(card, "menu");
            Assert.True(Menu.IsOpen(menu));

            // Reset Transform is the first row, before a line and Reset Position, Rotation and Scale.
            Vector2? reset = Find(app, Ui.Id(Ui.Id(menu, "menu"), 0), header!.Value.X + 30f, header.Value.Y, header.Value.Y + 160f);
            Assert.NotNull(reset);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app, 3);

            foreach (GameObject gameObject in (GameObject[])[crate, barrel])
            {
                Assert.Equal(Vector3.Zero, gameObject.Transform.Position);
                Assert.Equal(Quaternion.Identity, gameObject.Transform.Rotation);
                Assert.Equal(Vector3.One, gameObject.Transform.Scale);
            }

            app.Context.Undo();
            foreach (GameObject gameObject in (GameObject[])[crate, barrel])
            {
                Assert.Equal(new Vector3(3f, 1f, -2f), gameObject.Transform.Position);
                Assert.Equal(new Vector3(2f, 0.5f, 1f), gameObject.Transform.Scale);
            }
        }
    }
}
