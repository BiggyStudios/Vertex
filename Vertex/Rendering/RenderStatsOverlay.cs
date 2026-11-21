using System.Numerics;
using System.Text;

using Vertex.Input;
using Vertex.UI;

namespace Vertex.Rendering
{
    /// <summary>
    /// The console's <c>r_showstats</c>: frame rate, the CPU's and GPU's time for drawing (the GPU's by pass), and how
    /// many draws there were, in the top-right corner. Averaged over half a second so the numbers can be read.
    /// </summary>
    internal sealed class RenderStatsOverlay
    {
        private const float _interval = 0.5f;
        private const float _fontSize = 16f;

        private readonly Renderer _renderer;
        private readonly List<(string Pass, double Total)> _passTotals = new();
        private readonly List<string> _lines = new();
        private readonly StringBuilder _text = new();
        private float _elapsed;
        private int _frames;
        private double _cpuTotal;
        private double _updateTotal;
        private int _gpuFrames;
        private long _draws, _culled, _shadowDraws, _lightShadows;

        /// <summary>The key that shows and hides it in a debug build (<see cref="Application.IsDebugBuild"/>).</summary>
        public const Key ToggleKey = Key.F3;

        /// <summary>Whether it's shown. The GPU's time is only measured while it is, as measuring costs a little.</summary>
        public bool Visible
        {
            get => _visible;
            set
            {
                _visible = value;
                _renderer.MeasureGpuTime = value;
            }
        }

        private bool _visible;

        /// <summary>How long the last frame's game update took (everything but drawing), set by the window.</summary>
        public double UpdateMilliseconds { get; set; }

        public RenderStatsOverlay(Renderer renderer)
        {
            _renderer = renderer;
        }

        /// <summary>
        /// Adds the last frame's numbers, and draws the averages if <see cref="Visible"/>. In a debug build, F3 shows or
        /// hides it (not while the console's open and has the keyboard), and a note in the corner says it's a debug build.
        /// </summary>
        public void Update()
        {
            if (Application.IsDebugBuild)
            {
                if (!DevConsole.IsOpen && InputSystem.GetKeyDown(ToggleKey))
                {
                    Visible = !Visible;
                    _lines.Clear();
                    _elapsed = 0f;
                    _frames = 0;
                }

                DrawDebugNote();
            }

            if (!Visible) return;

            RenderStats stats = _renderer.Stats;
            _frames++;
            _elapsed += Time.DeltaTime;
            _cpuTotal += stats.CpuMilliseconds;
            _updateTotal += UpdateMilliseconds;
            _draws += stats.DrawCalls;
            _culled += stats.CulledDrawCalls;
            _shadowDraws += stats.ShadowDrawCalls;
            _lightShadows += stats.LightShadowLayers;

            if (stats.GpuPasses.Count > 0)
            {
                _gpuFrames++;
                foreach (GpuPassTime pass in stats.GpuPasses)
                {
                    int index = 0;
                    while (index < _passTotals.Count && _passTotals[index].Pass != pass.Pass) index++;
                    if (index < _passTotals.Count) _passTotals[index] = (pass.Pass, _passTotals[index].Total + pass.Milliseconds);
                    else _passTotals.Add((pass.Pass, pass.Milliseconds));
                }
            }

            if (_elapsed >= _interval) Summarise();
            Draw();
        }

        private void Summarise()
        {
            _lines.Clear();
            double frameTime = _elapsed * 1000.0 / _frames;
            _lines.Add($"{_frames / _elapsed:0} fps   {frameTime:0.00} ms a frame   {_renderer.Width}x{_renderer.Height}");
            _lines.Add($"CPU   game {_updateTotal / _frames:0.00} ms   drawing {_cpuTotal / _frames:0.00} ms");

            if (_gpuFrames > 0)
            {
                double gpu = 0;
                foreach ((_, double total) in _passTotals) gpu += total;
                _lines.Add($"GPU ({_renderer.Backend.Name})   {gpu / _gpuFrames:0.00} ms");
                foreach ((string pass, double total) in _passTotals) _lines.Add($"   {pass}   {total / _gpuFrames:0.00}");
            }
            else
            {
                _lines.Add($"GPU ({_renderer.Backend.Name})   not measured");
            }

            _lines.Add($"Draws {_draws / _frames}   culled {_culled / _frames}   shadow {_shadowDraws / _frames}   light shadows redrawn {_lightShadows}");

            _text.Clear();
            foreach (string line in _lines) _text.AppendLine(line);
            LastSummary = _text.ToString();

            _elapsed = 0f;
            _frames = 0;
            _gpuFrames = 0;
            _cpuTotal = 0;
            _updateTotal = 0;
            _draws = _culled = _shadowDraws = _lightShadows = 0;
            _passTotals.Clear();
        }

        /// <summary>The numbers last shown, as text, for logging.</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>"Debug build", small in the bottom-right corner, so a debug build isn't mistaken for one to give players.</summary>
        private void DrawDebugNote()
        {
            Font font = DevConsole.Font ?? Font.Default;
            string note = Visible ? "Debug build" : "Debug build  ·  F3 frame times";
            Vector2 size = Canvas.MeasureText(font, note, _noteSize);
            Canvas.DrawText(font, note, new Vector2(Canvas.Width - size.X - 12f, Canvas.Height - Canvas.LineHeight(font, _noteSize) - 8f), new Color(1f, 1f, 1f, 0.5f), _noteSize, shadow: true);
        }

        private const float _noteSize = 13f;

        private void Draw()
        {
            // The console's font if it's been opened, so they match; it's loaded then, though, not before.
            Font font = DevConsole.Font ?? Font.Default;
            if (_lines.Count == 0) return;

            float lineHeight = Canvas.LineHeight(font, _fontSize);
            float width = 0f;
            foreach (string line in _lines) width = MathF.Max(width, Canvas.MeasureText(font, line, _fontSize).X);

            var corner = new Vector2(Canvas.Width - width - 16f, 12f);
            Canvas.DrawRect(corner - new Vector2(8f, 6f), new Vector2(width + 16f, lineHeight * _lines.Count + 12f), new Color(0f, 0f, 0f, 0.55f));
            for (int i = 0; i < _lines.Count; i++)
                Canvas.DrawText(font, _lines[i], corner + new Vector2(0f, i * lineHeight), Color.White, _fontSize);
        }
    }
}
