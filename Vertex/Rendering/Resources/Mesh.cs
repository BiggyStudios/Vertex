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
        public MeshVertex[] Vertices { get; }
        public uint[] Indices { get; }

        /// <summary>Raised by <see cref="Dispose"/> so renderers can free their GPU copy.</summary>
        public event Action<Mesh>? Disposed;

        public Mesh(MeshVertex[] vertices, uint[] indices)
        {
            Vertices = vertices;
            Indices = indices;
        }

        /// <summary>Frees the GPU copy. The mesh is uploaded again if it is drawn afterwards.</summary>
        public void Dispose()
        {
            Disposed?.Invoke(this);
            Disposed = null;
        }
    }
}
