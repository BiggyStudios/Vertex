using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

using Vertex.Engine.Rendering;

namespace Vertex
{
    public static class Program
    {
        private static void Main()
        {
            var gameWindowSettings = new GameWindowSettings()
            {
                UpdateFrequency = 240
            };
            
            var nativeWindowSettings = new NativeWindowSettings()
            {
                ClientSize = new Vector2i(800, 600),
                Title = "Vertex",
                Flags = ContextFlags.ForwardCompatible
            };

            Window window;
            window = new Window(gameWindowSettings, nativeWindowSettings);

            window.Run();
        }
    }
}