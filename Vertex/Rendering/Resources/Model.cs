using System.Numerics;

using Silk.NET.Assimp;

using Vertex.Animation;

using AssimpAnimation = Silk.NET.Assimp.Animation;
using AssimpMaterial = Silk.NET.Assimp.Material;
using AssimpMesh = Silk.NET.Assimp.Mesh;
using AssimpNode = Silk.NET.Assimp.Node;
using AssimpScene = Silk.NET.Assimp.Scene;
using AssimpTexture = Silk.NET.Assimp.Texture;

namespace Vertex.Rendering
{
    /// <summary>
    /// Loads 3D model files (OBJ, FBX, glTF/GLB, DAE, 3DS, ...) into GameObjects using Assimp: their meshes and materials,
    /// and for models with skeletons, their bones (objects the meshes bend with) and animations.
    /// </summary>
    public static class Model
    {
        private static Assimp? _assimp;

        private const PostProcessSteps _flags = PostProcessSteps.Triangulate
            | PostProcessSteps.GenerateSmoothNormals
            | PostProcessSteps.JoinIdenticalVertices
            | PostProcessSteps.SortByPrimitiveType
            | PostProcessSteps.LimitBoneWeights
            | PostProcessSteps.FlipUVs;

        private static readonly Dictionary<string, Mesh?[]> _loadedMeshes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<AnimationClip>> _loadedAnimations = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One mesh from a model file, by its number in the file: how a saved scene refers to meshes from models (see
        /// <see cref="Mesh.Source"/>). Each file is read once; later meshes from it come from what was read.
        /// </summary>
        public static unsafe Mesh? LoadMesh(string path, int index)
        {
            path = Path.GetFullPath(path);
            if (!_loadedMeshes.TryGetValue(path, out Mesh?[]? meshes))
            {
                Read(path, (scene, importer) =>
                {
                    meshes = new Mesh?[scene->MNumMeshes];
                    for (int i = 0; i < meshes.Length; i++) meshes[i] = importer.GetMesh(i);
                    _loadedMeshes[path] = meshes;
                });
            }

            return index >= 0 && index < meshes!.Length ? meshes[index] : null;
        }

        /// <summary>
        /// The animations in a model file (its walk, run and idle), each named as in the file and found again with
        /// <see cref="Assets.LoadAnimation"/> as the file's path, a #, and the name. Empty for a model with none. Each file
        /// is read once.
        /// </summary>
        public static unsafe IReadOnlyList<AnimationClip> LoadAnimations(string path)
        {
            path = Path.GetFullPath(path);
            if (_loadedAnimations.TryGetValue(path, out List<AnimationClip>? clips)) return clips;

            clips = new List<AnimationClip>();
            Read(path, (scene, _) => clips.AddRange(ReadAnimations(scene, path)));
            _loadedAnimations[path] = clips;
            return clips;
        }

        /// <summary>
        /// Loads a model file and returns a root GameObject that mirrors the file's node hierarchy, with a MeshRenderer for
        /// every mesh (a <see cref="SkinnedMeshRenderer"/> for one that bends with bones). A model with animations (or bones)
        /// gets an <see cref="Animator"/> on its root, playing its first animation. The objects are added to the active scene.
        /// </summary>
        public static unsafe GameObject Load(string path)
        {
            string full = Path.GetFullPath(path);
            GameObject? root = null;
            Read(full, (scene, importer) =>
            {
                root = importer.ProcessNode(scene->MRootNode, null);
                root.Name = Path.GetFileNameWithoutExtension(path);

                IReadOnlyList<AnimationClip> clips = scene->MNumAnimations > 0 ? LoadAnimations(full) : [];
                if (clips.Count > 0 || importer.HasBones)
                {
                    root.AddComponent<Animator>(animator =>
                    {
                        animator.Clip = clips.Count > 0 ? Assets.LoadAnimation(clips[0].Source!) : null;
                    });
                }
            }, requireComplete: true);

            return root!;
        }

        private unsafe delegate void SceneUse(AssimpScene* scene, Importer importer);

        private static unsafe void Read(string path, SceneUse use, bool requireComplete = false)
        {
            _assimp ??= Assimp.GetApi();
            AssimpScene* scene = _assimp.ImportFile(path, (uint)_flags);
            if (scene == null || scene->MRootNode == null || (requireComplete && (scene->MFlags & Assimp.SceneFlagsIncomplete) != 0))
                throw new Exception($"Failed to load model '{path}': {_assimp.GetErrorStringS()}");

            try
            {
                use(scene, new Importer(_assimp, scene, path));
            }
            finally
            {
                _assimp.ReleaseImport(scene);
            }
        }

        /// <summary>A model file moved: the meshes and animations read from it are kept, and told where it is now.</summary>
        internal static void Moved(Func<string, string?> remap)
        {
            foreach (string key in _loadedMeshes.Keys.ToList())
            {
                if (remap(key) is not { } moved) continue;
                Mesh?[] meshes = _loadedMeshes[key];
                _loadedMeshes.Remove(key);
                _loadedMeshes[moved] = meshes;
                for (int i = 0; i < meshes.Length; i++)
                {
                    if (meshes[i] is { } mesh) mesh.Source = $"{moved}#{i}";
                }
            }

            foreach (string key in _loadedAnimations.Keys.ToList())
            {
                if (remap(key) is not { } moved) continue;
                List<AnimationClip> clips = _loadedAnimations[key];
                _loadedAnimations.Remove(key);
                _loadedAnimations[moved] = clips;
                foreach (AnimationClip clip in clips) clip.Source = $"{moved}#{clip.Name}";
            }
        }

        /// <summary>Forgets what was read from a model file, so it's read again next time: after it's changed on disk.</summary>
        internal static void Forget(string path)
        {
            _loadedMeshes.Remove(path);
            _loadedAnimations.Remove(path);
        }

        // ---------------------------------------------------------------- Animations

        private static unsafe List<AnimationClip> ReadAnimations(AssimpScene* scene, string path)
        {
            // Where each node is from the root, by name, as the objects made from them are (see AnimationTrack.Path).
            var paths = new Dictionary<string, string>();
            void Walk(AssimpNode* node, string at)
            {
                paths.TryAdd(node->MName.AsString, at);
                for (int i = 0; i < node->MNumChildren; i++)
                {
                    AssimpNode* child = node->MChildren[i];
                    Walk(child, at.Length == 0 ? child->MName.AsString : at + "/" + child->MName.AsString);
                }
            }

            Walk(scene->MRootNode, "");

            var clips = new List<AnimationClip>();
            var names = new HashSet<string>();
            for (int a = 0; a < scene->MNumAnimations; a++)
            {
                AssimpAnimation* animation = scene->MAnimations[a];
                double ticks = animation->MTicksPerSecond > 0 ? animation->MTicksPerSecond : 25.0;

                // "Armature|Walk", as Blender names them, is called Walk.
                string name = animation->MName.AsString;
                int bar = name.LastIndexOf('|');
                if (bar >= 0 && bar < name.Length - 1) name = name[(bar + 1)..];
                if (string.IsNullOrWhiteSpace(name)) name = $"Animation {a + 1}";
                string unique = name;
                for (int n = 2; !names.Add(unique); n++) unique = $"{name} {n}";

                var clip = new AnimationClip
                {
                    Name = unique,
                    Source = $"{path}#{unique}",
                    Length = (float)(animation->MDuration / ticks),
                    Loop = true
                };

                for (int c = 0; c < animation->MNumChannels; c++)
                {
                    NodeAnim* channel = animation->MChannels[c];
                    if (!paths.TryGetValue(channel->MNodeName.AsString, out string? at)) continue;

                    if (channel->MNumPositionKeys > 0)
                    {
                        AnimationTrack track = clip.AddTrack(at, "Position", 3);
                        for (int k = 0; k < channel->MNumPositionKeys; k++)
                        {
                            VectorKey key = channel->MPositionKeys[k];
                            track.Keys.Add(new Keyframe((float)(key.MTime / ticks), new Vector4(key.MValue, 0f), KeyInterpolation.Linear));
                        }
                    }

                    if (channel->MNumRotationKeys > 0)
                    {
                        AnimationTrack track = clip.AddTrack(at, "Rotation", 4);
                        Vector4 previous = default;
                        for (int k = 0; k < channel->MNumRotationKeys; k++)
                        {
                            QuatKey key = channel->MRotationKeys[k];
                            var value = new Vector4(key.MValue.X, key.MValue.Y, key.MValue.Z, key.MValue.W);
                            // The same turn either way round: kept on the side of the key before, so it goes the short way.
                            if (k > 0 && Vector4.Dot(value, previous) < 0f) value = -value;
                            previous = value;
                            track.Keys.Add(new Keyframe((float)(key.MTime / ticks), value, KeyInterpolation.Linear));
                        }
                    }

                    if (channel->MNumScalingKeys > 0)
                    {
                        AnimationTrack track = clip.AddTrack(at, "Scale", 3);
                        for (int k = 0; k < channel->MNumScalingKeys; k++)
                        {
                            VectorKey key = channel->MScalingKeys[k];
                            track.Keys.Add(new Keyframe((float)(key.MTime / ticks), new Vector4(key.MValue, 0f), KeyInterpolation.Linear));
                        }
                    }
                }

                foreach (AnimationTrack track in clip.Tracks) track.Sort();
                clips.Add(clip);
            }

            return clips;
        }

        // ---------------------------------------------------------------- Meshes and materials

        private unsafe class Importer
        {
            private readonly Assimp _assimp;
            private readonly AssimpScene* _scene;
            private readonly string _directory;
            private readonly string _path;
            private readonly Mesh?[] _meshes;
            private readonly Material[] _materials;
            private readonly Dictionary<string, Texture?> _textures = new();

            /// <summary>Whether any mesh made so far bends with bones.</summary>
            public bool HasBones { get; private set; }

            public Importer(Assimp assimp, AssimpScene* scene, string path)
            {
                _path = path;
                _assimp = assimp;
                _scene = scene;
                _directory = Path.GetDirectoryName(path)!;
                _meshes = new Mesh?[scene->MNumMeshes];

                // Materials are shared between every mesh that uses them, so editing one affects them all.
                _materials = new Material[scene->MNumMaterials];
                for (int i = 0; i < _materials.Length; i++)
                {
                    _materials[i] = LoadMaterial(scene->MMaterials[i]);
                }
            }

            /// <summary>The file's mesh number <paramref name="index"/>, loaded the first time it's asked for. Null if it isn't triangles.</summary>
            public Mesh? GetMesh(int index)
            {
                if (index < 0 || index >= _meshes.Length) return null;
                if (_meshes[index] == null && LoadMesh(_scene->MMeshes[index]) is { } loaded)
                {
                    loaded.Source = $"{_path}#{index}";
                    _meshes[index] = loaded;
                }

                return _meshes[index];
            }

            public GameObject ProcessNode(AssimpNode* node, GameObject? parent)
            {
                var gameObject = new GameObject(node->MName.AsString);
                gameObject.Transform.Parent = parent?.Transform;

                Matrix4x4.Decompose(Matrix4x4.Transpose(node->MTransformation), out Vector3 scale, out Quaternion rotation, out Vector3 position);
                gameObject.Transform.Position = position;
                gameObject.Transform.Rotation = rotation;
                gameObject.Transform.Scale = scale;

                for (int i = 0; i < node->MNumMeshes; i++)
                {
                    uint meshIndex = node->MMeshes[i];
                    Mesh? mesh = GetMesh((int)meshIndex);
                    if (mesh == null) continue;

                    MeshRenderer renderer = mesh.Skin != null ? gameObject.AddComponent<SkinnedMeshRenderer>() : gameObject.AddComponent<MeshRenderer>();
                    renderer.Mesh = mesh;
                    renderer.Material = _materials[_scene->MMeshes[meshIndex]->MMaterialIndex];
                    HasBones |= mesh.Skin != null;
                }

                for (int i = 0; i < node->MNumChildren; i++)
                {
                    ProcessNode(node->MChildren[i], gameObject);
                }

                return gameObject;
            }

            private static Mesh? LoadMesh(AssimpMesh* mesh)
            {
                // Skip point and line meshes; only triangles are rendered.
                if ((mesh->MPrimitiveTypes & (uint)PrimitiveType.Triangle) == 0) return null;

                var vertices = new MeshVertex[mesh->MNumVertices];
                Vector3* uvs = mesh->MTextureCoords[0];

                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = new MeshVertex(
                        mesh->MVertices[i],
                        mesh->MNormals != null ? mesh->MNormals[i] : Vector3.UnitY,
                        uvs != null ? new Vector2(uvs[i].X, uvs[i].Y) : Vector2.Zero);
                }

                var indices = new List<uint>((int)mesh->MNumFaces * 3);
                for (int i = 0; i < mesh->MNumFaces; i++)
                {
                    Face face = mesh->MFaces[i];
                    if (face.MNumIndices != 3) continue;

                    indices.Add(face.MIndices[0]);
                    indices.Add(face.MIndices[1]);
                    indices.Add(face.MIndices[2]);
                }

                return new Mesh(vertices, indices.ToArray()) { Skin = LoadSkin(mesh) };
            }

            /// <summary>The bones that bend a mesh, and how much each moves each vertex (the four that move it most); null if none do.</summary>
            private static MeshSkin? LoadSkin(AssimpMesh* mesh)
            {
                if (mesh->MNumBones == 0) return null;

                int bones = (int)mesh->MNumBones;
                var names = new string[bones];
                var bindPoses = new Matrix4x4[bones];
                var weights = new BoneWeights[mesh->MNumVertices];
                for (int b = 0; b < bones; b++)
                {
                    Bone* bone = mesh->MBones[b];
                    names[b] = bone->MName.AsString;
                    bindPoses[b] = Matrix4x4.Transpose(bone->MOffsetMatrix);
                    for (int w = 0; w < bone->MNumWeights; w++)
                    {
                        VertexWeight weight = bone->MWeights[w];
                        if (weight.MVertexId < weights.Length && weight.MWeight > 0f) AddWeight(ref weights[weight.MVertexId], b, weight.MWeight);
                    }
                }

                return new MeshSkin(names, bindPoses, weights);
            }

            /// <summary>Gives a vertex one more bone, in place of its weakest if it has four already.</summary>
            private static void AddWeight(ref BoneWeights weights, int bone, float weight)
            {
                if (weights.Weight0 == 0f) { weights.Bone0 = bone; weights.Weight0 = weight; return; }
                if (weights.Weight1 == 0f) { weights.Bone1 = bone; weights.Weight1 = weight; return; }
                if (weights.Weight2 == 0f) { weights.Bone2 = bone; weights.Weight2 = weight; return; }
                if (weights.Weight3 == 0f) { weights.Bone3 = bone; weights.Weight3 = weight; return; }

                float weakest = MathF.Min(MathF.Min(weights.Weight0, weights.Weight1), MathF.Min(weights.Weight2, weights.Weight3));
                if (weight <= weakest) return;
                if (weights.Weight0 == weakest) { weights.Bone0 = bone; weights.Weight0 = weight; }
                else if (weights.Weight1 == weakest) { weights.Bone1 = bone; weights.Weight1 = weight; }
                else if (weights.Weight2 == weakest) { weights.Bone2 = bone; weights.Weight2 = weight; }
                else { weights.Bone3 = bone; weights.Weight3 = weight; }
            }

            private Material LoadMaterial(AssimpMaterial* source)
            {
                var material = new Material();

                Vector4 color;
                if (_assimp.GetMaterialColor(source, Assimp.MaterialColorDiffuseBase, 0, 0, &color) == Return.Success)
                {
                    material.Color = new Color(color.X, color.Y, color.Z, color.W);
                }

                if (_assimp.GetMaterialTextureCount(source, TextureType.Diffuse) > 0)
                {
                    AssimpString texturePath;
                    _assimp.GetMaterialTexture(source, TextureType.Diffuse, 0, &texturePath, null, null, null, null, null, null);
                    material.Texture = LoadTexture(texturePath.AsString);
                }

                return material;
            }

            private Texture? LoadTexture(string path)
            {
                if (_textures.TryGetValue(path, out Texture? cached)) return cached;

                Texture? texture = null;
                AssimpTexture* embedded = _assimp.GetEmbeddedTexture(_scene, path);

                if (embedded != null)
                {
                    texture = LoadEmbeddedTexture(embedded);
                }
                else
                {
                    string fullPath = Path.Combine(_directory, path.Replace('\\', Path.DirectorySeparatorChar));
                    if (System.IO.File.Exists(fullPath)) texture = Texture.Load(fullPath);
                    else Log.Warning($"Model texture not found: {fullPath}");
                }

                _textures[path] = texture;
                return texture;
            }

            private static Texture LoadEmbeddedTexture(AssimpTexture* embedded)
            {
                // A height of 0 means the data is a compressed image file (PNG, JPG, ...) of MWidth bytes.
                if (embedded->MHeight == 0)
                {
                    return Texture.FromMemory(new Span<byte>(embedded->PcData, (int)embedded->MWidth).ToArray());
                }

                int width = (int)embedded->MWidth;
                int height = (int)embedded->MHeight;
                byte[] pixels = new byte[width * height * 4];

                for (int i = 0; i < width * height; i++)
                {
                    Texel texel = embedded->PcData[i];
                    pixels[i * 4] = texel.R;
                    pixels[i * 4 + 1] = texel.G;
                    pixels[i * 4 + 2] = texel.B;
                    pixels[i * 4 + 3] = texel.A;
                }

                return new Texture(width, height, pixels);
            }
        }
    }
}
