using System.Numerics;
using System.Runtime.CompilerServices;

using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// OpenGL 3.3 implementation of <see cref="IRenderBackend"/>. Forward rendering with Blinn-Phong lighting,
    /// up to <see cref="GLShaderSources.MaxDirectionalLights"/> directional and
    /// <see cref="GLShaderSources.MaxPointLights"/> point lights.
    /// </summary>
    public class GLRenderBackend : IRenderBackend
    {
        private GL _gl = null!;
        private GLShader _litShader = null!;
        private GLShader _unlitShader = null!;

        // GPU copies of meshes/textures. Weak keys, so dropping a Mesh doesn't keep it alive here.
        private readonly ConditionalWeakTable<Mesh, GLMesh> _meshes = new();
        private readonly ConditionalWeakTable<Texture, GLTexture> _textures = new();

        private static readonly string[] _directionalDirectionNames = UniformNames("uDirectionalLights", "direction", GLShaderSources.MaxDirectionalLights);
        private static readonly string[] _directionalColorNames = UniformNames("uDirectionalLights", "color", GLShaderSources.MaxDirectionalLights);
        private static readonly string[] _pointPositionNames = UniformNames("uPointLights", "position", GLShaderSources.MaxPointLights);
        private static readonly string[] _pointColorNames = UniformNames("uPointLights", "color", GLShaderSources.MaxPointLights);
        private static readonly string[] _pointRangeNames = UniformNames("uPointLights", "range", GLShaderSources.MaxPointLights);

        public WindowOptions ConfigureWindow(WindowOptions options)
        {
            return options with
            {
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
                PreferredDepthBufferBits = 24
            };
        }

        public void Initialise(IWindow window)
        {
            _gl = window.CreateOpenGL();

            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Back);

            _litShader = new GLShader(_gl, GLShaderSources.LitVertex, GLShaderSources.LitFragment);
            _unlitShader = new GLShader(_gl, GLShaderSources.LitVertex, GLShaderSources.UnlitFragment);
        }

        public void Resize(int width, int height)
        {
            _gl.Viewport(0, 0, (uint)width, (uint)height);
        }

        public void Render(RenderFrame frame)
        {
            Color clear = frame.ClearColor;
            _gl.ClearColor(clear.R, clear.G, clear.B, clear.A);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (!frame.HasCamera) return;

            GLShader? currentShader = null;

            foreach (DrawCall draw in frame.DrawCalls)
            {
                Material material = draw.Material;
                GLShader shader = material.Shading == ShadingModel.Unlit ? _unlitShader : _litShader;

                if (shader != currentShader)
                {
                    shader.Use();
                    SetFrameUniforms(shader, frame);
                    currentShader = shader;
                }

                Matrix4x4.Invert(draw.WorldMatrix, out Matrix4x4 inverseModel);

                shader.SetMatrix4("uModel", draw.WorldMatrix);
                shader.SetMatrix4("uNormalMatrix", Matrix4x4.Transpose(inverseModel));
                shader.SetColor("uColor", material.Color);
                shader.SetFloat("uSpecular", material.Specular);
                shader.SetFloat("uShininess", material.Shininess);
                shader.SetBool("uHasTexture", material.Texture != null);

                if (material.Texture != null)
                {
                    GetTexture(material.Texture).Bind(0);
                    shader.SetInt("uTexture", 0);
                }

                GetMesh(draw.Mesh).Draw();
            }
        }

        private void SetFrameUniforms(GLShader shader, RenderFrame frame)
        {
            shader.SetMatrix4("uView", frame.ViewMatrix);
            shader.SetMatrix4("uProjection", frame.ProjectionMatrix);
            shader.SetVector3("uCameraPosition", frame.CameraPosition);
            shader.SetColor3("uAmbientLight", frame.AmbientLight);

            int directionalCount = Math.Min(frame.DirectionalLights.Count, GLShaderSources.MaxDirectionalLights);
            shader.SetInt("uDirectionalLightCount", directionalCount);
            for (int i = 0; i < directionalCount; i++)
            {
                DirectionalLightData light = frame.DirectionalLights[i];
                shader.SetVector3(_directionalDirectionNames[i], light.Direction);
                shader.SetColor3(_directionalColorNames[i], light.Color);
            }

            int pointCount = Math.Min(frame.PointLights.Count, GLShaderSources.MaxPointLights);
            shader.SetInt("uPointLightCount", pointCount);
            for (int i = 0; i < pointCount; i++)
            {
                PointLightData light = frame.PointLights[i];
                shader.SetVector3(_pointPositionNames[i], light.Position);
                shader.SetColor3(_pointColorNames[i], light.Color);
                shader.SetFloat(_pointRangeNames[i], light.Range);
            }
        }

        private GLMesh GetMesh(Mesh mesh)
        {
            if (!_meshes.TryGetValue(mesh, out GLMesh? glMesh))
            {
                glMesh = new GLMesh(_gl, mesh);
                _meshes.Add(mesh, glMesh);
                mesh.Disposed += OnMeshDisposed;
            }

            return glMesh;
        }

        private GLTexture GetTexture(Texture texture)
        {
            if (!_textures.TryGetValue(texture, out GLTexture? glTexture))
            {
                glTexture = new GLTexture(_gl, texture);
                _textures.Add(texture, glTexture);
                texture.Disposed += OnTextureDisposed;
            }

            return glTexture;
        }

        private void OnMeshDisposed(Mesh mesh)
        {
            if (_meshes.TryGetValue(mesh, out GLMesh? glMesh))
            {
                glMesh.Dispose();
                _meshes.Remove(mesh);
            }
        }

        private void OnTextureDisposed(Texture texture)
        {
            if (_textures.TryGetValue(texture, out GLTexture? glTexture))
            {
                glTexture.Dispose();
                _textures.Remove(texture);
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<Mesh, GLMesh> entry in _meshes) entry.Value.Dispose();
            foreach (KeyValuePair<Texture, GLTexture> entry in _textures) entry.Value.Dispose();
            _meshes.Clear();
            _textures.Clear();

            _litShader.Dispose();
            _unlitShader.Dispose();
        }

        private static string[] UniformNames(string array, string field, int count)
        {
            return Enumerable.Range(0, count).Select(i => $"{array}[{i}].{field}").ToArray();
        }
    }
}
