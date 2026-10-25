using System.Numerics;

using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// Draws the frame's particles as quads facing the camera, built on the CPU each frame and sent in one batch. They're
    /// depth tested against the scene but don't write depth, so they never hide each other.
    /// </summary>
    internal sealed unsafe class GLParticles : IDisposable
    {
        private const int _floatsPerVertex = 11; // position xyz, corner xy, colour rgba, glow, additive

        private readonly GL _gl;
        private readonly GLShader _shader;
        private readonly uint _vao;
        private readonly uint _vbo;
        private float[] _vertices = new float[6 * _floatsPerVertex * 256];

        public GLParticles(GL gl)
        {
            _gl = gl;
            _shader = new GLShader(gl, GLShaderSources.ParticleVertex, GLShaderSources.ParticleFragment);

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
            gl.EnableVertexAttribArray(4);
            gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, (void*)(10 * sizeof(float)));

            gl.BindVertexArray(0);
        }

        public void Draw(RenderFrame frame)
        {
            IReadOnlyList<ParticleData> particles = frame.Particles;
            if (particles.Count == 0) return;

            int needed = particles.Count * 6 * _floatsPerVertex;
            if (needed > _vertices.Length) Array.Resize(ref _vertices, Math.Max(needed, _vertices.Length * 2));

            // The camera's right and up, from the view matrix's columns.
            Matrix4x4 view = frame.ViewMatrix;
            var cameraRight = new Vector3(view.M11, view.M21, view.M31);
            var cameraUp = new Vector3(view.M12, view.M22, view.M32);

            int offset = 0;
            foreach (ParticleData particle in particles)
            {
                Vector3 right = cameraRight;
                Vector3 up = cameraUp;
                float halfWidth = particle.Size * 0.5f;
                float halfLength = halfWidth;

                // Stretched along its movement, turned about that line to face the camera as much as it can.
                float stretch = particle.Velocity.Length();
                if (stretch > 1e-4f)
                {
                    Vector3 along = particle.Velocity / stretch;
                    Vector3 side = Vector3.Cross(along, frame.CameraPosition - particle.Position);
                    if (side.LengthSquared() > 1e-8f)
                    {
                        up = along;
                        right = Vector3.Normalize(side);
                        halfLength += stretch * 0.5f;
                    }
                }

                Vector3 x = right * halfWidth;
                Vector3 y = up * halfLength;
                Vector3 p = particle.Position;

                AddVertex(ref offset, p - x - y, -1f, -1f, particle);
                AddVertex(ref offset, p + x - y, 1f, -1f, particle);
                AddVertex(ref offset, p + x + y, 1f, 1f, particle);
                AddVertex(ref offset, p - x - y, -1f, -1f, particle);
                AddVertex(ref offset, p + x + y, 1f, 1f, particle);
                AddVertex(ref offset, p - x + y, -1f, 1f, particle);
            }

            EnvironmentData environment = frame.Environment;
            _shader.Use();
            _shader.SetMatrix4("uView", frame.ViewMatrix);
            _shader.SetMatrix4("uProjection", frame.ProjectionMatrix);
            _shader.SetVector3("uCameraPosition", frame.CameraPosition);
            _shader.SetFloat("uFogDensity", environment.FogDensity);
            _shader.SetColor3("uFogColor", environment.DrawSky ? environment.HorizonColor : frame.ClearColor);

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);

            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* data = _vertices)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(offset * sizeof(float)), data, BufferUsageARB.StreamDraw);
            }
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(offset / _floatsPerVertex));

            _gl.Enable(EnableCap.CullFace);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        private void AddVertex(ref int offset, Vector3 position, float cornerX, float cornerY, in ParticleData particle)
        {
            float[] v = _vertices;
            v[offset] = position.X;
            v[offset + 1] = position.Y;
            v[offset + 2] = position.Z;
            v[offset + 3] = cornerX;
            v[offset + 4] = cornerY;
            v[offset + 5] = particle.Color.R;
            v[offset + 6] = particle.Color.G;
            v[offset + 7] = particle.Color.B;
            v[offset + 8] = particle.Color.A;
            v[offset + 9] = particle.Glow;
            v[offset + 10] = particle.Additive ? 1f : 0f;
            offset += _floatsPerVertex;
        }

        public void Dispose()
        {
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteBuffer(_vbo);
            _shader.Dispose();
        }
    }
}
