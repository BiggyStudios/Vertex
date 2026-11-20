using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Draws a mesh that bends with a skeleton: a character from a model file, whose bones are objects under it (moved by
    /// an <see cref="Animation.Animator"/>, or by code). Each vertex follows the bones the mesh says move it. Models with
    /// bones get one of these for each of their meshes when they're loaded.
    /// <para>
    /// The bones are found by name under <see cref="Root"/>. The mesh is bent on the CPU when its bones have moved, so it
    /// draws, casts shadows and takes surface shaders just as any mesh does.
    /// </para>
    /// </summary>
    public sealed class SkinnedMeshRenderer : MeshRenderer
    {
        [Tooltip("The object its bones are found under, by name. Empty: the nearest object above with an Animator, or the topmost")]
        public GameObject? Root { get; set; }

        private Mesh? _source;
        private Mesh? _skinned;
        private Transform?[]? _bones;
        private Transform? _boundRoot;
        private Matrix4x4[] _matrices = [];
        private Matrix4x4[] _last = [];
        private bool _skinnedOnce;

        /// <summary>The bones it found, in the mesh's order (null for one that isn't there): for tests and the editor.</summary>
        internal IReadOnlyList<Transform?> Bones
        {
            get
            {
                _ = DrawnMesh;
                return _bones ?? [];
            }
        }

        internal override Mesh? DrawnMesh
        {
            get
            {
                if (Mesh is not { Skin: { } skin } source || skin.Weights.Length != source.Vertices.Length) return Mesh;

                if (source != _source || _skinned == null)
                {
                    _source = source;
                    _skinned = new Mesh((MeshVertex[])source.Vertices.Clone(), source.Indices) { Source = source.Source };
                    _bones = null;
                    _skinnedOnce = false;
                }

                Transform root = (Root ?? DefaultRoot()).Transform;
                if (_bones == null || _boundRoot != root || _bones.Length != skin.BoneNames.Length) FindBones(skin, root);

                Matrix4x4.Invert(Transform.WorldMatrix, out Matrix4x4 toMesh);
                if (_matrices.Length != skin.BoneNames.Length)
                {
                    _matrices = new Matrix4x4[skin.BoneNames.Length];
                    _last = new Matrix4x4[skin.BoneNames.Length];
                    _skinnedOnce = false;
                }

                bool moved = !_skinnedOnce;
                for (int b = 0; b < _matrices.Length; b++)
                {
                    // Into the bone's space as the mesh was made, out with the bone where it is now, then back into the
                    // renderer's own space (it's drawn at its own transform, like any mesh).
                    _matrices[b] = _bones![b] is { } bone ? skin.BindPoses[b] * bone.WorldMatrix * toMesh : Matrix4x4.Identity;
                    if (_matrices[b] != _last[b]) moved = true;
                }

                if (moved)
                {
                    Bend(source, skin, _skinned);
                    Array.Copy(_matrices, _last, _matrices.Length);
                    _skinnedOnce = true;
                }

                return _skinned;
            }
        }

        /// <summary>The nearest object above (or this one) with an Animator; the topmost if none has.</summary>
        private GameObject DefaultRoot()
        {
            Transform top = Transform;
            for (Transform? t = Transform; t != null; t = t.Parent)
            {
                if (t.GameObject.GetComponent<Animation.Animator>() != null) return t.GameObject;
                top = t;
            }

            return top.GameObject;
        }

        private void FindBones(MeshSkin skin, Transform root)
        {
            // Nearest first, so a bone's name used again further down (in another character held in its hand) loses.
            var byName = new Dictionary<string, Transform>();
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                Transform t = queue.Dequeue();
                byName.TryAdd(t.GameObject.Name, t);
                foreach (Transform child in t.Children) queue.Enqueue(child);
            }

            _bones = new Transform?[skin.BoneNames.Length];
            for (int b = 0; b < _bones.Length; b++) _bones[b] = byName.GetValueOrDefault(skin.BoneNames[b]);
            _boundRoot = root;
            _skinnedOnce = false;
        }

        private void Bend(Mesh source, MeshSkin skin, Mesh into)
        {
            MeshVertex[] from = source.Vertices, to = into.Vertices;
            Matrix4x4[] matrices = _matrices;
            int count = from.Length;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            object gate = new();

            void Range(int start, int end)
            {
                Vector3 low = new(float.MaxValue), high = new(float.MinValue);
                for (int i = start; i < end; i++)
                {
                    BoneWeights w = skin.Weights[i];
                    MeshVertex vertex = from[i];
                    float total = w.Weight0 + w.Weight1 + w.Weight2 + w.Weight3;
                    if (total > 1e-5f)
                    {
                        Matrix4x4 m = matrices[w.Bone0] * w.Weight0;
                        if (w.Weight1 > 0f) m += matrices[w.Bone1] * w.Weight1;
                        if (w.Weight2 > 0f) m += matrices[w.Bone2] * w.Weight2;
                        if (w.Weight3 > 0f) m += matrices[w.Bone3] * w.Weight3;
                        if (MathF.Abs(total - 1f) > 1e-3f) m *= 1f / total;

                        vertex.Position = Vector3.Transform(vertex.Position, m);
                        Vector3 normal = Vector3.TransformNormal(vertex.Normal, m);
                        float length = normal.Length();
                        if (length > 1e-8f) vertex.Normal = normal / length;
                    }

                    to[i] = vertex;
                    low = Vector3.Min(low, vertex.Position);
                    high = Vector3.Max(high, vertex.Position);
                }

                lock (gate)
                {
                    min = Vector3.Min(min, low);
                    max = Vector3.Max(max, high);
                }
            }

            // Big meshes are shared out between the CPU's cores.
            const int chunk = 8192;
            if (count <= chunk) Range(0, count);
            else Parallel.For(0, (count + chunk - 1) / chunk, c => Range(c * chunk, Math.Min(count, (c + 1) * chunk)));

            if (count > 0) into.VerticesChanged(min, max);
        }
    }
}
