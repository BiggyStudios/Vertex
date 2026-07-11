using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using Vertex.Input;

namespace Vertex.Rendering
{
    public class VertexWindow
    {  
        private static IWindow? _window;
        private WindowOptions _options;
        private static Game? _game;

        public VertexWindow(int sizeX, int sizeY, Game game, string title = "Window")
        {
            _options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(sizeX, sizeY),
                Title = title
            };

            _window = Window.Create(_options);
            
            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.Update += OnUpdate;

            _game = game;
        }

        public void Run()
        {
            _window?.Run();
        }

        public static void OnLoad()
        {
            IInputContext input = _window!.CreateInput();
            InputSystem.FindKeyboard(input);

            _game?.Load();
        }

        public static void OnRender(double deltaTime)
        {
            _game?.Render();
        }

        public static void OnUpdate(double deltaTime)
        {
            _game?.Update();
        }
    }
}