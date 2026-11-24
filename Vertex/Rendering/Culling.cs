using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Working out what can't be seen, so it isn't drawn: boxes outside the camera's view, and shadow casters outside
    /// a shadow cascade. Boxes are kept as a centre and half-size (extents) along each axis.
    /// </summary>
    internal static class Culling
    {
        /// <summary>
        /// The box round <paramref name="mesh"/> once moved by <paramref name="world"/>, kept lined up with the axes: a
        /// rotated box grows to fit, so it's never smaller than the mesh.
        /// </summary>
        public static void WorldBounds(Mesh mesh, in Matrix4x4 world, out Vector3 centre, out Vector3 extents)
        {
            centre = Vector3.Transform(mesh.BoundsCentre, world);
            extents = Extend(mesh.BoundsExtents, world);
        }

        /// <summary>How far a box of half-size <paramref name="e"/> reaches along each axis once transformed by <paramref name="m"/>.</summary>
        internal static Vector3 Extend(Vector3 e, in Matrix4x4 m) => new(
            MathF.Abs(m.M11) * e.X + MathF.Abs(m.M21) * e.Y + MathF.Abs(m.M31) * e.Z,
            MathF.Abs(m.M12) * e.X + MathF.Abs(m.M22) * e.Y + MathF.Abs(m.M32) * e.Z,
            MathF.Abs(m.M13) * e.X + MathF.Abs(m.M23) * e.Y + MathF.Abs(m.M33) * e.Z);

        /// <summary>
        /// Whether a world box overlaps what an orthographic <paramref name="lightSpace"/> matrix (like a shadow
        /// cascade's) sees: x and y from -1 to 1, depth from 0 to 1.
        /// </summary>
        /// <summary>
        /// <see cref="InOrthographic"/> for a box already turned into the sun's view (<paramref name="centre"/> and
        /// <paramref name="extents"/> there), against a cascade that's that view scaled and moved along each axis.
        /// </summary>
        public static bool InCascade(Vector3 centre, Vector3 extents, Vector3 scale, Vector3 offset)
        {
            Vector3 c = centre * scale + offset;
            Vector3 e = extents * Vector3.Abs(scale);
            return MathF.Abs(c.X) - e.X <= 1f && MathF.Abs(c.Y) - e.Y <= 1f && c.Z + e.Z >= 0f && c.Z - e.Z <= 1f;
        }

        public static bool InOrthographic(in Matrix4x4 lightSpace, Vector3 centre, Vector3 extents)
        {
            Vector3 c = Vector3.Transform(centre, lightSpace);
            Vector3 e = Extend(extents, lightSpace);
            return MathF.Abs(c.X) - e.X <= 1f && MathF.Abs(c.Y) - e.Y <= 1f && c.Z + e.Z >= 0f && c.Z - e.Z <= 1f;
        }
    }

    /// <summary>The six planes round what a camera sees, each facing inwards.</summary>
    internal readonly struct Frustum
    {
        private readonly Vector4 _left, _right, _bottom, _top, _near, _far;

        /// <param name="viewProjection">A view times a projection with depth from 0 to 1, as System.Numerics makes them.</param>
        public Frustum(in Matrix4x4 viewProjection)
        {
            // With row vectors, each clip-space coordinate is a point dotted with a column of the matrix.
            Matrix4x4 m = viewProjection;
            var x = new Vector4(m.M11, m.M21, m.M31, m.M41);
            var y = new Vector4(m.M12, m.M22, m.M32, m.M42);
            var z = new Vector4(m.M13, m.M23, m.M33, m.M43);
            var w = new Vector4(m.M14, m.M24, m.M34, m.M44);

            _left = w + x;
            _right = w - x;
            _bottom = w + y;
            _top = w - y;
            _near = z;
            _far = w - z;
        }

        /// <summary>False only if the box is wholly outside one of the planes, so it can't be seen.</summary>
        public bool Intersects(Vector3 centre, Vector3 extents) =>
            Inside(_left, centre, extents) && Inside(_right, centre, extents) && Inside(_bottom, centre, extents) &&
            Inside(_top, centre, extents) && Inside(_near, centre, extents) && Inside(_far, centre, extents);

        private static bool Inside(Vector4 plane, Vector3 centre, Vector3 extents)
        {
            var normal = new Vector3(plane.X, plane.Y, plane.Z);
            float reach = Vector3.Dot(extents, Vector3.Abs(normal));
            return Vector3.Dot(normal, centre) + plane.W >= -reach;
        }
    }
}
