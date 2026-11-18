using System.Reflection;
using System.Runtime.Loader;

namespace Vertex
{
    /// <summary>
    /// The code loaded now: the engine, and a game's scripts. A game's code can be let go and loaded again once it's
    /// rebuilt (in the editor); the old copy lingers until it's cleaned up, but isn't counted here, so types are looked up
    /// in the new one.
    /// </summary>
    public static class LoadedCode
    {
        private static readonly HashSet<AssemblyLoadContext> _watched = new();
        private static readonly HashSet<AssemblyLoadContext> _gone = new();

        /// <summary>Raised when code is loaded or let go, so lists made from it can be made again.</summary>
        public static event Action? Changed;

        static LoadedCode()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, loaded) =>
            {
                Watch(loaded.LoadedAssembly);
                Changed?.Invoke();
            };
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) Watch(assembly);
        }

        /// <summary>Every assembly loaded and still in use (not one being let go).</summary>
        public static IEnumerable<Assembly> Assemblies
        {
            get
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Watch(assembly);
                    if (!IsGone(assembly)) yield return assembly;
                }
            }
        }

        /// <summary>Whether <paramref name="assembly"/> is from code that's been let go (a game's code before it was rebuilt).</summary>
        public static bool IsGone(Assembly assembly)
        {
            AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(assembly);
            lock (_gone) return context != null && _gone.Contains(context);
        }

        private static void Watch(Assembly assembly)
        {
            if (AssemblyLoadContext.GetLoadContext(assembly) is not { IsCollectible: true } context) return;

            lock (_gone)
            {
                if (!_watched.Add(context)) return;
            }

            context.Unloading += _ =>
            {
                lock (_gone)
                {
                    _gone.Add(context);
                    _watched.Remove(context);
                }

                Changed?.Invoke();
            };
        }
    }
}
