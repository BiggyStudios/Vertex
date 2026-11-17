using System.Numerics;

using Silk.NET.OpenGL;

using Vertex.Rendering.Shaders;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>A compiled GLSL program with cached uniform locations.</summary>
    internal class GLShader : IDisposable
    {
        private readonly GL _gl;
        private readonly uint _handle;
        private readonly Dictionary<string, int> _uniformLocations = new();

        public GLShader(GL gl, string vertexSource, string fragmentSource)
        {
            _gl = gl;

            uint vertex = CompileStage(ShaderType.VertexShader, vertexSource);
            uint fragment = CompileStage(ShaderType.FragmentShader, fragmentSource);

            _handle = gl.CreateProgram();
            gl.AttachShader(_handle, vertex);
            gl.AttachShader(_handle, fragment);
            gl.LinkProgram(_handle);

            gl.GetProgram(_handle, ProgramPropertyARB.LinkStatus, out int status);
            if (status == 0) throw new Exception($"Shader link failed: {gl.GetProgramInfoLog(_handle)}");

            gl.DetachShader(_handle, vertex);
            gl.DetachShader(_handle, fragment);
            gl.DeleteShader(vertex);
            gl.DeleteShader(fragment);
        }

        /// <summary>One of the shared shaders (see <see cref="ShaderLibrary"/>), built for OpenGL.</summary>
        public static GLShader FromLibrary(GL gl, string vertex, string fragment) =>
            new(gl, ShaderLibrary.Build(ShaderTarget.OpenGL, vertex), ShaderLibrary.Build(ShaderTarget.OpenGL, fragment));

        public void Use() => _gl.UseProgram(_handle);

        /// <summary>Reads the uniform block called <paramref name="name"/>, if the program has one, from <paramref name="binding"/>.</summary>
        public void BindBlock(string name, uint binding)
        {
            uint index = _gl.GetUniformBlockIndex(_handle, name);
            if (index != uint.MaxValue) _gl.UniformBlockBinding(_handle, index, binding);
        }

        /// <summary>Points each sampler at its texture unit, once: they stay put.</summary>
        public void SetSamplers(params (string Name, int Unit)[] samplers)
        {
            Use();
            foreach ((string name, int unit) in samplers) SetInt(name, unit);
        }

        public void SetInt(string name, int value) => _gl.Uniform1(GetLocation(name), value);
        public void SetBool(string name, bool value) => _gl.Uniform1(GetLocation(name), value ? 1 : 0);
        public void SetFloat(string name, float value) => _gl.Uniform1(GetLocation(name), value);
        public void SetVector2(string name, Vector2 value) => _gl.Uniform2(GetLocation(name), value.X, value.Y);
        public void SetVector3(string name, Vector3 value) => _gl.Uniform3(GetLocation(name), value.X, value.Y, value.Z);
        public void SetVector4(string name, Vector4 value) => _gl.Uniform4(GetLocation(name), value.X, value.Y, value.Z, value.W);
        public void SetColor(string name, Color value) => _gl.Uniform4(GetLocation(name), value.R, value.G, value.B, value.A);
        public void SetColor3(string name, Color value) => _gl.Uniform3(GetLocation(name), value.R, value.G, value.B);

        public unsafe void SetMatrix4(string name, Matrix4x4 value)
        {
            // System.Numerics matrices are row-major with row vectors, which has the same memory
            // layout GLSL expects for column-major column-vector matrices, so no transpose is needed.
            _gl.UniformMatrix4(GetLocation(name), 1, false, (float*)&value);
        }

        /// <summary>The post passes' settings, which Vulkan gets as push constants, into the <c>post</c> uniform struct.</summary>
        public void SetPost(in PostUniforms post)
        {
            SetVector2("post.texelSize", post.TexelSize);
            SetFloat("post.bloomStrength", post.BloomStrength);
            SetFloat("post.outlines", post.Outlines);
            SetVector4("post.projection", post.Projection);
            SetVector2("post.outlineStep", post.OutlineStep);
            SetFloat("post.exposure", post.Exposure);
            SetFloat("post.vignette", post.Vignette);
            SetFloat("post.occlusion", post.Occlusion);
            SetFloat("post.occlusionRadius", post.OcclusionRadius);
            SetFloat("post.threshold", post.Threshold);
            SetFloat("post.time", post.Time);
            SetVector2("post.direction", post.Direction);
        }

        private int GetLocation(string name)
        {
            if (!_uniformLocations.TryGetValue(name, out int location))
            {
                location = _gl.GetUniformLocation(_handle, name);
                _uniformLocations[name] = location;
            }

            return location;
        }

        private uint CompileStage(ShaderType type, string source)
        {
            uint shader = _gl.CreateShader(type);
            _gl.ShaderSource(shader, source);
            _gl.CompileShader(shader);

            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
            if (status == 0) throw new Exception($"{type} compile failed: {_gl.GetShaderInfoLog(shader)}");

            return shader;
        }

        public void Dispose() => _gl.DeleteProgram(_handle);
    }
}
