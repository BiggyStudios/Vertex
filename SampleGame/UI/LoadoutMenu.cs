using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Build a loadout by drag and drop: one weapon in slot 2, one gadget in slot 3, in any combination (see
    /// <see cref="Loadout"/>). The two slots are at the top, and the armoury below holds everything there is. Drag a
    /// weapon from the armoury onto slot 2, or a gadget onto slot 3; whichever slot can take what you're holding lights
    /// up. Drag the gadget out of its slot to go without one; slot 2 always has a weapon. Each change is made the moment
    /// it's dropped, so there's nothing to confirm: Escape, B or Enter closes the menu. It comes up going into the game,
    /// and B (or Change loadout in the pause menu) brings it back.
    /// </summary>
    public class LoadoutMenu : Component
    {
        private enum Slot { None, Weapon, Gadget }

        private const float _slotWidth = 320f;
        private const float _slotHeight = 130f;
        private const float _slotGap = 40f;
        private const float _itemWidth = 210f;
        private const float _itemHeight = 84f;
        private const float _itemGap = 20f;
        private const int _itemsPerRow = 5;
        private const float _padding = 18f;

        private static readonly Color _panel = new(0.07f, 0.08f, 0.1f, 0.85f);
        private static readonly Color _panelHovered = new(0.12f, 0.13f, 0.16f, 0.92f);
        private static readonly Color _slotPanel = new(0.04f, 0.05f, 0.07f, 0.9f);
        private static readonly Color _slotReady = new(0.15f, 0.12f, 0.09f, 0.95f);
        private static readonly Color _dim = new(1f, 1f, 1f, 0.6f);
        private static readonly Color _faint = new(1f, 1f, 1f, 0.3f);

        public static bool IsOpen { get; private set; }

        public Player Player { get; set; } = null!;

        /// <summary>Open the menu when the game starts, so new players choose a loadout first.</summary>
        public bool OpenOnStart { get; set; } = true;

        // What's in the slots: the player's loadout, as it was when the menu opened and as it's changed since.
        private Weapon _weapon = Loadout.Default.Weapon;
        private Weapon? _gadget = Loadout.Default.Gadget;

        // What's being dragged, where from, and where on it it was picked up.
        private Weapon? _dragging;
        private Slot _draggedFrom;
        private Vector2 _grip;

        // The click that opened the menu (from the pause menu) mustn't also pick up whatever's under the pointer.
        private bool _ignoreClick;

        protected override void Start()
        {
            if (OpenOnStart) Open();
        }

        protected override void OnDestroy() => IsOpen = false;

        /// <summary>Opens the menu, starting from the loadout the player has now.</summary>
        public void Open()
        {
            IsOpen = true;
            _weapon = Player.Loadout.Weapon;
            _gadget = Player.Loadout.Gadget;
            _dragging = null;
            _ignoreClick = InputSystem.GetMouseButton(MouseButton.Left);
        }

        public void Close()
        {
            IsOpen = false;
            _dragging = null;
        }

        protected override void Update()
        {
            bool otherInputOpen = ChatBox.IsTyping || DevConsole.IsOpen || PauseMenu.IsOpen || MainMenu.IsOpen;

            if (!IsOpen)
            {
                if (InputSystem.GetKeyDown(Key.B) && !otherInputOpen) Open();
                return;
            }

            if (!InputSystem.GetMouseButton(MouseButton.Left)) _ignoreClick = false;

            if (!DevConsole.IsOpen)
            {
                if (InputSystem.GetKeyDown(Key.Escape) || InputSystem.GetKeyDown(Key.B))
                {
                    InputSystem.Consume(Key.Escape);
                    Close();
                    return;
                }

                if (InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter))
                {
                    // So the same press doesn't also start a chat message once the menu is gone.
                    InputSystem.Consume(Key.Enter);
                    InputSystem.Consume(Key.KeypadEnter);
                    Close();
                    return;
                }
            }

            Draw();
        }

        /// <summary>Puts on what's in the slots, straight away.</summary>
        private void Equip() => Player.SetLoadout(new Loadout(_weapon, _gadget));

        private static bool IsGadget(Weapon item) => Loadout.Gadgets.Contains(item);

        /// <summary>The slot <paramref name="item"/> goes in.</summary>
        private static Slot SlotFor(Weapon item) => IsGadget(item) ? Slot.Gadget : Slot.Weapon;

        private void Draw()
        {
            Canvas.DrawRect(Vector2.Zero, Canvas.Size, new Color(0.02f, 0.03f, 0.05f, 0.55f));

            // Everything stacked in the middle: the title, the two slots, the armoury, and how to close it.
            List<Weapon> items = [.. Loadout.Weapons, .. Loadout.Gadgets];
            int rows = (items.Count + _itemsPerRow - 1) / _itemsPerRow;
            float armouryHeight = rows * _itemHeight + (rows - 1) * 16f;
            float height = 90f + _slotHeight + 40f + 24f + armouryHeight + 30f + 20f;
            float y = (Canvas.Height - height) / 2f;
            MenuText.Centred(Fonts.Bold, "Build your loadout", y, 40f, Color.White);
            MenuText.Centred(Fonts.Regular, "Drag a weapon into slot 2 and a gadget into slot 3. B to change it later.", y + 52f, 17f, _dim);
            y += 90f;

            Vector2 mouse = Canvas.MousePosition;
            bool canGrab = !_ignoreClick && !DevConsole.IsOpen && _dragging == null && InputSystem.GetMouseButtonDown(MouseButton.Left);

            // The slots.
            float slotsLeft = (Canvas.Width - (_slotWidth * 2f + _slotGap)) / 2f;
            var weaponSlot = new Vector2(slotsLeft, y);
            var gadgetSlot = new Vector2(slotsLeft + _slotWidth + _slotGap, y);
            var slotSize = new Vector2(_slotWidth, _slotHeight);
            DrawSlot(Slot.Weapon, "2", "WEAPON", _weapon, weaponSlot);
            DrawSlot(Slot.Gadget, "3", "GADGET", _gadget, gadgetSlot);
            if (canGrab && Contains(weaponSlot, slotSize, mouse)) Grab(_weapon, Slot.Weapon, mouse - weaponSlot, slotSize);
            else if (canGrab && _gadget != null && Contains(gadgetSlot, slotSize, mouse)) Grab(_gadget, Slot.Gadget, mouse - gadgetSlot, slotSize);
            y += _slotHeight + 40f;

            // The armoury: everything there is, to drag from.
            float rowWidth = Math.Min(items.Count, _itemsPerRow) * (_itemWidth + _itemGap) - _itemGap;
            float armouryLeft = (Canvas.Width - rowWidth) / 2f;
            Canvas.DrawText(Fonts.Bold, "ARMOURY", new Vector2(armouryLeft, y), _dim, 15f);
            float labelWidth = Canvas.MeasureText(Fonts.Bold, "ARMOURY", 15f).X;
            Canvas.DrawText(Fonts.Regular, "drag from here", new Vector2(armouryLeft + labelWidth + 10f, y), _faint, 15f);
            y += 24f;

            var itemSize = new Vector2(_itemWidth, _itemHeight);
            for (int i = 0; i < items.Count; i++)
            {
                Weapon item = items[i];
                var position = new Vector2(armouryLeft + i % _itemsPerRow * (_itemWidth + _itemGap), y + i / _itemsPerRow * (_itemHeight + 16f));
                bool hovered = _dragging == null && Contains(position, itemSize, mouse);
                string? inSlot = item == _weapon ? "IN SLOT 2" : item == _gadget ? "IN SLOT 3" : null;
                DrawItem(item, position, itemSize, hovered ? _panelHovered : _panel, inSlot, alpha: _dragging == item ? 0.35f : 1f);
                if (canGrab && Contains(position, itemSize, mouse)) Grab(item, Slot.None, mouse - position, itemSize);
            }

            y += armouryHeight + 30f;
            MenuText.Centred(Fonts.Regular, "Changes are made as you drop them  ·  Escape or B to close", y, 16f, _faint);

            if (_dragging == null) return;

            // The item being dragged follows the pointer, on top of everything. Let go over the slot it goes in and it's
            // equipped there and then; take the gadget out of its slot and let go anywhere else, and the slot's emptied.
            if (InputSystem.GetMouseButton(MouseButton.Left))
            {
                DrawItem(_dragging, mouse - _grip, itemSize, _panelHovered, null, alpha: 0.9f);
                return;
            }

            Slot over = Contains(weaponSlot, slotSize, mouse) ? Slot.Weapon : Contains(gadgetSlot, slotSize, mouse) ? Slot.Gadget : Slot.None;
            if (over == SlotFor(_dragging))
            {
                if (over == Slot.Weapon) _weapon = _dragging;
                else _gadget = _dragging;
                Equip();
            }
            else if (_draggedFrom == Slot.Gadget && over != Slot.Gadget)
            {
                _gadget = null;
                Equip();
            }

            _dragging = null;
        }

        /// <summary>Picks <paramref name="item"/> up, held where it was grabbed, scaled to the size it's dragged at.</summary>
        private void Grab(Weapon item, Slot from, Vector2 grabbedAt, Vector2 size)
        {
            _dragging = item;
            _draggedFrom = from;
            _grip = grabbedAt / size * new Vector2(_itemWidth, _itemHeight);
        }

        /// <summary>
        /// One slot, with its key and what goes in it, and what's in it now, if anything. While something that goes in it
        /// is being dragged, it lights up, brighter with the pointer over it.
        /// </summary>
        private void DrawSlot(Slot slot, string key, string label, Weapon? item, Vector2 position)
        {
            var size = new Vector2(_slotWidth, _slotHeight);
            bool takesDragged = _dragging != null && SlotFor(_dragging) == slot;
            bool hovered = Contains(position, size, Canvas.MousePosition);

            Canvas.DrawRect(position, size, takesDragged ? _slotReady : _slotPanel);
            Color border = takesDragged ? Gui.AccentColor with { A = hovered ? 1f : 0.6f } : _faint with { A = _dragging != null ? 0.12f : 0.25f };
            Outline(position, size, takesDragged && hovered ? 3f : 1.5f, border);

            float left = position.X + _padding;
            float right = position.X + _slotWidth - _padding;
            float y = position.Y + _padding - 4f;

            // The key, then what goes in the slot.
            var badge = new Vector2(left, y);
            Canvas.DrawRect(badge, new Vector2(24f, 24f), new Color(1f, 1f, 1f, 0.12f));
            Canvas.DrawText(Fonts.Bold, key, badge + (new Vector2(24f) - Canvas.MeasureText(Fonts.Bold, key, 15f)) / 2f, Color.White, 15f);
            Canvas.DrawText(Fonts.Bold, label, new Vector2(left + 34f, y + 3f), _dim, 14f);
            y += 34f;

            // Empty, about to take what's dragged over it, or what's in it is being dragged out.
            bool draggedOut = _dragging == item && _draggedFrom == slot;
            if (item == null || draggedOut || (takesDragged && hovered))
            {
                string hint = takesDragged && hovered ? $"Let go to equip the {_dragging!.Name.ToLowerInvariant()}"
                    : item == null ? $"Drag a {label.ToLowerInvariant()} here"
                    : slot == Slot.Gadget ? "Let go anywhere else to go without one"
                    : "Slot 2 needs a weapon: let go to put it back";
                Canvas.DrawText(Fonts.Regular, hint, new Vector2(left, y + 12f), takesDragged && hovered ? Color.White : _faint, 16f);
                return;
            }

            Canvas.DrawText(Fonts.Bold, item.Name, new Vector2(left, y - 4f), Color.White, 26f);
            y += 34f;
            foreach (string line in MenuText.Wrap(Fonts.Regular, Loadout.Describe(item), 15f, right - left).Take(2))
            {
                Canvas.DrawText(Fonts.Regular, line, new Vector2(left, y), _dim, 15f);
                y += Canvas.LineHeight(Fonts.Regular, 15f);
            }
        }

        /// <summary>One item's card in the armoury, or being dragged: its name, what kind it is, and which slot it's in.</summary>
        private static void DrawItem(Weapon item, Vector2 position, Vector2 size, Color panel, string? inSlot, float alpha)
        {
            Canvas.DrawRect(position, size, panel with { A = panel.A * alpha });
            Color kindColor = IsGadget(item) ? new Color(0.45f, 0.75f, 1f) : Gui.AccentColor;
            Canvas.DrawRect(position, new Vector2(4f, size.Y), kindColor with { A = alpha });

            float left = position.X + _padding;
            float y = position.Y + 14f;
            Canvas.DrawText(Fonts.Bold, item.Name, new Vector2(left, y), Color.White with { A = alpha }, 20f);
            y += 32f;
            Canvas.DrawText(Fonts.Bold, IsGadget(item) ? "GADGET" : "WEAPON", new Vector2(left, y), kindColor with { A = alpha }, 12f);

            if (inSlot != null)
            {
                float width = Canvas.MeasureText(Fonts.Bold, inSlot, 12f).X;
                Canvas.DrawText(Fonts.Bold, inSlot, new Vector2(position.X + size.X - _padding - width, y), _dim with { A = 0.6f * alpha }, 12f);
            }
        }

        private static void Outline(Vector2 position, Vector2 size, float thickness, Color color)
        {
            Canvas.DrawRect(position, new Vector2(size.X, thickness), color);
            Canvas.DrawRect(position + new Vector2(0f, size.Y - thickness), new Vector2(size.X, thickness), color);
            Canvas.DrawRect(position, new Vector2(thickness, size.Y), color);
            Canvas.DrawRect(position + new Vector2(size.X - thickness, 0f), new Vector2(thickness, size.Y), color);
        }

        private static bool Contains(Vector2 position, Vector2 size, Vector2 point) =>
            point.X >= position.X && point.Y >= position.Y && point.X < position.X + size.X && point.Y < position.Y + size.Y;
    }
}
