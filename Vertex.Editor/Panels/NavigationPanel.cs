using Vertex.AI;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// How characters find their way round the scene (View › Navigation): baking its navmesh, the size of character it's
    /// made for, and what in the scene walks it. While it's open, the navmesh is drawn over the scene view.
    /// </summary>
    public sealed class NavigationPanel : IDockPanel
    {
        private readonly EditorContext _context;
        private readonly PropertyGrid _grid;

        public string Title => "Navigation";
        public Icon Icon => Icons.Route;

        public NavigationPanel(EditorContext context)
        {
            _context = context;
            _grid = new PropertyGrid(context);
        }

        public void Draw(Rect rect)
        {
            _context.NavigationShownAt = Ui.Time;
            Rect content = PanelFrame.Draw(rect, Title);
            var scroll = ScrollView.Begin(Ui.Id("navigation.scroll"), content);
            float x = content.X + Theme.Padding;
            float width = content.Width - Theme.Padding * 2f - 6f;
            float y = scroll.Top + Theme.Padding;
            float top = y;

            y = Section("Navmesh", x, y, width);
            y = DrawBaking(x, y, width);

            y = Section("Agents", x, y + 10f, width);
            y = Note("The size of character it's made for, and what they can walk up. Bake again after changing them.", x, y, width);
            y = _grid.Draw(_context.Scene.Navigation, x, y, width);

            y = Section("In this scene", x, y + 10f, width);
            y = DrawCounts(x, y, width);

            scroll.End(y - top + Theme.Padding * 2f);
        }

        private static float Section(string title, float x, float y, float width)
        {
            Ui.Text(new Rect(x, y, width, 18f), title.ToUpperInvariant(), Theme.TextFaint, size: 10.5f, bold: true);
            return y + 24f;
        }

        private static float Note(string text, float x, float y, float width)
        {
            foreach (string line in Ui.Wrap(text, width, Theme.SmallTextSize))
            {
                Ui.Text(new Rect(x, y, width, 16f), line, Theme.TextFaint, size: Theme.SmallTextSize);
                y += 16f;
            }

            return y + 6f;
        }

        /// <summary>Whether the scene's navmesh is baked, the buttons to bake it again or take it away, and whether it's shown.</summary>
        private float DrawBaking(float x, float y, float width)
        {
            NavigationSettings navigation = _context.Scene.Navigation;
            if (_context.BakingNavMesh)
            {
                Ui.Text(new Rect(x, y, width - 90f, 24f), "Baking the navmesh…", Theme.TextDim);
                if (Widgets.Button(Ui.Id("navigation.cancel"), new Rect(x + width - 84f, y, 84f, 24f), "Stop")) _context.CancelNavMeshBake();
                return y + 34f;
            }

            NavMesh? navMesh = navigation.NavMeshFile.Length > 0 ? navigation.NavMesh : null;
            string status = navMesh == null ? "Not baked" : navMesh.IsEmpty ? "Baked, but there's nowhere to walk" : $"Baked: {navMesh.WalkableArea:0} square units to walk on";
            Ui.Text(new Rect(x, y, width, 20f), status, navMesh is { IsEmpty: false } ? Theme.Text : Theme.TextDim);
            y += 22f;

            int sources = NavMeshBuilder.CountObjects(_context.Scene);
            y = Note(sources == 0
                ? "It's made from what never moves: give floors and walls colliders (without a Rigidbody), or tick Static on their Mesh Renderers."
                : $"Made from {sources} {(sources == 1 ? "object" : "objects")}: colliders of what has no Rigidbody, and static Mesh Renderers. Without a bake, it's made as the game starts.",
                x, y, width);

            y += 2f;
            var buttons = new Rect(x, y, width, 30f);
            if (Widgets.Button(Ui.Id("navigation.bake"), buttons.Column(0, 2, 6f), navMesh != null ? "Bake Again" : "Bake", ButtonStyle.Primary,
                    tooltip: "Works out where characters can walk, keeps it in a file beside the scene, and saves the scene")) _context.BakeNavMesh();
            if (Widgets.Button(Ui.Id("navigation.clear"), buttons.Column(1, 2, 6f), "Clear", tooltip: "Takes the baked navmesh away") && navMesh != null) _context.ClearNavMesh();
            y += 38f;

            bool show = _context.Preferences.ShowNavMesh;
            var row = new Rect(x, y, width, Theme.RowHeight);
            if (Widgets.Checkbox(Ui.Id("navigation.show"), new Rect(row.X, row.Y, Theme.RowHeight, Theme.RowHeight), ref show)) _context.Preferences.ShowNavMesh = show;
            Widgets.Label(Ui.Id("navigation.showLabel"), row.CutLeft(Theme.RowHeight + 6f), "Show in the scene view", "Draws the navmesh over the scene while this panel's open");
            return y + Theme.RowHeight + 4f;
        }

        /// <summary>How many agents walk the navmesh, and how many characters think with behaviour trees.</summary>
        private float DrawCounts(float x, float y, float width)
        {
            int agents = _context.Scene.FindComponents<NavMeshAgent>().Count();
            int trees = _context.Scene.FindComponents<BehaviorTree>().Count();

            void Row(string label, string value, string tooltip)
            {
                var row = new Rect(x, y, width, Theme.RowHeight);
                Widgets.Label(Ui.Id(Ui.Id("navigation.count"), label), row.TakeLeft(width * 0.5f), label, tooltip);
                Ui.Text(row.CutLeft(width * 0.5f), value, Theme.Text);
                y += Theme.RowHeight;
            }

            Row("Agents", $"{agents}", "Nav Mesh Agents: what walks the navmesh");
            Row("Behaviour trees", $"{trees}", "Behavior Tree components: characters deciding what to do");
            return y;
        }
    }
}
