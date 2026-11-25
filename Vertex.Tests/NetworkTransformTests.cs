using System.Numerics;

using Vertex.Networking;

namespace Vertex.Tests
{
    /// <summary>Another player's copy of something moving: it glides along between updates instead of lurching.</summary>
    public class NetworkTransformTests : EngineTest
    {
        [Fact]
        public void A_copy_of_something_moving_steadily_moves_steadily_between_updates()
        {
            var copy = new GameObject("Copy").AddComponent<NetworkTransform>();
            copy.SendRate = 15f;
            const float speed = 10f, frame = 1f / 60f;
            var steps = new List<float>();
            Vector3 last = Vector3.Zero;
            for (int i = 0; i < 120; i++)
            {
                // An update every fourth frame, as the owner sends 15 a second.
                if (i % 4 == 0) copy.Receive(new Vector3(speed * i * frame, 0f, 0f), Quaternion.Identity);
                copy.Follow(frame);
                Vector3 now = copy.Transform.WorldPosition;
                if (i >= 60) steps.Add(now.X - last.X);
                last = now;
            }

            // Gliding at about its speed every frame; it used to rush forward after each update and nearly stop before the next.
            Assert.True(steps.Min() > steps.Max() * 0.6f, $"Steps from {steps.Min()} to {steps.Max()}");
            Assert.InRange(steps.Average() / frame, speed * 0.9f, speed * 1.1f);
            Assert.InRange(copy.Transform.WorldPosition.X, speed * 119f * frame - 1.5f, speed * 119f * frame + 0.5f);
        }

        [Fact]
        public void A_copy_goes_straight_to_where_something_jumped_to_and_doesnt_carry_on_after()
        {
            var copy = new GameObject("Copy").AddComponent<NetworkTransform>();
            copy.Receive(Vector3.Zero, Quaternion.Identity);
            copy.Follow(0.05f);
            copy.Receive(new Vector3(80f, 0f, 0f), Quaternion.Identity);
            Assert.Equal(80f, copy.Transform.WorldPosition.X);

            for (int i = 0; i < 30; i++) copy.Follow(1f / 60f);
            Assert.Equal(80f, copy.Transform.WorldPosition.X, 0.01f);
        }
    }
}
