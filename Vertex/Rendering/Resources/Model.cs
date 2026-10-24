using System.Numerics;

using Silk.NET.Assimp;

using AssimpMaterial = Silk.NET.Assimp.Material;
using AssimpMesh = Silk.NET.Assimp.Mesh;
using AssimpNode = Silk.NET.Assimp.Node;
using AssimpScene = Silk.NET.Assimp.Scene;
using AssimpTexture = Silk.NET.Assimp.Texture;

namespace Vertex.Rendering
{
    /// <summary>
    /// Loads 3D model files (OBJ, FBX, glTF/GLB, DAE, 3DS, ...) into GameObjects using Assimp.
    /// </summary>
    public static class Model
    {
        private static Assimp? _assimp;

        /// <summary>
        /// Loads a model file and returns a root GameObject that mirrors the file's node hierarchy,
        /// with a MeshRenderer for every mesh. The objects are added to the active scene.
        /// </summary>
        public static unsafe GameObject Load(string path)
        {
            _assimp ??= Assimp.GetApi();

            const PostProcessSteps flags = PostProcessSteps.Triangulate
                | PostProcessSteps.GenerateSmoothNormals
                | PostProcessSteps.JoinIdenticalVertices
                | PostProcessSteps.SortByPrimitiveType
                | PostProcessSteps.FlipUVs;

            AssimpScene* scene = _assimp.ImportFile(path, (uint)flags);
            if (scene == null || scene->MRootNode == null || (scene->MFlags & Assimp.SceneFlagsIncomplete) != 0)
                throw new Exception($"Failed to load model '{path}': {_assimp.GetErrorStringS()}");

            try
            {
                var importer = new Importer(_assimp, scene, Path.GetDirectoryName(Path.GetFullPath(path))!);
                GameObject root = importer.ProcessNode(scene->MRootNode, null);
                root.Name = Path.GetFileNameWithoutExtension(path);
                return root;
            }
            finally
            {
                _assimp.ReleaseImport(scene);
            }
        }

        private unsafe class Importer
        {
            private readonly Assimp _assimp;
            private readonly AssimpScene* _scene;
            private readonly string _directory;
            private readonly Mesh?[] _meshes;
            private readonly Material[] _materials;
            private readonly Dictionary<string, Texture?> _textures = new();

            public Importer(Assimp assimp, AssimpScene* scene, string directory)
            {
                _assimp = assimp;
                _scene = scene;
                _directory = directory;
                _meshes = new Mesh?[scene->MNumMeshes];

                // Materials are shared between every mesh that uses them, so editing one affects them all.
                _materials = new Material[scene->MNumMaterials];
                for (int i = 0; i < _materials.Length; i++)
                {
                    _materials[i] = LoadMaterial(scene->MMaterials[i]);
                }
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
                    Mesh? mesh = _meshes[meshIndex] ??= LoadMesh(_scene->MMeshes[meshIndex]);
                    if (mesh == null) continue;

                    MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
                    renderer.Mesh = mesh;
                    renderer.Material = _materials[_scene->MMeshes[meshIndex]->MMaterialIndex];
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

                return new Mesh(vertices, indices.ToArray());
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
                    else Console.WriteLine($"[Vertex] Model texture not found: {fullPath}");
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
