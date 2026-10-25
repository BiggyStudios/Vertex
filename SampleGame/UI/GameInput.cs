using Vertex.UI;

namespace SampleGame
{
    /// <summary>Whether the keyboard and mouse belong to the player, or to a menu, the console or the chat box.</summary>
    public static class GameInput
    {
        /// <summary>Movement, shooting and game keys are off.</summary>
        public static bool Blocked => ChatBox.IsTyping || LookBlocked;

        /// <summary>The mouse is pointing at a menu or the console, so the view shouldn't turn and the cursor is free.</summary>
        public static bool LookBlocked => PauseMenu.IsOpen || ClassMenu.IsOpen || DevConsole.IsOpen;
    }
}
