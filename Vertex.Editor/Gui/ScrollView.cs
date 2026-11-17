using System.Numerics;

using Vertex.Input;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// A rectangle whose contents can be taller than it: the mouse wheel scrolls them, easing to a stop, and a thin bar
    /// down the right shows where you are, fading in while scrolling or with the mouse near it. Contents are laid out from
    /// <see cref="Top"/> and cut off at the edges.
    /// <code>
    /// var scroll = ScrollView.Begin(id, rect);
    /// float y = scroll.Top;  // ...draw rows, adding their heights to y...
    /// scroll.End(y - scroll.Top);
    /// </code>
    /// </summary>
    public readonly struct ScrollView
    {
        private sealed class State
        {
            public float Target;
            public float Offset;
            public float ContentHeight;
            public float LastScrolled = -10f;
        }

        private static readonly Dictionary<int, State> _states = new();

        private const float _wheelStep = 64f;
        private const float _barWidth = 4f;

        private readonly int _id;
        private readonly State _state;

        /// <summary>The visible rectangle.</summary>
        public Rect Viewport { get; }

        /// <summary>Where the contents start, scrolled: lay them out from here down.</summary>
        public float Top => Viewport.Y - _state.Offset;

        /// <summary>How far the contents are scrolled, in UI units.</summary>
        public float Offset => _state.Offset;

        private ScrollView(int id, Rect viewport, State state)
        {
            _id = id;
            Viewport = viewport;
            _state = state;
        }

        public static ScrollView Begin(int id, Rect viewport)
        {
            if (!_states.TryGetValue(id, out State? state))
            {
                state = new State();
                _states[id] = state;
            }

            Ui.ScrollArea(id, viewport);
            if (Ui.ScrollTarget == id && InputSystem.ScrollDelta != 0f && !Ui.MouseHeld(MouseButton.Right))
            {
                state.Target -= InputSystem.ScrollDelta * _wheelStep;
                state.LastScrolled = Ui.Time;
            }

            float max = MathF.Max(state.ContentHeight - viewport.Height, 0f);
            state.Target = Math.Clamp(state.Target, 0f, max);
            state.Offset = Math.Clamp(Ui.Approach(state.Offset, state.Target, 20f, Ui.DeltaTime), 0f, MathF.Max(max, state.Offset));

            Ui.PushClip(viewport);
            return new ScrollView(id, viewport, state);
        }

        /// <summary>Scrolls just enough that the band from <paramref name="top"/> to <paramref name="bottom"/> (in content terms, from <see cref="Top"/>) can be seen.</summary>
        public static void Reveal(int id, float top, float bottom, float viewportHeight)
        {
            if (!_states.TryGetValue(id, out State? state)) return;
            if (top < state.Target) state.Target = top;
            else if (bottom > state.Target + viewportHeight) state.Target = bottom - viewportHeight;
        }

        /// <summary>Ends the contents, <paramref name="contentHeight"/> tall, and draws the scroll bar if they don't all fit.</summary>
        public void End(float contentHeight)
        {
            Ui.PopClip();
            _state.ContentHeight = contentHeight;

            float overflow = contentHeight - Viewport.Height;
            if (overflow <= 0.5f) return;

            Rect track = Viewport.TakeRight(_barWidth + 4f).Shrink(2f, 3f);
            float thumbHeight = MathF.Max(track.Height * Viewport.Height / contentHeight, 24f);
            float thumbY = track.Y + (track.Height - thumbHeight) * Math.Clamp(_state.Offset / overflow, 0f, 1f);
            var thumb = new Rect(track.X, thumbY, track.Width, thumbHeight);

            int barId = Ui.Id(_id, "bar");
            Interaction interaction = Ui.Interact(barId, track.Grow(3f));
            if (interaction.Held)
            {
                _state.Target += Ui.MouseDelta.Y * contentHeight / Viewport.Height;
                _state.Target = Math.Clamp(_state.Target, 0f, overflow);
                _state.Offset = _state.Target;
                _state.LastScrolled = Ui.Time;
            }

            bool near = Viewport.Contains(Ui.Mouse) && Ui.Mouse.X > Viewport.Right - 24f;
            bool recent = Ui.Time - _state.LastScrolled < 0.8f;
            float visible = Ui.Smooth(Ui.Id(_id, "barVisible"), interaction.Hovered || interaction.Held ? 1f : near || recent ? 0.7f : 0.25f, 10f);
            Ui.Rect(thumb, Theme.Text.WithAlpha(0.35f * visible), _barWidth * 0.5f);
        }
    }
}
