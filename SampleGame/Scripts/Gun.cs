using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Audio;
using Vertex.Input;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// What the player holds: the knife on 1, or on 2 the hitscan gun their class carries. Left mouse fires; with the
    /// sniper rifle, hold right mouse to scope in. Shots go straight from the eye to whatever the crosshair is on,
    /// instantly; the shotgun fires a spread of pellets and kicks the player back. The knife slashes on left mouse and
    /// stabs on right. F inspects whatever's in hand. The model in view kicks back when firing, bobs while walking, lags
    /// behind the mouse, and the sniper's bolt and the shotgun's pump are worked between shots.
    /// </summary>
    public class Gun : Component
    {
        private const float _units = 0.0254f;
        private const float _deployTime = 0.5f;

        // How far off the crosshair a knife swing still connects, in degrees: melee in Source sweeps a box, not a line.
        private const float _meleeCone = 7f;

        /// <summary>A pose of the model in view, <see cref="Time"/> seconds into an animation: moved and turned from where it rests.</summary>
        private readonly record struct Pose(float Time, Vector3 Offset, Vector3 Angles);

        // A slash: wind up a touch, then turn the blade flat and sweep it across the view from right to left, and come back.
        private static readonly Pose[] _slash =
        [
            new(0f, Vector3.Zero, Vector3.Zero),
            new(0.05f, new Vector3(0.04f, 0.02f, 0.01f), new Vector3(4f, -15f, -20f)),
            new(0.14f, new Vector3(-0.16f, 0.02f, -0.08f), new Vector3(-4f, 35f, -65f)),
            new(0.38f, Vector3.Zero, Vector3.Zero)
        ];

        // Every other slash comes back the other way: wound across to the left, then swept out to the right.
        private static readonly Pose[] _backhandSlash =
        [
            new(0f, Vector3.Zero, Vector3.Zero),
            new(0.05f, new Vector3(-0.08f, 0.03f, -0.02f), new Vector3(4f, 30f, 10f)),
            new(0.14f, new Vector3(0.07f, 0f, -0.04f), new Vector3(-4f, 8f, 30f)),
            new(0.38f, Vector3.Zero, Vector3.Zero)
        ];

        // A stab: draw back, then drive the point forward.
        private static readonly Pose[] _stab =
        [
            new(0f, Vector3.Zero, Vector3.Zero),
            new(0.12f, new Vector3(0f, 0.01f, 0.05f), new Vector3(12f, 0f, 0f)),
            new(0.22f, new Vector3(-0.03f, 0.02f, -0.15f), new Vector3(-4f, 8f, 0f)),
            new(0.5f, Vector3.Zero, Vector3.Zero)
        ];

        // Inspecting a gun: swing the muzzle in across the view and tip it to show one side, then roll it over to show
        // the other, then put it back. The muzzle stays pointing ahead, so the stock never swings into your face.
        private static readonly Pose[] _inspectGun =
        [
            new(0f, Vector3.Zero, Vector3.Zero),
            new(0.45f, new Vector3(-0.08f, 0.04f, -0.02f), new Vector3(6f, 40f, 30f)),
            new(1.1f, new Vector3(-0.07f, 0.045f, -0.02f), new Vector3(8f, 44f, 34f)),
            new(1.55f, new Vector3(-0.06f, 0.05f, -0.03f), new Vector3(14f, 25f, -45f)),
            new(2f, new Vector3(-0.06f, 0.05f, -0.03f), new Vector3(15f, 24f, -48f)),
            new(2.4f, Vector3.Zero, Vector3.Zero)
        ];

        // Inspecting the knife: hold the blade up flat across the view, flip it over to show the other side, put it back.
        private static readonly Pose[] _inspectKnife =
        [
            new(0f, Vector3.Zero, Vector3.Zero),
            new(0.35f, new Vector3(-0.1f, 0.06f, -0.02f), new Vector3(10f, 60f, -60f)),
            new(0.9f, new Vector3(-0.1f, 0.06f, -0.02f), new Vector3(12f, 65f, -55f)),
            new(1.25f, new Vector3(-0.1f, 0.07f, -0.02f), new Vector3(10f, 65f, 125f)),
            new(1.8f, new Vector3(-0.1f, 0.07f, -0.02f), new Vector3(8f, 60f, 120f)),
            new(2.2f, Vector3.Zero, Vector3.Zero)
        ];

        // When the sniper's bolt is worked, in seconds after the shot. The sound's two clicks and clack line up with it.
        private const float _boltStart = 0.38f;
        private const float _boltEnd = 0.8f;

        // When the shotgun's pump is racked back and slid forward again, in seconds after the shot.
        private const float _pumpStart = 0.22f;
        private const float _pumpEnd = 0.58f;

        // A rocket fired at anything closer than this goes off against it at once. Far enough to reach the floor looking
        // straight down, so a rocket into your own feet is caught here.
        private const float _pointBlank = 2f;

        public Weapon Weapon { get; private set; } = Weapon.Deagle;

        /// <summary>The class's gun, on 2. The knife is always on 1.</summary>
        public Weapon Primary { get; private set; } = Weapon.Deagle;

        /// <summary>Some classes' second gun, on 3 (the Shotgunner's rocket launcher). Null for the rest.</summary>
        public Weapon? Secondary { get; private set; }

        /// <summary>Showing the weapon off, after pressing F.</summary>
        public bool IsInspecting => _inspectTime >= 0f;

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

        /// <summary>Raised for every rocket fired, once it's on its way.</summary>
        public event Action<Rocket>? RocketFired;

        /// <summary>Raised for every knife swing: the weapon, where the blade was, what it hit (if anything), and whether it was a stab.</summary>
        public event Action<Weapon, Vector3, RaycastHit?, bool>? Struck;

        /// <summary>One weapon's model in view.</summary>
        private sealed class ViewModel(GameObject root, Vector3 restPosition, float recoilDistance, float recoilPitch, float recoilRecovery = 14f)
        {
            public GameObject Root { get; } = root;
            public Vector3 RestPosition { get; } = restPosition;
            public float RecoilDistance { get; } = recoilDistance;
            public float RecoilPitch { get; } = recoilPitch;

            /// <summary>How quickly it settles back after a shot: lower is a slower, heavier recovery.</summary>
            public float RecoilRecovery { get; } = recoilRecovery;

            /// <summary>A pistol's slide, which slams back when it fires and springs forward again.</summary>
            public GameObject? Slide { get; set; }
            public Vector3 SlideRest { get; set; }
            public GameObject Muzzle { get; set; } = null!;
            public GameObject Flash { get; set; } = null!;

            /// <summary>The sniper's bolt handle, which turns up, slides back and forward, and turns down again.</summary>
            public GameObject? Bolt { get; set; }

            /// <summary>The shotgun's pump, which slides back towards you and forward again.</summary>
            public GameObject? Pump { get; set; }
            public Vector3 PumpRest { get; set; }
        }

        private readonly Dictionary<WeaponId, ViewModel> _models = new();

        // When each weapon last fired, and when it can fire again, in Time.TotalTime. They carry on while a weapon is
        // put away, so switching to the knife and back can't skip the wait for the sniper's bolt or the shotgun's pump.
        private readonly Dictionary<WeaponId, float> _firedAt = new();
        private readonly Dictionary<WeaponId, float> _readyAt = new();

        private Player _owner = null!;
        private GameObject _eye = null!;

        private float _cooldown;
        private float _deploy;
        private float _sinceShot = float.PositiveInfinity;
        private bool _actionSoundPlayed = true;
        private bool _triggerHeld;
        private bool _stabHeld;
        private bool _triggerLocked;

        private float _sinceSwing = float.PositiveInfinity;
        private bool _swingWasStab;
        // Whether the last slash was the backhand; starts true, so the first one is the forehand.
        private bool _backhand = true;
        private float _inspectTime = -1f;

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
            _models[WeaponId.Knife] = BuildKnife();
            _models[WeaponId.RocketLauncher] = BuildRocketLauncher();
            _models[WeaponId.Deagle] = BuildDeagle();
            Equip(Weapon.Deagle, deploy: false);
        }

        /// <summary>Gives the player their class's guns, for slot 2 and (if it has one) slot 3, and switches to slot 2's.</summary>
        public void SetLoadout(Weapon primary, Weapon? secondary)
        {
            Primary = primary;
            Secondary = secondary;
            Equip(primary);
        }

        /// <summary>
        /// Switches to <paramref name="weapon"/>, which is brought up into view before it can fire. A gun put away halfway
        /// through working its bolt or pump carries on from where it would be by now, and isn't ready any sooner.
        /// </summary>
        public void Equip(Weapon weapon, bool deploy = true)
        {
            Weapon = weapon;
            IsScoped = false;
            _sinceShot = Time.TotalTime - _firedAt.GetValueOrDefault(weapon.Id, float.NegativeInfinity);
            _sinceSwing = float.PositiveInfinity;
            _inspectTime = -1f;
            _actionSoundPlayed = _sinceShot >= ActionStart(_models[weapon.Id]);
            _recoil = 0f;
            _flashTime = 0f;
            _deploy = deploy ? 1f : 0f;

            float stillBusy = _readyAt.GetValueOrDefault(weapon.Id, float.NegativeInfinity) - Time.TotalTime;
            _cooldown = MathF.Max(deploy ? _deployTime : 0f, stillBusy);

            if (deploy && weapon.Melee) Sound.Play(Sounds.KnifeDraw, volume: 0.5f);

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
            _sinceSwing += dt;

            if (!_owner.IsDead && !GameInput.Blocked)
            {
                // Switching to what's already in hand does nothing, as in CS.
                if (InputSystem.GetKeyDown(Key.Number1) && Weapon != Weapon.Knife) Equip(Weapon.Knife);
                else if (InputSystem.GetKeyDown(Key.Number2) && Weapon != Primary) Equip(Primary);
                else if (InputSystem.GetKeyDown(Key.Number3) && Secondary != null && Weapon != Secondary) Equip(Secondary);

                // Pressing it again starts over. Not while the gun is busy: being brought up, or working its bolt or pump.
                if (InputSystem.GetKeyDown(Key.F) && _cooldown <= 0f && !IsScoped) _inspectTime = 0f;
            }

            UpdateTrigger(dt);
            UpdateScope();
            if (IsScoped || _owner.IsDead) _inspectTime = -1f;

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
            bool stabHeld = Weapon.Melee && InputSystem.GetMouseButton(MouseButton.Right) && !_owner.IsDead;
            if (GameInput.Blocked) _triggerLocked = true;
            if (!held && !stabHeld) _triggerLocked = false;

            // Automatic weapons fire as long as the trigger is held; the others need a fresh click for every shot. The
            // knife's stab, on right mouse, is one per click.
            bool pulled = held && !_triggerLocked && (Weapon.Automatic || !_triggerHeld);
            bool stab = stabHeld && !_triggerLocked && !_stabHeld;
            _triggerHeld = held;
            _stabHeld = stabHeld;

            if (_cooldown > 0f) return;

            if (stab)
            {
                Strike(stab: true);
                _cooldown = Weapon.SecondsPerStab;
            }
            else if (pulled)
            {
                if (Weapon.Melee) Strike(stab: false);
                else Fire();

                // Carry over the leftover time, so the fire rate doesn't depend on the frame rate.
                _cooldown = Weapon.Automatic ? MathF.Max(_cooldown, -dt) + Weapon.SecondsPerShot : Weapon.SecondsPerShot;
            }
            else
            {
                return;
            }

            _firedAt[Weapon.Id] = Time.TotalTime;
            _readyAt[Weapon.Id] = Time.TotalTime + _cooldown;
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
            if (Weapon.IsProjectile)
            {
                FireRocket();
                return;
            }

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
                    Effects.Impact(result);
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
            _inspectTime = -1f;
            if (Weapon.ViewKick > 0f) _owner.PunchView(Weapon.ViewKick);

            // A bolt or pump has to be worked after every shot, which takes you out of the scope until it's done.
            if (!Weapon.Automatic)
            {
                _actionSoundPlayed = false;
                IsScoped = false;
            }

            Fired?.Invoke(Weapon, muzzle, pellets, noScope);
        }

        /// <summary>
        /// Fires a rocket from in front of the eye along the crosshair. It starts a metre out, clear of the player. With
        /// something closer than <see cref="_pointBlank"/>, it goes off against it straight away instead; if that's the
        /// ground under your own feet, it's a direct hit on yourself. Once it's flying, your own rocket never hits you, so
        /// surfing faster than it can't catch it up and blow yourself up.
        /// </summary>
        private void FireRocket()
        {
            Vector3 eye = _eye.Transform.WorldPosition;
            Vector3 aim = _eye.Transform.Forward;

            bool blocked = GameObject.Scene.Physics.Raycast(eye, aim, _pointBlank, out RaycastHit wall, ignore: _owner.Controller);
            Vector3 start = blocked ? wall.Point - aim * 0.1f : eye + aim;
            Rocket rocket = Rocket.Spawn(_owner, Network.LocalPeer, mine: true, start, aim);
            Sound.Play(Weapon.Sound, Weapon.SoundVolume, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f);

            _recoil = 1f;
            _flashTime = 0.05f;
            _sinceShot = 0f;
            _inspectTime = -1f;

            RocketFired?.Invoke(rocket);

            if (blocked)
            {
                Vector3 feet = _owner.Transform.WorldPosition;
                bool underFeet = Vector2.Distance(new Vector2(wall.Point.X, wall.Point.Z), new Vector2(feet.X, feet.Z)) < 0.6f && wall.Point.Y < feet.Y + 0.5f;
                rocket.Explode(start, decidedHere: true, underFeet ? Network.LocalPeer : null);
            }
        }

        /// <summary>
        /// Swings the knife: a slash, or with <paramref name="stab"/> a shorter, harder stab. Whatever is within reach
        /// near the crosshair is hit; a wall or prop gives a spark and a clink. What it does to another player is worked
        /// out by whoever listens to <see cref="Struck"/>.
        /// </summary>
        public void Strike(bool stab)
        {
            Vector3 origin = _eye.Transform.WorldPosition;
            Vector3 aim = _eye.Transform.Forward;

            RaycastHit? hit = MeleeTrace(origin, aim, stab ? Weapon.StabRange : Weapon.Range);
            if (hit is { } result)
            {
                result.Collider.GetComponent<Rigidbody>()?.AddImpulse(aim * Weapon.ImpactForce);
                if (result.GameObject is not RemotePlayer)
                {
                    Effects.Impact(result);
                    Sound.Play(Sounds.KnifeHitWall, volume: 0.6f, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f);
                }
            }

            // A stab is a heavier, lower whoosh.
            Sound.Play(Weapon.Sound, Weapon.SoundVolume, pitch: (stab ? 0.75f : 0.95f) + Random.Shared.NextSingle() * 0.1f);

            _sinceSwing = 0f;
            _swingWasStab = stab;
            if (!stab) _backhand = !_backhand;
            _inspectTime = -1f;

            Struck?.Invoke(Weapon, Model.Muzzle.Transform.WorldPosition, hit, stab);
        }

        /// <summary>
        /// The ray straight ahead, then a ring of six around it, <see cref="_meleeCone"/> degrees out. A player caught by
        /// any of them is hit, even if the middle ray found the wall behind them; otherwise, the first thing found.
        /// </summary>
        private RaycastHit? MeleeTrace(Vector3 origin, Vector3 aim, float reach)
        {
            RaycastHit? first = null;
            for (int i = -1; i < 6; i++)
            {
                Vector3 direction = i < 0 ? aim : Tilt(aim, _eye.Transform.Right, _eye.Transform.Up, i * MathF.Tau / 6f, _meleeCone);
                if (!GameObject.Scene.Physics.Raycast(origin, direction, reach, out RaycastHit result, ignore: _owner.Controller)) continue;

                if (result.GameObject is RemotePlayer) return result;
                first ??= result;
            }

            return first;
        }

        /// <summary>A random direction within <paramref name="degrees"/> of <paramref name="forward"/>, spread evenly over the circle.</summary>
        private static Vector3 Scatter(Vector3 forward, Vector3 right, Vector3 up, float degrees)
        {
            if (degrees <= 0f) return forward;

            float angle = Random.Shared.NextSingle() * MathF.Tau;
            float distance = MathF.Sqrt(Random.Shared.NextSingle()) * MathF.Tan(MathHelper.ToRadians(degrees));
            return Vector3.Normalize(forward + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * distance);
        }

        /// <summary><paramref name="forward"/> tipped <paramref name="degrees"/> towards <paramref name="around"/> radians round the clock (0 is right, a quarter turn is up).</summary>
        private static Vector3 Tilt(Vector3 forward, Vector3 right, Vector3 up, float around, float degrees)
        {
            float distance = MathF.Tan(MathHelper.ToRadians(degrees));
            return Vector3.Normalize(forward + (right * MathF.Cos(around) + up * MathF.Sin(around)) * distance);
        }

        private void Animate(float dt)
        {
            ViewModel model = Model;

            // Recoil springs back, quickly for most guns; the flash only lasts a couple of frames.
            _recoil *= MathF.Exp(-model.RecoilRecovery * dt);
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
            AnimateSlide(model);
            (Vector3 swingOffset, Vector3 swingAngles) = AnimateSwing();
            (Vector3 inspectOffset, Vector3 inspectAngles) = AnimateInspect(dt);

            model.Root.Transform.Position = model.RestPosition + bob + swingOffset + inspectOffset
                + new Vector3(_sway.X, _sway.Y - lowered * 0.25f - roll * 0.002f, _recoil * model.RecoilDistance);
            model.Root.Transform.EulerAngles = swingAngles + inspectAngles
                + new Vector3(_recoil * model.RecoilPitch - lowered * 40f + tilt, 0f, roll);
        }

        /// <summary>The slide, if it has one: slammed straight back by the shot, then sprung forward again.</summary>
        private void AnimateSlide(ViewModel model)
        {
            if (model.Slide == null) return;

            const float slam = 0.025f;
            float back = _sinceShot < slam ? _sinceShot / slam : MathF.Exp(-(_sinceShot - slam) * 22f);
            model.Slide.Transform.Position = model.SlideRest + new Vector3(0f, 0f, back * 0.045f);
        }

        /// <summary>The knife's slash or stab, played out after a swing. Slashes go alternately one way, then the other.</summary>
        private (Vector3 Offset, Vector3 Angles) AnimateSwing()
        {
            if (!Weapon.Melee) return default;

            return Sample(_swingWasStab ? _stab : _backhand ? _backhandSlash : _slash, _sinceSwing);
        }

        /// <summary>Plays the inspect animation, if F was pressed, and stops it at the end.</summary>
        private (Vector3 Offset, Vector3 Angles) AnimateInspect(float dt)
        {
            if (!IsInspecting) return default;

            Pose[] poses = Weapon.Melee ? _inspectKnife : _inspectGun;
            _inspectTime += dt;
            if (_inspectTime >= poses[^1].Time)
            {
                _inspectTime = -1f;
                return default;
            }

            return Sample(poses, _inspectTime);
        }

        /// <summary>Where an animation is <paramref name="time"/> seconds in, easing in and out between each pair of poses.</summary>
        private static (Vector3 Offset, Vector3 Angles) Sample(Pose[] poses, float time)
        {
            if (time <= poses[0].Time) return (poses[0].Offset, poses[0].Angles);

            for (int i = 1; i < poses.Length; i++)
            {
                Pose from = poses[i - 1];
                Pose to = poses[i];
                if (time > to.Time) continue;

                float t = (time - from.Time) / (to.Time - from.Time);
                t = t * t * (3f - 2f * t);
                return (Vector3.Lerp(from.Offset, to.Offset, t), Vector3.Lerp(from.Angles, to.Angles, t));
            }

            return (poses[^1].Offset, poses[^1].Angles);
        }

        /// <summary>How long after a shot the bolt or pump starts being worked, for a model with one. Anything else has nothing to wait for.</summary>
        private static float ActionStart(ViewModel model) => model.Bolt != null ? _boltStart : model.Pump != null ? _pumpStart : 0f;

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

        /// <summary>
        /// A Desert Eagle held out in the right hand. Brushed stainless: a fat, square barrel block at the front with a rib
        /// along the top and a big square muzzle, and behind it a long, tall slide with grip lines cut in its back end, which
        /// slams back every shot. A black frame underneath, a squared-off trigger guard, a big hammer, and a thick grip.
        /// It kicks hard and settles slowly.
        /// </summary>
        private ViewModel BuildDeagle()
        {
            var model = new ViewModel(new GameObject("Deagle"), new Vector3(0.13f, -0.125f, -0.44f), recoilDistance: 0.05f, recoilPitch: 26f, recoilRecovery: 8f);
            GameObject root = model.Root;
            root.Transform.Parent = _eye.Transform;

            var stainless = new Color(0.64f, 0.65f, 0.68f);
            var darkSteel = new Color(0.28f, 0.28f, 0.3f);
            var black = new Color(0.09f, 0.09f, 0.1f);
            var rubber = new Color(0.16f, 0.16f, 0.17f);

            // The barrel stays put: a block taller and wider than the slide, with the rib and front sight on top.
            AddPart(root, "Barrel", new Vector3(0.056f, 0.062f, 0.14f), new Vector3(0f, 0.031f, -0.13f), 0f, stainless, metallic: true);
            AddPart(root, "Rib", new Vector3(0.02f, 0.008f, 0.14f), new Vector3(0f, 0.066f, -0.13f), 0f, darkSteel, metallic: true);
            AddPart(root, "Front Sight", new Vector3(0.008f, 0.016f, 0.014f), new Vector3(0f, 0.077f, -0.19f), 0f, black, metallic: true);
            AddPart(root, "Muzzle Face", new Vector3(0.034f, 0.034f, 0.004f), new Vector3(0f, 0.032f, -0.2015f), 0f, black, metallic: true);
            AddPart(root, "Dust Cover", new Vector3(0.048f, 0.026f, 0.14f), new Vector3(0f, -0.012f, -0.12f), 0f, black, metallic: false);

            // The slide, which moves.
            var slide = new GameObject("Slide");
            slide.Transform.Parent = root.Transform;
            model.SlideRest = Vector3.Zero;
            AddPart(slide, "Slide Body", new Vector3(0.05f, 0.054f, 0.2f), new Vector3(0f, 0.028f, 0.04f), 0f, stainless, metallic: true);
            for (int i = 0; i < 5; i++)
            {
                AddPart(slide, "Serration", new Vector3(0.053f, 0.042f, 0.004f), new Vector3(0f, 0.026f, 0.085f + i * 0.011f), 0f, black, metallic: true);
            }
            AddPart(slide, "Rear Sight", new Vector3(0.036f, 0.014f, 0.014f), new Vector3(0f, 0.061f, 0.125f), 0f, black, metallic: true);
            model.Slide = slide;

            AddPart(root, "Frame", new Vector3(0.046f, 0.026f, 0.13f), new Vector3(0f, -0.012f, 0f), 0f, black, metallic: false);
            AddPart(root, "Hammer", new Vector3(0.018f, 0.03f, 0.02f), new Vector3(0f, 0.045f, 0.152f), -30f, darkSteel, metallic: true);
            AddPart(root, "Beavertail", new Vector3(0.04f, 0.012f, 0.04f), new Vector3(0f, -0.02f, 0.135f), 0f, black, metallic: false);
            AddPart(root, "Trigger Guard", new Vector3(0.014f, 0.008f, 0.08f), new Vector3(0f, -0.068f, -0.035f), 0f, black, metallic: false);
            AddPart(root, "Trigger Guard Front", new Vector3(0.014f, 0.052f, 0.01f), new Vector3(0f, -0.044f, -0.075f), -10f, black, metallic: false);
            AddPart(root, "Trigger", new Vector3(0.008f, 0.03f, 0.01f), new Vector3(0f, -0.04f, -0.02f), 15f, darkSteel, metallic: true);
            AddPart(root, "Grip", new Vector3(0.052f, 0.135f, 0.072f), new Vector3(0f, -0.085f, 0.085f), -16f, black, metallic: false);
            AddPart(root, "Grip Panel", new Vector3(0.056f, 0.1f, 0.052f), new Vector3(0f, -0.09f, 0.087f), -16f, rubber, metallic: false);
            AddPart(root, "Magazine Base", new Vector3(0.046f, 0.012f, 0.066f), new Vector3(0f, -0.156f, 0.106f), -16f, darkSteel, metallic: true);

            AddMuzzle(model, new Vector3(0f, 0.032f, -0.21f), flashSize: 0.12f);
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

        /// <summary>A fat olive tube over the shoulder, with a grip under it, another further forward, and a sight off to the side.</summary>
        private ViewModel BuildRocketLauncher()
        {
            var model = new ViewModel(new GameObject("Rocket Launcher"), new Vector3(0.19f, -0.18f, -0.5f), recoilDistance: 0.1f, recoilPitch: 8f);
            GameObject root = model.Root;
            root.Transform.Parent = _eye.Transform;

            var olive = new Color(0.36f, 0.4f, 0.26f);
            var darkOlive = new Color(0.25f, 0.28f, 0.18f);
            var black = new Color(0.1f, 0.1f, 0.11f);

            AddPart(root, "Tube", new Vector3(0.1f, 0.1f, 0.7f), new Vector3(0f, 0.03f, -0.2f), 0f, olive, metallic: false);
            AddPart(root, "Muzzle Ring", new Vector3(0.12f, 0.12f, 0.05f), new Vector3(0f, 0.03f, -0.54f), 0f, darkOlive, metallic: true);
            AddPart(root, "Back Ring", new Vector3(0.12f, 0.12f, 0.05f), new Vector3(0f, 0.03f, 0.13f), 0f, darkOlive, metallic: true);
            AddPart(root, "Grip", new Vector3(0.03f, 0.1f, 0.04f), new Vector3(0f, -0.07f, 0f), 12f, black, metallic: false);
            AddPart(root, "Front Grip", new Vector3(0.03f, 0.08f, 0.04f), new Vector3(0f, -0.06f, -0.3f), 0f, black, metallic: false);
            AddPart(root, "Sight", new Vector3(0.02f, 0.05f, 0.06f), new Vector3(-0.06f, 0.08f, -0.25f), 0f, black, metallic: true);

            AddMuzzle(model, new Vector3(0f, 0.03f, -0.58f), flashSize: 0.2f);
            return model;
        }

        /// <summary>
        /// A combat knife held point-forward in the right hand, angled in towards the middle of the view: a black grip, a
        /// steel guard and pommel, and a long bright blade with a clipped point.
        /// </summary>
        private ViewModel BuildKnife()
        {
            var model = new ViewModel(new GameObject("Knife"), new Vector3(0.15f, -0.16f, -0.37f), recoilDistance: 0f, recoilPitch: 0f);
            GameObject root = model.Root;
            root.Transform.Parent = _eye.Transform;

            // The knife sits at an angle in the hand; the whole model moves and turns from here.
            var grip = new GameObject("Knife Grip");
            grip.Transform.Parent = root.Transform;
            grip.Transform.EulerAngles = new Vector3(15f, 12f, 0f);

            var steel = new Color(0.62f, 0.64f, 0.68f);
            var darkSteel = new Color(0.3f, 0.3f, 0.33f);
            var black = new Color(0.1f, 0.1f, 0.11f);

            AddPart(grip, "Handle", new Vector3(0.024f, 0.03f, 0.1f), new Vector3(0f, 0f, 0.025f), 0f, black, metallic: false);
            AddPart(grip, "Pommel", new Vector3(0.028f, 0.032f, 0.014f), new Vector3(0f, 0f, 0.082f), 0f, darkSteel, metallic: true);
            AddPart(grip, "Guard", new Vector3(0.05f, 0.014f, 0.016f), new Vector3(0f, 0f, -0.032f), 0f, darkSteel, metallic: true);
            AddPart(grip, "Blade", new Vector3(0.006f, 0.032f, 0.15f), new Vector3(0f, 0.002f, -0.115f), 0f, steel, metallic: true);
            AddPart(grip, "Point", new Vector3(0.006f, 0.022f, 0.045f), new Vector3(0f, -0.002f, -0.2f), 18f, steel, metallic: true);

            // Nothing fires from it; it only marks where the blade is, for the swing other players see.
            AddMuzzle(model, new Vector3(-0.04f, 0.05f, -0.2f), flashSize: 0f);
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
            flashRenderer.Material.Emission = new Color(1f, 0.75f, 0.35f);
            flashRenderer.Material.EmissionStrength = 2.5f;
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
