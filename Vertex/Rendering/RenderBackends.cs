using Vertex.Rendering.OpenGL;
using Vertex.Rendering.Vulkan;

namespace Vertex.Rendering
{
    /// <summary>Picks the graphics API to render with.</summary>
    public static class RenderBackends
    {
        /// <summary>
        /// Vulkan, Vertex's main renderer, wherever the GPU and driver support Vulkan 1.3; otherwise OpenGL 3.3, which
        /// runs on nearly anything. Players can choose for themselves by starting the game with <c>-vulkan</c> or
        /// <c>-opengl</c>, or by setting the <c>VERTEX_RENDERER</c> environment variable to <c>vulkan</c> or <c>opengl</c>.
        /// </summary>
        public static IRenderBackend CreateDefault() => Create(RendererChoice.Automatic);

        /// <summary>
        /// The graphics API a game prefers (<paramref name="preferred"/>), unless the player's chosen with <c>-vulkan</c>,
        /// <c>-opengl</c> or <c>VERTEX_RENDERER</c>: as <see cref="CreateDefault"/> otherwise.
        /// </summary>
        public static IRenderBackend Create(RendererChoice preferred)
        {
            string choice = Chosen(preferred == RendererChoice.OpenGL ? "opengl" : "vulkan");

            if (choice == "opengl") return new GLRenderBackend();

            if (VulkanRenderBackend.IsSupported(out string reason)) return new VulkanRenderBackend();

            Log.Warning($"Vulkan isn't available here ({reason}), so rendering with OpenGL.");
            return new GLRenderBackend();
        }

        private static string Chosen(string preferred)
        {
            foreach (string argument in Environment.GetCommandLineArgs().Skip(1))
            {
                switch (argument.TrimStart('-', '/').ToLowerInvariant())
                {
                    case "opengl" or "gl": return "opengl";
                    case "vulkan" or "vk": return "vulkan";
                }
            }

            return Environment.GetEnvironmentVariable("VERTEX_RENDERER")?.Trim().ToLowerInvariant() switch
            {
                "opengl" or "gl" => "opengl",
                "vulkan" or "vk" => "vulkan",
                _ => preferred
            };
        }
    }
}
