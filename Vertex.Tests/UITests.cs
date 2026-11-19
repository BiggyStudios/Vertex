using System.Numerics;

using Vertex.Graphs;
using Vertex.Input;
using Vertex.Scripting;
using Vertex.Serialization;
using Vertex.UI;

namespace Vertex.Tests
{
    /// <summary>The UI made of objects: its controls, layouts, masks and images, driven by the mouse and keys as a player would.</summary>
    public class UITests : EngineTest
    {
        private readonly GameObject _canvas;

        public UITests()
        {
            _canvas = new GameObject("Canvas");
            _canvas.AddComponent<UICanvas>();
        }

        public override void Dispose()
        {
            UISystem.Reset();
            base.Dispose();
        }

        /// <summary>A piece of UI <paramref name="size"/> big, in the middle of the 1280 × 720 screen (unless it's in a layout).</summary>
        private GameObject Piece(string name, Vector2 size, GameObject? parent = null)
        {
            var piece = new GameObject(name);
            piece.Transform.Parent = (parent ?? _canvas).Transform;
            piece.AddComponent<UIRect>().Size = size;
            return piece;
        }

        private static GameLoop Start()
        {
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 1);
            return loop;
        }

        private static void Click(GameLoop loop, Vector2 at)
        {
            InputSystem.MoveMouse(at);
            RunFrames(loop, 1);
            InputSystem.EndFrame();
            InputSystem.PressButton(MouseButton.Left);
            RunFrames(loop, 1);
            InputSystem.EndFrame();
            InputSystem.ReleaseButton(MouseButton.Left);
            RunFrames(loop, 1);
            InputSystem.EndFrame();
        }

        [Fact]
        public void A_toggle_switches_when_clicked_and_tells_code_and_visual_scripts()
        {
            GameObject piece = Piece("Sound On", new Vector2(200f, 32f));
            UIToggle toggle = piece.AddComponent<UIToggle>();
            UIText said = piece.AddComponent<UIText>();
            var heard = new List<bool>();
            toggle.Changed += on => heard.Add(on);

            // A visual script on it shows the new value: On Value Changed into Set Text.
            var graph = new Graph();
            GraphNode changed = graph.AddNode("Events/On Value Changed", Vector2.Zero);
            GraphNode setText = graph.AddNode("Actions/Set Text", Vector2.Zero);
            graph.Link(changed.Id, "Then", setText.Id, "In");
            graph.Link(changed.Id, "Value", setText.Id, "Text");
            piece.AddComponent<VisualScript>(s => s.Script = graph);

            GameLoop loop = Start();
            Click(loop, new Vector2(640f, 360f));
            Assert.True(toggle.IsOn);
            Click(loop, new Vector2(640f, 360f));

            Assert.False(toggle.IsOn);
            Assert.Equal([true, false], heard);
            Assert.Equal("False", said.Text, ignoreCase: true);
        }

        [Fact]
        public void A_buttons_click_starts_on_clicked_in_its_objects_visual_scripts()
        {
            GameObject piece = Piece("Play", new Vector2(200f, 60f));
            piece.AddComponent<UIButton>();
            UIText label = piece.AddComponent<UIText>();
            var graph = new Graph();
            GraphNode clicked = graph.AddNode("Events/On Clicked", Vector2.Zero);
            GraphNode setText = graph.AddNode("Actions/Set Text", Vector2.Zero);
            setText.Values["Text"] = "Clicked!";
            graph.Link(clicked.Id, "Then", setText.Id, "In");
            piece.AddComponent<VisualScript>(s => s.Script = graph);

            Click(Start(), new Vector2(640f, 360f));

            Assert.Equal("Clicked!", label.Text);
        }

        [Fact]
        public void A_slider_follows_the_mouse_along_its_track_and_keeps_to_whole_numbers_if_asked()
        {
            // 400 wide in the middle: from x = 440 to 840, its knob 24 across, so the knob's middle runs from 452 to 828.
            GameObject piece = Piece("Volume", new Vector2(400f, 30f));
            UISlider slider = piece.AddComponent<UISlider>();
            slider.Min = 0f;
            slider.Max = 10f;

            GameLoop loop = Start();
            InputSystem.MoveMouse(new Vector2(452f + 376f * 0.75f, 360f));
            RunFrames(loop, 1);
            InputSystem.PressButton(MouseButton.Left);
            RunFrames(loop, 1);
            Assert.Equal(7.5f, slider.Value, 0.01f);

            // Dragged off the end, it stops at the end.
            InputSystem.EndFrame();
            InputSystem.MoveMouse(new Vector2(100f, 500f));
            RunFrames(loop, 1);
            Assert.Equal(0f, slider.Value);
            InputSystem.ReleaseButton(MouseButton.Left);
            RunFrames(loop, 1);

            slider.WholeNumbers = true;
            slider.Value = 3.4f;
            Assert.Equal(3f, slider.Value);
            Assert.Equal(0.3f, slider.Fraction, 0.001f);
        }

        [Fact]
        public void An_input_field_is_typed_in_once_clicked_and_enter_submits_it()
        {
            GameObject piece = Piece("Name", new Vector2(260f, 40f));
            UIInputField field = piece.AddComponent<UIInputField>();
            string? submitted = null;
            field.Submitted += text => submitted = text;

            GameLoop loop = Start();
            Assert.False(UISystem.Typing);
            Click(loop, new Vector2(640f, 360f));
            Assert.True(field.IsFocused);
            Assert.True(UISystem.Typing);

            foreach (char c in "Ada") InputSystem.TypeCharacter(c);
            RunFrames(loop, 1);
            InputSystem.EndFrame();
            Assert.Equal("Ada", field.Text);

            InputSystem.PressKey(Key.Enter);
            RunFrames(loop, 1);
            InputSystem.EndFrame();
            InputSystem.ReleaseKey(Key.Enter);

            Assert.Equal("Ada", submitted);
            Assert.False(UISystem.Typing);

            // Clicking somewhere else lets it go too.
            Click(loop, new Vector2(640f, 360f));
            Assert.True(field.IsFocused);
            Click(loop, new Vector2(20f, 20f));
            Assert.False(field.IsFocused);
        }

        [Fact]
        public void A_vertical_layout_stacks_its_pieces_and_closes_up_when_one_is_hidden()
        {
            GameObject menu = Piece("Menu", new Vector2(300f, 400f));
            var layout = menu.AddComponent<UIVerticalLayout>();
            layout.Padding = 10f;
            layout.Spacing = 5f;
            GameObject a = Piece("A", new Vector2(100f, 50f), menu);
            GameObject b = Piece("B", new Vector2(100f, 30f), menu);
            GameObject c = Piece("C", new Vector2(100f, 40f), menu);

            GameLoop loop = Start();

            // The menu is from (490, 160) to (790, 560); inside its padding, 280 wide from (500, 170).
            Assert.Equal(new Vector2(500f, 170f), a.GetComponent<UIRect>()!.ScreenPosition);
            Assert.Equal(new Vector2(280f, 50f), a.GetComponent<UIRect>()!.ScreenSize);
            Assert.Equal(new Vector2(500f, 225f), b.GetComponent<UIRect>()!.ScreenPosition);
            Assert.Equal(260f, c.GetComponent<UIRect>()!.ScreenPosition.Y);

            b.Active = false;
            RunFrames(loop, 1);
            Assert.Equal(225f, c.GetComponent<UIRect>()!.ScreenPosition.Y);
        }

        [Fact]
        public void A_grid_layout_fills_rows_of_cells()
        {
            GameObject inventory = Piece("Inventory", new Vector2(200f, 200f));
            var grid = inventory.AddComponent<UIGridLayout>();
            grid.CellSize = new Vector2(40f, 40f);
            grid.Spacing = 10f;
            grid.HorizontalAlign = TextAlign.Left;
            var slots = Enumerable.Range(0, 5).Select(i => Piece($"Slot {i}", Vector2.One, inventory).GetComponent<UIRect>()!).ToList();

            Start();

            // 200 wide fits four 40-wide cells 10 apart; the fifth starts the next row. The grid is at (540, 260).
            Assert.Equal(new Vector2(540f, 260f), slots[0].ScreenPosition);
            Assert.Equal(new Vector2(690f, 260f), slots[3].ScreenPosition);
            Assert.Equal(new Vector2(540f, 310f), slots[4].ScreenPosition);
            Assert.Equal(new Vector2(40f, 40f), slots[4].ScreenSize);
        }

        [Fact]
        public void What_a_mask_hides_cant_be_clicked()
        {
            // A 100-square window in the middle, and a wide button inside it that sticks out both sides.
            GameObject window = Piece("Window", new Vector2(100f, 100f));
            window.AddComponent<UIMask>();
            GameObject piece = Piece("Wide", new Vector2(600f, 40f), window);
            UIButton button = piece.AddComponent<UIButton>();
            int clicks = 0;
            button.Clicked += () => clicks++;

            GameLoop loop = Start();
            Click(loop, new Vector2(800f, 360f));
            Assert.Equal(0, clicks);
            Click(loop, new Vector2(640f, 360f));
            Assert.Equal(1, clicks);
        }

        private static int QuadsDrawn(UIImage image, Vector2 size)
        {
            Canvas.BeginFrame(1280, 720);
            image.Draw(Vector2.Zero, size, hovered: false, pressed: false);
            Canvas.EndFrame();
            return Canvas.ReadyQuads.Count;
        }

        [Fact]
        public void An_image_is_drawn_in_pieces_by_its_mode()
        {
            var texture = new Rendering.Texture(64, 64, new byte[64 * 64 * 4]);
            var image = new UIImage { Texture = texture };

            image.Mode = ImageMode.Fit;
            Assert.Equal(1, QuadsDrawn(image, new Vector2(200f, 100f)));

            // Nine pieces for a frame; tiles 64 across on a 200 × 100 rectangle are 4 by 2.
            image.Mode = ImageMode.Sliced;
            Assert.Equal(9, QuadsDrawn(image, new Vector2(200f, 100f)));
            image.Mode = ImageMode.Tiled;
            Assert.Equal(8, QuadsDrawn(image, new Vector2(200f, 100f)));

            // A quarter of a radial fill goes from the top round to the right: past the top-right corner, so two slices.
            image.Mode = ImageMode.Filled;
            image.FillMethod = FillMethod.Radial;
            image.FillAmount = 0.25f;
            Assert.Equal(2, QuadsDrawn(image, new Vector2(100f, 100f)));
            image.FillAmount = 1f;
            Assert.Equal(8, QuadsDrawn(image, new Vector2(100f, 100f)));
            image.FillAmount = 0f;
            Assert.Equal(0, QuadsDrawn(image, new Vector2(100f, 100f)));
        }

        [Fact]
        public void An_images_old_keep_aspect_setting_still_means_fit_or_stretch()
        {
            var image = new UIImage();
            Assert.Equal(ImageMode.Fit, image.Mode);
            image.KeepAspect = false;
            Assert.Equal(ImageMode.Stretch, image.Mode);

            // A newer mode isn't undone by it.
            image.Mode = ImageMode.Sliced;
            image.KeepAspect = true;
            Assert.Equal(ImageMode.Sliced, image.Mode);

            GameObject piece = Piece("Frame", new Vector2(100f, 100f));
            piece.AddComponent<UIImage>().Mode = ImageMode.Tiled;
            var loaded = new Scene();
            SceneFile.LoadJson(SceneFile.Save(Scene.Active), loaded);
            loaded.ApplyChanges();
            Assert.Equal(ImageMode.Tiled, loaded.Find("Frame")!.GetComponent<UIImage>()!.Mode);
        }

        [Fact]
        public void Text_set_to_shrink_gets_smaller_until_it_fits()
        {
            var text = new UIText { Text = "A long line of words that won't fit in a small box", FontSize = 40f, Wrap = false, ShrinkToFit = true };
            float size = text.FittingSize(Font.Default, new Vector2(200f, 60f));

            Assert.True(size < 40f);
            Assert.True(Canvas.MeasureText(Font.Default, text.Text, size).X <= 200.5f);
            Assert.Equal(40f, text.FittingSize(Font.Default, new Vector2(4000f, 100f)));
        }

        [Fact]
        public void Visual_scripts_set_and_read_a_pieces_value()
        {
            GameObject piece = Piece("Health", new Vector2(200f, 20f));
            UIProgressBar bar = piece.AddComponent<UIProgressBar>();
            var graph = new Graph();
            GraphNode start = graph.AddNode("Events/On Start", Vector2.Zero);
            GraphNode set = graph.AddNode("UI/Set UI Value", Vector2.Zero);
            set.Values["Value"] = 0.25f;
            graph.Link(start.Id, "Then", set.Id, "In");
            piece.AddComponent<VisualScript>(s => s.Script = graph);

            Start();

            Assert.Equal(0.25f, bar.Value);
        }
    }
}
