using Silk.NET.Input;

namespace Vertex.Input
{
    public static class InputSystem
    {
        private static IKeyboard? _keyboard;

        public static void FindKeyboard(IInputContext input)
        {
            for(int i = 0; i < input.Keyboards.Count; i++)
            {
                _keyboard = input.Keyboards[i];
            }
        }

        public static bool GetKey(Key keyCode)
        {
            if (_keyboard!.IsKeyPressed(keyCode)) return true;

            else return false;
        }
    }
}