namespace SampleGame
{
    /// <summary>
    /// Movement settings shared by everyone in a game, named and measured like Source's cvars (in units, where a unit
    /// is an inch). Only the host can change them, and every change is sent to all players; anyone joining later is
    /// sent the current values. Playing alone, you're the host.
    /// </summary>
    internal static class ServerVariables
    {
        private const float _units = 0.0254f;

        internal sealed record Variable(string Name, string Description, Func<Player, float> Get, Action<Player, float> Set);

        public static readonly Variable[] All =
        [
            new("sv_maxspeed", "Top running speed, in units per second.", p => p.MaxSpeed / _units, (p, v) => p.MaxSpeed = MathF.Max(v, 0f) * _units),
            new("sv_accelerate", "How quickly you reach full speed on the ground.", p => p.Acceleration, (p, v) => p.Acceleration = MathF.Max(v, 0f)),
            new("sv_airaccelerate", "Air control. High (100, as on bhop servers) lets you turn hard without losing speed; CS's default is 10-12.", p => p.AirAcceleration, (p, v) => p.AirAcceleration = MathF.Max(v, 0f)),
            new("sv_air_max_wishspeed", "Air speed cap, in units per second. Higher builds speed faster when air strafing (Source: 30).", p => p.AirSpeedCap / _units, (p, v) => p.AirSpeedCap = MathF.Max(v, 0f) * _units),
            new("sv_surf_airaccelerate", "Air control on the surf map (surf servers usually use 150).", p => p.SurfAirAcceleration, (p, v) => p.SurfAirAcceleration = MathF.Max(v, 0f)),
            new("sv_friction", "How quickly you slow down on the ground.", p => p.Friction, (p, v) => p.Friction = MathF.Max(v, 0f)),
            new("sv_stopspeed", "Below this speed, friction stops you quickly. In units per second.", p => p.StopSpeed / _units, (p, v) => p.StopSpeed = MathF.Max(v, 0f) * _units),
            new("sv_gravity", "Gravity, in units per second squared.", p => p.Gravity / _units, (p, v) => p.Gravity = MathF.Max(v, 0f) * _units),
            new("sv_jumpheight", "How high a jump goes, in units.", p => p.JumpHeight / _units, (p, v) => p.JumpHeight = MathF.Max(v, 0f) * _units),
            new("sv_autobunnyhopping", "1: holding jump jumps again on landing. 0: each jump needs a fresh press.", p => p.AutoBunnyhop ? 1f : 0f, (p, v) => p.AutoBunnyhop = v != 0f),
            new("sv_cheats", "1 allows cheat commands, like noclip. Setting it back to 0 takes them away.", p => p.CheatsAllowed ? 1f : 0f, (p, v) => p.CheatsAllowed = v != 0f)
        ];

        public static Variable? Find(string name) => All.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
