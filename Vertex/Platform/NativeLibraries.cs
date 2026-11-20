using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.Core.Loader;

namespace Vertex.Platform
{
    /// <summary>
    /// Helps Silk.NET find the native libraries the engine comes with (models, shaders, the window, sound). It looks for
    /// them itself, and off Windows it goes wrong in two ways, put right here as soon as the engine is loaded:
    /// <list type="bullet">
    /// <item>On a Linux it doesn't know by name (Ubuntu among them), it doesn't look in the <c>runtimes/linux-x64</c>
    /// folder they come in, and finds none. It's pointed at the folder for this kind of computer.</item>
    /// <item>On macOS and Linux it asks for Assimp 5, while Windows gets Assimp 6 from the same package, and the two read
    /// models differently (a skinned mesh bends differently). Assimp 6 is used everywhere.</item>
    /// </list>
    /// </summary>
    internal static class NativeLibraries
    {
        // The newer Assimp, for the name Silk.NET asks for.
        private static readonly Dictionary<string, string> _newer = new()
        {
            ["libassimp.so.5"] = "libassimp.so.6",
            ["libassimp.5.dylib"] = "libassimp.6.dylib"
        };

#pragma warning disable CA2255 // The engine's own start-up: it must run before anything loads a library.
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Setup()
        {
            if (PathResolver.Default is DefaultPathResolver resolver) resolver.Resolvers.Insert(0, name => Find(name, RuntimeId));
        }

        /// <summary>
        /// Where library <paramref name="name"/> is for the kind of computer <paramref name="runtimeId"/> (see
        /// <see cref="RuntimeId"/>): the newer Assimp first. Only files that are there.
        /// </summary>
        internal static IEnumerable<string> Find(string name, string runtimeId)
        {
            if (Path.GetDirectoryName(name) is { Length: > 0 }) yield break;

            string folder = AppContext.BaseDirectory;
            foreach (string candidate in Names(name))
            {
                foreach (string path in (string[])[Path.Combine(folder, candidate), Path.Combine(folder, "runtimes", runtimeId, "native", candidate)])
                {
                    if (File.Exists(path)) yield return path;
                }
            }
        }

        /// <summary>The names a library may have, the one to use first: the newer Assimp, then the name asked for.</summary>
        internal static IEnumerable<string> Names(string name)
        {
            if (_newer.TryGetValue(name, out string? newer)) yield return newer;
            yield return name;
        }

        /// <summary>
        /// The kind of computer, as the <c>runtimes</c> folders are named: "linux-x64", "osx-arm64", "win-x64"... whatever
        /// the system's own name for itself ("ubuntu.24.04-x64").
        /// </summary>
        internal static string RuntimeId
        {
            get
            {
                string system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
                string architecture = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.X86 => "x86",
                    Architecture.Arm64 => "arm64",
                    Architecture.Arm => "arm",
                    var other => other.ToString().ToLowerInvariant()
                };
                return $"{system}-{architecture}";
            }
        }
    }
}
