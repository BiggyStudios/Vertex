using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    public class SampleGame : Game
    {
        private static VertexWindow? _window;

        public static void Main()
        {
            _window = new VertexWindow(800, 600, new SampleGame(), "Vertex");
            _window.Run();
        }

        public override void Load()
        {
            base.Load();
        }

        public override void Update()
        {
            base.Update();
        }

        public override void Render()
        {
            base.Render();
        }
    }
}