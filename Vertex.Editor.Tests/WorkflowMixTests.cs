using Vertex.Editor.Editing;
using Vertex.Serialization;

namespace Vertex.Editor.Tests
{
    /// <summary>Games made partly in code and partly in the editor: each opens and plays as the other would expect.</summary>
    public class WorkflowMixTests : EditorTest
    {
        private static void WaitForBuild(EditorContext context)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (context.Busy != null)
            {
                Assert.True(timer.Elapsed < TimeSpan.FromMinutes(3), "The build took too long");
                Thread.Sleep(50);
                context.Update();
            }
        }

        [Fact]
        public void A_game_with_a_start_scene_opens_it_to_edit_and_plays_it_with_its_code_around_it()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            string baseDirectory = Application.BaseDirectory;
            string[] assetFolders = Application.AssetFolders;
            string currentDirectory = Directory.GetCurrentDirectory();
            try
            {
                // The start scene, made in the editor: a floor.
                Directory.CreateDirectory(Path.Combine(folder, "Assets", "Scenes"));
                var made = new Scene();
                _ = new GameObject("Floor", made);
                made.ApplyChanges();
                SceneFile.Save(made, Path.Combine(folder, "Assets", "Scenes", "Main.scene"), Path.Combine(folder, "Assets"));

                // The game's code: it starts in that scene, and adds a player and counts frames.
                File.WriteAllText(Path.Combine(folder, "Arena.csproj"), GameCode.NewProjectFile("Arena"));
                File.WriteAllText(Path.Combine(folder, "ArenaGame.cs"), """
                    using Vertex;

                    public class ArenaGame : Game
                    {
                        public static int Frames;

                        public ArenaGame() => StartScene = "Scenes/Main.scene";

                        public override void Load()
                        {
                            base.Load();
                            new GameObject("Player from code");
                        }

                        public override void Update() => Frames++;
                    }
                    """);
                Project.Create(Path.Combine(folder, "Arena" + Project.Extension), "Arena", "Arena.csproj");

                var app = new EditorApp();
                EditorContext context = app.Context;
                context.OpenProject(folder);
                WaitForBuild(context);

                // Opened to edit, as a scene file, not what the game's Load builds.
                Assert.Equal(Path.Combine(folder, "Assets", "Scenes", "Main.scene"), context.ScenePath);
                Assert.Equal(["Floor"], context.Scene.GameObjects.Select(o => o.Name));

                // Play plays it as it's being edited, unsaved changes too, with the game's code around it.
                context.Create("Not saved yet");
                context.Play.Start();
                Frame(app, 5);
                Assert.Equal(["Floor", "Not saved yet", "Player from code"], Scene.Active.GameObjects.Select(o => o.Name).Order());
                var frames = (int)context.Game!.GameType!.GetField("Frames")!.GetValue(null)!;
                Assert.True(frames > 0);

                // Stop puts it back as it was.
                context.Play.Stop();
                Assert.Equal(["Floor", "Not saved yet"], context.Scene.GameObjects.Select(o => o.Name).Order());
                Assert.Equal(Path.Combine(folder, "Assets", "Scenes", "Main.scene"), context.ScenePath);
            }
            finally
            {
                Application.BaseDirectory = baseDirectory;
                Application.AssetFolders = assetFolders;
                Directory.SetCurrentDirectory(currentDirectory);
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                    // A build server can hold on to its files for a moment; they're in the temp folder anyway.
                }
            }
        }
    }
}
