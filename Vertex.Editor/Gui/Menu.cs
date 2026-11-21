using System.Numerics;

using Vertex.Input;
using Vertex.UI;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// One line of a <see cref="Menu"/>: a label, and what happens when it's picked. A separator is a thin line between
    /// groups instead. One with <see cref="Children"/> opens a menu of its own beside it.
    /// </summary>
    public sealed record MenuItem(string Label, Action? Action = null)
    {
        /// <summary>The keys that do the same, shown dimly on the right (only shown: the shortcut itself is handled elsewhere).</summary>
        public string? Shortcut { get; init; }
        public bool Enabled { get; init; } = true;
        public bool Checked { get; init; }
        public Icon? Icon { get; init; }
        public bool IsSeparator { get; init; }

        /// <summary>A menu that opens beside this item when it's pointed at ("Create ▸"), instead of an action.</summary>
        public IReadOnlyList<MenuItem>? Children { get; init; }

        public static MenuItem Separator { get; } = new("") { IsSeparator = true };
    }

    /// <summary>
    /// A list of choices that drops open over everything else: right-click menus and dropdowns. One is open at a time.
    /// It eases in, the highlight slides from item to item, and the arrow keys and Enter work too. Clicking outside or
    /// Escape closes it. An item with <see cref="MenuItem.Children"/> opens them in a second menu beside it, when the
    /// mouse rests on it (or with the right arrow key).
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

        // The menu opened beside an item: which item, its highlight, when it opened, and whether the keys are its.
        private static int _subOf = -1;
        private static int _subHighlighted = -1;
        private static float _subOpenedAt;
        private static bool _keysInSub;

        // The item the mouse is on, and since when. Pointing at an item opens its submenu (or closes another's) at once,
        // unless the mouse is on its way across other items to the submenu that's open: then that stays open until the
        // mouse stops, or has been on the other item a while.
        private static int _resting = -1;
        private static float _restingSince;

        // Where the mouse has been lately, to tell which way it's heading.
        private static readonly Queue<(float Time, Vector2 At)> _trail = new();

        private const float _itemHeight = 26f;
        private const float _separatorHeight = 9f;
        private const float _padding = 5f;
        private const float _aimLookBack = 0.06f;
        private const float _longestAim = 0.4f;

        public static bool IsAnyOpen => _items != null && _closedAt < 0f;

        public static bool IsOpen(int owner) => IsAnyOpen && _owner == owner;

        /// <summary>Which item's submenu is open beside the menu, or -1.</summary>
        internal static int SubmenuOf => IsAnyOpen ? _subOf : -1;

        /// <summary>Opens a menu at <paramref name="position"/> (its top-left), for the control <paramref name="owner"/>.</summary>
        public static void Open(int owner, Vector2 position, IReadOnlyList<MenuItem> items, float minWidth = 0f, float flipBy = 0f)
        {
            _items = items;
            _owner = owner;
            // Opened from inside a zoomed view (a node graph): placed where that is on screen.
            _position = Ui.ToScreen(position);
            _minWidth = minWidth;
            _openedAt = Ui.Time;
            _openedFrame = Ui.Frame;
            _flipBy = flipBy;
            _closedAt = -1f;
            _highlighted = -1;
            _resting = -1;
            _trail.Clear();
            CloseSub();

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

        /// <summary>The labels of the open menu's items, for tests.</summary>
        internal static IEnumerable<string> Labels => IsAnyOpen ? _items!.Where(i => !i.IsSeparator).Select(i => i.Label) : [];

        /// <summary>Picks the open menu's item labelled <paramref name="label"/>, as a click would, for tests. False if there's none, or it's off.</summary>
        internal static bool Pick(string label)
        {
            if (!IsAnyOpen || _items!.FirstOrDefault(i => i.Label == label) is not { Enabled: true } item) return false;
            Close();
            item.Action?.Invoke();
            return true;
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

        private static void OpenSub(int index, bool withKeys)
        {
            _subOf = index;
            _subOpenedAt = Ui.Time;
            _keysInSub = withKeys;
            _subHighlighted = withKeys ? FirstEnabled(_items![index].Children!) : -1;
        }

        private static void CloseSub()
        {
            _subOf = -1;
            _subHighlighted = -1;
            _keysInSub = false;
        }

        // Whether the mouse is on its way into the open submenu, this frame.
        private static bool _aiming;

        /// <summary>
        /// Whether the mouse, gone from <paramref name="from"/> to <paramref name="to"/>, is heading into the submenu at
        /// <paramref name="sub"/>: it's within the wedge from where it was to the near side of the submenu. Not if it's
        /// stopped.
        /// </summary>
        internal static bool HeadingFor(Vector2 from, Vector2 to, Rect sub)
        {
            if (Vector2.DistanceSquared(from, to) < 1f || sub.Contains(to)) return false;

            // The submenu's near side, a little taller, so a path that drifts slightly still counts.
            float edge = sub.X >= from.X ? sub.X : sub.Right;
            var top = new Vector2(edge, sub.Y - 6f);
            var bottom = new Vector2(edge, sub.Bottom + 6f);

            static float Side(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            float d1 = Side(from, top, to), d2 = Side(top, bottom, to), d3 = Side(bottom, from, to);
            bool negative = d1 < 0f || d2 < 0f || d3 < 0f, positive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(negative && positive);
        }

        private static int FirstEnabled(IReadOnlyList<MenuItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].IsSeparator && items[i].Enabled) return i;
            }

            return -1;
        }

        /// <summary>How big a menu of <paramref name="items"/> is.</summary>
        private static Vector2 Measure(IReadOnlyList<MenuItem> items, float minWidth)
        {
            float width = minWidth;
            float height = _padding * 2f;
            foreach (MenuItem item in items)
            {
                height += item.IsSeparator ? _separatorHeight : _itemHeight;
                if (item.IsSeparator) continue;

                float right = item.Children != null ? 24f : item.Shortcut != null ? Ui.TextWidth(item.Shortcut, Theme.SmallTextSize) + 24f : 0f;
                width = MathF.Max(width, Ui.TextWidth(item.Label) + right + 56f);
            }

            return new Vector2(MathF.Max(width, 150f), height);
        }

        /// <summary>Where item <paramref name="index"/>'s row is in a menu drawn at <paramref name="rect"/>.</summary>
        private static Rect Row(IReadOnlyList<MenuItem> items, Rect rect, int index)
        {
            float y = rect.Y + _padding;
            for (int i = 0; i < index; i++) y += items[i].IsSeparator ? _separatorHeight : _itemHeight;
            return new Rect(rect.X + _padding, y, rect.Width - _padding * 2f, _itemHeight);
        }

        /// <summary>Draws the open menu (and the one beside it, if one's open), over everything. Called by <see cref="Ui.EndFrame"/>.</summary>
        internal static void Draw()
        {
            if (_items == null) return;

            // Fades out quickly once closed, without taking any more clicks.
            bool closing = _closedAt >= 0f;
            float shown = closing ? 1f - Math.Clamp((Ui.Time - _closedAt) / 0.08f, 0f, 1f) : Ui.EaseOut((Ui.Time - _openedAt) / 0.14f);
            if (closing && shown <= 0f)
            {
                _items = null;
                CloseSub();
                return;
            }

            IReadOnlyList<MenuItem> items = _items;
            Vector2 size = Measure(items, _minWidth);
            Vector2 position = _position;
            if (position.X + size.X > Canvas.Width - 6f) position.X = MathF.Max(Canvas.Width - 6f - size.X, 6f);
            if (position.Y + size.Y > Canvas.Height - 6f) position.Y = MathF.Max(position.Y - size.Y - _flipBy, 6f);
            var rect = new Rect(position.X, position.Y + (1f - shown) * -6f, size.X, size.Y);

            // The menu beside an item: to its right, or its left if there's no room, level with the item.
            IReadOnlyList<MenuItem>? subItems = _subOf >= 0 && _subOf < items.Count ? items[_subOf].Children : null;
            Rect subRect = default;
            if (subItems != null)
            {
                Vector2 subSize = Measure(subItems, 0f);
                Rect parentRow = Row(items, rect, _subOf);
                float x = rect.Right - 3f;
                if (x + subSize.X > Canvas.Width - 6f) x = MathF.Max(rect.X - subSize.X + 3f, 6f);
                float y = Math.Clamp(parentRow.Y - _padding, 6f, MathF.Max(Canvas.Height - 6f - subSize.Y, 6f));
                float subShown = Ui.EaseOut((Ui.Time - _subOpenedAt) / 0.12f);
                subRect = new Rect(x + (1f - subShown) * -4f, y, subSize.X, subSize.Y);
            }

            Ui.Opacity = shown;
            int menuId = Ui.Id(_owner, "menu");
            if (!closing)
            {
                // Everything outside is covered by an invisible blocker, so a click outside only closes the menu.
                Interaction outside = Ui.Interact(Ui.Id(menuId, "outside"), new Rect(0f, 0f, Canvas.Width, Canvas.Height));
                bool inside = rect.Contains(Ui.Mouse) || subItems != null && subRect.Contains(Ui.Mouse);

                // Not on the frame it opened: the right-click that opened it would count as one outside it.
                bool justOpened = _openedFrame == Ui.Frame;
                if (!justOpened && (outside.Pressed || Ui.MousePressed(MouseButton.Right) && !inside)) Close();
                else if (InputSystem.GetKeyDown(Key.Escape))
                {
                    // Escape backs out of a submenu first.
                    InputSystem.Consume(Key.Escape);
                    if (subItems != null && _keysInSub) CloseSub();
                    else Close();
                }

                Ui.Block(menuId, rect);
                if (subItems != null) Ui.Block(Ui.Id(menuId, "sub"), subRect);
                HandleKeys(items);
            }

            _trail.Enqueue((Ui.Time, Ui.Mouse));
            while (_trail.Count > 1 && Ui.Time - _trail.Peek().Time > _aimLookBack) _trail.Dequeue();
            _aiming = subItems != null && HeadingFor(_trail.Peek().At, Ui.Mouse, subRect);

            DrawPanel(items, rect, menuId, ref _highlighted, sub: false, closing);
            if (subItems != null && _subOf >= 0)
            {
                DrawPanel(subItems, subRect, Ui.Id(menuId, "subPanel" + _subOf), ref _subHighlighted, sub: true, closing);
            }

            Ui.Opacity = 1f;
        }

        private static void DrawPanel(IReadOnlyList<MenuItem> items, Rect rect, int id, ref int highlighted, bool sub, bool closing)
        {
            Ui.Shadow(rect, 7f, 18f);
            Ui.Rect(rect, Theme.Popup, 7f);
            Ui.Outline(rect, Theme.BorderStrong, 7f);

            bool overSomething = false;
            for (int i = 0; i < items.Count; i++)
            {
                MenuItem item = items[i];
                if (item.IsSeparator)
                {
                    Rect line = Row(items, rect, i);
                    Ui.Rect(new Rect(rect.X + 10f, line.Y + _separatorHeight * 0.5f, rect.Width - 20f, 1f), Theme.Border);
                    continue;
                }

                if (closing || !item.Enabled) continue;

                Rect row = Row(items, rect, i);
                Interaction interaction = Ui.Interact(Ui.Id(id, i), row);
                if (interaction.Hovered)
                {
                    overSomething = true;
                    if (Ui.MouseMovedLately)
                    {
                        highlighted = i;
                        _keysInSub = sub;
                    }
                }

                if (!sub && interaction.Hovered && _resting != i)
                {
                    _resting = i;
                    _restingSince = Ui.Time;
                }

                if (interaction.Clicked)
                {
                    if (sub) PickSub(i);
                    else if (item.Children != null) OpenSub(i, withKeys: false);
                    else Pick(i);
                }
            }

            // Pointing at an item opens its submenu, or closes another's, unless the mouse is heading into the open one.
            if (!sub && !closing && overSomething && _resting >= 0 && _resting != _subOf && !(_aiming && Ui.Time - _restingSince < _longestAim))
            {
                if (items[_resting].Children != null) OpenSub(_resting, withKeys: false);
                else if (_subOf >= 0) CloseSub();
            }

            if (!sub && !overSomething) _resting = -1;

            // The highlight slides from item to item; an item whose submenu is open stays lit.
            int lit = !sub && _subOf >= 0 ? _subOf : highlighted;
            if (lit >= 0 && lit < items.Count && items[lit].Enabled && !items[lit].IsSeparator)
            {
                Rect row = Row(items, rect, lit);
                float slid = Ui.Smooth(Ui.Id(id, "highlight"), row.Y, 30f, row.Y);
                Ui.Rect(new Rect(row.X, slid, row.Width, _itemHeight), Theme.Accent.WithAlpha(!sub && _keysInSub ? 0.45f : 0.85f), 4f);
            }

            for (int i = 0; i < items.Count; i++)
            {
                MenuItem item = items[i];
                if (item.IsSeparator) continue;

                Rect row = Row(items, rect, i);
                bool on = i == lit && item.Enabled;
                Color text = !item.Enabled ? Theme.TextFaint : on ? Color.White : Theme.Text;

                if (item.Checked) Icons.Check(new Vector2(row.X + 12f, row.Centre.Y), 13f, on ? Color.White : Theme.AccentText);
                else item.Icon?.Invoke(new Vector2(row.X + 12f, row.Centre.Y), 14f, on ? Color.White : Theme.TextDim);

                Ui.Text(new Rect(row.X + 26f, row.Y, row.Width - 30f, row.Height), item.Label, text);
                if (item.Children != null)
                {
                    Icons.Chevron(new Vector2(row.Right - 10f, row.Centre.Y), 12f, on ? Color.White : Theme.TextDim);
                }
                else if (item.Shortcut != null)
                {
                    Ui.Text(row.Shrink(10f, 0f), item.Shortcut, on ? Color.White.WithAlpha(0.8f) : Theme.TextFaint, align: 1f, size: Theme.SmallTextSize);
                }
            }
        }

        private static void HandleKeys(IReadOnlyList<MenuItem> items)
        {
            IReadOnlyList<MenuItem>? subItems = _subOf >= 0 ? items[_subOf].Children : null;
            bool enter = InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter);
            int step = InputSystem.GetKeyDown(Key.Down) ? 1 : InputSystem.GetKeyDown(Key.Up) ? -1 : 0;

            if (subItems != null && _keysInSub)
            {
                if (step != 0) _subHighlighted = Step(subItems, _subHighlighted, step);
                if (InputSystem.GetKeyDown(Key.Left)) _keysInSub = false;
                if (enter && _subHighlighted >= 0)
                {
                    InputSystem.Consume(Key.Enter);
                    PickSub(_subHighlighted);
                }

                return;
            }

            if (step != 0)
            {
                _highlighted = Step(items, _highlighted, step);
                if (_subOf >= 0 && _subOf != _highlighted) CloseSub();
            }

            bool hasChildren = _highlighted >= 0 && items[_highlighted].Children != null;
            if (hasChildren && (InputSystem.GetKeyDown(Key.Right) || enter))
            {
                InputSystem.Consume(Key.Enter);
                OpenSub(_highlighted, withKeys: true);
            }
            else if (enter && _highlighted >= 0)
            {
                InputSystem.Consume(Key.Enter);
                Pick(_highlighted);
            }
        }

        /// <summary>The next item that can be picked, going <paramref name="step"/> from <paramref name="from"/>, round the ends.</summary>
        private static int Step(IReadOnlyList<MenuItem> items, int from, int step)
        {
            int index = from;
            for (int tries = 0; tries < items.Count; tries++)
            {
                index = ((index < 0 && step < 0 ? 0 : index) + step + items.Count) % items.Count;
                if (!items[index].IsSeparator && items[index].Enabled) return index;
            }

            return from;
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

        private static void PickSub(int index)
        {
            if (_items == null || _subOf < 0 || _items[_subOf].Children is not { } children) return;

            MenuItem item = children[index];
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
            _anchor = Ui.ToScreen(anchor);
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
