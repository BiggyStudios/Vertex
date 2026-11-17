using Silk.NET.Windowing;

namespace Vertex.Rendering
{
    /// <summary>
    /// The contract every graphics API backend (Vulkan, OpenGL, ...) implements.
    /// Only <see cref="Renderer"/> talks to the backend, so backends can be swapped
    /// without changing game code, components, meshes, textures or materials.
    /// <para>
    /// Each frame the renderer calls <see cref="BeginFrame"/>, then <see cref="UpdateParticles"/> if the backend
    /// <see cref="SupportsGpuParticles"/>, then <see cref="Render"/> once for every camera drawing into a texture and last
    /// for the screen, then <see cref="EndFrame"/>.
    /// </para>
    /// </summary>
    public interface IRenderBackend : IDisposable
    {
        /// <summary>A short name for logs and the console, like "Vulkan" or "OpenGL".</summary>
        string Name { get; }

        /// <summary>
        /// Called before the window is created, so the backend can request what it needs
        /// (graphics API, depth buffer, ...).
        /// </summary>
        WindowOptions ConfigureWindow(WindowOptions options);

        /// <summary>Called once the window exists. Create the device/context and built-in resources here.</summary>
        void Initialise(IWindow window);

        /// <summary>Called when the window's framebuffer changes size.</summary>
        void Resize(int width, int height);

        /// <summary>
        /// Whether the backend simulates <see cref="GpuParticleSystem"/>s itself (with compute shaders). If not, the
        /// engine simulates them on the CPU and hands them over as ordinary <see cref="RenderFrame.Particles"/>.
        /// </summary>
        bool SupportsGpuParticles { get; }

        /// <summary>Starts a frame, before any <see cref="Render"/>.</summary>
        void BeginFrame();

        /// <summary>
        /// Moves the GPU particle pool on by a frame and makes the new ones. Called once a frame, after
        /// <see cref="BeginFrame"/>, only if <see cref="SupportsGpuParticles"/>. Every <see cref="Render"/> with a camera
        /// then draws the pool's visible particles.
        /// </summary>
        void UpdateParticles(GpuParticleUpdate update);

        /// <summary>Draws one view. Everything the backend needs is in <paramref name="frame"/>.</summary>
        void Render(RenderFrame frame);

        /// <summary>Finishes the frame: everything's been drawn, so show it.</summary>
        void EndFrame();

        /// <summary>Whether to time each pass on the GPU. Backends that can't leave it off.</summary>
        bool MeasureGpuTime { get => false; set { } }

        /// <summary>
        /// Fills <paramref name="into"/> with the GPU's time for each pass of the latest frame it has finished, while
        /// <see cref="MeasureGpuTime"/> is on; called after <see cref="EndFrame"/>. Left as it was if no newer frame
        /// has finished since.
        /// </summary>
        void ReadGpuTimes(List<GpuPassTime> into) => into.Clear();
    }
}
