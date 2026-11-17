using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Input;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>The editor's controls, driven through frames as the mouse and keyboard would.</summary>
    public class UiTests : EditorTest
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

        private static readonly Rect _box = new(100f, 100f, 200f, 28f);

        [Fact]
        public void A_search_box_changes_as_each_key_is_typed_and_escape_puts_it_back()
        {
            string text = "";
            int id = Ui.Id("search");
            void Draw() => TextInput.Draw(id, _box, ref text, "Search", live: true);

            InputSystem.MoveMouse(new Vector2(150f, 110f));
            UiFrame(Draw);
            UiFrame(Draw);
            InputSystem.PressButton(MouseButton.Left);
            UiFrame(Draw);
            InputSystem.ReleaseButton(MouseButton.Left);
            UiFrame(Draw);
            Assert.Equal(id, Ui.FocusId);

            foreach (char c in "cub")
            {
                InputSystem.TypeCharacter(c);
                UiFrame(Draw);
            }

            Assert.Equal("cub", text);

            InputSystem.PressKey(Key.Escape);
            UiFrame(Draw);
            Assert.Equal("", text);
            Assert.Equal(0, Ui.FocusId);
        }

        [Fact]
        public void A_name_box_only_hands_its_text_over_once_typing_is_done()
        {
            string name = "Cube";
            int id = Ui.Id("name");
            bool committed = false;
            void Draw() => committed |= TextInput.Draw(id, _box, ref name);

            TextInput.Focus(id);
            UiFrame(Draw);
            foreach (char c in "Box")
            {
                InputSystem.TypeCharacter(c);
                UiFrame(Draw);
            }

            // Typing replaced the selected text, but nothing's handed over yet.
            Assert.Equal("Cube", name);
            Assert.False(committed);

            InputSystem.PressKey(Key.Enter);
            UiFrame(Draw);
            Assert.True(committed);
            Assert.Equal("Box", name);
        }

        [Fact]
        public void Of_two_controls_on_top_of_each_other_the_one_drawn_last_gets_the_mouse()
        {
            int under = Ui.Id("under"), over = Ui.Id("over");
            Interaction underResult = default, overResult = default;
            void Draw()
            {
                underResult = Ui.Interact(under, _box);
                overResult = Ui.Interact(over, _box);
            }

            InputSystem.MoveMouse(new Vector2(150f, 110f));
            UiFrame(Draw);
            UiFrame(Draw);

            Assert.False(underResult.Hovered);
            Assert.True(overResult.Hovered);
        }

        [Fact]
        public void A_click_outside_an_open_menu_closes_it_without_clicking_what_is_under_it()
        {
            int buttonId = Ui.Id("button");
            bool clicked = false;
            void Draw() => clicked |= Widgets.Button(buttonId, new Rect(400f, 400f, 100f, 30f), "Under");

            Menu.Open(Ui.Id("owner"), new Vector2(10f, 10f), [new MenuItem("One")]);
            InputSystem.MoveMouse(new Vector2(450f, 415f));
            UiFrame(Draw);
            UiFrame(Draw);

            InputSystem.PressButton(MouseButton.Left);
            UiFrame(Draw);
            InputSystem.ReleaseButton(MouseButton.Left);
            UiFrame(Draw);

            Assert.False(Menu.IsAnyOpen);
            Assert.False(clicked);
        }

        [Fact]
        public void Picking_from_a_dropdown_reports_the_choice()
        {
            int id = Ui.Id("dropdown");
            int? picked = null;
            string[] options = ["None", "Cube", "Sphere"];
            var rect = new Rect(100f, 100f, 160f, 24f);
            void Draw() => picked ??= Widgets.Dropdown(id, rect, options, 0);

            InputSystem.MoveMouse(new Vector2(150f, 110f));
            UiFrame(Draw);
            UiFrame(Draw);
            InputSystem.PressButton(MouseButton.Left);
            UiFrame(Draw);
            InputSystem.ReleaseButton(MouseButton.Left);
            UiFrame(Draw);
            Assert.True(Menu.IsOpen(id));

            // Down to the third item and Enter.
            InputSystem.PressKey(Key.Down);
            UiFrame(Draw);
            InputSystem.ReleaseKey(Key.Down);
            InputSystem.PressKey(Key.Down);
            UiFrame(Draw);
            InputSystem.PressKey(Key.Enter);
            UiFrame(Draw);
            UiFrame(Draw);

            Assert.Equal(2, picked);
        }

        [Fact]
        public void Long_text_ends_in_an_ellipsis_that_fits()
        {
            Canvas.BeginFrame(ScreenWidth, ScreenHeight);
            string fitted = Ui.Fit("A very long object name indeed", 80f);

            Assert.EndsWith("…", fitted);
            Assert.True(Ui.TextWidth(fitted) <= 80f);
            Assert.Equal("Short", Ui.Fit("Short", 80f));
        }

        [Fact]
        public void Numbers_typed_with_a_comma_or_a_point_both_work()
        {
            Assert.True(NumberField.TryParse("1,5", out float comma));
            Assert.True(NumberField.TryParse(" 2.25 ", out float point));
            Assert.Equal((1.5f, 2.25f), (comma, point));
            Assert.Equal("0", NumberField.Format(-0f, "0.###"));
        }

        [Fact]
        public void Colours_go_to_hex_and_back()
        {
            Color color = Color.FromBytes(230, 107, 90, 128);
            Assert.Equal("E66B5A80", ColorField.ToHex(color));
            Assert.True(ColorField.TryParseHex("#E66B5A80", out Color back));
            Assert.Equal(color, back);

            Vector3 hsv = ColorField.ToHsv(Color.FromBytes(255, 0, 0));
            Assert.Equal(new Vector3(0f, 1f, 1f), hsv);
        }
    }
}
