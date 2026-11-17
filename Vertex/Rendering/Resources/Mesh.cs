using System.Numerics;
using System.Runtime.InteropServices;

namespace Vertex.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MeshVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;

        public MeshVertex(Vector3 position, Vector3 normal, Vector2 uv)
        {
            Position = position;
            Normal = normal;
            UV = uv;
        }
    }

    /// <summary>
    /// Triangle geometry (counter-clockwise front faces). Pure data: the active renderer makes
    /// its own GPU copy the first time the mesh is drawn.
    /// See <see cref="Primitives"/> for built-in shapes and <see cref="Model"/> for loading files.
    /// </summary>
    public class Mesh : IDisposable
    {
        /// <summary>
        /// Where the mesh came from, so a saved scene can refer to it: a built-in shape ("builtin:Cube"), or a model file
        /// and which of its meshes ("path#2"). Null for meshes made in code, which are saved whole.
        /// </summary>
        public string? Source { get; set; }

        public MeshVertex[] Vertices { get; }
        public uint[] Indices { get; }

        /// <summary>The middle of the box the vertices fit in, in the mesh's own space.</summary>
        public Vector3 BoundsCentre { get; }

        /// <summary>Half that box's size along each axis. Things whose box is out of view aren't drawn.</summary>
        public Vector3 BoundsExtents { get; }

        /// <summary>Raised by <see cref="Dispose"/> so renderers can free their GPU copy.</summary>
        public event Action<Mesh>? Disposed;

        public Mesh(MeshVertex[] vertices, uint[] indices)
        {
            Vertices = vertices;
            Indices = indices;

            if (vertices.Length == 0) return;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (MeshVertex vertex in vertices)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }
            BoundsCentre = (min + max) * 0.5f;
            BoundsExtents = (max - min) * 0.5f;
        }

        /// <summary>Frees the GPU copy. The mesh is uploaded again if it is drawn afterwards.</summary>
        public void Dispose()
        {
            Disposed?.Invoke(this);
            Disposed = null;
        }
    }
}
