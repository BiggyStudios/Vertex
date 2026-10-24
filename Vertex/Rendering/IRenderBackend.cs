using Silk.NET.Windowing;

namespace Vertex.Rendering
{
    /// <summary>
    /// The contract every graphics API backend (OpenGL, Vulkan, ...) implements.
    /// Only <see cref="Renderer"/> talks to the backend, so backends can be swapped
    /// without changing game code, components, meshes, textures or materials.
    /// </summary>
    public interface IRenderBackend : IDisposable
    {
        /// <summary>
        /// Called before the window is created, so the backend can request what it needs
        /// (graphics API, depth buffer, ...).
        /// </summary>
        WindowOptions ConfigureWindow(WindowOptions options);

        /// <summary>Called once the window exists. Create the device/context and built-in resources here.</summary>
        void Initialise(IWindow window);

        /// <summary>Called when the window's framebuffer changes size.</summary>
        void Resize(int width, int height);

        /// <summary>Draws one frame. Everything the backend needs is in <paramref name="frame"/>.</summary>
        void Render(RenderFrame frame);
    }
}
