using System.Runtime.InteropServices;

namespace Vertex.Platform
{
    /// <summary>
    /// The terminal a game or the editor was started from, if any. On Windows they're built as window programs, so
    /// opening them doesn't open a console window too; started from a terminal (<c>dotnet run</c>), they join that
    /// terminal, so what's logged still shows there.
    /// </summary>
    internal static class Terminal
    {
        private static bool _done;

        /// <summary>Joins the terminal the program was started from, if there is one and it hasn't one already. Call first thing.</summary>
        public static void Attach()
        {
            if (_done || !OperatingSystem.IsWindows()) return;
            _done = true;

            try
            {
                // Output already going somewhere (a console of its own, or a file or pipe) is left as it is.
                if (GetConsoleWindow() != IntPtr.Zero || Console.IsOutputRedirected) return;
                AttachConsole(ParentProcess);
            }
            catch (Exception)
            {
                // No terminal to join: messages still go to the in-game console and the editor's Console.
            }
        }

        private const int ParentProcess = -1;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
    }
}
