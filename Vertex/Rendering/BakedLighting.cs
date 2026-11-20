using System.IO.Compression;
using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// A scene's baked light (made by <see cref="LightBaker"/>; in the editor, the scene's Lighting › Bake): the lightmap,
    /// a picture of the light falling on every static mesh; those meshes as they're drawn with it; and the light probes,
    /// which hold the light in the space between them for everything else. It's kept in a .bake file beside the scene,
    /// which <see cref="Lighting.BakedLightingFile"/> names, and loaded with it.
    /// </summary>
    public sealed class BakedLighting
    {
        public const string Extension = ".bake";

        private const int FormatVersion = 1;
        private static ReadOnlySpan<byte> Magic => "VXBAKE"u8;

        /// <summary>
        /// The light falling on static meshes, as the shaders read it: in each pixel, the square root of the light over
        /// <see cref="LightmapRange"/>, its colour in rgb and how bright in a (RGBM), so dark places keep their detail.
        /// </summary>
        public Texture? Lightmap { get; set; }

        /// <summary>The brightest light the lightmap holds: brighter is kept at this.</summary>
        public const float LightmapRange = 16f;

        /// <summary>The static meshes as they're drawn with the lightmap, by <see cref="MeshRenderer.LightmapIndex"/>.</summary>
        public List<BakedMesh> Meshes { get; } = new();

        /// <summary>The light in the space round the static meshes, for things that move. Null if there are none.</summary>
        public LightProbeGrid? Probes { get; set; }

        /// <summary>The file it was loaded from, if it was.</summary>
        public string? Source { get; set; }

        /// <summary>The mesh <paramref name="renderer"/> draws with the lightmap, or null if it hasn't been baked as it is now.</summary>
        public Mesh? MeshFor(MeshRenderer renderer)
        {
            if (!renderer.Static || renderer.LightmapIndex < 0 || renderer.LightmapIndex >= Meshes.Count || renderer.Mesh is not { } mesh) return null;
            return Meshes[renderer.LightmapIndex].For(mesh);
        }

        /// <summary>Light <paramref name="rgb"/> (linear) as a lightmap pixel: see <see cref="Lightmap"/>.</summary>
        public static (byte R, byte G, byte B, byte A) Encode(Vector3 rgb)
        {
            Vector3 root = Vector3.SquareRoot(Vector3.Clamp(rgb / LightmapRange, Vector3.Zero, Vector3.One));
            float largest = MathF.Max(root.X, MathF.Max(root.Y, root.Z));
            if (largest <= 1e-6f) return (0, 0, 0, 0);

            float m = MathF.Ceiling(largest * 255f) / 255f;
            Vector3 scaled = root / m;
            return ((byte)MathF.Round(scaled.X * 255f), (byte)MathF.Round(scaled.Y * 255f), (byte)MathF.Round(scaled.Z * 255f), (byte)(m * 255f));
        }

        /// <summary>A lightmap pixel's light: the other way from <see cref="Encode"/>.</summary>
        public static Vector3 Decode(byte r, byte g, byte b, byte a)
        {
            Vector3 root = new Vector3(r, g, b) / 255f * (a / 255f);
            return root * root * LightmapRange;
        }

        public void Save(string path)
        {
            string temporary = path + ".saving";
            using (FileStream file = File.Create(temporary)) Write(file);
            File.Move(temporary, path, overwrite: true);
        }

        public static BakedLighting Load(string path)
        {
            using FileStream file = File.OpenRead(path);
            BakedLighting baked = Read(file);
            baked.Source = path;
            return baked;
        }

        public void Write(Stream stream)
        {
            stream.Write(Magic);
            stream.WriteByte(FormatVersion);
            using var zip = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true);
            using var writer = new BinaryWriter(zip);

            writer.Write(Lightmap?.Width ?? 0);
            writer.Write(Lightmap?.Height ?? 0);
            if (Lightmap != null) writer.Write(Lightmap.Pixels);

            writer.Write(Meshes.Count);
            foreach (BakedMesh mesh in Meshes) mesh.Write(writer);

            writer.Write(Probes != null);
            Probes?.Write(writer);
        }

        public static BakedLighting Read(Stream stream)
        {
            Span<byte> magic = stackalloc byte[Magic.Length];
            stream.ReadExactly(magic);
            if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("This isn't baked lighting.");
            int version = stream.ReadByte();
            if (version != FormatVersion) throw new InvalidDataException($"This baked lighting is from a different version of Vertex ({version}): bake it again.");

            using var zip = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(zip);
            var baked = new BakedLighting();

            int width = reader.ReadInt32(), height = reader.ReadInt32();
            if (width > 0 && height > 0) baked.Lightmap = new Texture(width, height, reader.ReadBytes(width * height * 4));

            int meshes = reader.ReadInt32();
            for (int i = 0; i < meshes; i++) baked.Meshes.Add(BakedMesh.Read(reader));

            if (reader.ReadBoolean()) baked.Probes = LightProbeGrid.Read(reader);
            return baked;
        }
    }

    /// <summary>
    /// A static mesh as it's drawn with the lightmap: its corners, split where the lightmap's pieces of it meet, each with
    /// where it is in the lightmap. Made from the mesh it was baked with; if that's changed since, it isn't used.
    /// </summary>
    public sealed class BakedMesh
    {
        /// <summary>Which of the original mesh's corners each corner is.</summary>
        public int[] SourceVertices { get; }

        /// <summary>The triangles, three corners each, as the original's but of these corners.</summary>
        public uint[] Indices { get; }

        /// <summary>Where each corner is in the lightmap, 0 to 1 across.</summary>
        public Vector2[] LightmapUVs { get; }

        /// <summary>A fingerprint of the mesh it was made from, to tell if that's still the same.</summary>
        public ulong SourceHash { get; }

        private Mesh? _source;
        private Mesh? _built;

        public BakedMesh(int[] sourceVertices, uint[] indices, Vector2[] lightmapUVs, ulong sourceHash)
        {
            SourceVertices = sourceVertices;
            Indices = indices;
            LightmapUVs = lightmapUVs;
            SourceHash = sourceHash;
        }

        /// <summary>The mesh to draw in place of <paramref name="source"/>, or null if it isn't the mesh this was baked from.</summary>
        public Mesh? For(Mesh source)
        {
            if (source == _source) return _built;
            _source = source;
            _built = null;
            if (Hash(source) != SourceHash) return null;

            var vertices = new MeshVertex[SourceVertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = source.Vertices[SourceVertices[i]];
                vertices[i].LightmapUV = LightmapUVs[i];
            }

            _built = new Mesh(vertices, Indices) { Source = source.Source };
            return _built;
        }

        /// <summary>A fingerprint of a mesh's shape: its corners' positions and its triangles.</summary>
        public static ulong Hash(Mesh mesh)
        {
            ulong hash = 14695981039346656037UL;
            void Add(uint value) => hash = (hash ^ value) * 1099511628211UL;
            Add((uint)mesh.Vertices.Length);
            Add((uint)mesh.Indices.Length);
            foreach (MeshVertex vertex in mesh.Vertices)
            {
                Add(BitConverter.SingleToUInt32Bits(vertex.Position.X));
                Add(BitConverter.SingleToUInt32Bits(vertex.Position.Y));
                Add(BitConverter.SingleToUInt32Bits(vertex.Position.Z));
            }

            foreach (uint index in mesh.Indices) Add(index);
            return hash;
        }

        internal void Write(BinaryWriter writer)
        {
            writer.Write(SourceHash);
            writer.Write(SourceVertices.Length);
            foreach (int vertex in SourceVertices) writer.Write(vertex);
            foreach (Vector2 uv in LightmapUVs)
            {
                writer.Write(uv.X);
                writer.Write(uv.Y);
            }

            writer.Write(Indices.Length);
            foreach (uint index in Indices) writer.Write(index);
        }

        internal static BakedMesh Read(BinaryReader reader)
        {
            ulong hash = reader.ReadUInt64();
            int count = reader.ReadInt32();
            var vertices = new int[count];
            for (int i = 0; i < count; i++) vertices[i] = reader.ReadInt32();
            var uvs = new Vector2[count];
            for (int i = 0; i < count; i++) uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            var indices = new uint[reader.ReadInt32()];
            for (int i = 0; i < indices.Length; i++) indices[i] = reader.ReadUInt32();
            return new BakedMesh(vertices, indices, uvs, hash);
        }
    }

    /// <summary>
    /// Light probes: the light arriving from all round at points on an even grid through the scene, baked with the
    /// lightmap, for lighting things that move (and anything not in the lightmap). Each is lit by those round where it
    /// is, blended between them. Probes that ended up inside something solid are left out.
    /// </summary>
    public sealed class LightProbeGrid
    {
        /// <summary>Where the first probe is.</summary>
        public Vector3 Origin { get; }

        /// <summary>How far apart they are.</summary>
        public float Spacing { get; }

        public int CountX { get; }
        public int CountY { get; }
        public int CountZ { get; }

        /// <summary>Each probe's light, x first, then y, then z.</summary>
        public ProbeLight[] Light { get; }

        /// <summary>Whether each probe's in the open (true) or inside something, where its light means nothing.</summary>
        public bool[] Valid { get; }

        /// <summary>
        /// How far each probe can see along each axis (+x, -x, +y, -y, +z, -z), six to a probe: a probe with a wall or
        /// floor between it and a point doesn't light it, so light doesn't leak through them.
        /// </summary>
        public float[] Reach { get; }

        public LightProbeGrid(Vector3 origin, float spacing, int countX, int countY, int countZ)
        {
            Origin = origin;
            Spacing = spacing;
            CountX = countX;
            CountY = countY;
            CountZ = countZ;
            Light = new ProbeLight[countX * countY * countZ];
            Valid = new bool[Light.Length];
            Reach = new float[Light.Length * 6];
            Array.Fill(Reach, float.MaxValue);
        }

        public int Index(int x, int y, int z) => (z * CountY + y) * CountX + x;

        public Vector3 PositionOf(int x, int y, int z) => Origin + new Vector3(x, y, z) * Spacing;

        /// <summary>
        /// The light at <paramref name="position"/>: blended from the eight probes round it, by how near each is, leaving
        /// out those inside things. False if there are none to blend (outside the grid, it's taken from its edge).
        /// </summary>
        public bool Sample(Vector3 position, out ProbeLight light)
        {
            Vector3 cell = (position - Origin) / Spacing;
            cell = Vector3.Clamp(cell, Vector3.Zero, new Vector3(CountX - 1, CountY - 1, CountZ - 1));
            int x0 = Math.Min((int)cell.X, Math.Max(CountX - 2, 0));
            int y0 = Math.Min((int)cell.Y, Math.Max(CountY - 2, 0));
            int z0 = Math.Min((int)cell.Z, Math.Max(CountZ - 2, 0));
            Vector3 t = cell - new Vector3(x0, y0, z0);

            light = default;
            float total = 0f;
            for (int corner = 0; corner < 8; corner++)
            {
                int dx = corner & 1, dy = (corner >> 1) & 1, dz = (corner >> 2) & 1;
                int x = Math.Min(x0 + dx, CountX - 1), y = Math.Min(y0 + dy, CountY - 1), z = Math.Min(z0 + dz, CountZ - 1);
                int index = Index(x, y, z);
                if (!Valid[index] || Blocked(index, position - PositionOf(x, y, z))) continue;

                float weight = (dx == 1 ? t.X : 1f - t.X) * (dy == 1 ? t.Y : 1f - t.Y) * (dz == 1 ? t.Z : 1f - t.Z);
                if (weight <= 0f) continue;
                light += Light[index] * weight;
                total += weight;
            }

            if (total <= 1e-5f) return NearestValid(cell, out light);
            light *= 1f / total;
            return true;
        }

        /// <summary>Whether something's between probe <paramref name="index"/> and a point <paramref name="offset"/> from it.</summary>
        private bool Blocked(int index, Vector3 offset) =>
            MathF.Abs(offset.X) > Reach[index * 6 + (offset.X >= 0f ? 0 : 1)] ||
            MathF.Abs(offset.Y) > Reach[index * 6 + (offset.Y >= 0f ? 2 : 3)] ||
            MathF.Abs(offset.Z) > Reach[index * 6 + (offset.Z >= 0f ? 4 : 5)];

        // With all eight round it inside things (in a wall's thickness, say), the nearest that isn't, a few cells out.
        private bool NearestValid(Vector3 cell, out ProbeLight light)
        {
            light = default;
            float best = float.MaxValue;
            int cx = (int)MathF.Round(cell.X), cy = (int)MathF.Round(cell.Y), cz = (int)MathF.Round(cell.Z);
            for (int z = Math.Max(cz - 2, 0); z <= Math.Min(cz + 2, CountZ - 1); z++)
            for (int y = Math.Max(cy - 2, 0); y <= Math.Min(cy + 2, CountY - 1); y++)
            for (int x = Math.Max(cx - 2, 0); x <= Math.Min(cx + 2, CountX - 1); x++)
            {
                int index = Index(x, y, z);
                float distance = Vector3.DistanceSquared(new Vector3(x, y, z), cell);
                if (!Valid[index] || distance >= best) continue;
                best = distance;
                light = Light[index];
            }

            return best < float.MaxValue;
        }

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Origin.X);
            writer.Write(Origin.Y);
            writer.Write(Origin.Z);
            writer.Write(Spacing);
            writer.Write(CountX);
            writer.Write(CountY);
            writer.Write(CountZ);
            for (int i = 0; i < Light.Length; i++)
            {
                writer.Write(Valid[i]);
                for (int k = 0; k < 6; k++) writer.Write(Reach[i * 6 + k]);
                foreach (Vector4 channel in new[] { Light[i].Red, Light[i].Green, Light[i].Blue })
                {
                    writer.Write(channel.X);
                    writer.Write(channel.Y);
                    writer.Write(channel.Z);
                    writer.Write(channel.W);
                }
            }
        }

        internal static LightProbeGrid Read(BinaryReader reader)
        {
            var origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float spacing = reader.ReadSingle();
            var grid = new LightProbeGrid(origin, spacing, reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            Vector4 Channel() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (int i = 0; i < grid.Light.Length; i++)
            {
                grid.Valid[i] = reader.ReadBoolean();
                for (int k = 0; k < 6; k++) grid.Reach[i * 6 + k] = reader.ReadSingle();
                grid.Light[i] = new ProbeLight(Channel(), Channel(), Channel());
            }

            return grid;
        }
    }
}
