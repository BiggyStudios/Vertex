using System.Numerics;

using Vertex.Input;
using Vertex.UI;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// One line of a <see cref="Menu"/>: a label, and what happens when it's picked. A separator is a thin line between
    /// groups instead.
    /// </summary>
    public sealed record MenuItem(string Label, Action? Action = null)
    {
        /// <summary>The keys that do the same, shown dimly on the right (only shown: the shortcut itself is handled elsewhere).</summary>
        public string? Shortcut { get; init; }
        public bool Enabled { get; init; } = true;
        public bool Checked { get; init; }
        public Icon? Icon { get; init; }
        public bool IsSeparator { get; init; }

        public static MenuItem Separator { get; } = new("") { IsSeparator = true };
    }

    /// <summary>
    /// A list of choices that drops open over everything else: right-click menus and dropdowns. One is open at a time.
    /// It eases in, the highlight slides from item to item, and the arrow keys and Enter work too. Clicking outside or
    /// Escape closes it.
    /// <para>
    /// Picking an item runs its action, and its number can be collected by whatever opened the menu with
    /// <see cref="TakeChoice"/> on the next frame.
    /// </para>
    /// </summary>
    public static class Menu
    {
        private static IReadOnlyList<MenuItem>? _items;
        private static int _owner;
        private static Vector2 _position;
        private static float _minWidth;
        private static float _openedAt;
        private static int _openedFrame;

        // How far up to move the menu when it doesn't fit below where it was opened: over the dropdown it hangs from,
        // or just above the mouse for a right-click menu.
        private static float _flipBy;
        private static float _closedAt = -1f;
        private static int _highlighted = -1;
        private static int _choiceOwner;
        private static int _choice = -1;

        private const float _itemHeight = 26f;
        private const float _separatorHeight = 9f;
        private const float _padding = 5f;

        public static bool IsAnyOpen => _items != null && _closedAt < 0f;

        public static bool IsOpen(int owner) => IsAnyOpen && _owner == owner;

        /// <summary>Opens a menu at <paramref name="position"/> (its top-left), for the control <paramref name="owner"/>.</summary>
        public static void Open(int owner, Vector2 position, IReadOnlyList<MenuItem> items, float minWidth = 0f, float flipBy = 0f)
        {
            _items = items;
            _owner = owner;
            _position = position;
            _minWidth = minWidth;
            _openedAt = Ui.Time;
            _openedFrame = Ui.Frame;
            _flipBy = flipBy;
            _closedAt = -1f;
            _highlighted = -1;

            // Starts on the ticked item, for a dropdown.
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Checked) _highlighted = i;
            }
        }

        /// <summary>Opens a menu just under <paramref name="anchor"/>, at least as wide: for a dropdown.</summary>
        public static void OpenBelow(int owner, Rect anchor, IReadOnlyList<MenuItem> items) =>
            Open(owner, new Vector2(anchor.X, anchor.Bottom + 3f), items, anchor.Width, flipBy: anchor.Height + 6f);

        public static void Close()
        {
            if (IsAnyOpen) _closedAt = Ui.Time;
        }

        /// <summary>The number of the item picked from <paramref name="owner"/>'s menu, once, on the frame after; otherwise null.</summary>
        public static int? TakeChoice(int owner)
        {
            if (_choiceOwner != owner || _choice < 0) return null;

            int choice = _choice;
            _choice = -1;
            _choiceOwner = 0;
            return choice;
        }

        /// <summary>Draws the open menu, over everything. Called by <see cref="Ui.EndFrame"/>.</summary>
        internal static void Draw()
        {
            if (_items == null) return;

            // Fades out quickly once closed, without taking any more clicks.
            bool closing = _closedAt >= 0f;
            float shown = closing ? 1f - Math.Clamp((Ui.Time - _closedAt) / 0.08f, 0f, 1f) : Ui.EaseOut((Ui.Time - _openedAt) / 0.14f);
            if (closing && shown <= 0f)
            {
                _items = null;
                return;
            }

            IReadOnlyList<MenuItem> items = _items;

            float width = _minWidth;
            float height = _padding * 2f;
            foreach (MenuItem item in items)
            {
                height += item.IsSeparator ? _separatorHeight : _itemHeight;
                if (!item.IsSeparator)
                {
                    float shortcut = item.Shortcut != null ? Ui.TextWidth(item.Shortcut, Theme.SmallTextSize) + 24f : 0f;
                    width = MathF.Max(width, Ui.TextWidth(item.Label) + shortcut + 56f);
                }
            }

            width = MathF.Max(width, 150f);
            Vector2 position = _position;
            if (position.X + width > Canvas.Width - 6f) position.X = MathF.Max(Canvas.Width - 6f - width, 6f);
            if (position.Y + height > Canvas.Height - 6f) position.Y = MathF.Max(position.Y - height - _flipBy, 6f);

            var rect = new Rect(position.X, position.Y + (1f - shown) * -6f, width, height);
            Ui.Opacity = shown;

            int menuId = Ui.Id(_owner, "menu");
            if (!closing)
            {
                // Everything outside is covered by an invisible blocker, so a click outside only closes the menu.
                Interaction outside = Ui.Interact(Ui.Id(menuId, "outside"), new Rect(0f, 0f, Canvas.Width, Canvas.Height));
                // Not on the frame it opened: the right-click that opened it would count as one outside it.
                bool justOpened = _openedFrame == Ui.Frame;
                if (!justOpened && (outside.Pressed || Ui.MousePressed(MouseButton.Right) && !rect.Contains(Ui.Mouse)) || InputSystem.GetKeyDown(Key.Escape))
                {
                    InputSystem.Consume(Key.Escape);
                    Close();
                }

                Ui.Block(menuId, rect);
            }

            Ui.Shadow(rect, 7f, 18f);
            Ui.Rect(rect, Theme.Popup, 7f);
            Ui.Outline(rect, Theme.BorderStrong, 7f);

            if (!closing) HandleKeys(items);

            // Where the highlight is now, so it can slide there.
            float y = rect.Y + _padding;
            float highlightY = -1f;
            for (int i = 0; i < items.Count; i++)
            {
                MenuItem item = items[i];
                if (item.IsSeparator)
                {
                    Ui.Rect(new Rect(rect.X + 10f, y + _separatorHeight * 0.5f, rect.Width - 20f, 1f), Theme.Border);
                    y += _separatorHeight;
                    continue;
                }

                var row = new Rect(rect.X + _padding, y, rect.Width - _padding * 2f, _itemHeight);
                if (!closing && item.Enabled)
                {
                    Interaction interaction = Ui.Interact(Ui.Id(menuId, i), row);
                    if (interaction.Hovered && Ui.MouseMovedLately) _highlighted = i;
                    if (interaction.Clicked) Pick(i);
                }

                if (i == _highlighted && item.Enabled) highlightY = row.Y;
                y += _itemHeight;
            }

            if (highlightY >= 0f)
            {
                float slid = Ui.Smooth(Ui.Id(menuId, "highlight"), highlightY, 30f, highlightY);
                Ui.Rect(new Rect(rect.X + _padding, slid, rect.Width - _padding * 2f, _itemHeight), Theme.Accent.WithAlpha(0.85f), 4f);
            }

            y = rect.Y + _padding;
            for (int i = 0; i < items.Count; i++)
            {
                MenuItem item = items[i];
                if (item.IsSeparator)
                {
                    y += _separatorHeight;
                    continue;
                }

                var row = new Rect(rect.X + _padding, y, rect.Width - _padding * 2f, _itemHeight);
                bool lit = i == _highlighted && item.Enabled;
                Color text = !item.Enabled ? Theme.TextFaint : lit ? Color.White : Theme.Text;

                if (item.Checked) Icons.Check(new Vector2(row.X + 12f, row.Centre.Y), 13f, lit ? Color.White : Theme.AccentText);
                else item.Icon?.Invoke(new Vector2(row.X + 12f, row.Centre.Y), 14f, lit ? Color.White : Theme.TextDim);

                Ui.Text(new Rect(row.X + 26f, row.Y, row.Width - 30f, row.Height), item.Label, text);
                if (item.Shortcut != null)
                {
                    Ui.Text(row.Shrink(10f, 0f), item.Shortcut, lit ? Color.White.WithAlpha(0.8f) : Theme.TextFaint, align: 1f, size: Theme.SmallTextSize);
                }

                y += _itemHeight;
            }

            Ui.Opacity = 1f;
        }

        private static void HandleKeys(IReadOnlyList<MenuItem> items)
        {
            int step = InputSystem.GetKeyDown(Key.Down) ? 1 : InputSystem.GetKeyDown(Key.Up) ? -1 : 0;
            if (step != 0)
            {
                for (int tries = 0; tries < items.Count; tries++)
                {
                    _highlighted = ((_highlighted < 0 && step < 0 ? 0 : _highlighted) + step + items.Count) % items.Count;
                    if (!items[_highlighted].IsSeparator && items[_highlighted].Enabled) break;
                }
            }

            if ((InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter)) && _highlighted >= 0)
            {
                InputSystem.Consume(Key.Enter);
                Pick(_highlighted);
            }
        }

        private static void Pick(int index)
        {
            if (_items == null) return;

            _choiceOwner = _owner;
            _choice = index;
            MenuItem item = _items[index];
            Close();
            item.Action?.Invoke();
        }
    }

    /// <summary>
    /// A panel that opens over everything else with contents drawn by whoever opened it: the add-component search, the
    /// colour picker. Its owner calls <see cref="Show"/> every frame; it only draws while open. One is open at a time.
    /// </summary>
    public static class Popup
    {
        private static int _openId;
        private static Rect _anchor;
        private static float _openedAt;
        private static int _shownFrame;

        public static bool AnyOpen => _openId != 0 && _shownFrame >= Ui.Frame - 1;

        public static bool IsOpen(int id) => _openId == id && (_shownFrame >= Ui.Frame - 1 || _openedAt == Ui.Time);

        /// <summary>Opens popup <paramref name="id"/> next to <paramref name="anchor"/> (below it, or above if there's no room).</summary>
        public static void Open(int id, Rect anchor)
        {
            _openId = id;
            _anchor = anchor;
            _openedAt = Ui.Time;
            _shownFrame = Ui.Frame;
        }

        public static void Close() => _openId = 0;

        /// <summary>How far through opening the open popup is, 0 to 1: for things inside it that ease in too.</summary>
        public static float Shown => Ui.EaseOut((Ui.Time - _openedAt) / 0.16f);

        /// <summary>
        /// Draws popup <paramref name="id"/>, <paramref name="size"/> UI units, if it's open: at the end of the frame, over
        /// everything, calling <paramref name="content"/> with the rectangle inside its border. Clicking outside or Escape
        /// closes it.
        /// </summary>
        public static void Show(int id, Vector2 size, Action<Rect> content)
        {
            if (!IsOpen(id)) return;
            _shownFrame = Ui.Frame;
            Rect anchor = _anchor;

            Ui.Overlay(() =>
            {
                if (_openId != id) return;

                float x = Math.Clamp(anchor.X, 6f, MathF.Max(Canvas.Width - size.X - 6f, 6f));
                float y = anchor.Bottom + 4f;
                if (y + size.Y > Canvas.Height - 6f) y = MathF.Max(anchor.Y - size.Y - 4f, 6f);

                float shown = Shown;
                var rect = new Rect(x, y + (1f - shown) * -8f, size.X, size.Y);

                Interaction outside = Ui.Interact(Ui.Id(id, "outside"), new Rect(0f, 0f, Canvas.Width, Canvas.Height));
                if (outside.Pressed || InputSystem.GetKeyDown(Key.Escape))
                {
                    InputSystem.Consume(Key.Escape);
                    Close();
                    Ui.FocusId = 0;
                    return;
                }

                Ui.Block(Ui.Id(id, "panel"), rect);
                Ui.Opacity = shown;
                Ui.Shadow(rect, 8f, 22f);
                Ui.Rect(rect, Theme.Popup, 8f);
                Ui.Outline(rect, Theme.BorderStrong, 8f);

                Ui.PushClip(rect);
                content(rect.Shrink(8f));
                Ui.PopClip();
                Ui.Opacity = 1f;
            });
        }
    }
}
