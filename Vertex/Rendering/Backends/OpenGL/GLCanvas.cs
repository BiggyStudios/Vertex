using System.Numerics;

using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// Draws the 2D layer over the scene: no depth test, premultiplied-alpha blending. Quads go to the GPU in batches,
    /// one per run of quads that share a texture, so text from one font is a single draw however long it is.
    /// </summary>
    internal sealed class GLCanvas : IDisposable
    {
        private const int _floatsPerVertex = 8; // position xy, uv, colour rgba

        private readonly GL _gl;
        private readonly GLShader _shader;
        private readonly uint _vao;
        private readonly uint _vbo;
        private float[] _vertices = new float[6 * _floatsPerVertex * 512];
        private int _vertexCount;

        // Plain-colour quads sample this, so every quad can use the same shader.
        private readonly Texture _white = new(1, 1, [255, 255, 255, 255]);

        public unsafe GLCanvas(GL gl)
        {
            _gl = gl;
            _shader = new GLShader(gl, GLShaderSources.CanvasVertex, GLShaderSources.CanvasFragment);

            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);
            _vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

            uint stride = _floatsPerVertex * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));

            gl.BindVertexArray(0);
        }

        public void Draw(IReadOnlyList<CanvasQuad> quads, int width, int height, Func<Texture, GLTexture> getTexture)
        {
            if (quads.Count == 0 || width <= 0 || height <= 0) return;

            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.CullFace);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

            _shader.Use();
            _shader.SetVector2("uScreenSize", new Vector2(width, height));
            _shader.SetInt("uTexture", 0);
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

            Texture? batchTexture = null;
            foreach (CanvasQuad quad in quads)
            {
                Texture texture = quad.Texture ?? _white;
                if (texture != batchTexture)
                {
                    Flush(batchTexture, getTexture);
                    batchTexture = texture;
                }

                // Two triangles per quad.
                AddVertex(quad.TopLeft);
                AddVertex(quad.TopRight);
                AddVertex(quad.BottomRight);
                AddVertex(quad.TopLeft);
                AddVertex(quad.BottomRight);
                AddVertex(quad.BottomLeft);
            }

            Flush(batchTexture, getTexture);

            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.CullFace);
            _gl.Enable(EnableCap.DepthTest);
        }

        private void AddVertex(in CanvasVertex vertex)
        {
            int offset = _vertexCount * _floatsPerVertex;
            if (offset + _floatsPerVertex > _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);

            _vertices[offset] = vertex.Position.X;
            _vertices[offset + 1] = vertex.Position.Y;
            _vertices[offset + 2] = vertex.TexCoord.X;
            _vertices[offset + 3] = vertex.TexCoord.Y;
            _vertices[offset + 4] = vertex.Color.X;
            _vertices[offset + 5] = vertex.Color.Y;
            _vertices[offset + 6] = vertex.Color.Z;
            _vertices[offset + 7] = vertex.Color.W;
            _vertexCount++;
        }

        private unsafe void Flush(Texture? texture, Func<Texture, GLTexture> getTexture)
        {
            if (texture == null || _vertexCount == 0) return;

            getTexture(texture).Bind(0);

            fixed (float* data = _vertices)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vertexCount * _floatsPerVertex * sizeof(float)), data, BufferUsageARB.StreamDraw);
            }

            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_vertexCount);
            _vertexCount = 0;
        }

        public void Dispose()
        {
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteBuffer(_vbo);
            _shader.Dispose();
            _white.Dispose();
        }
    }
}
