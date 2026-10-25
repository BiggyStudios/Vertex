using System.Numerics;

namespace Vertex
{
    /// <summary>An RGBA colour with components in the 0-1 range.</summary>
    public struct Color
    {
        public float R, G, B, A;

        public Color(float r, float g, float b, float a = 1f)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        /// <summary>Creates a colour from 0-255 values.</summary>
        public static Color FromBytes(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

        public static Color White => new(1f, 1f, 1f);
        public static Color Black => new(0f, 0f, 0f);
        public static Color Gray => new(0.5f, 0.5f, 0.5f);
        public static Color Red => new(1f, 0f, 0f);
        public static Color Green => new(0f, 1f, 0f);
        public static Color Blue => new(0f, 0f, 1f);
        public static Color Yellow => new(1f, 0.92f, 0.016f);
        public static Color Orange => new(1f, 0.5f, 0f);
        public static Color CornflowerBlue => FromBytes(100, 149, 237);

        public Vector3 ToVector3() => new(R, G, B);
        public Vector4 ToVector4() => new(R, G, B, A);

        /// <summary>Part way from <paramref name="a"/> to <paramref name="b"/>, alpha included: 0 is a, 1 is b.</summary>
        public static Color Lerp(Color a, Color b, float t) =>
            new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

        public static Color operator *(Color color, float scalar) => new(color.R * scalar, color.G * scalar, color.B * scalar, color.A);
    }
}
