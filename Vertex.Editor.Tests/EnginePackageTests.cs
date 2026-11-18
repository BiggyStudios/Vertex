using System.Diagnostics;
using Vertex.Editor.Editing;

namespace Vertex.Editor.Tests
{
    /// <summary>
    /// Games made from the download use the engine's package (Vertex.Engine) rather than the engine's project. Opened in
    /// the editor they're built against the editor's own engine, as games referring to the project are, so the code
    /// that's built is the code that runs.
    /// </summary>
    public class EnginePackageTests
    {
        private static string RepoFolder([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, ".."));

        private static void Run(string arguments, string folder)
        {
            var start = new ProcessStartInfo("dotnet", arguments) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"dotnet {arguments} failed:\n{output.Result}{errors.Result}");
        }

        [Fact]
        public void A_game_using_the_engines_package_is_built_against_the_editors_engine()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                // The engine's package, with a version the editor's engine hasn't, built away from the repository's own
                // output so it doesn't disturb it, nor compile what's in it. (Its folders end in "/": a "\" before a closing
                // quote would escape it.)
                const string version = "0.0.0-editortest";
                string packages = Path.Combine(folder, "packages");
                string build = Path.Combine(folder, "engine").Replace('\\', '/') + "/";
                Run($"pack \"{Path.Combine(RepoFolder(), "Vertex", "Vertex.csproj")}\" -c Release -nologo -p:Version={version} -o \"{packages}\" " +
                    $"-p:BaseOutputPath=\"{build}bin/\" -p:BaseIntermediateOutputPath=\"{build}obj/\" -p:DefaultItemExcludes=obj/**", folder);

                // A game set up as the download's starter is: the package, found through a nuget.config beside it.
                string game = Path.Combine(folder, "Game");
                Directory.CreateDirectory(game);
                File.WriteAllText(Path.Combine(game, "nuget.config"), $"""
                    <configuration>
                      <packageSources>
                        <add key="Vertex" value="{packages}" />
                      </packageSources>
                    </configuration>
                    """);
                File.WriteAllText(Path.Combine(game, "Game.csproj"), $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="Vertex.Engine" Version="{version}" />
                      </ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(game, "MyGame.cs"), """
                    using Vertex;

                    public class MyGame : Game
                    {
                        public override void Load() => new GameObject("Made in code");
                    }
                    """);

                (bool built, string output, string? assemblyPath) = GameCode.Build(Path.Combine(game, "Game.csproj"));
                Assert.True(built, output);
                GameCode code = GameCode.Load(Path.Combine(game, "Game.csproj"), assemblyPath!);
                try
                {
                    Assert.Equal("MyGame", code.GameType?.Name);
                    Version? engine = code.Assembly.GetReferencedAssemblies().Single(a => a.Name == "Vertex").Version;
                    Assert.Equal(typeof(GameObject).Assembly.GetName().Version, engine);
                }
                finally
                {
                    code.Unload();
                }
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
