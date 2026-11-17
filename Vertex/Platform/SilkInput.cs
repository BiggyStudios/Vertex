using Silk.NET.Input;
using Silk.NET.Windowing;

using Vertex.Input;

using Key = Vertex.Input.Key;
using MouseButton = Vertex.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace Vertex.Platform
{
    /// <summary>Feeds a Silk.NET window's keyboard and mouse into <see cref="InputSystem"/>.</summary>
    internal sealed class SilkInput : IInputHost
    {
        private readonly IKeyboard? _keyboard;
        private readonly IMouse? _mouse;

        public SilkInput(IWindow window)
        {
            IInputContext input = window.CreateInput();
            _keyboard = input.Keyboards.FirstOrDefault();
            _mouse = input.Mice.FirstOrDefault();

            if (_keyboard != null)
            {
                _keyboard.KeyDown += (_, key, _) => Press(key);
                _keyboard.KeyUp += (_, key, _) => Release(key);
                _keyboard.KeyChar += (_, character) => InputSystem.TypeCharacter(character);
            }

            if (_mouse != null)
            {
                _mouse.MouseDown += (_, button) => Press(button);
                _mouse.MouseUp += (_, button) => Release(button);
                _mouse.Scroll += (_, wheel) => InputSystem.Scroll(wheel.Y);
                _mouse.MouseMove += (_, position) => InputSystem.MoveMouse(position);
                InputSystem.MoveMouse(_mouse.Position);
            }

            InputSystem.SetPlatform(this);
        }

        public string ClipboardText
        {
            get => _keyboard?.ClipboardText ?? "";
            set
            {
                if (_keyboard != null) _keyboard.ClipboardText = value;
            }
        }

        public void SetCursorLocked(bool locked)
        {
            if (_mouse == null) return;

            _mouse.Cursor.CursorMode = !locked ? CursorMode.Normal
                : _mouse.Cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw
                : CursorMode.Disabled;
        }

        // Vertex's keys and buttons have the same numbers as Silk's (both are GLFW's), so converting is a cast.
        private static void Press(SilkKey key)
        {
            if (Convert(key) is { } converted) InputSystem.PressKey(converted);
        }

        private static void Release(SilkKey key)
        {
            if (Convert(key) is { } converted) InputSystem.ReleaseKey(converted);
        }

        private static void Press(SilkMouseButton button)
        {
            if (Convert(button) is { } converted) InputSystem.PressButton(converted);
        }

        private static void Release(SilkMouseButton button)
        {
            if (Convert(button) is { } converted) InputSystem.ReleaseButton(converted);
        }

        private static Key? Convert(SilkKey key)
        {
            var converted = (Key)(int)key;
            return converted != Key.Unknown && Enum.IsDefined(converted) ? converted : null;
        }

        private static MouseButton? Convert(SilkMouseButton button)
        {
            var converted = (MouseButton)(int)button;
            return converted != MouseButton.Unknown && Enum.IsDefined(converted) ? converted : null;
        }
    }
}
