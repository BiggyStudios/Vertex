using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Rendering;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The scene's lighting in one place (View › Lighting): baking its light, with the bake's settings and what it
    /// lights; and the sky, fog and the rest of how the scene's lit and finished.
    /// </summary>
    public sealed class LightingPanel : IDockPanel
    {
        private readonly EditorContext _context;
        private readonly PropertyGrid _grid;

        public string Title => "Lighting";
        public Icon Icon => Icons.Light;

        public LightingPanel(EditorContext context)
        {
            _context = context;
            _grid = new PropertyGrid(context);
        }

        public void Draw(Rect rect)
        {
            Rect content = PanelFrame.Draw(rect, Title);
            var scroll = ScrollView.Begin(Ui.Id("lighting.scroll"), content);
            float x = content.X + Theme.Padding;
            float width = content.Width - Theme.Padding * 2f - 6f;
            float y = scroll.Top + Theme.Padding;
            float top = y;

            y = Section("Baked lighting", x, y, width);
            y = DrawBaking(_context, _grid, x, y, width);

            y = Section("In this scene", x, y + 10f, width);
            y = DrawCounts(x, y, width);

            y = Section("Sky and environment", x, y + 10f, width);
            y = _grid.Draw(_context.Scene.Lighting, x, y, width);

            scroll.End(y - top + Theme.Padding * 2f);
        }

        private static float Section(string title, float x, float y, float width)
        {
            Ui.Text(new Rect(x, y, width, 18f), title.ToUpperInvariant(), Theme.TextFaint, size: 10.5f, bold: true);
            return y + 24f;
        }

        /// <summary>How many static meshes, and lights of each mode, the scene has: what a bake will take in.</summary>
        private float DrawCounts(float x, float y, float width)
        {
            int statics = 0, moving = 0;
            foreach (MeshRenderer renderer in _context.Scene.FindComponents<MeshRenderer>())
            {
                if (renderer.Static) statics++;
                else moving++;
            }

            // Baked lights that cast shadows have things that move cast shadows from them, if the bake found them a channel
            // of the shadow mask: at most four can reach the same place.
            bool isBaked = _context.Scene.Lighting.BakedLightingFile != "";
            int realtime = 0, mixed = 0, baked = 0, shadowed = 0, movingShadows = 0, leftOut = 0;
            foreach (Light light in _context.Scene.FindComponents<Light>())
            {
                if (light.Mode == LightMode.Baked) baked++;
                else if (light.Mode == LightMode.Mixed) mixed++;
                else realtime++;
                if (light.Type != LightType.Directional && light.Mode != LightMode.Baked && light.CastShadows) shadowed++;
                if (light.Mode != LightMode.Baked || !light.CastShadows) continue;
                movingShadows++;
                if (isBaked && light.ShadowMaskChannel < 0) leftOut++;
            }

            void Row(string label, string value, string? tooltip = null)
            {
                var row = new Rect(x, y, width, Theme.RowHeight);
                Widgets.Label(Ui.Id(Ui.Id("lighting.count"), label), row.TakeLeft(width * 0.5f), label, tooltip);
                Ui.Text(row.CutLeft(width * 0.5f), value, Theme.Text);
                y += Theme.RowHeight;
            }

            Row("Static meshes", $"{statics}", "Mesh Renderers with Static ticked: their light's baked into the lightmap");
            Row("Moving meshes", $"{moving}", "Lit by the light probes once baked");
            Row("Baked lights", $"{baked}", "All their light's baked: they cost nothing as the game runs");
            Row("Mixed lights", $"{mixed}", "Light things as the game runs; their bounced light's baked");
            Row("Realtime lights", $"{realtime}", "Worked out every frame, with no bounce");
            Row("With shadows", $"{shadowed}", "Point and spot lights that aren't baked and cast shadows as the game runs");
            Row("Moving shadows", leftOut > 0 ? $"{movingShadows - leftOut} ({leftOut} left out)" : $"{movingShadows}",
                leftOut > 0
                    ? "Baked lights with Cast Shadows on, so things that move cast shadows from them. Only four can reach the same place: the dimmest that overlap are left out, and only have their baked shadows"
                    : "Baked lights with Cast Shadows on, so things that move cast shadows from them too");
            return y;
        }

        /// <summary>
        /// Whether the scene's light is baked, its bake settings, and the buttons to bake it again or take it away; while
        /// baking, how far it's got. Shared by this panel and the inspector's Baked Lighting card.
        /// </summary>
        internal static float DrawBaking(EditorContext context, PropertyGrid grid, float x, float y, float width)
        {
            Lighting lighting = context.Scene.Lighting;
            int statics = 0;
            foreach (MeshRenderer renderer in context.Scene.FindComponents<MeshRenderer>()) if (renderer.Static) statics++;

            if (context.Baking)
            {
                (string step, float done) = context.BakeProgress;
                var bar = new Rect(x, y + 4f, width, 8f);
                float shown = Ui.Smooth(Ui.Id("bake.progress"), done, 10f);
                Ui.Rect(bar, Theme.Field, 4f);
                Ui.Rect(new Rect(bar.X, bar.Y, MathF.Max(bar.Width * shown, 8f), bar.Height), Theme.Accent, 4f);
                Ui.Text(new Rect(x, y + 16f, width - 90f, 20f), step + "…", Theme.TextDim, size: Theme.SmallTextSize);
                if (Widgets.Button(Ui.Id("bake.cancel"), new Rect(x + width - 84f, y + 16f, 84f, 24f), "Stop")) context.CancelBake();
                y += 48f;
            }
            else
            {
                BakedLighting? baked = lighting.Baked;
                string status = baked?.Lightmap is { } map
                    ? $"Baked: a {map.Width} × {map.Height} lightmap for {baked.Meshes.Count} meshes" + (baked.Probes != null ? ", with light probes" : "")
                    : baked != null ? "Baked: light probes only" : "Not baked";
                Ui.Text(new Rect(x, y, width, 20f), status, baked != null ? Theme.Text : Theme.TextDim);
                y += 22f;

                string hint = statics == 0
                    ? "Tick Static on the Mesh Renderers of what never moves, and set lights to Baked or Mixed."
                    : $"{statics} static {(statics == 1 ? "mesh" : "meshes")}; lights set to Baked or Mixed light them.";
                foreach (string line in Ui.Wrap(hint, width, Theme.SmallTextSize))
                {
                    Ui.Text(new Rect(x, y, width, 16f), line, Theme.TextFaint, size: Theme.SmallTextSize);
                    y += 16f;
                }

                y += 8f;
                var buttons = new Rect(x, y, width, 30f);
                if (Widgets.Button(Ui.Id("bake.go"), buttons.Column(0, 2, 6f), baked != null ? "Bake Again" : "Bake", ButtonStyle.Primary,
                        tooltip: "Works out the light falling on static meshes and in the space between them, and saves the scene")) context.BakeLighting();
                if (Widgets.Button(Ui.Id("bake.clear"), buttons.Column(1, 2, 6f), "Clear", tooltip: "Takes away the baked light, so the scene's lit as the game runs")
                    && baked != null) context.ClearBakedLighting();
                y += 38f;
            }

            return grid.Draw(lighting.Baking, x, y, width);
        }
    }
}
