using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;

namespace Vertex.Editor.Panels
{
    /// <summary>A panel that can be docked as a tab: the outliner, inspector, assets and console.</summary>
    public interface IDockPanel
    {
        string Title { get; }
        Icon Icon { get; }

        /// <summary>A number on its tab, and its colour (errors in the Console it hasn't shown yet); null for none.</summary>
        (string Text, Color Color)? Badge => null;

        void Draw(Rect rect);
    }

    /// <summary>The places round the scene view where panels go.</summary>
    public enum DockArea
    {
        Left,
        Right,
        Bottom
    }

    /// <summary>
    /// Where the panels are: each of the three places round the scene view holds panels as tabs, and shows one at a time.
    /// Drag a tab to another place (or along its row to reorder it); the place it would land lights up. A place with no
    /// tabs is gone, and the scene view takes its room. Kept in the preferences.
    /// </summary>
    public sealed class DockLayout
    {
        private readonly Preferences _preferences;
        private readonly Dictionary<string, IDockPanel> _panels = new();

        // Where each panel belongs if it's opened from the View menu while closed.
        private readonly Dictionary<string, DockArea> _homes = new();

        // A tab pressed on, and where; the tab being dragged; and where each place and tab was drawn last, for dropping.
        private string? _pressed;
        private Vector2 _pressedAt;
        private string? _dragging;
        private readonly Dictionary<DockArea, Rect> _areas = new();
        private readonly Dictionary<DockArea, List<(string Name, Rect Rect)>> _tabRects = new();

        private const float _tabHeight = 26f;

        public DockLayout(Preferences preferences)
        {
            _preferences = preferences;
        }

        /// <summary>Adds a panel, which goes to <paramref name="home"/> when it's opened while closed.</summary>
        public void Add(IDockPanel panel, DockArea home)
        {
            _panels[panel.Title] = panel;
            _homes[panel.Title] = home;
        }

        /// <summary>The names of the panels in a place, in order, leaving out any that don't exist (from an older preferences file).</summary>
        public List<string> Tabs(DockArea area)
        {
            List<string> tabs = area switch { DockArea.Left => _preferences.LeftTabs, DockArea.Right => _preferences.RightTabs, _ => _preferences.BottomTabs };
            tabs.RemoveAll(name => !_panels.ContainsKey(name));
            return tabs;
        }

        public bool Has(DockArea area) => Tabs(area).Count > 0;

        public bool IsOpen(string name) => Find(name) != null;

        private DockArea? Find(string name)
        {
            foreach (DockArea area in Enum.GetValues<DockArea>())
            {
                if (Tabs(area).Contains(name)) return area;
            }

            return null;
        }

        /// <summary>The tab a place shows: the one picked last, or its first.</summary>
        public string? Active(DockArea area)
        {
            List<string> tabs = Tabs(area);
            if (tabs.Count == 0) return null;
            return _preferences.ActiveTabs.TryGetValue(area.ToString(), out string? active) && tabs.Contains(active) ? active : tabs[0];
        }

        /// <summary>Brings a panel to the front of its place, opening it in its usual place first if it's closed.</summary>
        public void Show(string name)
        {
            if (!_panels.ContainsKey(name)) return;
            DockArea area = Find(name) ?? Open(name);
            _preferences.ActiveTabs[area.ToString()] = name;
        }

        private DockArea Open(string name)
        {
            DockArea home = _homes[name];
            Tabs(home).Add(name);
            return home;
        }

        public void Close(string name)
        {
            foreach (DockArea area in Enum.GetValues<DockArea>()) Tabs(area).Remove(name);
        }

        public void Toggle(string name)
        {
            if (IsOpen(name)) Close(name);
            else Show(name);
        }

        /// <summary>Puts every panel back where it started.</summary>
        public void Reset()
        {
            var defaults = new Preferences();
            _preferences.LeftTabs = defaults.LeftTabs;
            _preferences.RightTabs = defaults.RightTabs;
            _preferences.BottomTabs = defaults.BottomTabs;
            _preferences.ActiveTabs.Clear();
        }

        /// <summary>Moves a panel to a place, at <paramref name="index"/> among its tabs (the end if -1), and shows it there.</summary>
        public void Move(string name, DockArea to, int index = -1)
        {
            DockArea? from = Find(name);
            List<string> target = Tabs(to);
            int oldIndex = target.IndexOf(name);
            Close(name);

            if (from == to && oldIndex >= 0 && index > oldIndex) index--;
            target.Insert(index < 0 || index > target.Count ? target.Count : index, name);
            _preferences.ActiveTabs[to.ToString()] = name;
        }

        // ---------------------------------------------------------------- Drawing

        /// <summary>Draws a place: its tabs along the top, and the panel whose tab is picked.</summary>
        public void Draw(DockArea area, Rect rect)
        {
            _areas[area] = rect;
            List<string> tabs = Tabs(area);
            string? active = Active(area);
            if (active == null) return;

            Ui.Rect(rect, Theme.Panel, Theme.PanelRadius);
            Ui.Outline(rect, Theme.Border, Theme.PanelRadius);
            Ui.Rect(new Rect(rect.X, rect.Y + PanelFrame.HeaderHeight - 1f, rect.Width, 1f), Theme.Border);

            // The tabs, the picked one lit, with a line under it that slides to whichever is picked.
            var drawn = new List<(string, Rect)>();
            float x = rect.X + 6f;
            float y = rect.Y + (PanelFrame.HeaderHeight - _tabHeight) * 0.5f;
            int areaId = Ui.Id("dock." + area);
            Rect? activeRect = null;
            foreach (string name in tabs.ToList())
            {
                IDockPanel panel = _panels[name];
                (string Text, Color Color)? badge = name == active ? null : panel.Badge;
                float badgeWidth = badge is { } b ? Ui.TextWidth(b.Text, Theme.SmallTextSize) + 12f : 0f;
                var tab = new Rect(x, y, Ui.TextWidth(name, Theme.TitleSize, bold: true) + 36f + (badgeWidth > 0f ? badgeWidth + 4f : 0f), _tabHeight);
                drawn.Add((name, tab));
                if (name == active) activeRect = tab;

                int id = Ui.Id(areaId, name);
                Interaction interaction = Ui.Interact(id, tab, rightClick: true);
                if (interaction.Pressed)
                {
                    _preferences.ActiveTabs[area.ToString()] = name;
                    _pressed = name;
                    _pressedAt = Ui.Mouse;
                }

                if (interaction.RightClicked) Menu.Open(Ui.Id(id, "menu"), Ui.Mouse, TabMenu(name, area));

                bool on = name == active;
                float hover = Ui.Smooth(Ui.Id(id, "hover"), interaction.Hovered && !on ? 1f : 0f, 20f);
                if (on) Ui.Rect(tab, Theme.ControlHover.WithAlpha(0.7f), 6f);
                else if (hover > 0.01f) Ui.Rect(tab, Theme.Hover.WithAlpha(1.6f * hover), 6f);
                if (_dragging == name) Ui.Outline(tab, Theme.Accent.WithAlpha(0.6f), 6f);

                panel.Icon(new Vector2(tab.X + 13f, tab.Centre.Y), 14f, on ? Theme.AccentText : Theme.TextFaint);
                Ui.Text(new Rect(tab.X + 26f, tab.Y, tab.Width - 26f, tab.Height), name, on ? Theme.Text : Theme.TextDim, bold: on, size: Theme.TitleSize);
                if (badge is { } shownBadge)
                {
                    var chip = new Rect(tab.Right - badgeWidth - 6f, tab.Y + 5f, badgeWidth, tab.Height - 10f);
                    Ui.Rect(chip, shownBadge.Color, chip.Height * 0.5f);
                    Ui.Text(chip, shownBadge.Text, Color.White, align: 0.5f, size: Theme.SmallTextSize - 1f, bold: true);
                }

                x = tab.Right + 2f;
            }

            _tabRects[area] = drawn;
            if (activeRect is { } lit)
            {
                float lineX = Ui.Smooth(Ui.Id(areaId, "lineX"), lit.X + 8f, 24f, lit.X + 8f);
                float lineWidth = Ui.Smooth(Ui.Id(areaId, "lineW"), lit.Width - 16f, 24f, lit.Width - 16f);
                Ui.Rect(new Rect(lineX, rect.Y + PanelFrame.HeaderHeight - 2.5f, lineWidth, 2f), Theme.Accent, 1f);
            }

            // The panel itself, drawing into the area under the tabs; its own header controls go right of the tabs.
            PanelFrame.Docked = true;
            PanelFrame.TitleRight = x + 10f;
            try
            {
                _panels[active].Draw(rect);
            }
            finally
            {
                PanelFrame.Docked = false;
            }
        }

        private List<MenuItem> TabMenu(string name, DockArea area)
        {
            var items = new List<MenuItem>();
            foreach (DockArea place in Enum.GetValues<DockArea>())
            {
                if (place != area) items.Add(new MenuItem($"Move to the {(place == DockArea.Bottom ? "Bottom" : place.ToString())}", () => Move(name, place)));
            }

            items.Add(MenuItem.Separator);
            items.Add(new MenuItem("Close", () => Close(name)) { Icon = Icons.Close });
            return items;
        }

        // ---------------------------------------------------------------- Dragging tabs

        /// <summary>
        /// Moves a dragged tab: it follows the mouse, the place it would land lights up (an empty place shows where it
        /// would open, along that edge of <paramref name="middle"/>), and it lands there when let go. Called after the
        /// places are drawn.
        /// </summary>
        public void UpdateDrag(Rect body, Rect middle)
        {
            if (_pressed != null && _dragging == null && Ui.MouseHeld() && Vector2.Distance(Ui.Mouse, _pressedAt) > 6f) _dragging = _pressed;
            if (!Ui.MouseHeld() && _dragging == null) _pressed = null;
            if (_dragging is not { } dragged) return;

            (DockArea Area, Rect Zone, int Index)? target = Target(body, middle);
            Vector2 mouse = Ui.Mouse;
            IDockPanel panel = _panels[dragged];
            Ui.Overlay(() =>
            {
                if (target is { } landing)
                {
                    Ui.Rect(landing.Zone, Theme.Accent.WithAlpha(0.14f), Theme.PanelRadius);
                    Ui.Outline(landing.Zone, Theme.Accent.WithAlpha(0.8f), Theme.PanelRadius, 1.5f);
                }

                var label = new Rect(mouse.X + 12f, mouse.Y + 8f, Ui.TextWidth(dragged, Theme.TitleSize, bold: true) + 40f, 28f);
                Ui.Shadow(label, 6f, 12f, 0.6f);
                Ui.Rect(label, Theme.Popup, 6f);
                Ui.Outline(label, Theme.Accent.WithAlpha(0.7f), 6f);
                panel.Icon(new Vector2(label.X + 14f, label.Centre.Y), 14f, Theme.AccentText);
                Ui.Text(label.CutLeft(28f), dragged, Theme.Text, bold: true, size: Theme.TitleSize);
            });

            if (Ui.MouseHeld()) return;

            if (target is { } dropped) Move(dragged, dropped.Area, dropped.Index);
            _dragging = null;
            _pressed = null;
        }

        /// <summary>Where a tab let go of now would land: the place under the mouse (before the tab it's over), or an empty place's edge.</summary>
        private (DockArea Area, Rect Zone, int Index)? Target(Rect body, Rect middle)
        {
            Vector2 mouse = Ui.Mouse;
            foreach (DockArea area in Enum.GetValues<DockArea>())
            {
                if (!Has(area) || !_areas.TryGetValue(area, out Rect rect) || !rect.Contains(mouse)) continue;

                // Along its tabs: between them. Anywhere else in it: at the end.
                int index = -1;
                if (mouse.Y < rect.Y + PanelFrame.HeaderHeight && _tabRects.TryGetValue(area, out var tabs))
                {
                    index = tabs.Count;
                    for (int i = 0; i < tabs.Count; i++)
                    {
                        if (mouse.X < tabs[i].Rect.Centre.X)
                        {
                            index = i;
                            break;
                        }
                    }
                }

                return (area, rect, index);
            }

            // An empty place opens along its edge.
            const float reach = 110f;
            if (!Has(DockArea.Left) && mouse.X < body.X + reach) return (DockArea.Left, body.TakeLeft(MathF.Min(_preferences.OutlinerWidth, body.Width * 0.4f)), -1);
            if (!Has(DockArea.Right) && mouse.X > body.Right - reach) return (DockArea.Right, body.TakeRight(MathF.Min(_preferences.InspectorWidth, body.Width * 0.4f)), -1);
            if (!Has(DockArea.Bottom) && middle.Contains(mouse) && mouse.Y > middle.Bottom - reach)
            {
                return (DockArea.Bottom, middle.TakeBottom(MathF.Min(_preferences.AssetsHeight, middle.Height * 0.5f)), -1);
            }

            return null;
        }
    }
}
