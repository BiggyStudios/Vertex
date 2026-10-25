using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Audio;
using Vertex.Input;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// The player's hitscan gun: whichever <see cref="Weapon"/> their class carries. Left mouse fires; with the sniper
    /// rifle, hold right mouse to scope in. Shots go straight from the eye to whatever the crosshair is on, instantly; the
    /// shotgun fires a spread of pellets and kicks the player back. The model in view kicks back when firing, bobs while
    /// walking, lags behind the mouse, and the sniper's bolt and the shotgun's pump are worked between shots.
    /// </summary>
    public class Gun : Component
    {
        private const float _units = 0.0254f;
        private const float _deployTime = 0.5f;

        // When the sniper's bolt is worked, in seconds after the shot. The sound's two clicks and clack line up with it.
        private const float _boltStart = 0.38f;
        private const float _boltEnd = 0.8f;

        // When the shotgun's pump is racked back and slid forward again, in seconds after the shot.
        private const float _pumpStart = 0.22f;
        private const float _pumpEnd = 0.58f;

        public Weapon Weapon { get; private set; } = Weapon.Rifle;

        /// <summary>Looking down the scope: the view zooms in, the gun is hidden, and shots go exactly where aimed.</summary>
        public bool IsScoped { get; private set; }

        /// <summary>How far the view is zoomed in right now: 1 normally, easing up to the weapon's zoom when scoped.</summary>
        public float Zoom { get; private set; } = 1f;

        /// <summary>The fastest the player can run holding this gun, in metres per second.</summary>
        public float MoveSpeed => (IsScoped ? Weapon.ScopedMoveSpeed : Weapon.MoveSpeed) * _units;

        /// <summary>How far shots stray from the crosshair, in degrees: the shotgun's pellets, and the sniper rifle off the scope.</summary>
        public float Spread => Weapon.HasScope && !IsScoped ? Weapon.UnscopedSpread : Weapon.Spread;

        /// <summary>Where one bullet or pellet ended, and what it hit, if anything.</summary>
        public readonly record struct Pellet(Vector3 End, RaycastHit? Hit);

        /// <summary>
        /// Raised for every shot: the weapon, where it left the muzzle, where each pellet went (one, for most weapons), and
        /// whether it was a no-scope: fired from a scoped weapon without looking down the scope.
        /// </summary>
        public event Action<Weapon, Vector3, IReadOnlyList<Pellet>, bool>? Fired;

        /// <summary>One weapon's model in view.</summary>
        private sealed class ViewModel(GameObject root, Vector3 restPosition, float recoilDistance, float recoilPitch)
        {
            public GameObject Root { get; } = root;
            public Vector3 RestPosition { get; } = restPosition;
            public float RecoilDistance { get; } = recoilDistance;
            public float RecoilPitch { get; } = recoilPitch;
            public GameObject Muzzle { get; set; } = null!;
            public GameObject Flash { get; set; } = null!;

            /// <summary>The sniper's bolt handle, which turns up, slides back and forward, and turns down again.</summary>
            public GameObject? Bolt { get; set; }

            /// <summary>The shotgun's pump, which slides back towards you and forward again.</summary>
            public GameObject? Pump { get; set; }
            public Vector3 PumpRest { get; set; }
        }

        private readonly Dictionary<WeaponId, ViewModel> _models = new();

        private Player _owner = null!;
        private GameObject _eye = null!;

        private float _cooldown;
        private float _deploy;
        private float _sinceShot = float.PositiveInfinity;
        private bool _actionSoundPlayed = true;
        private bool _triggerHeld;
        private bool _triggerLocked;

        private float _recoil;
        private float _flashTime;
        private float _bobPhase;
        private float _bobAmount;
        private Vector2 _sway;
        private bool _wasGrounded = true;
        private float _fallSpeed;
        private float _landingPush;
        private float _landingDip;
        private float _landingDipVelocity;

        private ViewModel Model => _models[Weapon.Id];

        /// <summary>Called by the Player that owns this gun, before its first Update.</summary>
        internal void Attach(Player owner, GameObject eye)
        {
            _owner = owner;
            _eye = eye;

            _models[WeaponId.Rifle] = BuildRifle();
            _models[WeaponId.Sniper] = BuildSniper();
            _models[WeaponId.Shotgun] = BuildShotgun();
            Equip(Weapon.Rifle, deploy: false);
        }

        /// <summary>Switches to <paramref name="weapon"/>, which is brought up into view before it can fire.</summary>
        public void Equip(Weapon weapon, bool deploy = true)
        {
            Weapon = weapon;
            IsScoped = false;
            _sinceShot = float.PositiveInfinity;
            _actionSoundPlayed = true;
            _recoil = 0f;
            _flashTime = 0f;
            _deploy = deploy ? 1f : 0f;
            _cooldown = deploy ? _deployTime : 0f;

            foreach ((WeaponId id, ViewModel model) in _models)
            {
                model.Root.Active = id == weapon.Id;
                model.Flash.Active = false;
            }
        }

        protected override void Update()
        {
            float dt = Time.DeltaTime;
            _cooldown -= dt;
            _sinceShot += dt;

            UpdateTrigger(dt);
            UpdateScope();

            float zoomTarget = IsScoped ? Weapon.ScopeZoom : 1f;
            Zoom += (zoomTarget - Zoom) * (1f - MathF.Exp(-30f * dt));
            if (MathF.Abs(Zoom - zoomTarget) < 0.01f) Zoom = zoomTarget;

            // Looking down the scope, there's nothing of the gun to see.
            Model.Root.Active = !_owner.IsDead && !IsScoped;
            if (!_owner.IsDead) Animate(dt);
        }

        private void UpdateTrigger(float dt)
        {
            // After a menu, the console or chat had the mouse, the trigger has to be let go first, so the click that
            // closed the menu (or a button still held) doesn't fire.
            bool held = InputSystem.GetMouseButton(MouseButton.Left) && !_owner.IsDead;
            if (GameInput.Blocked) _triggerLocked = true;
            if (!held) _triggerLocked = false;

            // Automatic weapons fire as long as the trigger is held; the others need a fresh click for every shot.
            bool pulled = held && !_triggerLocked && (Weapon.Automatic || !_triggerHeld);
            _triggerHeld = held;

            if (!pulled || _cooldown > 0f) return;

            Fire();
            // Carry over the leftover time, so the fire rate doesn't depend on the frame rate.
            _cooldown = Weapon.Automatic ? MathF.Max(_cooldown, -dt) + Weapon.SecondsPerShot : Weapon.SecondsPerShot;
        }

        /// <summary>
        /// Scoped while right mouse is held, but only when the gun is ready to fire: not while it's being brought up, or
        /// while the bolt is being worked after a shot. Keep holding, and you're back in the scope once it's ready.
        /// </summary>
        private void UpdateScope()
        {
            IsScoped = Weapon.HasScope && !_owner.IsDead && !GameInput.Blocked && _cooldown <= 0f
                && InputSystem.GetMouseButton(MouseButton.Right);
        }

        /// <summary>
        /// Fires one shot from the eye: each of the weapon's pellets goes straight ahead, or anywhere within its spread.
        /// A weapon with knockback pushes the player the opposite way to where they're aiming.
        /// </summary>
        public void Fire()
        {
            Vector3 origin = _eye.Transform.WorldPosition;
            Vector3 aim = _eye.Transform.Forward;
            Vector3 muzzle = Model.Muzzle.Transform.WorldPosition;

            // Scoped shots come from the middle of the view, where the gun would be if you could see it.
            Vector3 tracerStart = IsScoped ? origin - _eye.Transform.Up * 0.08f : muzzle;
            bool noScope = Weapon.HasScope && !IsScoped;

            var pellets = new Pellet[Weapon.Pellets];
            for (int i = 0; i < pellets.Length; i++)
            {
                Vector3 direction = Scatter(aim, _eye.Transform.Right, _eye.Transform.Up, Spread);
                RaycastHit? hit = null;
                Vector3 end = origin + direction * Weapon.Range;

                if (GameObject.Scene.Physics.Raycast(origin, direction, Weapon.Range, out RaycastHit result, ignore: _owner.Controller))
                {
                    hit = result;
                    end = result.Point;
                    Effects.Impact(end);
                    result.Collider.GetComponent<Rigidbody>()?.AddImpulse(direction * (Weapon.ImpactForce / pellets.Length));
                }

                Effects.Tracer(tracerStart, end);
                pellets[i] = new Pellet(end, hit);
            }

            if (Weapon.Knockback > 0f) _owner.Push(-aim * Weapon.Knockback);

            // Your own gun sounds the same in both ears; other players hear it from where you are (see RemotePlayer).
            // A slightly different pitch each time keeps repeated shots from sounding mechanical.
            Sound.Play(Weapon.Sound, Weapon.SoundVolume, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f);

            _recoil = 1f;
            _flashTime = 0.05f;
            _sinceShot = 0f;

            // A bolt or pump has to be worked after every shot, which takes you out of the scope until it's done.
            if (!Weapon.Automatic)
            {
                _actionSoundPlayed = false;
                IsScoped = false;
            }

            Fired?.Invoke(Weapon, muzzle, pellets, noScope);
        }

        /// <summary>A random direction within <paramref name="degrees"/> of <paramref name="forward"/>, spread evenly over the circle.</summary>
        private static Vector3 Scatter(Vector3 forward, Vector3 right, Vector3 up, float degrees)
        {
            if (degrees <= 0f) return forward;

            float angle = Random.Shared.NextSingle() * MathF.Tau;
            float distance = MathF.Sqrt(Random.Shared.NextSingle()) * MathF.Tan(MathHelper.ToRadians(degrees));
            return Vector3.Normalize(forward + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * distance);
        }

        private void Animate(float dt)
        {
            ViewModel model = Model;

            // Recoil springs back quickly; the flash only lasts a couple of frames.
            _recoil *= MathF.Exp(-14f * dt);
            _flashTime -= dt;
            model.Flash.Active = _flashTime > 0f;
            if (model.Flash.Active) model.Flash.Transform.EulerAngles = new Vector3(0f, 0f, Random.Shared.NextSingle() * 360f);

            // A newly equipped gun swings up from below.
            _deploy = MathF.Max(_deploy - dt / _deployTime, 0f);
            float lowered = _deploy * _deploy * (3f - 2f * _deploy);

            // Bob side to side and dip with each step, more the faster you run. Easing the amount in and out, rather
            // than switching it, keeps the gun from jumping when you leave the ground or land (every hop, when bunny hopping).
            bool grounded = _owner.Controller.IsGrounded;
            float speed = (_owner.Controller.Velocity with { Y = 0f }).Length();
            float bobTarget = grounded ? MathF.Min(speed / _owner.RunSpeed, 1f) : 0f;
            _bobAmount += (bobTarget - _bobAmount) * (1f - MathF.Exp(-6f * dt));
            _bobPhase += dt * speed * 1.7f * _bobAmount;
            var bob = new Vector3(MathF.Sin(_bobPhase) * 0.012f, -MathF.Abs(MathF.Cos(_bobPhase)) * 0.01f, 0f) * _bobAmount;

            // Landing pushes the gun down a little, harder the faster you were falling. The gun hangs on a spring that
            // follows the push as it fades, so it starts moving gently, dips, and settles back without jumping.
            if (!grounded) _fallSpeed = MathF.Max(-_owner.Controller.Velocity.Y, 0f);
            else if (!_wasGrounded) _landingPush = MathF.Max(_landingPush, MathF.Min(_fallSpeed / 12f, 1f));
            _wasGrounded = grounded;
            _landingPush *= MathF.Exp(-5f * dt);
            UpdateLandingSpring(dt);
            bob.Y -= _landingDip * 0.04f;

            // Lag a little behind the mouse, then catch up.
            Vector2 mouse = InputSystem.MouseDelta;
            Vector2 swayTarget = Vector2.Clamp(new Vector2(-mouse.X, mouse.Y) * 0.0005f, new Vector2(-0.03f), new Vector2(0.03f));
            _sway = Vector2.Lerp(_sway, swayTarget, 1f - MathF.Exp(-10f * dt));

            float roll = AnimateBolt(model);
            float tilt = AnimatePump(model);

            model.Root.Transform.Position = model.RestPosition + bob + new Vector3(_sway.X, _sway.Y - lowered * 0.25f - roll * 0.002f, _recoil * model.RecoilDistance);
            model.Root.Transform.EulerAngles = new Vector3(_recoil * model.RecoilPitch - lowered * 40f + tilt, 0f, roll);
        }

        /// <summary>
        /// Racks the shotgun's pump after a shot: it slides back towards you and forward again, while the muzzle tips up
        /// a little. Returns how far it's tipped, in degrees.
        /// </summary>
        private float AnimatePump(ViewModel model)
        {
            if (model.Pump == null) return 0f;

            if (!_actionSoundPlayed && _sinceShot >= _pumpStart)
            {
                Sound.Play(Sounds.Pump, volume: 0.7f);
                _actionSoundPlayed = true;
            }

            float t = Math.Clamp((_sinceShot - _pumpStart) / (_pumpEnd - _pumpStart), 0f, 1f);
            float stroke = MathF.Sin(t * MathF.PI);
            model.Pump.Transform.Position = model.PumpRest + new Vector3(0f, 0f, stroke * 0.09f);
            return stroke * 5f;
        }

        /// <summary>
        /// Works the sniper's bolt after a shot: the handle turns up, slides back and forward, and turns down, while the
        /// gun tips over to the side to show it. Returns how far the gun is tipped, in degrees.
        /// </summary>
        private float AnimateBolt(ViewModel model)
        {
            if (model.Bolt == null) return 0f;

            if (!_actionSoundPlayed && _sinceShot >= _boltStart)
            {
                Sound.Play(Sounds.Bolt, volume: 0.6f);
                _actionSoundPlayed = true;
            }

            // 0 to 1 over the bolt's movement, split into quarters: up, back, forward, down.
            float t = Math.Clamp((_sinceShot - _boltStart) / (_boltEnd - _boltStart), 0f, 1f);
            float lift = Smooth(Math.Clamp(t * 4f, 0f, 1f)) - Smooth(Math.Clamp(t * 4f - 3f, 0f, 1f));
            float pull = Smooth(Math.Clamp(t * 4f - 1f, 0f, 1f)) - Smooth(Math.Clamp(t * 4f - 2f, 0f, 1f));
            model.Bolt.Transform.EulerAngles = new Vector3(0f, 0f, lift * 75f);
            model.Bolt.Transform.Position = new Vector3(0.028f, 0.012f, 0.07f + pull * 0.07f);

            // Tip over a little before the bolt moves and right itself just after.
            float tip = Math.Clamp((_sinceShot - _boltStart + 0.1f) / (_boltEnd - _boltStart + 0.2f), 0f, 1f);
            return MathF.Sin(tip * MathF.PI) * 14f;

            static float Smooth(float x) => x * x * (3f - 2f * x);
        }

        /// <summary>
        /// A critically damped spring (as fast as possible without overshooting) pulling the dip towards the push.
        /// Stepped in small fixed slices, so it behaves the same at any frame rate.
        /// </summary>
        private void UpdateLandingSpring(float dt)
        {
            const float stiffness = 400f;
            const float damping = 40f; // 2 * sqrt(stiffness): critically damped
            const float step = 1f / 240f;

            for (float left = dt; left > 0f; left -= step)
            {
                float slice = MathF.Min(step, left);
                _landingDipVelocity += ((_landingPush - _landingDip) * stiffness - _landingDipVelocity * damping) * slice;
                _landingDip += _landingDipVelocity * slice;
            }
        }

        private ViewModel BuildRifle()
        {
            var model = new ViewModel(new GameObject("Rifle"), new Vector3(0.17f, -0.16f, -0.36f), recoilDistance: 0.06f, recoilPitch: 7f);
            GameObject root = model.Root;
            root.Transform.Parent = _eye.Transform;

            // Gunmetal body, a darker barrel, and matte black polymer for the grip and magazine.
            var gunmetal = new Color(0.34f, 0.35f, 0.38f);
            var handguard = new Color(0.26f, 0.27f, 0.29f);
            var barrel = new Color(0.17f, 0.17f, 0.18f);
            var polymer = new Color(0.12f, 0.12f, 0.13f);
            AddPart(root, "Receiver", new Vector3(0.06f, 0.08f, 0.3f), Vector3.Zero, 0f, gunmetal, metallic: true);
            AddPart(root, "Handguard", new Vector3(0.05f, 0.06f, 0.14f), new Vector3(0f, 0.004f, -0.2f), 0f, handguard, metallic: true);
            AddPart(root, "Barrel", new Vector3(0.025f, 0.025f, 0.12f), new Vector3(0f, 0.015f, -0.32f), 0f, barrel, metallic: true);
            AddPart(root, "Sight", new Vector3(0.015f, 0.025f, 0.04f), new Vector3(0f, 0.05f, -0.06f), 0f, polymer, metallic: false);
            AddPart(root, "Grip", new Vector3(0.045f, 0.11f, 0.06f), new Vector3(0f, -0.08f, 0.08f), -15f, polymer, metallic: false);
            AddPart(root, "Magazine", new Vector3(0.04f, 0.1f, 0.05f), new Vector3(0f, -0.08f, -0.06f), 8f, polymer, metallic: false);

            AddMuzzle(model, new Vector3(0f, 0.015f, -0.37f), flashSize: 0.07f);
            return model;
        }

        /// <summary>A long bolt-action rifle in olive green, with a big scope on top and a stock that runs back out of view.</summary>
        private ViewModel BuildSniper()
        {
            var model = new ViewModel(new GameObject("Sniper Rifle"), new Vector3(0.16f, -0.16f, -0.44f), recoilDistance: 0.12f, recoilPitch: 14f);
            GameObject root = model.Root;
            root.Transform.Parent = _eye.Transform;

            var olive = new Color(0.3f, 0.35f, 0.22f);
            var darkOlive = new Color(0.22f, 0.26f, 0.16f);
            var steel = new Color(0.2f, 0.2f, 0.22f);
            var black = new Color(0.13f, 0.13f, 0.14f);

            AddPart(root, "Receiver", new Vector3(0.055f, 0.07f, 0.3f), Vector3.Zero, 0f, steel, metallic: true);
            AddPart(root, "Stock", new Vector3(0.06f, 0.1f, 0.26f), new Vector3(0f, -0.025f, 0.03f), 0f, olive, metallic: false);
            AddPart(root, "Cheek Rest", new Vector3(0.05f, 0.03f, 0.08f), new Vector3(0f, 0.035f, 0.12f), 0f, darkOlive, metallic: false);
            AddPart(root, "Forend", new Vector3(0.058f, 0.065f, 0.22f), new Vector3(0f, -0.01f, -0.22f), 0f, olive, metallic: false);
            AddPart(root, "Grip", new Vector3(0.045f, 0.1f, 0.05f), new Vector3(0f, -0.09f, 0.12f), -18f, darkOlive, metallic: false);
            AddPart(root, "Magazine", new Vector3(0.04f, 0.06f, 0.07f), new Vector3(0f, -0.075f, -0.04f), 0f, black, metallic: false);
            AddPart(root, "Barrel", new Vector3(0.024f, 0.024f, 0.36f), new Vector3(0f, 0.012f, -0.46f), 0f, steel, metallic: true);
            AddPart(root, "Muzzle Brake", new Vector3(0.036f, 0.036f, 0.06f), new Vector3(0f, 0.012f, -0.66f), 0f, black, metallic: true);

            AddPart(root, "Scope", new Vector3(0.036f, 0.036f, 0.24f), new Vector3(0f, 0.075f, -0.03f), 0f, black, metallic: true);
            AddPart(root, "Scope Front", new Vector3(0.052f, 0.052f, 0.05f), new Vector3(0f, 0.075f, -0.17f), 0f, black, metallic: true);
            AddPart(root, "Scope Rear", new Vector3(0.046f, 0.046f, 0.04f), new Vector3(0f, 0.075f, 0.1f), 0f, black, metallic: true);
            AddPart(root, "Scope Mount", new Vector3(0.02f, 0.03f, 0.025f), new Vector3(0f, 0.045f, -0.1f), 0f, steel, metallic: true);
            AddPart(root, "Scope Mount", new Vector3(0.02f, 0.03f, 0.025f), new Vector3(0f, 0.045f, 0.05f), 0f, steel, metallic: true);

            // The bolt turns about the bore, so its handle swings up and over to the right.
            var bolt = new GameObject("Bolt");
            bolt.Transform.Parent = root.Transform;
            bolt.Transform.Position = new Vector3(0.028f, 0.012f, 0.07f);
            AddPart(bolt, "Bolt Handle", new Vector3(0.05f, 0.012f, 0.012f), new Vector3(0.025f, 0f, 0f), 0f, steel, metallic: true);
            AddPart(bolt, "Bolt Knob", new Vector3(0.024f), new Vector3(0.05f, 0f, 0f), 0f, black, metallic: true, Primitives.Sphere);
            model.Bolt = bolt;

            AddMuzzle(model, new Vector3(0f, 0.012f, -0.7f), flashSize: 0.12f);
            return model;
        }

        /// <summary>A pump-action shotgun: a thick steel barrel over a magazine tube, with a wooden stock and pump.</summary>
        private ViewModel BuildShotgun()
        {
            var model = new ViewModel(new GameObject("Shotgun"), new Vector3(0.15f, -0.15f, -0.46f), recoilDistance: 0.12f, recoilPitch: 18f);
            GameObject root = model.Root;
            root.Transform.Parent = _eye.Transform;

            var steel = new Color(0.32f, 0.32f, 0.35f);
            var darkSteel = new Color(0.2f, 0.2f, 0.22f);
            var wood = new Color(0.45f, 0.27f, 0.14f);
            var darkWood = new Color(0.36f, 0.21f, 0.11f);

            AddPart(root, "Receiver", new Vector3(0.06f, 0.075f, 0.22f), Vector3.Zero, 0f, steel, metallic: true);
            AddPart(root, "Barrel", new Vector3(0.036f, 0.036f, 0.5f), new Vector3(0f, 0.02f, -0.35f), 0f, darkSteel, metallic: true);
            AddPart(root, "Magazine Tube", new Vector3(0.03f, 0.03f, 0.42f), new Vector3(0f, -0.016f, -0.31f), 0f, steel, metallic: true);
            AddPart(root, "Bead", new Vector3(0.008f, 0.01f, 0.008f), new Vector3(0f, 0.042f, -0.59f), 0f, new Color(0.85f, 0.8f, 0.6f), metallic: true);
            AddPart(root, "Stock", new Vector3(0.05f, 0.09f, 0.2f), new Vector3(0f, -0.035f, 0.18f), -6f, wood, metallic: false);
            AddPart(root, "Trigger Guard", new Vector3(0.02f, 0.03f, 0.06f), new Vector3(0f, -0.05f, 0.04f), 0f, darkSteel, metallic: true);

            // Wraps around the magazine tube, and slides along it.
            var pump = new GameObject("Pump");
            pump.Transform.Parent = root.Transform;
            model.PumpRest = new Vector3(0f, -0.016f, -0.27f);
            pump.Transform.Position = model.PumpRest;
            AddPart(pump, "Pump Grip", new Vector3(0.066f, 0.058f, 0.17f), Vector3.Zero, 0f, darkWood, metallic: false);
            model.Pump = pump;

            AddMuzzle(model, new Vector3(0f, 0.02f, -0.61f), flashSize: 0.14f);
            return model;
        }

        private static void AddMuzzle(ViewModel model, Vector3 position, float flashSize)
        {
            var muzzle = new GameObject("Muzzle");
            muzzle.Transform.Parent = model.Root.Transform;
            muzzle.Transform.Position = position;

            var flash = new GameObject("Muzzle Flash");
            flash.Transform.Parent = muzzle.Transform;
            flash.Transform.Scale = new Vector3(flashSize, flashSize, flashSize * 1.4f);
            MeshRenderer flashRenderer = flash.AddComponent<MeshRenderer>();
            flashRenderer.Mesh = Primitives.Sphere;
            flashRenderer.Material.Shading = ShadingModel.Unlit;
            flashRenderer.Material.Color = new Color(1f, 0.9f, 0.5f);
            flashRenderer.CastShadows = false;
            flash.Active = false;

            model.Muzzle = muzzle;
            model.Flash = flash;
        }

        private static void AddPart(GameObject parent, string name, Vector3 size, Vector3 position, float pitch, Color color, bool metallic, Mesh? mesh = null)
        {
            var part = new GameObject(name);
            part.Transform.Parent = parent.Transform;
            part.Transform.Position = position;
            part.Transform.EulerAngles = new Vector3(pitch, 0f, 0f);

            MeshRenderer renderer = part.AddComponent<MeshRenderer>();
            if (mesh != null)
            {
                renderer.Mesh = mesh;
                part.Transform.Scale = size;
            }
            else
            {
                renderer.Mesh = Primitives.CreateBox(size);
            }

            renderer.Material.Color = color;
            // Metal has small, sharp highlights; polymer only a faint, broad sheen.
            renderer.Material.Specular = metallic ? 0.6f : 0.15f;
            renderer.Material.Shininess = metallic ? 64f : 16f;
            // A first-person gun shouldn't throw a shadow onto the world in front of you.
            renderer.CastShadows = false;
        }
    }
}
