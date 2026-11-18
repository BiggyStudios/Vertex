using Vertex;
using Vertex.Audio;
using Vertex.Input;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;

namespace Duel
{
    /// <summary>
    /// Shoots where the player looks while the left mouse button is held. A hit tells the player who was hit, on their
    /// own machine, which is in charge of their health. Everyone sees and hears every shot.
    /// </summary>
    public class PlayerGun : Component
    {
        public float Damage { get; set; } = 25f;

        [Tooltip("Shots a second while the button's held")]
        public float FireRate { get; set; } = 5f;

        public float Range { get; set; } = 100f;

        /// <summary>Where shots come from, and the way they go: the camera's object.</summary>
        public GameObject? Head { get; set; }

        public AudioClip? ShotSound { get; set; }

        /// <summary>A particle system that flashes at the gun on each shot.</summary>
        public ParticleSystem? MuzzleFlash { get; set; }

        private float _nextShot;

        protected override void Update()
        {
            if (!IsMine || !InputSystem.CursorLocked) return;
            if (!InputSystem.GetMouseButton(MouseButton.Left) || Time.TotalTime < _nextShot) return;

            _nextShot = Time.TotalTime + 1f / FireRate;
            Shoot();
        }

        private void Shoot()
        {
            NetworkScene.SendEvent("Shot", GameObject, NetworkTarget.Everyone);

            Transform aim = (Head ?? GameObject).Transform;
            bool hit = GameObject.Scene.Physics.Raycast(aim.WorldPosition, aim.Forward, Range, out RaycastHit found, GetComponent<Collider>());
            if (hit && found.Collider.GameObject.GetComponent<Health>() != null)
            {
                // Told to whoever plays the one hit, with how much it hurt and who shot.
                NetworkScene.SendEvent("Hit", found.Collider.GameObject, NetworkTarget.Owner, Damage, GameObject);
            }
        }

        protected override void OnNetworkEvent(NetworkEvent networkEvent)
        {
            if (networkEvent.Name != "Shot") return;

            if (ShotSound != null) Sound.PlayAt(ShotSound, Transform.WorldPosition, 0.6f);
            MuzzleFlash?.Play();
        }
    }
}
