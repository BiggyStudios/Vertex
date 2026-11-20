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

    /// <summary>Which bones (up to four) move a vertex, and how much each: the weights add up to 1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BoneWeights
    {
        public int Bone0, Bone1, Bone2, Bone3;
        public float Weight0, Weight1, Weight2, Weight3;
    }

    /// <summary>
    /// How a mesh bends with a skeleton: the bones that move it (by name, found under a
    /// <see cref="SkinnedMeshRenderer"/>'s root), where each bone was when the mesh was made, and which bones move each vertex.
    /// </summary>
    public sealed class MeshSkin
    {
        /// <summary>The bones' names: the objects a skinned mesh renderer looks for.</summary>
        public string[] BoneNames { get; }

        /// <summary>
        /// For each bone, what takes a point on the mesh (in the mesh's own space, as it was made) into the bone's own
        /// space: the inverse of where the bone was then.
        /// </summary>
        public Matrix4x4[] BindPoses { get; }

        /// <summary>For each vertex, the bones that move it.</summary>
        public BoneWeights[] Weights { get; }

        public MeshSkin(string[] boneNames, Matrix4x4[] bindPoses, BoneWeights[] weights)
        {
            BoneNames = boneNames;
            BindPoses = bindPoses;
            Weights = weights;
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

        /// <summary>How it bends with a skeleton, if it does (a character from a model file): see <see cref="SkinnedMeshRenderer"/>.</summary>
        public MeshSkin? Skin { get; init; }

        /// <summary>The middle of the box the vertices fit in, in the mesh's own space.</summary>
        public Vector3 BoundsCentre { get; private set; }

        /// <summary>Half that box's size along each axis. Things whose box is out of view aren't drawn.</summary>
        public Vector3 BoundsExtents { get; private set; }

        /// <summary>
        /// Counts changes to <see cref="Vertices"/> (see <see cref="VerticesChanged"/>): 0 for a mesh that never changes.
        /// Renderers keep a mesh that has changed somewhere they can write each frame.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>Raised by <see cref="Dispose"/> so renderers can free their GPU copy.</summary>
        public event Action<Mesh>? Disposed;

        public Mesh(MeshVertex[] vertices, uint[] indices)
        {
            Vertices = vertices;
            Indices = indices;
            UpdateBounds();
        }

        /// <summary>
        /// Says <see cref="Vertices"/> were changed in place (moved by bones, say), so the mesh is drawn as they are now
        /// and its bounds fit them.
        /// </summary>
        public void VerticesChanged()
        {
            UpdateBounds();
            Version++;
        }

        /// <summary>The same, with the bounds already worked out (the skinning has been through every vertex anyway).</summary>
        internal void VerticesChanged(Vector3 min, Vector3 max)
        {
            BoundsCentre = (min + max) * 0.5f;
            BoundsExtents = (max - min) * 0.5f;
            Version++;
        }

        private void UpdateBounds()
        {
            if (Vertices.Length == 0) return;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (MeshVertex vertex in Vertices)
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
