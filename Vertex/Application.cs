namespace Vertex
{
    /// <summary>The running program as a whole.</summary>
    public static class Application
    {
        /// <summary>Raised by <see cref="Quit"/>. Whatever hosts the game (its window, an editor) closes it.</summary>
        internal static event Action? QuitRequested;

        /// <summary>Closes the game at the end of this frame. In an editor's play mode, it stops playing instead.</summary>
        public static void Quit() => QuitRequested?.Invoke();
    }
}
