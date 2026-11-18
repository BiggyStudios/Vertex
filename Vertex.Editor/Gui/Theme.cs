using Vertex.UI;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// The editor's look: colours, sizes and fonts in one place. Dark neutral greys that stay out of the way of the scene,
    /// one blue accent for what's selected or focused, and red, green and blue for the X, Y and Z axes.
    /// </summary>
    public static class Theme
    {
        public static Font Font { get; set; } = null!;
        public static Font Bold { get; set; } = null!;

        public const float TextSize = 13f;
        public const float SmallTextSize = 11.5f;
        public const float TitleSize = 13.5f;

        public const float RowHeight = 24f;
        public const float Spacing = 4f;
        public const float Padding = 10f;
        public const float PanelRadius = 8f;
        public const float ControlRadius = 4f;
        public const float Gap = 6f;

        // Surfaces, darkest to lightest.
        public static readonly Color Background = Hex(0x111214);
        public static readonly Color Panel = Hex(0x1B1C1F);
        public static readonly Color PanelRaised = Hex(0x222327);
        public static readonly Color Field = Hex(0x141517);
        public static readonly Color Control = Hex(0x2A2C31);
        public static readonly Color ControlHover = Hex(0x33363C);
        public static readonly Color ControlPressed = Hex(0x26282C);
        public static readonly Color Popup = Hex(0x26282D);

        public static readonly Color Border = new(1f, 1f, 1f, 0.06f);
        public static readonly Color BorderStrong = new(1f, 1f, 1f, 0.11f);
        public static readonly Color Shadow = new(0f, 0f, 0f, 0.55f);

        public static readonly Color Text = Hex(0xE3E4E8);
        public static readonly Color TextDim = Hex(0x9C9FA8);
        public static readonly Color TextFaint = Hex(0x666A73);

        public static readonly Color Accent = Hex(0x3B82F6);
        public static readonly Color AccentHover = Hex(0x5596FF);
        public static readonly Color AccentText = Hex(0x8AB8FF);
        public static readonly Color Selection = new(0.23f, 0.51f, 0.96f, 0.28f);
        public static readonly Color Hover = new(1f, 1f, 1f, 0.045f);

        public static readonly Color AxisX = Hex(0xE5534B);
        public static readonly Color AxisY = Hex(0x7BC74D);
        public static readonly Color AxisZ = Hex(0x4C8DF6);

        public static readonly Color Danger = Hex(0xE5534B);
        public static readonly Color Warning = Hex(0xE8B339);

        public static Color WithAlpha(this Color color, float alpha) => new(color.R, color.G, color.B, color.A * alpha);

        public static Color Hex(uint rgb) => Color.FromBytes((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        /// <summary>Sets up the editor's fonts: the engine's own Noto Sans.</summary>
        public static void LoadFonts()
        {
            Font = Font.Default;
            Bold = Font.DefaultBold;
        }
    }
}
