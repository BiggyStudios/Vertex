using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    public class SampleGame
    {
        private static VertexWindow? _window;

        public static void Main()
        {
            _window = new VertexWindow(800, 600, "Vertex");
            _window.Run();
        }
    }
}