using Vertex.UI;

namespace Vertex
{
    public enum LogLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>One message written to the <see cref="Log"/>: how serious, what it says, and more (a stack trace) if there's more.</summary>
    public readonly record struct LogEntry(LogLevel Level, string Message, string? Details, DateTime Time);

    /// <summary>
    /// Messages about what's going on, for whoever's making the game: the engine's (which GPU it's drawing with, a texture
    /// it couldn't find) and the game's own. They show in the in-game console (` key), in an editor's Console, and in
    /// the terminal if the game was started from one.
    /// <code>
    /// Log.Info($"Spawned {count} enemies");
    /// Log.Warning("No spawn points: using the origin");
    /// Log.Error("Couldn't load the save", exception);
    /// </code>
    /// </summary>
    public static class Log
    {
        /// <summary>Raised for every message, on whichever thread wrote it.</summary>
        public static event Action<LogEntry>? Written;

        [ThreadStatic] private static bool _echoing;

        /// <summary>True while a message is being echoed to the terminal: something capturing the terminal's output already has it.</summary>
        public static bool Echoing => _echoing;

        public static void Info(string message) => Write(LogLevel.Info, message);

        public static void Warning(string message) => Write(LogLevel.Warning, message);

        /// <summary>An error; with the exception that caused it, its type and where it happened go in the details.</summary>
        public static void Error(string message, Exception? exception = null) =>
            Write(LogLevel.Error, message, exception?.ToString());

        public static void Write(LogLevel level, string message, string? details = null)
        {
            var entry = new LogEntry(level, message, details, DateTime.Now);
            Written?.Invoke(entry);
            DevConsole.AddLog(entry);

            _echoing = true;
            try
            {
                TextWriter terminal = level == LogLevel.Error ? Console.Error : Console.Out;
                string prefix = level switch { LogLevel.Warning => "[Vertex] Warning: ", LogLevel.Error => "[Vertex] Error: ", _ => "[Vertex] " };
                terminal.WriteLine(prefix + message);
                if (details != null && level == LogLevel.Error) terminal.WriteLine(details);
            }
            catch (Exception)
            {
                // No terminal to write to.
            }
            finally
            {
                _echoing = false;
            }
        }
    }
}
