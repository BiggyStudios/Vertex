using System.Numerics;

using Silk.NET.OpenGL;

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

        public void Use() => _gl.UseProgram(_handle);

        public void SetInt(string name, int value) => _gl.Uniform1(GetLocation(name), value);
        public void SetBool(string name, bool value) => _gl.Uniform1(GetLocation(name), value ? 1 : 0);
        public void SetFloat(string name, float value) => _gl.Uniform1(GetLocation(name), value);
        public void SetVector3(string name, Vector3 value) => _gl.Uniform3(GetLocation(name), value.X, value.Y, value.Z);
        public void SetColor(string name, Color value) => _gl.Uniform4(GetLocation(name), value.R, value.G, value.B, value.A);
        public void SetColor3(string name, Color value) => _gl.Uniform3(GetLocation(name), value.R, value.G, value.B);

        public unsafe void SetMatrix4(string name, Matrix4x4 value)
        {
            // System.Numerics matrices are row-major with row vectors, which has the same memory
            // layout GLSL expects for column-major column-vector matrices, so no transpose is needed.
            _gl.UniformMatrix4(GetLocation(name), 1, false, (float*)&value);
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
