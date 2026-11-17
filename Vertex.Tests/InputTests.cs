using Vertex.Input;

namespace Vertex.Tests
{
    public class InputTests : EngineTest
    {
        [Fact]
        public void A_press_is_down_for_one_frame_and_held_until_released()
        {
            InputSystem.PressKey(Key.W);
            Assert.True(InputSystem.GetKeyDown(Key.W));
            Assert.True(InputSystem.GetKey(Key.W));

            InputSystem.EndFrame();
            Assert.False(InputSystem.GetKeyDown(Key.W));
            Assert.True(InputSystem.GetKey(Key.W));

            InputSystem.ReleaseKey(Key.W);
            Assert.True(InputSystem.GetKeyUp(Key.W));
            Assert.False(InputSystem.GetKey(Key.W));
        }

        [Fact]
        public void Key_repeat_while_held_is_not_a_new_press()
        {
            InputSystem.PressKey(Key.Space);
            InputSystem.EndFrame();
            InputSystem.PressKey(Key.Space);

            Assert.False(InputSystem.GetKeyDown(Key.Space));
        }

        [Fact]
        public void A_consumed_press_is_not_seen_again()
        {
            InputSystem.PressKey(Key.Escape);
            InputSystem.Consume(Key.Escape);

            Assert.False(InputSystem.GetKeyDown(Key.Escape));
            Assert.True(InputSystem.GetKey(Key.Escape));
        }

        [Fact]
        public void Losing_focus_lets_go_of_everything()
        {
            InputSystem.PressKey(Key.A);
            InputSystem.PressButton(MouseButton.Left);
            InputSystem.EndFrame();

            InputSystem.ReleaseAll();

            Assert.False(InputSystem.GetKey(Key.A));
            Assert.True(InputSystem.GetKeyUp(Key.A));
            Assert.False(InputSystem.GetMouseButton(MouseButton.Left));
            Assert.True(InputSystem.GetMouseButtonUp(MouseButton.Left));
        }

        [Fact]
        public void Mouse_movement_adds_up_over_a_frame()
        {
            InputSystem.MoveMouse(new(100f, 100f));
            InputSystem.MoveMouse(new(110f, 95f));
            InputSystem.MoveMouse(new(115f, 90f));

            Assert.Equal(new(15f, -10f), InputSystem.MouseDelta);
            Assert.Equal(new(115f, 90f), InputSystem.MousePosition);

            InputSystem.EndFrame();
            Assert.Equal(System.Numerics.Vector2.Zero, InputSystem.MouseDelta);
        }

        [Fact]
        public void Simulated_input_reaches_the_game_like_real_input()
        {
            var game = new EmptyGame();
            var loop = new GameLoop(game);
            loop.Load();

            SimulatedInput.PressButton(MouseButton.Left);
            SimulatedInput.Type("hi");
            Assert.True(InputSystem.GetMouseButtonDown(MouseButton.Left));
            Assert.Equal("hi", InputSystem.TypedText);

            RunFrames(loop, 1);
            Assert.False(InputSystem.GetMouseButtonDown(MouseButton.Left));
            Assert.True(InputSystem.GetMouseButton(MouseButton.Left));

            SimulatedInput.Reset();
            Assert.False(InputSystem.GetMouseButton(MouseButton.Left));
        }
    }
}
