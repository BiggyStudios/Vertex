using Vertex.Editor.Gui;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.UI;

// The engine and the editor's UI keep their state in statics, so tests mustn't run at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Vertex.Editor.Tests
{
    /// <summary>Starts each test with a fresh scene, nothing pressed, and the editor's fonts, and drives frames without a window.</summary>
    public abstract class EditorTest : IDisposable
    {
        protected const int ScreenWidth = 1600;
        protected const int ScreenHeight = 900;

        private static bool _fontsLoaded;

        protected EditorTest()
        {
            Scene.Active = new Scene();
            Camera.Main = null;
            InputSystem.Reset();
            Canvas.FixedScale = 1f;
            Ui.ActiveId = 0;
            Ui.FocusId = 0;
            Menu.Close();
            Popup.Close();

            if (!_fontsLoaded)
            {
                Theme.LoadFonts();
                _fontsLoaded = true;
            }
        }

        /// <summary>Runs one 60th of a second of the editor.</summary>
        protected static void Frame(EditorApp app, int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                Time.Advance(1.0 / 60.0);
                Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
                app.Update(1f / 60f);
                Canvas.EndFrame();
                InputSystem.EndFrame();
            }
        }

        protected static void Press(Key key, params Key[] holding)
        {
            foreach (Key held in holding) InputSystem.PressKey(held);
            InputSystem.PressKey(key);
        }

        protected static void ReleaseAll() => InputSystem.ReleaseAll();

        public virtual void Dispose()
        {
            Scene.Active.Unload();
            InputSystem.Reset();
            Canvas.FixedScale = 0f;
        }
    }
}
