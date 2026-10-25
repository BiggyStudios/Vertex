using System.Numerics;

using Vertex;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>Destroys its GameObject after <see cref="Seconds"/>.</summary>
    public class Lifetime : Component
    {
        public float Seconds { get; set; } = 1f;

        protected override void Update()
        {
            Seconds -= Time.DeltaTime;
            if (Seconds <= 0f) GameObject.Destroy();
        }
    }

    /// <summary>
    /// Quick visual effects: tracers, bullet impacts and landing dust. The particle effects share one emitter each, made
    /// the first time they're needed, so a shotgun blast doesn't make dozens of objects.
    /// </summary>
    public static class Effects
    {
        private static readonly Color _tracerColor = new(1f, 0.85f, 0.4f);

        private static ParticleSystem? _sparks;
        private static ParticleSystem? _flashes;
        private static ParticleSystem? _dust;
        private static ParticleSystem? _blood;
        private static ParticleSystem? _landing;
        private static ParticleSystem? _fireballs;
        private static ParticleSystem? _blastSmoke;
        private static ParticleSystem? _debris;
        private static ParticleSystem? _blastFlashes;

        /// <summary>A bright streak along a bullet's path, gone after a couple of frames.</summary>
        public static void Tracer(Vector3 from, Vector3 to)
        {
            float length = Vector3.Distance(from, to);
            if (length < 0.05f) return;

            var tracer = new GameObject("Tracer");
            tracer.Transform.Position = (from + to) * 0.5f;
            tracer.Transform.LookAt(to);
            tracer.Transform.Scale = new Vector3(0.012f, 0.012f, length);

            MeshRenderer renderer = tracer.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Cube;
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = _tracerColor;
            renderer.Material.Emission = _tracerColor;
            renderer.Material.EmissionStrength = 1.5f;
            renderer.CastShadows = false;
            tracer.AddComponent<Lifetime>().Seconds = 0.05f;
        }

        /// <summary>
        /// Where a bullet hit something: sparks and a puff of dust off a wall or prop, or a spray of blood off another
        /// player. Hits on your own player show nothing, since you'd only see the inside of it.
        /// </summary>
        public static void Impact(RaycastHit hit)
        {
            if (hit.GameObject is Player) return;

            Vector3 normal = hit.Normal;
            Vector3 point = hit.Point + normal * 0.02f;

            if (hit.GameObject is RemotePlayer)
            {
                Get(ref _blood, CreateBlood).Emit(10, point, normal);
                return;
            }

            Get(ref _flashes, CreateFlashes).Emit(1, point, normal);
            Get(ref _sparks, CreateSparks).Emit(10, point, normal);
            Get(ref _dust, CreateDust).Emit(4, point, normal);
        }

        /// <summary>A rocket going off: a bright flash, a ball of fire, burning debris thrown out, and a cloud of smoke.</summary>
        public static void Explosion(Vector3 position)
        {
            Get(ref _blastFlashes, CreateBlastFlashes).Emit(1, position, Vector3.UnitY);
            Get(ref _fireballs, CreateFireballs).Emit(28, position, Vector3.UnitY);
            Get(ref _debris, CreateDebris).Emit(26, position, Vector3.UnitY);
            Get(ref _blastSmoke, CreateBlastSmoke).Emit(14, position, Vector3.UnitY);
        }

        /// <summary>Dust kicked up around the feet on landing, more the harder the landing.</summary>
        public static void Landing(Vector3 feet, float fallSpeed)
        {
            // A normal jump lands at about 6 m/s; a bounce pad or a long drop at 15 or more.
            float strength = Math.Clamp((fallSpeed - 3f) / 12f, 0f, 1f);
            if (strength <= 0f) return;

            int count = (int)MathF.Round(4f + strength * 20f);
            Get(ref _landing, CreateLanding).Emit(count, feet + new Vector3(0f, 0.08f, 0f), Vector3.UnitY);
        }

        private static ParticleSystem Get(ref ParticleSystem? system, Func<ParticleSystem> create)
        {
            if (system == null || system.GameObject.IsDestroyed) system = create();
            return system;
        }

        private static ParticleSystem CreateEmitter(string name)
        {
            var emitter = new GameObject(name);
            return emitter.AddComponent<ParticleSystem>();
        }

        // Hot, fast and short: they streak out, fall and fade from yellow to deep orange.
        private static ParticleSystem CreateSparks()
        {
            ParticleSystem sparks = CreateEmitter("Impact Sparks");
            sparks.Lifetime = new FloatRange(0.15f, 0.4f);
            sparks.Speed = new FloatRange(3f, 9f);
            sparks.Spread = 60f;
            sparks.Size = new FloatRange(0.03f, 0.05f);
            sparks.EndSize = 0.5f;
            sparks.StartColor = new Color(1f, 0.72f, 0.28f);
            sparks.EndColor = new Color(1f, 0.28f, 0f, 0f);
            sparks.Glow = 4f;
            sparks.Stretch = 0.035f;
            sparks.Gravity = 9.8f;
            sparks.Drag = 2f;
            sparks.MaxParticles = 600;
            return sparks;
        }

        // A bright pop right where it hit, gone in a few frames.
        private static ParticleSystem CreateFlashes()
        {
            ParticleSystem flashes = CreateEmitter("Impact Flashes");
            flashes.Lifetime = 0.07f;
            flashes.Speed = 0f;
            flashes.Size = new FloatRange(0.25f, 0.35f);
            flashes.EndSize = 0.4f;
            flashes.StartColor = new Color(1f, 0.9f, 0.6f);
            flashes.EndColor = new Color(1f, 0.6f, 0.2f, 0f);
            flashes.Glow = 3f;
            flashes.MaxParticles = 100;
            return flashes;
        }

        // Grit knocked off the surface: it drifts out, slows and spreads into a haze.
        private static ParticleSystem CreateDust()
        {
            ParticleSystem dust = CreateEmitter("Impact Dust");
            dust.Blend = ParticleBlend.Alpha;
            dust.Lifetime = new FloatRange(0.45f, 0.8f);
            dust.Speed = new FloatRange(0.4f, 1.4f);
            dust.Spread = 35f;
            dust.Size = new FloatRange(0.14f, 0.22f);
            dust.EndSize = 3f;
            dust.StartColor = new Color(0.42f, 0.39f, 0.35f, 0.7f);
            dust.EndColor = new Color(0.5f, 0.47f, 0.43f, 0f);
            dust.Gravity = -0.3f;
            dust.Drag = 3f;
            dust.MaxParticles = 300;
            return dust;
        }

        // A dark red mist with a few heavier drops that fall.
        private static ParticleSystem CreateBlood()
        {
            ParticleSystem blood = CreateEmitter("Blood");
            blood.Blend = ParticleBlend.Alpha;
            blood.Lifetime = new FloatRange(0.25f, 0.55f);
            blood.Speed = new FloatRange(0.8f, 3.5f);
            blood.Spread = 45f;
            blood.Size = new FloatRange(0.05f, 0.12f);
            blood.EndSize = 2.2f;
            blood.StartColor = new Color(0.55f, 0.03f, 0.03f, 0.85f);
            blood.EndColor = new Color(0.35f, 0.02f, 0.02f, 0f);
            blood.Gravity = 6f;
            blood.Drag = 2.5f;
            blood.MaxParticles = 400;
            return blood;
        }

        private static ParticleSystem CreateBlastFlashes()
        {
            ParticleSystem flashes = CreateEmitter("Blast Flashes");
            flashes.Lifetime = 0.12f;
            flashes.Speed = 0f;
            flashes.Size = new FloatRange(2.6f, 3.2f);
            flashes.EndSize = 1.3f;
            flashes.StartColor = new Color(1f, 0.85f, 0.55f);
            flashes.EndColor = new Color(1f, 0.5f, 0.15f, 0f);
            flashes.Glow = 2.5f;
            flashes.MaxParticles = 20;
            return flashes;
        }

        // Billowing out fast, then slowing, cooling from yellow through orange to a dark red as it fades.
        private static ParticleSystem CreateFireballs()
        {
            ParticleSystem fire = CreateEmitter("Fireballs");
            fire.Lifetime = new FloatRange(0.25f, 0.55f);
            fire.Speed = new FloatRange(2f, 7f);
            fire.Spread = 180f;
            fire.Area = new Vector3(0.6f);
            fire.Size = new FloatRange(0.7f, 1.2f);
            fire.EndSize = 2.4f;
            fire.StartColor = new Color(1f, 0.75f, 0.3f);
            fire.EndColor = new Color(0.8f, 0.15f, 0.02f, 0f);
            fire.Glow = 2.2f;
            fire.Drag = 5f;
            fire.Gravity = -2f;
            fire.MaxParticles = 400;
            return fire;
        }

        // Burning bits flung out in every direction, streaking and falling.
        private static ParticleSystem CreateDebris()
        {
            ParticleSystem debris = CreateEmitter("Blast Debris");
            debris.Lifetime = new FloatRange(0.4f, 1f);
            debris.Speed = new FloatRange(6f, 16f);
            debris.Spread = 180f;
            debris.Size = new FloatRange(0.05f, 0.09f);
            debris.EndSize = 0.4f;
            debris.StartColor = new Color(1f, 0.75f, 0.35f);
            debris.EndColor = new Color(1f, 0.25f, 0f, 0f);
            debris.Glow = 4f;
            debris.Stretch = 0.03f;
            debris.Gravity = 9.8f;
            debris.Drag = 1f;
            debris.MaxParticles = 400;
            return debris;
        }

        // Dark smoke that rolls out, rises and spreads into a big haze.
        private static ParticleSystem CreateBlastSmoke()
        {
            ParticleSystem smoke = CreateEmitter("Blast Smoke");
            smoke.Blend = ParticleBlend.Alpha;
            smoke.Lifetime = new FloatRange(1.2f, 2.2f);
            smoke.Speed = new FloatRange(1f, 4f);
            smoke.Spread = 180f;
            smoke.Area = new Vector3(0.8f);
            smoke.Size = new FloatRange(0.9f, 1.4f);
            smoke.EndSize = 3.5f;
            smoke.StartColor = new Color(0.25f, 0.23f, 0.22f, 0.75f);
            smoke.EndColor = new Color(0.45f, 0.44f, 0.43f, 0f);
            smoke.Gravity = -1.2f;
            smoke.Drag = 2.5f;
            smoke.MaxParticles = 300;
            return smoke;
        }

        // Low and wide: kicked out sideways along the ground, slowing quickly and spreading out.
        private static ParticleSystem CreateLanding()
        {
            ParticleSystem landing = CreateEmitter("Landing Dust");
            landing.Blend = ParticleBlend.Alpha;
            landing.Lifetime = new FloatRange(0.5f, 0.9f);
            landing.Speed = new FloatRange(3f, 8f);
            landing.Direction = Vector3.UnitY;
            landing.Spread = 88f;
            landing.Area = new Vector3(0.5f, 0f, 0.5f);
            landing.Size = new FloatRange(0.2f, 0.34f);
            landing.EndSize = 2.8f;
            landing.StartColor = new Color(0.5f, 0.47f, 0.42f, 0.6f);
            landing.EndColor = new Color(0.6f, 0.57f, 0.52f, 0f);
            landing.Gravity = 2.5f;
            landing.Drag = 4f;
            landing.MaxParticles = 300;
            return landing;
        }
    }
}
