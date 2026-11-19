using Silk.NET.OpenGL;

using Vertex.Rendering.Shaders;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// Draws the frame's sprites and world text (see <see cref="RenderFrame.WorldQuads"/>): built on the CPU each frame,
    /// sent in one buffer, and drawn in order in runs that share a texture. Depth tested against the scene but not
    /// written, so see-through edges don't hide what's behind them.
    /// </summary>
    internal sealed unsafe class GLSprites : IDisposable
    {
        private const int _floatsPerVertex = WorldQuad.FloatsPerVertex;
        private const int _unit = 0;

        private readonly GL _gl;
        private readonly GLShader _shader;
        private readonly uint _vao;
        private readonly uint _vbo;

        // How sprites are sampled, in place of the texture's own settings: smoothly, or in sharp, square pixels for pixel
        // art. Both stop at the picture's edges rather than repeating it, so one frame of a sheet doesn't bleed into the next.
        private readonly uint _smooth;
        private readonly uint _pixelated;

        private float[] _vertices = new float[6 * _floatsPerVertex * 256];
        private readonly List<(Texture? Texture, bool Pixelated, int First, int Count)> _batches = new();

        public GLSprites(GL gl)
        {
            _gl = gl;
            _shader = GLShader.FromLibrary(gl, ShaderLibrary.SpriteVertex, ShaderLibrary.SpriteFragment);
            _shader.BindBlock("FrameData", 0);
            _shader.SetSamplers(("spriteTexture", _unit));

            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);
            _vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

            uint stride = _floatsPerVertex * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
            gl.BindVertexArray(0);

            _smooth = Sampler(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear);
            _pixelated = Sampler(TextureMinFilter.NearestMipmapLinear, TextureMagFilter.Nearest);
        }

        private uint Sampler(TextureMinFilter min, TextureMagFilter mag)
        {
            uint sampler = _gl.GenSampler();
            _gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)min);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)mag);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.ClampToEdge);
            return sampler;
        }

        /// <param name="getTexture">The GPU copy of a texture, uploading it if need be; null for plain colour.</param>
        public void Draw(RenderFrame frame, Func<Texture?, GLTexture> getTexture)
        {
            IReadOnlyList<WorldQuad> quads = frame.WorldQuads;
            if (quads.Count == 0) return;

            int needed = quads.Count * 6 * _floatsPerVertex;
            if (needed > _vertices.Length) Array.Resize(ref _vertices, Math.Max(needed, _vertices.Length * 2));

            _batches.Clear();
            int vertex = 0;
            foreach (WorldQuad quad in quads)
            {
                if (_batches.Count == 0 || _batches[^1].Texture != quad.Texture || _batches[^1].Pixelated != quad.Pixelated)
                {
                    _batches.Add((quad.Texture, quad.Pixelated, vertex, 0));
                }

                vertex = quad.Write(_vertices, vertex);
                var batch = _batches[^1];
                _batches[^1] = batch with { Count = batch.Count + 6 };
            }

            _shader.Use();
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);

            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* data = _vertices)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertex * _floatsPerVertex * sizeof(float)), data, BufferUsageARB.StreamDraw);
            }

            foreach ((Texture? texture, bool pixelated, int first, int count) in _batches)
            {
                getTexture(texture).Bind(_unit);
                _gl.BindSampler(_unit, pixelated ? _pixelated : _smooth);
                _gl.DrawArrays(PrimitiveType.Triangles, first, (uint)count);
            }

            _gl.BindSampler(_unit, 0);
            _gl.Enable(EnableCap.CullFace);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        public void Dispose()
        {
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteSampler(_smooth);
            _gl.DeleteSampler(_pixelated);
            _shader.Dispose();
        }
    }
}
