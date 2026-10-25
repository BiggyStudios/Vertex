using System.Numerics;

using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// Shadows from the sun. The scene's depth is drawn from the sun's point of view into a texture; anything further
    /// from the sun than what's stored there is in shadow. The area covered follows the camera.
    /// </summary>
    internal sealed unsafe class GLShadowMap : IDisposable
    {
        public const int Size = 2048;

        // How far the sun "sees", centred on the shadowed area. Tall enough for the surf map and anything above it.
        private const float _depthRange = 300f;

        private readonly GL _gl;
        private readonly uint _texture;
        private readonly uint _framebuffer;
        private readonly GLShader _shader;

        /// <summary>World to shadow-map space, from the last <see cref="Draw"/>.</summary>
        public Matrix4x4 LightSpace { get; private set; }

        /// <summary>The size of one shadow-map pixel in world units, from the last <see cref="Draw"/>.</summary>
        public float TexelWorldSize { get; private set; }

        public GLShadowMap(GL gl)
        {
            _gl = gl;
            _shader = new GLShader(gl, GLShaderSources.ShadowVertex, GLShaderSources.ShadowFragment);

            _texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, Size, Size, 0,
                PixelFormat.DepthComponent, PixelType.UnsignedInt, null);

            // Linear filtering with comparison: each lookup blends 4 neighbouring texels' shadow tests, softening edges.
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);

            // Outside the map counts as lit.
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToBorder);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToBorder);
            float* border = stackalloc float[4] { 1f, 1f, 1f, 1f };
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, border);

            _framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _texture, 0);
            gl.DrawBuffer(DrawBufferMode.None);
            gl.ReadBuffer(ReadBufferMode.None);

            GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"Shadow map framebuffer is incomplete: {status}");
        }

        /// <summary>Draws the shadow casters' depth from the sun, covering the frame's shadow distance around the camera.</summary>
        public void Draw(RenderFrame frame, Vector3 sunDirection, Func<Mesh, GLMesh> getMesh)
        {
            LightSpace = ComputeLightSpace(frame.CameraPosition, frame.CameraForward, sunDirection, frame.ShadowDistance);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            _gl.Viewport(0, 0, Size, Size);
            _gl.Clear(ClearBufferMask.DepthBufferBit);

            // Drawing back faces only keeps lit surfaces from shadowing themselves.
            _gl.CullFace(TriangleFace.Front);

            _shader.Use();
            _shader.SetMatrix4("uLightSpace", LightSpace);
            foreach (DrawCall draw in frame.DrawCalls)
            {
                if (!draw.CastShadows) continue;

                _shader.SetMatrix4("uModel", draw.WorldMatrix);
                getMesh(draw.Mesh).Draw();
            }

            _gl.CullFace(TriangleFace.Back);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Viewport(0, 0, (uint)frame.Width, (uint)frame.Height);
        }

        public void Bind(int unit)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
        }

        private Matrix4x4 ComputeLightSpace(Vector3 cameraPosition, Vector3 cameraForward, Vector3 sunDirection, float distance)
        {
            float halfSize = MathF.Max(distance, 1f);
            TexelWorldSize = halfSize * 2f / Size;

            // Centre the area a little ahead of the camera, where most of what's visible is.
            Vector3 centre = cameraPosition + (cameraForward with { Y = 0f }) * (halfSize * 0.4f);
            Vector3 up = MathF.Abs(sunDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

            // Snap the centre to whole shadow-map pixels, so shadow edges don't shimmer as the camera moves.
            Matrix4x4 rotation = Matrix4x4.CreateLookAt(Vector3.Zero, sunDirection, up);
            Vector3 local = Vector3.Transform(centre, rotation);
            local.X = MathF.Round(local.X / TexelWorldSize) * TexelWorldSize;
            local.Y = MathF.Round(local.Y / TexelWorldSize) * TexelWorldSize;
            Matrix4x4.Invert(rotation, out Matrix4x4 inverseRotation);
            centre = Vector3.Transform(local, inverseRotation);

            Matrix4x4 view = Matrix4x4.CreateLookAt(centre - sunDirection * (_depthRange * 0.5f), centre, up);
            Matrix4x4 projection = Matrix4x4.CreateOrthographic(halfSize * 2f, halfSize * 2f, 0f, _depthRange);
            return view * projection;
        }

        public void Dispose()
        {
            _gl.DeleteFramebuffer(_framebuffer);
            _gl.DeleteTexture(_texture);
            _shader.Dispose();
        }
    }
}
