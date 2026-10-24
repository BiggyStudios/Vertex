namespace Vertex
{
    public static class MathHelper
    {
        public static float ToRadians(float degrees) => degrees * (MathF.PI / 180f);
        public static float ToDegrees(float radians) => radians * (180f / MathF.PI);
    }
}
