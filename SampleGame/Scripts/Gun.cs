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
    /// The player's hitscan rifle: hold left mouse to fire. Shots go straight from the eye to whatever the crosshair
    /// is on, instantly. The model in view kicks back when firing, bobs while walking and lags behind the mouse.
    /// </summary>
    public class Gun : Component
    {
        // Where the gun sits in view when still, relative to the eye.
        private static readonly Vector3 _restPosition = new(0.17f, -0.16f, -0.36f);
        private static readonly Vector3 _muzzleOffset = new(0f, 0.015f, -0.37f);

        public float ShotsPerSecond { get; set; } = 6f;
        public float Range { get; set; } = 200f;

        /// <summary>How hard a shot pushes rigidbodies, in newton-seconds.</summary>
        public float ImpactForce { get; set; } = 3f;

        /// <summary>Raised for every shot: where it left the muzzle, where it ended, and what it hit, if anything.</summary>
        public event Action<Vector3, Vector3, RaycastHit?>? Fired;

        private Player _owner = null!;
        private GameObject _eye = null!;
        private GameObject _model = null!;
        private GameObject _muzzle = null!;
        private GameObject _flash = null!;

        private float _cooldown;
        private float _recoil;
        private float _flashTime;
        private float _bobPhase;
        private Vector2 _sway;

        /// <summary>Called by the Player that owns this gun, before its first Update.</summary>
        internal void Attach(Player owner, GameObject eye)
        {
            _owner = owner;
            _eye = eye;

            _model = new GameObject("Gun");
            _model.Transform.Parent = eye.Transform;
            _model.Transform.Position = _restPosition;

            var metal = new Color(0.22f, 0.23f, 0.25f);
            var grip = new Color(0.12f, 0.1f, 0.09f);
            AddPart("Receiver", new Vector3(0.06f, 0.08f, 0.3f), Vector3.Zero, 0f, metal);
            AddPart("Barrel", new Vector3(0.03f, 0.03f, 0.22f), new Vector3(0f, 0.015f, -0.25f), 0f, metal);
            AddPart("Sight", new Vector3(0.015f, 0.025f, 0.04f), new Vector3(0f, 0.05f, -0.06f), 0f, metal);
            AddPart("Grip", new Vector3(0.045f, 0.11f, 0.06f), new Vector3(0f, -0.08f, 0.08f), -15f, grip);
            AddPart("Magazine", new Vector3(0.04f, 0.1f, 0.05f), new Vector3(0f, -0.08f, -0.06f), 8f, grip);

            _muzzle = new GameObject("Muzzle");
            _muzzle.Transform.Parent = _model.Transform;
            _muzzle.Transform.Position = _muzzleOffset;

            _flash = new GameObject("Muzzle Flash");
            _flash.Transform.Parent = _muzzle.Transform;
            _flash.Transform.Scale = new Vector3(0.07f, 0.07f, 0.1f);
            MeshRenderer flashRenderer = _flash.AddComponent<MeshRenderer>();
            flashRenderer.Mesh = Primitives.Sphere;
            flashRenderer.Material.Shading = ShadingModel.Unlit;
            flashRenderer.Material.Color = new Color(1f, 0.9f, 0.5f);
            _flash.Active = false;
        }

        protected override void Update()
        {
            float dt = Time.DeltaTime;
            _cooldown -= dt;

            _model.Active = !_owner.IsDead;
            if (_owner.IsDead) return;

            if (InputSystem.GetMouseButton(MouseButton.Left) && _cooldown <= 0f)
            {
                Fire();
                // Carry over the leftover time, so the fire rate doesn't depend on the frame rate.
                _cooldown = MathF.Max(_cooldown, -dt) + 1f / ShotsPerSecond;
            }

            Animate(dt);
        }

        /// <summary>Fires one shot from the eye, straight ahead.</summary>
        public void Fire()
        {
            Vector3 origin = _eye.Transform.WorldPosition;
            Vector3 direction = _eye.Transform.Forward;
            Vector3 muzzle = _muzzle.Transform.WorldPosition;

            RaycastHit? hit = null;
            Vector3 end = origin + direction * Range;

            if (GameObject.Scene.Physics.Raycast(origin, direction, Range, out RaycastHit result, ignore: _owner.Controller))
            {
                hit = result;
                end = result.Point;
                Effects.Impact(end);
                result.Collider.GetComponent<Rigidbody>()?.AddImpulse(direction * ImpactForce);
            }

            Effects.Tracer(muzzle, end);

            // Your own gun sounds the same in both ears; other players hear it from where you are (see RemotePlayer).
            // A slightly different pitch each time keeps repeated shots from sounding mechanical.
            Sound.Play(Sounds.Gunshot, volume: 0.7f, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f);

            _recoil = 1f;
            _flashTime = 0.05f;
            Fired?.Invoke(muzzle, end, hit);
        }

        private void Animate(float dt)
        {
            // Recoil springs back quickly; the flash only lasts a couple of frames.
            _recoil *= MathF.Exp(-14f * dt);
            _flashTime -= dt;
            _flash.Active = _flashTime > 0f;
            if (_flash.Active) _flash.Transform.EulerAngles = new Vector3(0f, 0f, Random.Shared.NextSingle() * 360f);

            // Bob side to side and dip with each step, more the faster you run. None in the air.
            Vector3 velocity = _owner.Controller.Velocity with { Y = 0f };
            float speed = _owner.Controller.IsGrounded ? velocity.Length() : 0f;
            float bobAmount = MathF.Min(speed / _owner.MaxSpeed, 1f);
            _bobPhase += dt * speed * 1.7f;
            var bob = new Vector3(MathF.Sin(_bobPhase) * 0.012f, -MathF.Abs(MathF.Cos(_bobPhase)) * 0.01f, 0f) * bobAmount;

            // Lag a little behind the mouse, then catch up.
            Vector2 mouse = InputSystem.MouseDelta;
            Vector2 swayTarget = Vector2.Clamp(new Vector2(-mouse.X, mouse.Y) * 0.0005f, new Vector2(-0.03f), new Vector2(0.03f));
            _sway = Vector2.Lerp(_sway, swayTarget, 1f - MathF.Exp(-10f * dt));

            _model.Transform.Position = _restPosition + bob + new Vector3(_sway.X, _sway.Y, _recoil * 0.06f);
            _model.Transform.EulerAngles = new Vector3(_recoil * 7f, 0f, 0f);
        }

        private void AddPart(string name, Vector3 size, Vector3 position, float pitch, Color color)
        {
            var part = new GameObject(name);
            part.Transform.Parent = _model.Transform;
            part.Transform.Position = position;
            part.Transform.EulerAngles = new Vector3(pitch, 0f, 0f);

            MeshRenderer renderer = part.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.CreateBox(size);
            renderer.Material.Color = color;
            renderer.Material.Specular = 0.5f;
            renderer.Material.Shininess = 48f;
        }
    }
}
