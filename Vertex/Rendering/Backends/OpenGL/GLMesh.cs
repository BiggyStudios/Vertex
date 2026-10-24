using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>The GPU copy of a <see cref="Mesh"/>: a VAO with its vertex and index buffers.</summary>
    internal class GLMesh : IDisposable
    {
        private readonly GL _gl;
        private readonly uint _vao;
        private readonly uint _vbo;
        private readonly uint _ebo;
        private readonly uint _indexCount;

        public unsafe GLMesh(GL gl, Mesh mesh)
        {
            _gl = gl;
            _indexCount = (uint)mesh.Indices.Length;

            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);

            _vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (MeshVertex* data = mesh.Vertices)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(mesh.Vertices.Length * sizeof(MeshVertex)), data, BufferUsageARB.StaticDraw);

            _ebo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            fixed (uint* data = mesh.Indices)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(mesh.Indices.Length * sizeof(uint)), data, BufferUsageARB.StaticDraw);

            uint stride = (uint)sizeof(MeshVertex);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

            gl.BindVertexArray(0);
        }

        public unsafe void Draw()
        {
            _gl.BindVertexArray(_vao);
            _gl.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, (void*)0);
        }

        public void Dispose()
        {
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteBuffer(_ebo);
        }
    }
}
