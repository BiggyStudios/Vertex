using Silk.NET.Windowing;

namespace Vertex.Rendering
{
    /// <summary>
    /// The engine side of rendering. Each frame it gathers cameras, lights and mesh renderers from the
    /// scene into a <see cref="RenderFrame"/>, then hands that to the <see cref="IRenderBackend"/> to draw.
    /// API-independent work (collecting, and later culling or sorting) belongs here, not in a backend.
    /// </summary>
    public sealed class Renderer : IDisposable
    {
        private readonly RenderFrame _frame = new();

        public IRenderBackend Backend { get; }

        /// <summary>Framebuffer size in pixels.</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }

        public Renderer(IRenderBackend backend)
        {
            Backend = backend;
        }

        internal WindowOptions ConfigureWindow(WindowOptions options) => Backend.ConfigureWindow(options);

        internal void Initialise(IWindow window)
        {
            Backend.Initialise(window);
            Resize(window.FramebufferSize.X, window.FramebufferSize.Y);
        }

        internal void Resize(int width, int height)
        {
            Width = width;
            Height = height;
            Backend.Resize(width, height);
        }

        internal void Render(Scene scene)
        {
            Collect(scene);
            Backend.Render(_frame);
        }

        /// <summary>Refills the frame from the scene. The frame's lists are reused between frames.</summary>
        private void Collect(Scene scene)
        {
            _frame.Clear();
            _frame.Width = Width;
            _frame.Height = Height;
            _frame.AmbientLight = scene.AmbientLight;

            Camera? camera = Camera.Main;
            _frame.ClearColor = camera?.ClearColor ?? Color.Black;
            _frame.HasCamera = camera != null && Width > 0 && Height > 0;
            if (camera == null || !_frame.HasCamera) return;

            _frame.ViewMatrix = camera.ViewMatrix;
            _frame.ProjectionMatrix = camera.GetProjectionMatrix((float)Width / Height);
            _frame.CameraPosition = camera.Transform.WorldPosition;

            foreach (Light light in scene.FindComponents<Light>())
            {
                Color color = light.Color * light.Intensity;

                if (light.Type == LightType.Directional)
                    _frame.DirectionalLightList.Add(new DirectionalLightData(light.Transform.Forward, color));
                else
                    _frame.PointLightList.Add(new PointLightData(light.Transform.WorldPosition, color, light.Range));
            }

            foreach (MeshRenderer meshRenderer in scene.FindComponents<MeshRenderer>())
            {
                if (meshRenderer.Mesh == null) continue;

                _frame.DrawCallList.Add(new DrawCall(meshRenderer.Mesh, meshRenderer.Material, meshRenderer.Transform.WorldMatrix));
            }
        }

        public void Dispose() => Backend.Dispose();
    }
}
