using System.Numerics;

using Vertex.Input;
using Vertex.UI;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// A window inside the editor that has to be dealt with before going back to the rest: settings, messages, questions.
    /// The editor dims behind it; it eases in, and closes with its × button, Escape, or clicking outside. One at a time.
    /// </summary>
    public static class Dialog
    {
        private sealed record Open(string Title, Vector2 Size, Action<Rect> Content, Action? Closed);

        private static Open? _open;
        private static float _openedAt;

        public static bool IsOpen => _open != null;

        /// <summary>Opens a dialog <paramref name="size"/> UI units, its contents drawn by <paramref name="content"/> each frame.</summary>
        public static void Show(string title, Vector2 size, Action<Rect> content, Action? closed = null)
        {
            _open?.Closed?.Invoke();
            _open = new Open(title, size, content, closed);
            _openedAt = Ui.Time;
            Menu.Close();
            Popup.Close();
        }

        /// <summary>A message with an OK button, and Copy for long ones (build errors).</summary>
        public static void Message(string title, string message, string? copyText = null, float width = 560f)
        {
            const float lineHeight = 18f;
            float textWidth = width - 36f - 12f;
            List<string> lines = Ui.Wrap(message.Trim(), textWidth);
            float height = MathF.Min(120f + lines.Count * lineHeight, 560f);
            Show(title, new Vector2(width, height), rect =>
            {
                var scroll = ScrollView.Begin(Ui.Id("dialog.message"), rect.CutBottom(40f));
                float y = scroll.Top;
                foreach (string line in lines)
                {
                    // A blank line between paragraphs is half height.
                    if (line.Length == 0)
                    {
                        y += lineHeight * 0.5f;
                        continue;
                    }

                    Ui.Text(new Rect(rect.X, y, rect.Width - 10f, lineHeight), line, Theme.Text);
                    y += lineHeight;
                }

                scroll.End(y - scroll.Top);

                Rect buttons = rect.TakeBottom(30f);
                if (Widgets.Button(Ui.Id("dialog.ok"), buttons.TakeRight(90f), "OK", ButtonStyle.Primary)) Close();
                if ((lines.Count > 3 || copyText != null) && Widgets.Button(Ui.Id("dialog.copy"), buttons.CutRight(98f).TakeRight(90f), "Copy"))
                {
                    InputSystem.ClipboardText = copyText ?? message;
                }
            });
        }

        /// <summary>A question with a choice of answers, the first highlighted. Each answer runs its action and closes the dialog.</summary>
        public static void Ask(string title, string question, params (string Label, Action Action)[] answers)
        {
            Show(title, new Vector2(460f, 150f), rect =>
            {
                Ui.Text(rect.TakeTop(40f), question, Theme.Text);
                float right = rect.Right;
                for (int i = answers.Length - 1; i >= 0; i--)
                {
                    float width = MathF.Max(Ui.TextWidth(answers[i].Label) + 28f, 84f);
                    var button = new Rect(right - width, rect.Bottom - 30f, width, 30f);
                    if (Widgets.Button(Ui.Id("dialog.answer" + i), button, answers[i].Label, i == 0 ? ButtonStyle.Primary : ButtonStyle.Normal))
                    {
                        Close();
                        answers[i].Action();
                        return;
                    }

                    right -= width + 8f;
                }
            });
        }

        public static void Close()
        {
            Open? open = _open;
            _open = null;
            open?.Closed?.Invoke();
        }

        /// <summary>Draws the open dialog over everything. Called by <see cref="Ui.EndFrame"/>.</summary>
        internal static void Draw()
        {
            if (_open is not { } open) return;

            float shown = Ui.EaseOut((Ui.Time - _openedAt) / 0.18f);
            var screen = new Rect(0f, 0f, Canvas.Width, Canvas.Height);
            Ui.Opacity = shown;
            Ui.Rect(screen, new Color(0f, 0f, 0f, 0.45f));

            int id = Ui.Id("dialog." + open.Title);
            Interaction outside = Ui.Interact(Ui.Id(id, "outside"), screen);

            Vector2 size = Vector2.Min(open.Size, screen.Size - new Vector2(40f));
            Rect rect = screen.CentreOf(size).Offset(new Vector2(0f, (1f - shown) * 12f));
            Ui.Block(Ui.Id(id, "panel"), rect);
            Ui.Shadow(rect, 10f, 30f);
            Ui.Rect(rect, Theme.Panel, 10f);
            Ui.Outline(rect, Theme.BorderStrong, 10f);

            Rect header = rect.TakeTop(44f);
            Ui.Text(header.Shrink(18f, 0f), open.Title, Theme.Text, bold: true, size: 14.5f);
            if (Widgets.IconButton(Ui.Id(id, "close"), header.TakeRight(44f).Shrink(9f), Icons.Close, "Close") ||
                outside.Pressed || InputSystem.GetKeyDown(Key.Escape) && Ui.FocusId == 0 && !Menu.IsAnyOpen && !Popup.AnyOpen)
            {
                InputSystem.Consume(Key.Escape);
                Ui.Opacity = 1f;
                Close();
                return;
            }

            Ui.Rect(new Rect(rect.X, header.Bottom, rect.Width, 1f), Theme.Border);
            Ui.PushClip(rect.CutTop(44f));
            open.Content(rect.CutTop(44f).Shrink(18f, 14f));
            Ui.PopClip();
            Ui.Opacity = 1f;
        }
    }

    /// <summary>
    /// Dragging something from one panel to another: an asset onto a texture slot or into the scene. Whoever starts the
    /// drag sets <see cref="Payload"/>; places it can be dropped check it while the mouse is over them, and take it on
    /// <see cref="Dropped"/>.
    /// </summary>
    public static class DragDrop
    {
        private static Vector2 _from;
        private static bool _moved;

        /// <summary>What's being dragged, or null.</summary>
        public static object? Payload { get; private set; }

        public static string Label { get; private set; } = "";

        /// <summary>True on the frame the mouse is let go with something being dragged (after it's moved a little).</summary>
        public static bool Dropped => Payload != null && _moved && Ui.MouseReleased();

        /// <summary>Whether a drag is under way and has moved far enough to count.</summary>
        public static bool Dragging => Payload != null && _moved;

        public static void Begin(object payload, string label)
        {
            Payload = payload;
            Label = label;
            _from = Ui.Mouse;
            _moved = false;
        }

        /// <summary>Follows the mouse with what's being dragged, and lets it go once dropped. Called by <see cref="Ui.EndFrame"/>.</summary>
        internal static void Update()
        {
            if (Payload == null) return;

            if (!_moved && Vector2.Distance(_from, Ui.Mouse) > 5f) _moved = true;
            if (!Ui.MouseHeld())
            {
                Payload = null;
                return;
            }

            if (!_moved) return;

            string text = Ui.Fit(Label, 200f);
            var label = new Rect(Ui.Mouse.X + 14f, Ui.Mouse.Y + 8f, Ui.TextWidth(text) + 20f, 24f);
            Ui.Shadow(label, 5f, 10f, 0.6f);
            Ui.Rect(label, Theme.Popup, 5f);
            Ui.Outline(label, Theme.Accent.WithAlpha(0.7f), 5f);
            Ui.Text(label.Shrink(10f, 0f), text, Theme.Text);
        }
    }
}
