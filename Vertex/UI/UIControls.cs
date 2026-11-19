using System.Numerics;

using Vertex.Input;

namespace Vertex.UI
{
    /// <summary>
    /// A box to tick, with a label beside it: a setting that's on or off. Clicking it (the box or the label) switches it.
    /// Code hears through <see cref="Changed"/>, visual scripts on its object through On Value Changed.
    /// </summary>
    public sealed class UIToggle : UIGraphic
    {
        private bool _isOn;

        [Tooltip("Whether it's ticked")]
        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value) return;
                _isOn = value;
                Changed?.Invoke(value);
                TellScripts("Value Changed", ("Value", value));
            }
        }

        public string Label { get; set; } = "Toggle";

        /// <summary>The label's font; with none, the engine's own.</summary>
        public Font? Font { get; set; }

        [Range(6f, 120f)]
        public float FontSize { get; set; } = 20f;

        public Color LabelColor { get; set; } = Color.White;
        public Color BoxColor { get; set; } = new(0.2f, 0.22f, 0.26f, 0.95f);
        public Color CheckColor { get; set; } = new(0.35f, 0.6f, 1f, 1f);

        /// <summary>Whether it can be clicked. One that can't is drawn faded.</summary>
        public bool Interactable { get; set; } = true;

        /// <summary>Raised when it's ticked or unticked, with whether it's now on.</summary>
        public event Action<bool>? Changed;

        // Eases the tick in and out.
        private float _shown;

        internal override bool TakesMouse => Interactable;

        internal override void Release(bool over)
        {
            if (over) IsOn = !IsOn;
        }

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            _shown += ((_isOn ? 1f : 0f) - _shown) * (1f - MathF.Exp(-20f * Time.DeltaTime));

            float box = MathF.Min(size.Y, 28f);
            var at = new Vector2(position.X, position.Y + (size.Y - box) * 0.5f);
            Color boxColor = Interactable ? BoxColor : UIButton.Faded(BoxColor);
            if (hovered && Interactable) boxColor = Color.Lerp(boxColor, Color.White, 0.08f);
            Canvas.DrawRect(at, new Vector2(box), boxColor, box * 0.2f);

            if (_shown > 0.01f)
            {
                // Ticked, the box fills with its colour and a white tick is drawn on, stroke by stroke.
                Color fill = Interactable ? CheckColor : UIButton.Faded(CheckColor);
                Canvas.DrawRect(at, new Vector2(box), fill with { A = fill.A * _shown }, box * 0.2f);

                Vector2 a = at + new Vector2(box * 0.24f, box * 0.52f), b = at + new Vector2(box * 0.42f, box * 0.7f), c = at + new Vector2(box * 0.76f, box * 0.32f);
                float first = Math.Clamp(_shown / 0.4f, 0f, 1f), second = Math.Clamp((_shown - 0.4f) / 0.6f, 0f, 1f);
                Color tick = new(1f, 1f, 1f, Interactable ? 1f : 0.5f);
                Canvas.DrawLine(a, Vector2.Lerp(a, b, first), box * 0.11f, tick);
                if (second > 0f) Canvas.DrawLine(b, Vector2.Lerp(b, c, second), box * 0.11f, tick);
            }

            Color label = Interactable ? LabelColor : UIButton.Faded(LabelColor);
            var labelAt = new Vector2(position.X + box + 10f, position.Y);
            UIText.DrawText(Font ?? UI.Font.Default, Label, FontSize, label, TextAlign.Left, TextVerticalAlign.Middle, wrap: false, shadow: false,
                labelAt, new Vector2(MathF.Max(size.X - box - 10f, 0f), size.Y));
        }
    }

    /// <summary>
    /// A knob dragged along a track, for a number between <see cref="Min"/> and <see cref="Max"/>: volume, a mouse's
    /// sensitivity. Clicking the track jumps there. Code hears through <see cref="Changed"/>, visual scripts on its object
    /// through On Value Changed.
    /// </summary>
    public sealed class UISlider : UIGraphic
    {
        private float _value = 0.5f;

        [Tooltip("Where the knob is, from Min to Max")]
        public float Value
        {
            get => _value;
            set
            {
                float kept = Keep(value);
                if (kept == _value) return;
                _value = kept;
                Changed?.Invoke(kept);
                TellScripts("Value Changed", ("Value", kept));
            }
        }

        [Tooltip("The value with the knob at the left end")]
        public float Min { get; set; }

        [Tooltip("The value with the knob at the right end")]
        public float Max { get; set; } = 1f;

        [Tooltip("Only lets it stop on whole numbers: 1, 2, 3")]
        public bool WholeNumbers { get; set; }

        public Color TrackColor { get; set; } = new(1f, 1f, 1f, 0.15f);
        public Color FillColor { get; set; } = new(0.35f, 0.6f, 1f, 1f);
        public Color KnobColor { get; set; } = Color.White;

        /// <summary>Whether it can be dragged. One that can't is drawn faded.</summary>
        public bool Interactable { get; set; } = true;

        /// <summary>Raised when its value changes, with the new value.</summary>
        public event Action<float>? Changed;

        /// <summary>How far along the track the value is, from 0 to 1.</summary>
        public float Fraction => Max != Min ? Math.Clamp((_value - Min) / (Max - Min), 0f, 1f) : 0f;

        private float Keep(float value)
        {
            float low = MathF.Min(Min, Max), high = MathF.Max(Min, Max);
            value = Math.Clamp(value, low, high);
            return WholeNumbers ? MathF.Round(value) : value;
        }

        internal override bool TakesMouse => Interactable;

        internal override void Press(Vector2 mouse, Vector2 position, Vector2 size) => Drag(mouse, position, size);

        internal override void Drag(Vector2 mouse, Vector2 position, Vector2 size)
        {
            float knob = KnobSize(size);
            float travel = MathF.Max(size.X - knob, 1f);
            float fraction = Math.Clamp((mouse.X - position.X - knob * 0.5f) / travel, 0f, 1f);
            Value = Min + (Max - Min) * fraction;
        }

        private static float KnobSize(Vector2 size) => MathF.Min(size.Y, 24f);

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            float knob = KnobSize(size);
            float trackHeight = MathF.Max(knob * 0.25f, 3f);
            float y = position.Y + (size.Y - trackHeight) * 0.5f;
            float travel = MathF.Max(size.X - knob, 0f);
            float knobX = position.X + travel * Fraction;

            Color Shown(Color color) => Interactable ? color : UIButton.Faded(color);
            Canvas.DrawRect(new Vector2(position.X + knob * 0.5f, y), new Vector2(travel, trackHeight), Shown(TrackColor), trackHeight * 0.5f);
            Canvas.DrawRect(new Vector2(position.X + knob * 0.5f, y), new Vector2(knobX - position.X, trackHeight), Shown(FillColor), trackHeight * 0.5f);

            float grow = Interactable && (hovered || pressed) ? 1.1f : 1f;
            Canvas.DrawCircle(new Vector2(knobX + knob * 0.5f, position.Y + size.Y * 0.5f), knob * 0.5f * grow, Shown(KnobColor));
        }
    }

    /// <summary>Which way a <see cref="UIProgressBar"/> fills.</summary>
    public enum FillDirection
    {
        LeftToRight,
        RightToLeft,
        BottomToTop,
        TopToBottom
    }

    /// <summary>A bar that fills as <see cref="Value"/> goes from 0 to 1: health, loading, a reload. It only shows; it isn't clicked.</summary>
    public sealed class UIProgressBar : UIGraphic
    {
        [Tooltip("How full it is: 0 empty, 1 full")]
        [Range(0f, 1f)]
        public float Value { get; set; } = 0.5f;

        [Tooltip("Which way it fills")]
        public FillDirection Direction { get; set; } = FillDirection.LeftToRight;

        public Color BackgroundColor { get; set; } = new(0f, 0f, 0f, 0.5f);
        public Color FillColor { get; set; } = new(0.3f, 0.85f, 0.4f, 1f);

        [Range(0f, 64f)]
        public float CornerRadius { get; set; } = 4f;

        [Tooltip("Space between the edge of the bar and its fill, in UI units")]
        [Range(0f, 16f)]
        public float Padding { get; set; } = 2f;

        // The fill eases to a new value, rather than jumping.
        private float _shown = -1f;

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            float value = Math.Clamp(Value, 0f, 1f);
            _shown = _shown < 0f ? value : _shown + (value - _shown) * (1f - MathF.Exp(-14f * Time.DeltaTime));

            Canvas.DrawRect(position, size, BackgroundColor, CornerRadius);

            Vector2 inner = Vector2.Max(size - new Vector2(Padding * 2f), Vector2.Zero);
            Vector2 at = position + new Vector2(Padding);
            Vector2 fill = Direction is FillDirection.LeftToRight or FillDirection.RightToLeft ? new Vector2(inner.X * _shown, inner.Y) : new Vector2(inner.X, inner.Y * _shown);
            if (Direction == FillDirection.RightToLeft) at.X += inner.X - fill.X;
            if (Direction == FillDirection.BottomToTop) at.Y += inner.Y - fill.Y;
            if (fill.X > 0.01f && fill.Y > 0.01f) Canvas.DrawRect(at, fill, FillColor, MathF.Max(CornerRadius - Padding, 0f));
        }
    }

    /// <summary>
    /// A box to type into: a name, a server's address, a chat message. Click it to type; Enter or clicking elsewhere
    /// finishes. Code hears each change through <see cref="Changed"/> and Enter through <see cref="Submitted"/>; visual
    /// scripts on its object through On Value Changed and On Submitted.
    /// <para>While it's being typed in, <see cref="UISystem.Typing"/> is true, so a game can leave the keys alone.</para>
    /// </summary>
    public sealed class UIInputField : UIGraphic
    {
        private readonly TextField _field = new();
        private string _text = "";

        [Tooltip("What's typed in it")]
        public string Text
        {
            get => _text;
            set
            {
                value ??= "";
                if (_text == value) return;
                _text = value;
                if (_field.Text != value) _field.SetText(value);
                Changed?.Invoke(value);
                TellScripts("Value Changed", ("Value", value));
            }
        }

        [Tooltip("Shown faintly while it's empty: what to type")]
        public string Placeholder { get; set; } = "Type here...";

        [Tooltip("The most characters it takes")]
        public int MaxLength { get; set; } = 64;

        [Tooltip("Shows dots in place of what's typed, for a password")]
        public bool Password { get; set; }

        /// <summary>The font; with none, the engine's own.</summary>
        public Font? Font { get; set; }

        [Range(6f, 120f)]
        public float FontSize { get; set; } = 20f;

        public Color TextColor { get; set; } = Color.White;
        public Color PlaceholderColor { get; set; } = new(1f, 1f, 1f, 0.4f);
        public Color BackgroundColor { get; set; } = new(0f, 0f, 0f, 0.45f);
        public Color FocusColor { get; set; } = new(0.35f, 0.6f, 1f, 1f);

        [Range(0f, 64f)]
        public float CornerRadius { get; set; } = 6f;

        /// <summary>Whether it can be typed in. One that can't is drawn faded.</summary>
        public bool Interactable { get; set; } = true;

        /// <summary>Raised as it's typed in, with the text now.</summary>
        public event Action<string>? Changed;

        /// <summary>Raised when Enter is pressed in it, with the text.</summary>
        public event Action<string>? Submitted;

        /// <summary>Whether it's being typed in.</summary>
        public bool IsFocused => UISystem.FocusedInput == this;

        internal override bool TakesMouse => Interactable;

        internal override void Press(Vector2 mouse, Vector2 position, Vector2 size)
        {
            UISystem.FocusedInput = this;
            if (_field.Text != _text) _field.SetText(_text);
        }

        /// <summary>Takes this frame's typing. Called while it's focused.</summary>
        internal void Type()
        {
            _field.MaxLength = MaxLength;
            _field.PasswordChar = Password ? '•' : null;
            _field.Update();
            Text = _field.Text;

            if (InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter))
            {
                Submitted?.Invoke(_text);
                TellScripts("Submitted", ("Text", _text));
                UISystem.FocusedInput = null;
            }
            else if (InputSystem.GetKeyDown(Key.Escape))
            {
                UISystem.FocusedInput = null;
            }
        }

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            Color Shown(Color color) => Interactable ? color : UIButton.Faded(color);
            Canvas.DrawRect(position, size, Shown(BackgroundColor), CornerRadius);
            if (IsFocused) Canvas.DrawRectOutline(position, size, FocusColor, 1.5f, CornerRadius);
            else if (hovered && Interactable) Canvas.DrawRectOutline(position, size, new Color(1f, 1f, 1f, 0.25f), 1f, CornerRadius);

            Font font = Font ?? UI.Font.Default;
            const float padding = 10f;
            float width = MathF.Max(size.X - padding * 2f, 0f);
            var at = new Vector2(position.X + padding, position.Y + (size.Y - Canvas.LineHeight(font, FontSize)) * 0.5f);

            Canvas.PushClip(position, size);
            if (_text.Length == 0 && !IsFocused)
            {
                Canvas.DrawText(font, Placeholder, at, Shown(PlaceholderColor), FontSize);
            }
            else
            {
                _field.PasswordChar = Password ? '•' : null;
                if (!IsFocused && _field.Text != _text) _field.SetText(_text);
                _field.Draw(font, at, width, FontSize, Shown(TextColor), caret: IsFocused);
            }

            Canvas.PopClip();
        }
    }
}
