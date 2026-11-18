using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Serialization;

namespace Vertex.Editor.Tests
{
    /// <summary>Making C# scripts in the editor, and building them.</summary>
    public class ScriptTests : EditorTest
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
        public void Script_names_are_made_into_class_names()
        {
            Assert.Equal("EnemyAi", EditorContext.ClassName("enemy ai"));
            Assert.Equal("PlayerMover", EditorContext.ClassName("PlayerMover"));
            Assert.Equal("_2dCamera", EditorContext.ClassName("2d camera"));
            Assert.Equal("NewScript", EditorContext.ClassName("  !! "));
        }

        [Fact]
        public void A_new_script_in_a_project_without_code_sets_it_up_and_one_from_add_component_is_built_and_added()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            string baseDirectory = Application.BaseDirectory;
            string[] assetFolders = Application.AssetFolders;
            string currentDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.CreateDirectory(folder);
                Project.Create(Path.Combine(folder, "Test Game" + Project.Extension), "Test Game");
                var context = new EditorContext();
                context.OpenProject(folder);
                Assert.Null(context.Project!.GameProjectFile);

                GameObject hero = context.Create("Hero");
                hero.Transform.Position = new Vector3(1f, 2f, 3f);
                context.Create("Other");
                context.Selection.Select(hero);

                // Made from Add Component: the project gets code of its own, then it's built and added.
                string path = context.CreateScriptFor([hero], "hero mover")!;
                Assert.Equal(Path.Combine(folder, "Assets", "Scripts", "HeroMover.cs"), path);
                Assert.Equal("TestGame.csproj", context.Project.Settings.GameProject);
                Assert.True(File.Exists(Path.Combine(folder, "TestGame.csproj")));
                Assert.NotNull(context.Busy);

                WaitForBuild(context);
                Assert.NotNull(context.Game);

                // The scene's kept through it: the same objects (made again), where they were, what was selected.
                GameObject kept = Assert.Single(context.Scene.GameObjects, o => o.Name == "Hero");
                Assert.NotSame(hero, kept);
                Assert.Equal(new Vector3(1f, 2f, 3f), kept.Transform.Position);
                Assert.Single(context.Scene.GameObjects, o => o.Name == "Other");
                Assert.Same(kept, context.Selection.Object);
                Assert.True(context.Dirty);

                // With the new component on it, undoably.
                Component added = Assert.Single(kept.Components, c => c.GetType().Name == "HeroMover");
                Assert.Same(added.GetType(), SceneFile.FindType(SceneFile.TypeName(added.GetType())));
                context.Undo();
                Assert.DoesNotContain(kept.Components, c => c.GetType().Name == "HeroMover");

                // Another, from the Assets panel's New Script: named apart from the first.
                string second = context.CreateScript(Path.Combine(folder, "Assets", "Scripts"), "hero mover");
                Assert.Equal("HeroMover2.cs", Path.GetFileName(second));
                Assert.Contains("public class HeroMover2 : Component", File.ReadAllText(second));
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
