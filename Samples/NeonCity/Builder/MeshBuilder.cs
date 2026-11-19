using System.Numerics;

using Vertex.Rendering;

namespace NeonCity.Builder
{
    /// <summary>
    /// Gathers boxes and flat pieces into one mesh, so a whole building's walls, or every letter of a sign, is one draw.
    /// Texture coordinates are in world units times <see cref="TexturesPerUnit"/>, so a texture tiles evenly however big
    /// each piece is.
    /// </summary>
    internal sealed class MeshBuilder
    {
        private readonly List<MeshVertex> _vertices = new();
        private readonly List<uint> _indices = new();

        /// <summary>How many times a texture repeats per unit.</summary>
        public float TexturesPerUnit { get; set; } = 0.25f;

        public bool IsEmpty => _indices.Count == 0;

        /// <summary>
        /// A flat four-cornered piece facing <paramref name="normal"/>: corners counter-clockwise seen from in front, with
        /// the texture's corners at <paramref name="uvMin"/> and <paramref name="uvMax"/>.
        /// </summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 uvMin, Vector2 uvMax)
        {
            uint first = (uint)_vertices.Count;
            _vertices.Add(new MeshVertex(a, normal, new Vector2(uvMin.X, uvMax.Y)));
            _vertices.Add(new MeshVertex(b, normal, new Vector2(uvMax.X, uvMax.Y)));
            _vertices.Add(new MeshVertex(c, normal, new Vector2(uvMax.X, uvMin.Y)));
            _vertices.Add(new MeshVertex(d, normal, new Vector2(uvMin.X, uvMin.Y)));
            _indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        /// <summary>A flat rectangle centred on <paramref name="centre"/>, facing <paramref name="normal"/>, with <paramref name="up"/> along its height.</summary>
        public void Panel(Vector3 centre, Vector3 normal, Vector3 up, float width, float height, Vector2? uvMin = null, Vector2? uvMax = null)
        {
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, normal)) * (width * 0.5f);
            Vector3 top = Vector3.Normalize(up) * (height * 0.5f);
            Quad(centre - right - top, centre + right - top, centre + right + top, centre - right + top, Vector3.Normalize(normal),
                uvMin ?? Vector2.Zero, uvMax ?? Vector2.One);
        }

        /// <summary>A box lined up with the world's axes.</summary>
        public void Box(Vector3 centre, Vector3 size) => Box(centre, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, size);

        /// <summary>A box from <paramref name="min"/> to <paramref name="max"/>.</summary>
        public void Span(Vector3 min, Vector3 max) => Box((min + max) * 0.5f, max - min);

        /// <summary>A box turned <paramref name="yaw"/> degrees about the vertical.</summary>
        public void Box(Vector3 centre, Vector3 size, float yaw)
        {
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * MathF.PI / 180f);
            Box(centre, Vector3.Transform(Vector3.UnitX, turn), Vector3.UnitY, Vector3.Transform(Vector3.UnitZ, turn), size);
        }

        /// <summary>A box whose sides run along <paramref name="x"/>, <paramref name="y"/> and <paramref name="z"/> (at right angles), <paramref name="size"/> along each.</summary>
        public void Box(Vector3 centre, Vector3 x, Vector3 y, Vector3 z, Vector3 size)
        {
            Vector3 hx = x * (size.X * 0.5f), hy = y * (size.Y * 0.5f), hz = z * (size.Z * 0.5f);

            // Each side: its outward direction, and two along it whose cross product is that direction, so the corners
            // go round counter-clockwise seen from outside.
            Face(centre + hx, x, -z, y, size.Z, size.Y);
            Face(centre - hx, -x, z, y, size.Z, size.Y);
            Face(centre + hz, z, x, y, size.X, size.Y);
            Face(centre - hz, -z, -x, y, size.X, size.Y);
            Face(centre + hy, y, x, -z, size.X, size.Z);
            Face(centre - hy, -y, x, z, size.X, size.Z);
        }

        /// <summary>A long box from <paramref name="from"/> to <paramref name="to"/>, <paramref name="thickness"/> across: a pipe, a cable, a neon tube.</summary>
        public void Beam(Vector3 from, Vector3 to, float thickness, Vector3? side = null)
        {
            Vector3 along = to - from;
            float length = along.Length();
            if (length < 1e-4f) return;

            along /= length;
            Vector3 across = side ?? (MathF.Abs(along.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX);
            Vector3 z = Vector3.Normalize(Vector3.Cross(along, across));
            Vector3 y = Vector3.Cross(z, along);

            // A little longer than the gap, so joined tubes meet without a notch at the corner.
            Box((from + to) * 0.5f, along, y, z, new Vector3(length + thickness, thickness, thickness));
        }

        private void Face(Vector3 centre, Vector3 normal, Vector3 u, Vector3 v, float width, float height)
        {
            Vector3 hu = u * (width * 0.5f), hv = v * (height * 0.5f);
            float scale = TexturesPerUnit;

            // Textures keep their place on the wall, so neighbouring boxes line up.
            Vector2 offset = new(Vector3.Dot(centre, u), -Vector3.Dot(centre, v));
            Vector2 half = new(width * 0.5f, height * 0.5f);
            Quad(centre - hu - hv, centre + hu - hv, centre + hu + hv, centre - hu + hv, normal, (offset - half) * scale, (offset + half) * scale);
        }

        public Mesh Build() => new(_vertices.ToArray(), _indices.ToArray());
    }
}
