using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Built-in shapes. The properties return a shared, cached mesh; the Create methods build a new one.
    /// All shapes are 1 unit across and centred on the origin.
    /// </summary>
    public static class Primitives
    {
        private static Mesh? _cube;
        private static Mesh? _sphere;
        private static Mesh? _plane;

        public static Mesh Cube => _cube ??= CreateCube();
        public static Mesh Sphere => _sphere ??= CreateSphere();
        public static Mesh Plane => _plane ??= CreatePlane();

        public static Mesh CreateCube()
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();

            AddQuad(vertices, indices, Vector3.UnitX, -Vector3.UnitZ, 0.5f, Vector2.One, Vector2.One);
            AddQuad(vertices, indices, -Vector3.UnitX, Vector3.UnitZ, 0.5f, Vector2.One, Vector2.One);
            AddQuad(vertices, indices, Vector3.UnitY, Vector3.UnitX, 0.5f, Vector2.One, Vector2.One);
            AddQuad(vertices, indices, -Vector3.UnitY, Vector3.UnitX, 0.5f, Vector2.One, Vector2.One);
            AddQuad(vertices, indices, Vector3.UnitZ, Vector3.UnitX, 0.5f, Vector2.One, Vector2.One);
            AddQuad(vertices, indices, -Vector3.UnitZ, -Vector3.UnitX, 0.5f, Vector2.One, Vector2.One);

            return new Mesh(vertices.ToArray(), indices.ToArray());
        }

        /// <summary>
        /// A box of the given size, centred on the origin. Unlike scaling <see cref="Cube"/>, a texture keeps the same size
        /// on every face however big the box is, repeating <paramref name="texturesPerUnit"/> times per unit.
        /// </summary>
        public static Mesh CreateBox(Vector3 size, float texturesPerUnit = 1f)
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();

            // Each face: its normal, its right direction, and its width and height.
            AddBoxFace(Vector3.UnitX, -Vector3.UnitZ, size.X, new Vector2(size.Z, size.Y));
            AddBoxFace(-Vector3.UnitX, Vector3.UnitZ, size.X, new Vector2(size.Z, size.Y));
            AddBoxFace(Vector3.UnitY, Vector3.UnitX, size.Y, new Vector2(size.X, size.Z));
            AddBoxFace(-Vector3.UnitY, Vector3.UnitX, size.Y, new Vector2(size.X, size.Z));
            AddBoxFace(Vector3.UnitZ, Vector3.UnitX, size.Z, new Vector2(size.X, size.Y));
            AddBoxFace(-Vector3.UnitZ, -Vector3.UnitX, size.Z, new Vector2(size.X, size.Y));

            return new Mesh(vertices.ToArray(), indices.ToArray());

            void AddBoxFace(Vector3 normal, Vector3 right, float depth, Vector2 faceSize)
            {
                AddQuad(vertices, indices, normal, right, depth * 0.5f, faceSize, faceSize * texturesPerUnit);
            }
        }

        /// <summary>
        /// A convex shape drawn in the XY plane (<paramref name="crossSection"/>, corners in counter-clockwise order)
        /// stretched along Z for <paramref name="length"/>, centred on Z = 0. A triangle makes a wedge or surf ramp.
        /// Textures repeat <paramref name="texturesPerUnit"/> times per unit, like <see cref="CreateBox"/>.
        /// </summary>
        public static Mesh CreatePrism(ReadOnlySpan<Vector2> crossSection, float length, float texturesPerUnit = 1f)
        {
            if (crossSection.Length < 3) throw new ArgumentException("A prism needs at least 3 corners.", nameof(crossSection));

            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            float front = length * 0.5f;

            // Sides: one rectangle per edge, with the texture running along the edge and along the length.
            for (int i = 0; i < crossSection.Length; i++)
            {
                Vector2 a = crossSection[i];
                Vector2 b = crossSection[(i + 1) % crossSection.Length];
                Vector2 edge = b - a;
                var normal = Vector3.Normalize(new Vector3(edge.Y, -edge.X, 0f));
                float u = edge.Length() * texturesPerUnit;
                float v = length * texturesPerUnit;
                uint start = (uint)vertices.Count;

                vertices.Add(new MeshVertex(new Vector3(a, -front), normal, new Vector2(0f, 0f)));
                vertices.Add(new MeshVertex(new Vector3(b, -front), normal, new Vector2(u, 0f)));
                vertices.Add(new MeshVertex(new Vector3(b, front), normal, new Vector2(u, v)));
                vertices.Add(new MeshVertex(new Vector3(a, front), normal, new Vector2(0f, v)));

                indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
            }

            // Ends: the cross-section itself, as a fan of triangles from the first corner.
            AddCap(front, Vector3.UnitZ, reverse: false, crossSection);
            AddCap(-front, -Vector3.UnitZ, reverse: true, crossSection);

            return new Mesh(vertices.ToArray(), indices.ToArray());

            void AddCap(float z, Vector3 normal, bool reverse, ReadOnlySpan<Vector2> corners)
            {
                uint start = (uint)vertices.Count;
                foreach (Vector2 corner in corners)
                {
                    vertices.Add(new MeshVertex(new Vector3(corner, z), normal, corner * texturesPerUnit));
                }

                for (uint i = 1; i < corners.Length - 1; i++)
                {
                    if (reverse) indices.AddRange([start, start + i + 1, start + i]);
                    else indices.AddRange([start, start + i, start + i + 1]);
                }
            }
        }

        /// <summary>A flat square on the XZ plane, facing up.</summary>
        /// <param name="uvScale">How many times a texture repeats across the plane.</param>
        public static Mesh CreatePlane(float uvScale = 1f)
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();

            AddQuad(vertices, indices, Vector3.UnitY, Vector3.UnitX, 0f, Vector2.One, new Vector2(uvScale));

            return new Mesh(vertices.ToArray(), indices.ToArray());
        }

        public static Mesh CreateSphere(int segments = 32, int rings = 16)
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();

            for (int r = 0; r <= rings; r++)
            {
                float phi = MathF.PI * r / rings;

                for (int s = 0; s <= segments; s++)
                {
                    float theta = 2f * MathF.PI * s / segments;
                    var normal = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), -MathF.Sin(phi) * MathF.Sin(theta));
                    vertices.Add(new MeshVertex(normal * 0.5f, normal, new Vector2((float)s / segments, (float)r / rings)));
                }
            }

            uint rowLength = (uint)segments + 1;
            for (uint r = 0; r < rings; r++)
            {
                for (uint s = 0; s < segments; s++)
                {
                    uint a = r * rowLength + s;
                    uint b = a + rowLength;

                    indices.AddRange([a, b, a + 1]);
                    indices.AddRange([a + 1, b, b + 1]);
                }
            }

            return new Mesh(vertices.ToArray(), indices.ToArray());
        }

        /// <summary>
        /// Adds a rectangle facing <paramref name="normal"/>, pushed out from the origin by <paramref name="offset"/>.
        /// <paramref name="size"/> is its width along <paramref name="right"/> and its height; <paramref name="uvScale"/> is how
        /// many times a texture repeats across each.
        /// </summary>
        private static void AddQuad(List<MeshVertex> vertices, List<uint> indices, Vector3 normal, Vector3 right, float offset, Vector2 size, Vector2 uvScale)
        {
            Vector3 up = Vector3.Cross(normal, right);
            Vector3 centre = normal * offset;
            Vector3 halfRight = right * size.X * 0.5f;
            Vector3 halfUp = up * size.Y * 0.5f;
            uint start = (uint)vertices.Count;

            vertices.Add(new MeshVertex(centre - halfRight - halfUp, normal, new Vector2(0f, uvScale.Y)));
            vertices.Add(new MeshVertex(centre + halfRight - halfUp, normal, new Vector2(uvScale.X, uvScale.Y)));
            vertices.Add(new MeshVertex(centre + halfRight + halfUp, normal, new Vector2(uvScale.X, 0f)));
            vertices.Add(new MeshVertex(centre - halfRight + halfUp, normal, new Vector2(0f, 0f)));

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
    }
}
