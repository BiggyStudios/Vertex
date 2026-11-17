using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// The GPU side of a <see cref="RenderTexture"/>: a colour texture with a framebuffer to draw the finished picture
    /// into, and view images of its own, since those are sized to match what they draw into.
    /// </summary>
    internal sealed class GLRenderTarget : IDisposable
    {
        private readonly GL _gl;

        public GLTexture Color { get; private set; } = null!;
        public uint Framebuffer { get; private set; }
        public GLViewTargets Views { get; }

        /// <summary>The <see cref="Texture.Version"/> the images were made for: a resize bumps it.</summary>
        public int Version { get; private set; }

        public GLRenderTarget(GL gl, RenderTexture texture)
        {
            _gl = gl;
            Views = new GLViewTargets(gl);
            Create(texture);
        }

        /// <summary>Remakes the images if the render texture has been resized since.</summary>
        public void Update(RenderTexture texture)
        {
            if (texture.Version == Version) return;

            DeleteImages();
            Create(texture);
        }

        private void Create(RenderTexture texture)
        {
            // This can happen partway through drawing something else (a material showing a render texture no camera
            // has drawn yet), so put back whatever framebuffer was being drawn into.
            _gl.GetInteger(GetPName.DrawFramebufferBinding, out int previous);

            Color = new GLTexture(_gl, texture.Width, texture.Height);
            Framebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, Color.Handle, 0);

            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"Render texture framebuffer is incomplete: {status}");

            // Black until a camera first draws into it, rather than whatever the GPU's memory held. Clearing ignores
            // the viewport, so that's left alone.
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previous);

            Version = texture.Version;
        }

        private void DeleteImages()
        {
            _gl.DeleteFramebuffer(Framebuffer);
            Color.Dispose();
        }

        public void Dispose()
        {
            DeleteImages();
            Views.Dispose();
        }
    }
}
