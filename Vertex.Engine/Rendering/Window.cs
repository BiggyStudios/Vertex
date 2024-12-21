using Raylib_cs;

namespace Vertex.Engine.Rendering
{
    public class Window
    {
        private int _windowWidth;
        private int _windowHeight;
        private string _windowName;
        private int _fps;

        public Window(int windowWidth, int windowHeight, string windowName, int fps)
        {
            this._windowWidth = windowWidth;
            this._windowHeight = windowHeight;
            this._windowName = windowName;
            this._fps = fps;
        }

        public void Run()
        {
            Raylib.InitWindow(_windowWidth, _windowHeight, _windowName);
            Raylib.SetTargetFPS(_fps);

            while(!Raylib.WindowShouldClose())
            {
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.RayWhite);

                Raylib.EndDrawing();
            }

            Raylib.CloseWindow();
        }
    }
}