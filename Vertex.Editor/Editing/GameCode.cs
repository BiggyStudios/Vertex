using System.Diagnostics;
using System.Security;
using System.Text;
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
            // Built against the editor's own engine, not whatever copy the game's project points at: that's the engine
            // the game's code runs on here, and the game's copy may not be there at all (the game cloned on its own).
            string engine = $"-p:CustomAfterMicrosoftCommonTargets=\"{WriteEngineTargets()}\"";
            (int exitCode, string output) = Run("dotnet", $"build \"{projectFile}\" -nologo -v:q -clp:NoSummary {engine}");
            if (exitCode != 0) return (false, output, null);

            (int pathCode, string pathOutput) = Run("dotnet", $"msbuild \"{projectFile}\" -nologo -getProperty:TargetPath {engine}");
            string path = pathOutput.Trim().Split('\n').Last().Trim();
            return pathCode == 0 && File.Exists(path) ? (true, output, path) : (false, output + "\n" + pathOutput, null);
        }

        /// <summary>
        /// The build errors in <paramref name="output"/>, each once and short ("Player.cs(12): The name 'x' does not
        /// exist"), for showing; the full output has every one with its whole path, often several times over.
        /// </summary>
        public static List<string> Errors(string output) => ErrorsWithPlaces(output).Select(e => e.Shown).ToList();

        /// <summary>The build errors, each once: short, and the file and line it's in (for opening it there).</summary>
        public static List<(string Shown, string? File, int Line)> ErrorsWithPlaces(string output)
        {
            var errors = new List<(string Shown, string? File, int Line)>();
            foreach (string line in output.Split('\n'))
            {
                int at = line.IndexOf(": error ", StringComparison.Ordinal);
                if (at < 0) continue;

                // "C:\Game\Player.cs(12,5): error CS0103: The name 'x' does not exist [C:\Game\Game.csproj]"
                string where = line[..at].Trim();
                string message = line[(at + ": error ".Length)..].Trim();
                int project = message.LastIndexOf(" [", StringComparison.Ordinal);
                if (project > 0 && message.EndsWith(']')) message = message[..project];
                int code = message.IndexOf(": ", StringComparison.Ordinal);
                if (code > 0 && code < 12) message = message[(code + 2)..];

                int bracket = where.IndexOf('(');
                string path = bracket > 0 ? where[..bracket] : where;
                // Either kind of slash: Path.GetFileName only knows this system's, and a build's output can have both.
                string file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
                string lineNumber = bracket > 0 ? where[(bracket + 1)..].Split(',', ')')[0] : "";
                string shown = lineNumber.Length > 0 ? $"{file}({lineNumber}): {message}" : $"{file}: {message}";
                if (errors.Any(e => e.Shown == shown)) continue;
                errors.Add((shown, path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? path : null, int.TryParse(lineNumber, out int number) ? number : 0));
            }

            return errors;
        }

        /// <summary>
        /// Writes the MSBuild file that swaps the game's reference to the engine's project for the engine the editor runs
        /// on (and what that needs to compile against), and returns where it is. Imported into the game's build after
        /// everything else, so the game's project file doesn't change.
        /// </summary>
        private static string WriteEngineTargets()
        {
            // Everything the engine uses, for a game whose engine project isn't there; just the engine itself when it is,
            // as the game's build already has the engine's packages through it then.
            var everything = new StringBuilder();
            string engineOnly = "";
            foreach (string assembly in EngineAssemblies())
            {
                string name = SecurityElement.Escape(Path.GetFileNameWithoutExtension(assembly));
                string reference = $"""      <Reference Include="{name}"><HintPath>{SecurityElement.Escape(assembly)}</HintPath><Private>false</Private></Reference>""";
                everything.AppendLine(reference);
                if (engineOnly.Length == 0) engineOnly = reference + "\n";
            }

            string targets = $"""
                <Project>
                  <!-- Written by the Vertex editor: builds the game against the engine the editor runs on. -->
                  <PropertyGroup>
                    <VertexEngineFromEditor>true</VertexEngineFromEditor>
                  </PropertyGroup>
                  <Target Name="VertexUseEditorEngine" BeforeTargets="AssignProjectConfiguration;ResolveAssemblyReferences">
                    <ItemGroup>
                      <_VertexEngineProject Include="@(ProjectReference)" Condition="'%(Filename)' == 'Vertex'" />
                      <_VertexEngineMissing Include="@(_VertexEngineProject)" Condition="!Exists('%(FullPath)')" />
                      <ProjectReference Remove="@(_VertexEngineProject)" />
                      <!-- A game the editor set up refers to the engine's files where the editor was then: swapped for where it is now. -->
                      <_VertexEngineFiles Include="@(Reference)" Condition="'%(Reference.VertexEngine)' == 'true'" />
                      <Reference Remove="@(_VertexEngineFiles)" />
                    </ItemGroup>
                    <ItemGroup Condition="'@(_VertexEngineMissing)' != '' or '@(_VertexEngineFiles)' != ''">
                {everything}    </ItemGroup>
                    <ItemGroup Condition="'@(_VertexEngineMissing)' == '' and '@(_VertexEngineProject)' != ''">
                {engineOnly}    </ItemGroup>
                  </Target>
                  <!-- A game that uses the engine's package (one made from the download): just the engine itself is swapped,
                       as the package brings what the engine uses. -->
                  <Target Name="VertexUseEditorEngineForPackage" AfterTargets="ResolveLockFileReferences">
                    <ItemGroup>
                      <_VertexEnginePackage Include="@(Reference)" Condition="'%(Reference.NuGetPackageId)' == 'Vertex.Engine'" />
                      <Reference Remove="@(_VertexEnginePackage)" />
                    </ItemGroup>
                    <ItemGroup Condition="'@(_VertexEnginePackage)' != ''">
                {engineOnly}    </ItemGroup>
                  </Target>
                </Project>
                """;

            string folder = Path.Combine(Path.GetTempPath(), "VertexEditor");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "UseEditorEngine.targets");
            if (!File.Exists(path) || File.ReadAllText(path) != targets) File.WriteAllText(path, targets);
            return path;
        }

        /// <summary>
        /// A .csproj for a game's code, for a project that hasn't any yet: every .cs file in its folder (the scripts in
        /// Assets included), built against the engine the editor runs on. It builds outside the editor too, so a code
        /// editor knows the engine's types.
        /// </summary>
        public static string NewProjectFile(string name)
        {
            var references = new StringBuilder();
            foreach (string assembly in EngineAssemblies())
            {
                string reference = SecurityElement.Escape(Path.GetFileNameWithoutExtension(assembly));
                references.AppendLine($"""    <Reference Include="{reference}" VertexEngine="true"><HintPath>{SecurityElement.Escape(assembly)}</HintPath><Private>false</Private></Reference>""");
            }

            string rootNamespace = new string(name.Where(char.IsLetterOrDigit).ToArray());
            if (rootNamespace.Length == 0 || char.IsDigit(rootNamespace[0])) rootNamespace = "Game" + rootNamespace;

            return $"""
                <Project Sdk="Microsoft.NET.Sdk">

                  <!-- The game's code: every .cs file in this folder and under it. Made by the Vertex editor. -->
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <RootNamespace>{rootNamespace}</RootNamespace>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                  </PropertyGroup>

                  <!-- The engine, from the editor. Building in the editor uses whichever editor that is. -->
                  <ItemGroup>
                {references}  </ItemGroup>

                </Project>

                """;
        }

        /// <summary>The engine's assembly and every one it uses that came with the editor (Silk.NET, Jolt...), not the framework's.</summary>
        private static List<string> EngineAssemblies()
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<AssemblyName>([typeof(GameObject).Assembly.GetName()]);
            while (pending.TryDequeue(out AssemblyName? name))
            {
                if (name.Name == null || !seen.Add(name.Name)) continue;

                string file = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
                if (!File.Exists(file)) continue;
                found.Add(file);

                try
                {
                    foreach (AssemblyName used in ReferencesOf(file)) pending.Enqueue(used);
                }
                catch (Exception)
                {
                    // Not one that can be read: compiled against without looking further.
                }
            }

            return found;
        }

        private static AssemblyName[] ReferencesOf(string file)
        {
            using var stream = File.OpenRead(file);
            using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
            System.Reflection.Metadata.MetadataReader metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);
            return metadata.AssemblyReferences.Select(handle => metadata.GetAssemblyReference(handle).GetAssemblyName()).ToArray();
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
