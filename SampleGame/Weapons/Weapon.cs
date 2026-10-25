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

        /// <summary>Hold to fire. Quick and steady. Not in any class for now; the Pistol took the Rifleman's place.</summary>
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
        /// The Shotgunner's second gun, on 3: fires rockets that fly straight, not too fast, and explode on whatever they
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
    /// What the player chooses from the class menu: which weapon they carry on 2, and for some, a second one on 3.
    /// </summary>
    public sealed record PlayerClass(string Name, string Description, Weapon Weapon, Weapon? Secondary = null)
    {
        public static PlayerClass Pistol { get; } = new("Pistol",
            "A Deagle. One to the head, two to the body.", Weapon.Deagle);

        /// <summary>Not in the class menu for now: the Pistol took its place. Kept to bring back.</summary>
        public static PlayerClass Rifleman { get; } = new("Rifleman",
            "Gun that shoots, I think?", Weapon.Rifle);

        public static PlayerClass Sniper { get; } = new("Sniper",
            "It can snipe people.", Weapon.Sniper);

        public static PlayerClass Shotgunner { get; } = new("Shotgunner",
            "A shotgun that might have a special trick up it's sleave.", Weapon.Shotgun, Weapon.RocketLauncher);

        public static IReadOnlyList<PlayerClass> All { get; } = [Pistol, Sniper, Shotgunner];
    }
}
