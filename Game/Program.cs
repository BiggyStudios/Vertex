using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

using Vertex.Engine;
using Vertex.Engine.Core;
using Vertex.Engine.Core.Components;
using Vertex.Engine.Rendering;

namespace Game
{
    public class Program
    {
        public static void Main()
        {
            var gameWindowSettings = new GameWindowSettings()
            {
                UpdateFrequency = 240
            };

            var nativeWindowSettings = new NativeWindowSettings()
            {
                ClientSize = new Vector2i(800, 600),
                Title = "Vertex",
                Flags = ContextFlags.ForwardCompatible
            };

            var app = Application.Instance;

            app.OnInitialize += () =>
            {
                var scene = new Scene("MainScene");
                app.SetActiveScene(scene);

                var cameraObject = scene.CreateGameObject("MainCamera");
                var camera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<CameraController>();

                camera.AspectRatio = 800f / 600f;
                camera.Transform.Position = new Vector3(0, 0, 3);

                float[] vertices =
                {
                    // Positions          Normals              Texture coords
                    -0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,  0.0f, 0.0f,
                    0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,  1.0f, 0.0f,
                    0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,  1.0f, 1.0f,
                    0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,  1.0f, 1.0f,
                    -0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,  0.0f, 1.0f,
                    -0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,  0.0f, 0.0f,

                    -0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,  0.0f, 0.0f,
                    0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,  1.0f, 0.0f,
                    0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,  1.0f, 1.0f,
                    0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,  1.0f, 1.0f,
                    -0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,  0.0f, 1.0f,
                    -0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,  0.0f, 0.0f,

                    -0.5f,  0.5f,  0.5f, -1.0f,  0.0f,  0.0f,  1.0f, 0.0f,
                    -0.5f,  0.5f, -0.5f, -1.0f,  0.0f,  0.0f,  1.0f, 1.0f,
                    -0.5f, -0.5f, -0.5f, -1.0f,  0.0f,  0.0f,  0.0f, 1.0f,
                    -0.5f, -0.5f, -0.5f, -1.0f,  0.0f,  0.0f,  0.0f, 1.0f,
                    -0.5f, -0.5f,  0.5f, -1.0f,  0.0f,  0.0f,  0.0f, 0.0f,
                    -0.5f,  0.5f,  0.5f, -1.0f,  0.0f,  0.0f,  1.0f, 0.0f,

                    0.5f,  0.5f,  0.5f,  1.0f,  0.0f,  0.0f,  1.0f, 0.0f,
                    0.5f,  0.5f, -0.5f,  1.0f,  0.0f,  0.0f,  1.0f, 1.0f,
                    0.5f, -0.5f, -0.5f,  1.0f,  0.0f,  0.0f,  0.0f, 1.0f,
                    0.5f, -0.5f, -0.5f,  1.0f,  0.0f,  0.0f,  0.0f, 1.0f,
                    0.5f, -0.5f,  0.5f,  1.0f,  0.0f,  0.0f,  0.0f, 0.0f,
                    0.5f,  0.5f,  0.5f,  1.0f,  0.0f,  0.0f,  1.0f, 0.0f,

                    -0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,  0.0f, 1.0f,
                    0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,  1.0f, 1.0f,
                    0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,  1.0f, 0.0f,
                    0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,  1.0f, 0.0f,
                    -0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,  0.0f, 0.0f,
                    -0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,  0.0f, 1.0f,

                    -0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f,  0.0f, 1.0f,
                    0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f,  1.0f, 1.0f,
                    0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,  1.0f, 0.0f,
                    0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,  1.0f, 0.0f,
                    -0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,  0.0f, 0.0f,
                    -0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f,  0.0f, 1.0f
                };

                Vector3[] _pointLightPositions =
                {
                    new Vector3(0.7f, 0.2f, 2.0f),
                    new Vector3(2.3f, -3.3f, -4.0f),
                    new Vector3(-4.0f, 2.0f, -12.0f),
                    new Vector3(0.0f, 0.0f, -3.0f)
                };

                var cubeMesh = new Mesh(vertices);

                var shader = new Shader("Vertex.Engine/Rendering/Shaders/shader.vert",
                                                    "Vertex.Engine/Rendering/Shaders/lighting.frag");
                var material = new Material(shader);

                var diffuseMap = Texture.LoadFromFile("Vertex.Engine/Assets/container2.png");
                var specularMap = Texture.LoadFromFile("Vertex.Engine/Assets/container2_specular.png");

                material.SetTexture("material.diffuse", diffuseMap, 0);
                material.SetTexture("material.specular", specularMap, 1);
                material.SetFloat("material.shininess", 32.0f);

                material.SetVector3("dirLight.direction", new Vector3(-0.2f, -1.0f, -0.3f));
                material.SetVector3("dirLight.ambient", new Vector3(0.05f, 0.05f, 0.05f));
                material.SetVector3("dirLight.diffuse", new Vector3(0.4f, 0.4f, 0.4f));
                material.SetVector3("dirLight.specular", new Vector3(0.5f, 0.5f, 0.5f));

                for (int i = 0; i < _pointLightPositions.Length; i++)
                {
                    shader.SetVector3($"pointLights[{i}].position", _pointLightPositions[i]);
                    shader.SetVector3($"pointLights[{i}].ambient", new Vector3(0.05f, 0.05f, 0.05f));
                    shader.SetVector3($"pointLights[{i}].diffuse", new Vector3(0.8f, 0.8f, 0.8f));
                    shader.SetVector3($"pointLights[{i}].specular", new Vector3(1.0f, 1.0f, 1.0f));
                    shader.SetFloat($"pointLights[{i}].constant", 1.0f);
                    shader.SetFloat($"pointLights[{i}].linear", 0.09f);
                    shader.SetFloat($"pointLights[{i}].quadratic", 0.032f);
                }

                shader.SetVector3("spotLight.position", camera.Transform.Position);
                shader.SetVector3("spotLight.direction", camera.Front);
                shader.SetVector3("spotLight.ambient", new Vector3(0.0f, 0.0f, 0.0f));
                shader.SetVector3("spotLight.diffuse", new Vector3(1.0f, 1.0f, 1.0f));
                shader.SetVector3("spotLight.specular", new Vector3(1.0f, 1.0f, 1.0f));
                shader.SetFloat("spotLight.constant", 1.0f);
                shader.SetFloat("spotLight.linear", 0.09f);
                shader.SetFloat("spotLight.quadratic", 0.032f);
                shader.SetFloat("spotLight.cutOff", MathF.Cos(MathHelper.DegreesToRadians(12.5f)));
                shader.SetFloat("spotLight.outerCutOff", MathF.Cos(MathHelper.DegreesToRadians(17.5f)));

                material.SetVector3("viewPos", camera.Transform.Position);

                var cube = scene.CreateGameObject("Cube");
                var meshRenderer = cube.AddComponent<MeshRenderer>();

                meshRenderer.Mesh = cubeMesh;
                meshRenderer.Material = material;
            };

            app.Initialize(gameWindowSettings, nativeWindowSettings);
            app.Run();
        }
    }
}