using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Vertex.Editor.Editing
{
    /// <summary>
    /// A game's code, built and loaded into the editor: its components show in Add Component, and its <see cref="Game"/>
    /// class builds the world the editor shows and runs it in play mode. The engine is shared with the editor rather
    /// than loaded again, so the game's components are the very types the editor works with.
    /// </summary>
    public sealed class GameCode
    {
        public string ProjectFile { get; }
        public string OutputDirectory { get; }
        public Assembly Assembly { get; }

        /// <summary>The game's <see cref="Game"/> class, or null if it hasn't one.</summary>
        public Type? GameType { get; }

        private readonly GameLoadContext _context;

        private GameCode(string projectFile, string outputDirectory, Assembly assembly, GameLoadContext context)
        {
            ProjectFile = projectFile;
            OutputDirectory = outputDirectory;
            Assembly = assembly;
            _context = context;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray()!;
            }

            GameType = types.FirstOrDefault(type => type.IsSubclassOf(typeof(Game)) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null);
        }

        public Game? CreateGame() => GameType != null ? (Game)Activator.CreateInstance(GameType)! : null;

        /// <summary>Lets the code go, so a rebuilt copy can be loaded in its place.</summary>
        public void Unload() => _context.Unload();

        /// <summary>
        /// Builds the game with <c>dotnet build</c>, then loads what was built. Slow (seconds), so run it off the main
        /// thread; <see cref="Load"/> finishes on it. Returns the build's output, and whether it worked.
        /// </summary>
        public static (bool Built, string Output, string? AssemblyPath) Build(string projectFile)
        {
            (int exitCode, string output) = Run("dotnet", $"build \"{projectFile}\" -nologo -v:q -clp:NoSummary");
            if (exitCode != 0) return (false, output, null);

            (int pathCode, string pathOutput) = Run("dotnet", $"msbuild \"{projectFile}\" -nologo -getProperty:TargetPath");
            string path = pathOutput.Trim().Split('\n').Last().Trim();
            return pathCode == 0 && File.Exists(path) ? (true, output, path) : (false, output + "\n" + pathOutput, null);
        }

        /// <summary>Loads a built game. On the main thread: loading runs the game's static setup.</summary>
        public static GameCode Load(string projectFile, string assemblyPath)
        {
            var context = new GameLoadContext(assemblyPath);
            Assembly assembly = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(assemblyPath));
            return new GameCode(projectFile, Path.GetDirectoryName(assemblyPath)!, assembly, context);
        }

        private static (int ExitCode, string Output) Run(string file, string arguments)
        {
            var start = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, output.Result + errors.Result);
        }

        /// <summary>
        /// Where the game's own code is loaded. Anything the editor already has (the engine, Silk.NET, Jolt...) comes from
        /// the editor; the rest from the game's build folder, read into memory so the files aren't locked against the
        /// next build.
        /// </summary>
        private sealed class GameLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;

            public GameLoadContext(string assemblyPath) : base($"Game: {Path.GetFileNameWithoutExtension(assemblyPath)}", isCollectible: true)
            {
                _resolver = new AssemblyDependencyResolver(assemblyPath);
            }

            protected override Assembly? Load(AssemblyName name)
            {
                foreach (Assembly loaded in Default.Assemblies)
                {
                    if (loaded.GetName().Name == name.Name) return null;
                }

                if (File.Exists(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"))) return null;

                string? path = _resolver.ResolveAssemblyToPath(name);
                if (path == null) return null;

                using var assembly = new MemoryStream(File.ReadAllBytes(path));
                string symbols = Path.ChangeExtension(path, ".pdb");
                if (!File.Exists(symbols)) return LoadFromStream(assembly);

                using var pdb = new MemoryStream(File.ReadAllBytes(symbols));
                return LoadFromStream(assembly, pdb);
            }

            protected override IntPtr LoadUnmanagedDll(string name)
            {
                string? path = _resolver.ResolveUnmanagedDllToPath(name);
                return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
            }
        }
    }
}
