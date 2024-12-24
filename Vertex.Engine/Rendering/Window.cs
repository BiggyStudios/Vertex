using Raylib_cs;
using Vertex.Engine.Rendering.Backends.RLib;

namespace Vertex.Engine.Rendering
{
    public class Window
    {
        private readonly int _windowWidth;
        private readonly int _windowHeight;
        private readonly string _windowName;
        private readonly int _fps;

        public Window(int windowWidth, int windowHeight, string windowName, int fps)
        {
            _windowWidth = windowWidth;
            _windowHeight = windowHeight;
            _windowName = windowName;
            _fps = fps;
        }

        public void Run()
        {
            Raylib.SetTargetFPS(_fps);

            while(!Raylib.WindowShouldClose())
            {
                Update();
                Render();
            }
        }

        private void Update()
        {
            float deltaTime = Raylib.GetFrameTime();
        }

        private void Render()
        {

        }
    }
}