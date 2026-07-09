using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Vertex.Rendering
{
    public class VertexWindow
    {  
        private static IWindow? _window;
        private WindowOptions _options;

        public VertexWindow(int sizeX, int sizeY, string title = "Window")
        {
            _options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(sizeX, sizeY),
                Title = title
            };

            _window = Window.Create(_options);
        }

        public void Run()
        {
            _window?.Run();
        }
    }
}