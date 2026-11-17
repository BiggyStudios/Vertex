using System.Numerics;

namespace Vertex.Input
{
    /// <summary>
    /// Presses keys and buttons as if the player had: for automated tests, bots and replays. Everything here reaches the
    /// game through <see cref="InputSystem"/> exactly like real input, including GetKeyDown on the next frame.
    /// </summary>
    public static class SimulatedInput
    {
        public static void PressKey(Key key) => InputSystem.PressKey(key);
        public static void ReleaseKey(Key key) => InputSystem.ReleaseKey(key);

        public static void PressButton(MouseButton button) => InputSystem.PressButton(button);
        public static void ReleaseButton(MouseButton button) => InputSystem.ReleaseButton(button);

        /// <summary>Moves the mouse to <paramref name="position"/> in window pixels; the move shows in MouseDelta.</summary>
        public static void MoveMouse(Vector2 position) => InputSystem.MoveMouse(position);

        public static void Scroll(float amount) => InputSystem.Scroll(amount);

        public static void Type(string text)
        {
            foreach (char character in text) InputSystem.TypeCharacter(character);
        }

        /// <summary>Lets go of every key and button that's held.</summary>
        public static void ReleaseAll() => InputSystem.ReleaseAll();

        /// <summary>Forgets all input, as if the game had just started: nothing held, no mouse position, cursor unlocked.</summary>
        public static void Reset() => InputSystem.Reset();
    }
}
