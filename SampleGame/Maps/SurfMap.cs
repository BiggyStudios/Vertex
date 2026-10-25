using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// A practice surf course beside the sandbox: a start platform, three ramps with gaps between them and an end
    /// platform. Falling off lands in the water, which sends you back to the start. Pads on both platforms go back to
    /// the sandbox, so you can leave any time without finishing the course.
    /// <para>
    /// The ramps are steeper than the player can stand on, so standing on one slides you down. To surf, hold the strafe key
    /// towards the ramp (A on the left side, D on the right) and keep the mouse turned the same way, so the slope holds you
    /// up while gravity speeds you along it.
    /// </para>
    /// </summary>
    public static class SurfMap
    {
        public static Vector3 Start { get; } = new(60f, 40f, 6f);
        public const float StartYaw = 0f;

        public static Zone Zone { get; } = new("Surf course", Surf: true, [new SpawnPoint(Start, StartYaw)]);

        /// <param name="exit">Where the pads on the start and end platforms send the player, back in the sandbox.</param>
        public static void Build(SpawnPoint exit)
        {
            Texture rampTexture = MapBuilder.Checker(new Color(0.55f, 0.65f, 0.8f), new Color(0.42f, 0.5f, 0.66f));
            Texture platformTexture = MapBuilder.Checker(new Color(0.5f, 0.7f, 0.45f), new Color(0.38f, 0.56f, 0.35f));

            MapBuilder.Block("Surf Start", new Vector3(60f, 39.5f, 4f), new Vector3(12f, 1f, 12f), Color.White, platformTexture);
            MapBuilder.TeleportPad("Surf Start Exit", new Vector3(64.5f, 40f, 8.5f), exit, Zone.Sandbox, new Color(0.2f, 0.9f, 1f));

            // Each ramp starts a little lower than the last one ended, shifted sideways, with a 12-unit gap to fly across.
            MapBuilder.Ramp("Surf Ramp 1", ridgeStart: new Vector3(60f, 37f, -6f), length: 80f, drop: 8f, rampTexture);
            MapBuilder.Ramp("Surf Ramp 2", ridgeStart: new Vector3(66f, 28f, -98f), length: 80f, drop: 8f, rampTexture);
            MapBuilder.Ramp("Surf Ramp 3", ridgeStart: new Vector3(60f, 15f, -190f), length: 80f, drop: 6f, rampTexture);

            MapBuilder.Block("Surf End", new Vector3(62f, -0.5f, -295f), new Vector3(30f, 1f, 30f), Color.White, platformTexture);
            MapBuilder.TeleportPad("Surf Exit", new Vector3(62f, 0f, -302f), exit, Zone.Sandbox, new Color(0.2f, 0.9f, 1f));

            // Well below everything, and kept clear of the sandbox at x -15 to 15.
            MapBuilder.Water("Surf Water", new Vector3(65f, -10f, -150f), new Vector2(60f, 340f));
            Teleporter reset = MapBuilder.Trigger("Surf Reset", new Vector3(65f, -12f, -150f), new Vector3(60f, 4f, 340f)).AddComponent<Teleporter>();
            reset.Destination = Start;
            reset.DestinationYaw = StartYaw;
            reset.KeepVelocity = false;
        }
    }
}
