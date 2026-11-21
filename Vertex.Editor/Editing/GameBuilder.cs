using System.Reflection;
using System.Security;
using System.Text;

namespace Vertex.Editor.Editing
{
    /// <summary>The systems a game can be built for.</summary>
    public enum BuildPlatform
    {
        Windows,
        Linux,

        /// <summary>macOS on Apple Silicon.</summary>
        Mac
    }

    /// <summary>
    /// Builds a project into a game that runs on its own (File › Build Game), for players without the editor or .NET:
    /// a small program that starts it (<see cref="BuiltGame"/>), the game's code, the engine and everything it uses for
    /// that system, the Assets folder, and game.json with how it opens. The program is one file with all of that code (and
    /// .NET) packed inside, so the folder is just it, Assets and game.json. On Windows the program is the game's .exe
    /// (with its icon and details); on Linux it has its icon beside it; on macOS it all goes in a .app.
    /// <para>
    /// The program is made with <c>dotnet publish</c>, self-contained, so the .NET SDK has to be there to build (as it
    /// does for a game's code), and the first build for each system downloads that system's runtime.
    /// </para>
    /// </summary>
    public static class GameBuilder
    {
        /// <summary>What a build made: whether it worked, what dotnet said, where it went, and anything to know about it.</summary>
        public sealed record Result(bool Built, string Output, string Folder, IReadOnlyList<string> Notes);

        /// <summary>The program's own assembly: always this name, so it can't clash with the game's; its file is renamed after the game.</summary>
        internal const string PlayerAssembly = "VertexPlayer";

        public static string RuntimeOf(BuildPlatform platform) => platform switch
        {
            BuildPlatform.Windows => "win-x64",
            BuildPlatform.Linux => "linux-x64",
            _ => "osx-arm64"
        };

        public static string FolderNameOf(BuildPlatform platform) => platform switch
        {
            BuildPlatform.Windows => "Windows",
            BuildPlatform.Linux => "Linux",
            _ => "macOS"
        };

        /// <summary>Where a build for <paramref name="platform"/> goes: a folder of its own in the project's build folder.</summary>
        public static string FolderFor(Project project, BuildPlatform platform)
        {
            string root = project.Settings.BuildFolder.Trim().Length > 0 ? project.Settings.BuildFolder.Trim() : "Builds";
            return Path.GetFullPath(Path.Combine(project.Directory, root, FolderNameOf(platform)));
        }

        /// <summary>The game's name as a file name: what can't be in one taken out.</summary>
        public static string FileName(string name)
        {
            string safe = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '/' && c != '\\').ToArray()).Trim().TrimEnd('.');
            return safe.Length > 0 ? safe : "Game";
        }

        /// <summary>
        /// Builds <paramref name="project"/> for <paramref name="platform"/> into its build folder. Slow (tens of seconds),
        /// so run it off the main thread; <paramref name="progress"/> hears what it's doing.
        /// </summary>
        public static Result Build(Project project, BuildPlatform platform, Action<string>? progress = null)
        {
            ProjectSettings settings = project.Settings;
            string name = settings.Name.Trim().Length > 0 ? settings.Name.Trim() : project.Name;
            string rid = RuntimeOf(platform);
            string final = FolderFor(project, platform);
            var notes = new List<string>();

            if (Problem(project) is { } problem) return new Result(false, problem, final, notes);

            // Where the program's made: somewhere of its own, outside the project, so the game's code doesn't take it in.
            string work = WorkFolder(project, rid);
            string published = Path.Combine(work, "out");
            Directory.CreateDirectory(work);

            // The game's code, and what its assembly's called, for the program to find its Game class in.
            string? gameProject = project.GameProjectFile is { } code && File.Exists(code) ? code : null;
            string? gameAssembly = null;
            if (gameProject != null)
            {
                progress?.Invoke("Looking at the game's code…");
                (int found, string output) = GameCode.Run("dotnet", $"msbuild \"{gameProject}\" -nologo -getProperty:AssemblyName");
                gameAssembly = found == 0 ? output.Trim().Split('\n').Last().Trim() : Path.GetFileNameWithoutExtension(gameProject);
            }

            // The icon, as Windows keeps it in the program.
            string? icon = settings.Icon.Length > 0 ? Path.GetFullPath(Path.Combine(project.AssetsDirectory, settings.Icon)) : null;
            if (icon != null && !File.Exists(icon))
            {
                notes.Add($"The icon ({settings.Icon}) isn't in the Assets folder, so the game has the engine's.");
                icon = null;
            }

            string? windowsIcon = null;
            if (icon != null && platform == BuildPlatform.Windows)
            {
                windowsIcon = Path.Combine(work, "game.ico");
                GameIcons.WriteIco(windowsIcon, icon);
            }

            File.WriteAllText(Path.Combine(work, PlayerAssembly + ".csproj"), PlayerProject(settings, rid, gameProject, windowsIcon));
            File.WriteAllText(Path.Combine(work, "Program.cs"), PlayerProgram(gameAssembly));

            // Made against the editor's own engine, as the game's code is in the editor.
            progress?.Invoke($"Building for {FolderNameOf(platform)}…");
            if (Directory.Exists(published)) Directory.Delete(published, recursive: true);
            string engine = $"-p:CustomAfterMicrosoftCommonTargets=\"{GameCode.WriteEngineTargets()}\"";
            (int exitCode, string log) = GameCode.Run("dotnet", $"publish \"{Path.Combine(work, PlayerAssembly + ".csproj")}\" -c {Configuration(settings)} -o \"{published}\" -nologo -v:q -clp:NoSummary {engine}", work);
            if (exitCode != 0) return new Result(false, log, final, notes);

            progress?.Invoke("Copying the assets…");
            CopyAssets(project.AssetsDirectory, Path.Combine(published, "Assets"));
            File.WriteAllText(Path.Combine(published, BuiltGame.SettingsFile), settings.ToPlayerSettings().ToJson());
            Tidy(published, gameAssembly, platform, keepSymbols: settings.DebugBuild);

            // The program's named after the game.
            string fileName = FileName(name);
            string program = Path.Combine(published, PlayerAssembly + (platform == BuildPlatform.Windows ? ".exe" : ""));
            string renamed = Path.Combine(published, fileName + (platform == BuildPlatform.Windows ? ".exe" : ""));
            if (File.Exists(program) && !string.Equals(program, renamed, StringComparison.OrdinalIgnoreCase)) File.Move(program, renamed, overwrite: true);

            string made = published;
            if (platform == BuildPlatform.Linux)
            {
                if (icon != null) GameIcons.WritePng(Path.Combine(published, "icon.png"), icon);
                Executable(renamed);
                if (OperatingSystem.IsWindows()) notes.Add($"Built on Windows: on Linux, mark {fileName} as a program before it'll run (chmod +x \"{fileName}\").");
            }
            else if (platform == BuildPlatform.Mac)
            {
                made = MacBundle(work, published, fileName, name, settings, icon);
                if (!OperatingSystem.IsMacOS())
                {
                    notes.Add($"Built on {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}: a Mac only opens it once it's signed there. On the Mac, run: codesign --force --deep -s - \"{fileName}.app\" (and chmod +x \"{fileName}.app/Contents/MacOS/{fileName}\" if it was copied from Windows).");
                }
            }

            // Into the build folder, in place of the last build (but never over a folder that isn't one).
            progress?.Invoke("Finishing…");
            if (Directory.Exists(final))
            {
                if (!IsBuild(final)) return new Result(false, $"{final} has other files in it, so the build wasn't put there. Empty it, or choose another build folder.", final, notes);
                Directory.Delete(final, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            string destination = platform == BuildPlatform.Mac ? Path.Combine(final, fileName + ".app") : final;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            MoveFolder(made, destination);
            return new Result(true, log, final, notes);
        }

        /// <summary>What the game's code is built as: Debug for a debug build (unoptimised, so a debugger can step through it), otherwise Release.</summary>
        internal static string Configuration(ProjectSettings settings) => settings.DebugBuild ? "Debug" : "Release";

        /// <summary>Why the project can't be built yet, or null if it can: it needs somewhere to start.</summary>
        public static string? Problem(Project project)
        {
            bool hasCode = project.GameProjectFile is { } code && File.Exists(code);
            string scene = project.Settings.StartupScene;
            if (scene.Length == 0 && !hasCode) return "The game has nowhere to start: choose a Startup Scene in Project Settings.";
            if (scene.Length > 0 && !File.Exists(Path.Combine(project.AssetsDirectory, scene))) return $"The Startup Scene ({scene}) isn't in the Assets folder any more: choose it again in Project Settings.";
            return null;
        }

        private static string WorkFolder(Project project, string rid)
        {
            // One per project (by where it is) and system, kept between builds so the next is quicker.
            uint hash = 2166136261;
            foreach (char c in project.FilePath.ToLowerInvariant()) hash = (hash ^ c) * 16777619;
            return Path.Combine(Path.GetTempPath(), "VertexBuild", $"{FileName(project.Name)}-{hash:x8}", rid);
        }

        /// <summary>
        /// The program's project: self-contained for <paramref name="rid"/>, with the engine the editor runs on, the
        /// packages the engine uses (their native libraries come for that system), and the game's code.
        /// </summary>
        internal static string PlayerProject(ProjectSettings settings, string rid, string? gameProject, string? icon)
        {
            var packages = new StringBuilder();
            foreach ((string package, string version) in EnginePackages())
            {
                packages.AppendLine($"""    <PackageReference Include="{SecurityElement.Escape(package)}" Version="{SecurityElement.Escape(version)}" />""");
            }

            string engine = SecurityElement.Escape(typeof(GameObject).Assembly.Location);
            string code = gameProject != null ? $"""    <ProjectReference Include="{SecurityElement.Escape(gameProject)}" />""" + "\n" : "";
            string iconLine = icon != null ? $"    <ApplicationIcon>{SecurityElement.Escape(icon)}</ApplicationIcon>\n" : "";
            string name = SecurityElement.Escape(settings.Name.Trim().Length > 0 ? settings.Name.Trim() : "Game");

            return $"""
                <Project Sdk="Microsoft.NET.Sdk">

                  <!-- The program that starts a game the Vertex editor built. Made by the editor each build. -->
                  <PropertyGroup>
                    <OutputType>WinExe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>{PlayerAssembly}</AssemblyName>
                    <RootNamespace>VertexPlayer</RootNamespace>
                    <Nullable>enable</Nullable>
                    <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                    <SelfContained>true</SelfContained>
                    <!-- One program file with .NET, the engine, the game's code and the native libraries inside it, so the
                         folder players get is just that, the Assets and game.json. The native libraries are unpacked to a
                         cache folder the first time it runs. -->
                    <PublishSingleFile>true</PublishSingleFile>
                    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
                    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
                    <!-- The game's own code can be a program too (one made in code, with its own Main). -->
                    <ValidateExecutableReferencesMatchSelfContained>false</ValidateExecutableReferencesMatchSelfContained>
                    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
                    <DebugType>embedded</DebugType>
                    <GenerateDocumentationFile>false</GenerateDocumentationFile>
                    <Product>{name}</Product>
                    <AssemblyTitle>{name}</AssemblyTitle>
                    <Company>{SecurityElement.Escape(settings.Company.Trim())}</Company>
                    <Version>{NumericVersion(settings.Version)}</Version>
                    <InformationalVersion>{SecurityElement.Escape(settings.Version.Trim())}</InformationalVersion>
                {iconLine}  </PropertyGroup>

                  <ItemGroup>
                    <Reference Include="Vertex"><HintPath>{engine}</HintPath><Private>true</Private></Reference>
                {packages}{code}  </ItemGroup>

                </Project>

                """;
        }

        internal static string PlayerProgram(string? gameAssembly) => $"""
            // Starts the game: made by the Vertex editor when it built it.
            Vertex.BuiltGame.Run({(gameAssembly != null ? "\"" + gameAssembly.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : "null")});

            """;

        /// <summary>A version the program's details can take (numbers and dots): "0.1" stays, "1.2 beta" is 1.2.</summary>
        internal static string NumericVersion(string version)
        {
            var parts = new List<string>();
            foreach (string part in version.Trim().Split('.'))
            {
                string digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                if (digits.Length == 0) break;
                parts.Add(digits.TrimStart('0').Length > 0 ? digits.TrimStart('0') : "0");
                if (parts.Count == 4 || digits.Length != part.Length) break;
            }

            if (parts.Count == 0) return "1.0.0";
            while (parts.Count < 2) parts.Add("0");
            return string.Join('.', parts);
        }

        /// <summary>The packages the engine was built with, as it records them in itself (see Vertex.csproj).</summary>
        internal static List<(string Package, string Version)> EnginePackages()
        {
            const string prefix = "Vertex.Package.";
            return typeof(GameObject).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => a.Key.StartsWith(prefix, StringComparison.Ordinal) && !string.IsNullOrEmpty(a.Value))
                .Select(a => (a.Key[prefix.Length..], a.Value!))
                .ToList();
        }

        /// <summary>The project's Assets, without what's only for making the game: its code, and a code editor's files.</summary>
        internal static void CopyAssets(string from, string to)
        {
            Directory.CreateDirectory(to);
            if (!Directory.Exists(from)) return;
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(from, file);
                string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(p => p is "bin" or "obj" || p.StartsWith('.'))) continue;
                if (Path.GetExtension(file).ToLowerInvariant() is ".cs" or ".csproj") continue;

                string target = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        /// <summary>
        /// What the built game doesn't use: the engine's doc comments, and from a game whose code is a program of its own,
        /// that program's starter and its settings (the built game starts through its own).
        /// </summary>
        private static void Tidy(string folder, string? gameAssembly, BuildPlatform platform, bool keepSymbols)
        {
            var unused = new List<string> { "Vertex.xml" };
            if (gameAssembly != null && gameAssembly != PlayerAssembly)
            {
                unused.Add(gameAssembly + (platform == BuildPlatform.Windows ? ".exe" : ""));
                unused.Add(gameAssembly + ".deps.json");
                unused.Add(gameAssembly + ".runtimeconfig.json");
            }

            foreach (string file in unused)
            {
                string path = Path.Combine(folder, file);
                if (File.Exists(path)) File.Delete(path);
            }

            // Debugging symbols that didn't go into the program (the engine's and the game's): only for whoever made it,
            // though a debug build keeps them, so errors say which line they were on.
            if (keepSymbols) return;
            foreach (string symbols in Directory.EnumerateFiles(folder, "*.pdb")) File.Delete(symbols);
        }

        /// <summary>Whether a folder's a build (made by this), or empty: what a new build can go in place of.</summary>
        private static bool IsBuild(string folder) =>
            !Directory.EnumerateFileSystemEntries(folder).Any()
            || File.Exists(Path.Combine(folder, BuiltGame.SettingsFile))
            || Directory.EnumerateDirectories(folder, "*.app").Any(app => File.Exists(Path.Combine(app, "Contents", "MacOS", BuiltGame.SettingsFile)));


        /// <summary>Moves a folder, copying it if it's going to another drive (the temporary folder's often on another).</summary>
        private static void MoveFolder(string from, string to)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (IOException)
            {
                // Another drive: copied, then the original's taken away.
            }

            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }

            Directory.Delete(from, recursive: true);
        }
        private static void Executable(string file)
        {
            if (OperatingSystem.IsWindows() || !File.Exists(file)) return;
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        /// <summary>A macOS app: the game's files in Contents/MacOS, its icon in Contents/Resources, and an Info.plist saying what it is.</summary>
        private static string MacBundle(string work, string published, string fileName, string name, ProjectSettings settings, string? icon)
        {
            string app = Path.Combine(work, fileName + ".app");
            if (Directory.Exists(app)) Directory.Delete(app, recursive: true);
            string contents = Path.Combine(app, "Contents");
            Directory.CreateDirectory(Path.Combine(contents, "Resources"));
            Directory.Move(published, Path.Combine(contents, "MacOS"));
            Executable(Path.Combine(contents, "MacOS", fileName));
            if (icon != null) GameIcons.WriteIcns(Path.Combine(contents, "Resources", "icon.icns"), icon);

            string Id(string text) => new string(text.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()) is { Length: > 0 } id ? id : "game";
            string version = SecurityElement.Escape(settings.Version.Trim().Length > 0 ? settings.Version.Trim() : "1.0");
            string plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>CFBundleName</key><string>{SecurityElement.Escape(name)}</string>
                  <key>CFBundleDisplayName</key><string>{SecurityElement.Escape(name)}</string>
                  <key>CFBundleIdentifier</key><string>com.{Id(settings.Company.Trim().Length > 0 ? settings.Company : "vertex")}.{Id(name)}</string>
                  <key>CFBundleVersion</key><string>{version}</string>
                  <key>CFBundleShortVersionString</key><string>{version}</string>
                  <key>CFBundleExecutable</key><string>{SecurityElement.Escape(fileName)}</string>
                  <key>CFBundlePackageType</key><string>APPL</string>
                  {(icon != null ? "<key>CFBundleIconFile</key><string>icon.icns</string>" : "")}
                  <key>NSHighResolutionCapable</key><true/>
                  <key>LSMinimumSystemVersion</key><string>12.0</string>
                </dict>
                </plist>

                """;
            File.WriteAllText(Path.Combine(contents, "Info.plist"), plist);
            return app;
        }
    }
}
