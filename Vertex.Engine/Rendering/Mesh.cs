using OpenTK.Graphics.OpenGL4;

namespace Vertex.Engine.Rendering
{
    /// <summary>
    /// Represents a 3D mesh with vertex data and redering capabilities.
    /// </summary>
    public class Mesh
    {
        private readonly int _vao;
        private readonly int _vbo;
        private readonly int _vertexCount;

        /// <summary>
        /// Creates a new mesh with the specified vertex data.
        /// </summary>
        /// <param name="vertices">Array of vertex positions.</param>
        /// <param name="stride">The size of a single vertex in floats (default: 8 for position(3) + normal(3) + texcoord(2)).</param>
        public Mesh(float[] vertices, int stride = 8)
        {
            _vertexCount = vertices.Length / stride;

            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride * sizeof(float), 0);

            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride * sizeof(float), 3 * sizeof(float));

            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride * sizeof(float), 6 * sizeof(float));
        }

        /// <summary>
        /// Renders the mesh.
        /// </summary>
        public void Draw()
        {
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _vertexCount);
        }

        /// <summary>
        /// Releases all OpenGL resources used by this mesh.
        /// </summary>
        public void Dispose()
        {
            GL.DeleteBuffer(_vbo);
            GL.DeleteVertexArray(_vao);
        }
    }
}