using Vertex.Audio;

namespace SampleGame
{
    /// <summary>Sent over the network to say which weapon a player is holding, or killed someone with.</summary>
    public enum WeaponId : byte
    {
        Rifle,
        Sniper,
        Shotgun,
        Knife,
        RocketLauncher,
        Deagle
    }

    /// <summary>
    /// Everything that makes one weapon different from another. Speeds are in Source's units per second: as in CS, the
    /// weapon you hold caps how fast you can run, and sv_maxspeed caps everything. Damage is per pellet; most weapons
    /// fire one. Range is in metres, spread in degrees, impact force (how hard a shot pushes rigidbodies) in
    /// newton-seconds, and knockback (how hard firing pushes you, away from where you aim) in metres per second.
    /// A melee weapon swings instead of firing: left mouse uses the damage, rate and range above, right mouse stabs.
    /// A projectile weapon fires something that flies (<see cref="ProjectileSpeed"/>, in units per second) and explodes,
    /// hurting everyone within <see cref="BlastRadius"/> metres by up to its body damage, less further out.
    /// <see cref="ViewKick"/> throws your view up by that many degrees each shot, before it settles back: a heavy kick.
    /// </summary>
    public sealed record Weapon(
        WeaponId Id,
        string Name,
        int BodyDamage,
        int HeadDamage,
        int Pellets,
        float SecondsPerShot,
        bool Automatic,
        float Range,
        float ImpactForce,
        float MoveSpeed,
        float Spread,
        float ScopeZoom,
        float ScopedMoveSpeed,
        float UnscopedSpread,
        float Knockback,
        AudioClip Sound,
        float SoundVolume,
        bool Melee = false,
        int StabDamage = 0,
        float SecondsPerStab = 0f,
        float StabRange = 0f,
        float ProjectileSpeed = 0f,
        float BlastRadius = 0f,
        float ViewKick = 0f)
    {
        /// <summary>
        /// A Desert Eagle: one shot at a time, hard-hitting and slow to fire, with a big kick that throws the view up. A
        /// headshot kills outright; it takes two in the body. Firing again before the kick settles, the aim is still high.
        /// </summary>
        public static Weapon Deagle { get; } = new(WeaponId.Deagle, "Deagle",
            BodyDamage: 55, HeadDamage: 200, Pellets: 1, SecondsPerShot: 0.35f, Automatic: false, Range: 200f, ImpactForce: 12f,
            MoveSpeed: 240f, Spread: 0f, ScopeZoom: 1f, ScopedMoveSpeed: 240f, UnscopedSpread: 0f, Knockback: 0f,
            Sounds.DeagleShot, SoundVolume: 0.85f, ViewKick: 4f);

        /// <summary>Hold to fire. Quick and steady. Not in the loadout menu for now; the Deagle took its place.</summary>
        public static Weapon Rifle { get; } = new(WeaponId.Rifle, "Rifle",
            BodyDamage: 20, HeadDamage: 50, Pellets: 1, SecondsPerShot: 1f / 6f, Automatic: true, Range: 200f, ImpactForce: 3f,
            MoveSpeed: 250f, Spread: 0f, ScopeZoom: 1f, ScopedMoveSpeed: 250f, UnscopedSpread: 0f, Knockback: 0f,
            Sounds.Gunshot, SoundVolume: 0.7f);

        /// <summary>
        /// Like CS's AWP: any hit kills outright, but it's slow to fire, slows you down, and is wildly inaccurate without
        /// the scope (hold right mouse). Firing knocks you out of the scope until the bolt is back.
        /// </summary>
        public static Weapon Sniper { get; } = new(WeaponId.Sniper, "Sniper rifle",
            BodyDamage: 100, HeadDamage: 200, Pellets: 1, SecondsPerShot: 1.4f, Automatic: false, Range: 500f, ImpactForce: 12f,
            MoveSpeed: 210f, Spread: 0f, ScopeZoom: 4f, ScopedMoveSpeed: 120f, UnscopedSpread: 6f, Knockback: 0f,
            Sounds.SniperShot, SoundVolume: 0.9f);

        /// <summary>
        /// A pump-action shotgun: ten pellets in a wide cone, so a point-blank body shot with all of them kills, and
        /// further away more and more miss. Every shot kicks you back the opposite way to where you aim: aim at the floor
        /// to blast yourself into the air.
        /// </summary>
        public static Weapon Shotgun { get; } = new(WeaponId.Shotgun, "Shotgun",
            BodyDamage: 12, HeadDamage: 24, Pellets: 10, SecondsPerShot: 0.85f, Automatic: false, Range: 50f, ImpactForce: 10f,
            MoveSpeed: 240f, Spread: 5f, ScopeZoom: 1f, ScopedMoveSpeed: 240f, UnscopedSpread: 5f, Knockback: 15f,
            Sounds.ShotgunShot, SoundVolume: 0.9f);

        /// <summary>
        /// Everyone carries one, on 1, as in CS. Left mouse slashes (hold to keep slashing); right mouse stabs, slower and
        /// harder, and a stab in the back kills outright. It only reaches a step in front of you.
        /// </summary>
        public static Weapon Knife { get; } = new(WeaponId.Knife, "Knife",
            BodyDamage: 25, HeadDamage: 25, Pellets: 1, SecondsPerShot: 0.4f, Automatic: true, Range: 2.5f, ImpactForce: 2f,
            MoveSpeed: 250f, Spread: 0f, ScopeZoom: 1f, ScopedMoveSpeed: 250f, UnscopedSpread: 0f, Knockback: 0f,
            Sounds.KnifeSwing, SoundVolume: 0.5f, Melee: true, StabDamage: 65, SecondsPerStab: 1f, StabRange: 2.2f);

        /// <summary>
        /// A gadget, on 3: fires rockets that fly straight, not too fast, and explode on whatever they
        /// hit, throwing everyone nearby (you too) away from the blast. A rocket is solid on top, so you can ride one: shoot
        /// it under a teammate's feet and they're carried along; they can jump and land back on it, or jump off it. Land in
        /// front of it, though, and it hits them.
        /// </summary>
        public static Weapon RocketLauncher { get; } = new(WeaponId.RocketLauncher, "Rocket launcher",
            BodyDamage: 110, HeadDamage: 110, Pellets: 1, SecondsPerShot: 1.5f, Automatic: true, Range: 0f, ImpactForce: 30f,
            MoveSpeed: 230f, Spread: 0f, ScopeZoom: 1f, ScopedMoveSpeed: 230f, UnscopedSpread: 0f, Knockback: 0f,
            Sounds.RocketLaunch, SoundVolume: 0.9f, ProjectileSpeed: 1100f, BlastRadius: 4.5f);

        public bool HasScope => ScopeZoom > 1f;
        public bool IsProjectile => ProjectileSpeed > 0f;

        public static Weapon Get(WeaponId id) => id switch
        {
            WeaponId.Sniper => Sniper,
            WeaponId.Shotgun => Shotgun,
            WeaponId.Knife => Knife,
            WeaponId.RocketLauncher => RocketLauncher,
            WeaponId.Deagle => Deagle,
            _ => Rifle
        };
    }

    /// <summary>
    /// What a player carries, picked in the loadout menu: their weapon on 2 and their gadget on 3, in any combination.
    /// Everyone has the knife on 1 as well. A gadget is something extra to the weapon, like the rocket launcher.
    /// </summary>
    public sealed record Loadout(Weapon Weapon, Weapon? Gadget)
    {
        /// <summary>The weapons to pick from, for slot 2. The rifle isn't one for now: the Deagle took its place.</summary>
        public static IReadOnlyList<Weapon> Weapons { get; } = [Weapon.Deagle, Weapon.Sniper, Weapon.Shotgun];

        /// <summary>The gadgets to pick from, for slot 3.</summary>
        public static IReadOnlyList<Weapon> Gadgets { get; } = [Weapon.RocketLauncher];

        /// <summary>What everyone starts with.</summary>
        public static Loadout Default { get; } = new(Weapon.Deagle, Weapon.RocketLauncher);

        /// <summary>A line about how a weapon or gadget plays, for the loadout menu.</summary>
        public static string Describe(Weapon weapon) => weapon.Id switch
        {
            WeaponId.Deagle => "It goes pew pew bang bang.",
            WeaponId.Sniper => "It can snipe people.",
            WeaponId.Shotgun => "A shotgun that might have a special trick up its sleeve.",
            WeaponId.RocketLauncher => "Rockets that blast everyone nearby away, you too. Jump on them, or ride one.",
            WeaponId.Rifle => "Gun that shoots, I think?",
            _ => ""
        };

        /// <summary>
        /// The weapon or gadget out of <paramref name="from"/> that <paramref name="name"/> means: its whole name, or the
        /// start of it, or of any word in it ("sniper", "rocket", "launcher"), ignoring case. Null if none or several do.
        /// </summary>
        public static Weapon? Find(IReadOnlyList<Weapon> from, string name)
        {
            if (from.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;

            List<Weapon> matches = from.Where(w => name.Length > 0
                && w.Name.Split(' ').Any(word => word.StartsWith(name, StringComparison.OrdinalIgnoreCase))).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
