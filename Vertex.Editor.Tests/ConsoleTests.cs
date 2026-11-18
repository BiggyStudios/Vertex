using System.Numerics;
using System.Reflection;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Panels;
using Vertex.Input;

namespace Vertex.Editor.Tests
{
    /// <summary>The Console's log, and panels docked as tabs.</summary>
    public class ConsoleTests : EditorTest
    {
        [Fact]
        public void Logged_and_printed_messages_reach_the_console_and_repeats_are_counted()
        {
            EditorLog log = EditorLog.Shared;
            log.Clear();

            Log.Info("Hello");
            Log.Warning("Careful");
            Console.WriteLine("Something printed");
            Console.Error.WriteLine("Something broke");
            for (int i = 0; i < 3; i++) Log.Error("Again");
            log.Update();

            Assert.Equal(["Hello", "Careful", "Something printed", "Something broke", "Again"], log.Items.Select(item => item.Message));
            Assert.Equal([LogLevel.Info, LogLevel.Warning, LogLevel.Info, LogLevel.Error, LogLevel.Error], log.Items.Select(item => item.Level));
            Assert.Equal(3, log.Items[^1].Count);
            Assert.Equal((2, 1, 4), (log.Infos, log.Warnings, log.Errors));
        }

        [Fact]
        public void A_message_that_points_at_code_says_where()
        {
            string file = Path.Combine(Path.GetTempPath(), $"VertexConsole{Guid.NewGuid():N}.cs");
            File.WriteAllText(file, "class A {}");
            try
            {
                var build = new EditorLog.Item { Level = LogLevel.Error, Message = "Player.cs(12): The name 'x' does not exist", Details = $"{file}(12)" };
                var thrown = new EditorLog.Item { Level = LogLevel.Error, Message = "The game stopped", Details = $"   at Player.Update() in {file}:line 40" };
                var nowhere = new EditorLog.Item { Level = LogLevel.Info, Message = "Rendering with Vulkan" };

                Assert.Equal((file, 12), ConsolePanel.Source(build));
                Assert.Equal((file, 40), ConsolePanel.Source(thrown));
                Assert.Null(ConsolePanel.Source(nowhere));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Panels_move_between_docks_and_an_empty_dock_goes_away()
        {
            var app = new EditorApp();
            DockLayout dock = app.Dock;

            Assert.Equal(["Assets", "Console"], dock.Tabs(DockArea.Bottom));
            dock.Move("Console", DockArea.Left);
            Assert.Equal(["Outliner", "Console"], dock.Tabs(DockArea.Left));
            Assert.Equal("Console", dock.Active(DockArea.Left));

            dock.Move("Inspector", DockArea.Bottom, 0);
            Assert.False(dock.Has(DockArea.Right));
            Assert.Equal(["Inspector", "Assets"], dock.Tabs(DockArea.Bottom));

            // Closed, then shown again: back in its own place.
            dock.Close("Inspector");
            Assert.False(dock.IsOpen("Inspector"));
            dock.Show("Inspector");
            Assert.Equal(["Inspector"], dock.Tabs(DockArea.Right));
            Frame(app, 3);
        }

        [Fact]
        public void A_tab_dragged_onto_another_dock_moves_there()
        {
            var app = new EditorApp();
            Frame(app, 3);

            // Where the Console's tab and the left dock were drawn.
            var tabs = (Dictionary<DockArea, List<(string Name, Rect Rect)>>)typeof(DockLayout).GetField("_tabRects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app.Dock)!;
            var areas = (Dictionary<DockArea, Rect>)typeof(DockLayout).GetField("_areas", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app.Dock)!;
            Rect console = tabs[DockArea.Bottom].Single(tab => tab.Name == "Console").Rect;
            Rect left = areas[DockArea.Left];

            InputSystem.MoveMouse(console.Centre);
            Frame(app, 2);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            for (int i = 1; i <= 10; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(console.Centre, left.Centre, i / 10f));
                Frame(app);
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app, 2);

            Assert.Equal(["Outliner", "Console"], app.Dock.Tabs(DockArea.Left));
            Assert.Equal(["Assets"], app.Dock.Tabs(DockArea.Bottom));
        }
    }
}
