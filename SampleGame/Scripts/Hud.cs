using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// A crosshair and a health bar. The engine has no 2D drawing yet, so they're small glowing shapes
    /// fixed just in front of the camera, close enough to be drawn over the world and the gun.
    /// </summary>
    public class Hud : Component
    {
        private const float _distance = 0.3f;
        private const float _healthBarWidth = 0.14f;
        private const float _healthBarHeight = 0.01f;

        private static readonly Color _crosshairColor = new(1f, 1f, 1f);
        private static readonly Color _hitColor = new(1f, 0.2f, 0.2f);

        private Player _player = null!;
        private readonly List<MeshRenderer> _crosshair = new();
        private GameObject _healthFill = null!;
        private MeshRenderer _healthFillRenderer = null!;
        private float _hitMarkerTime;

        internal void Attach(Player player, GameObject eye)
        {
            _player = player;

            // Four short lines around the centre, with a gap in the middle.
            const float gap = 0.004f;
            const float length = 0.006f;
            const float thickness = 0.0012f;
            float offset = gap + length * 0.5f;
            AddCrosshairLine(eye, new Vector3(offset, 0f, 0f), new Vector3(length, thickness, 0.0005f));
            AddCrosshairLine(eye, new Vector3(-offset, 0f, 0f), new Vector3(length, thickness, 0.0005f));
            AddCrosshairLine(eye, new Vector3(0f, offset, 0f), new Vector3(thickness, length, 0.0005f));
            AddCrosshairLine(eye, new Vector3(0f, -offset, 0f), new Vector3(thickness, length, 0.0005f));

            // Bottom centre of the screen, so it stays in view at any aspect ratio.
            var barPosition = new Vector3(0f, -0.19f, -_distance);
            AddQuad(eye, "Health Bar Background", barPosition with { Z = -_distance - 0.001f },
                new Vector3(_healthBarWidth + 0.004f, _healthBarHeight + 0.004f, 0.0005f), new Color(0.05f, 0.05f, 0.05f));
            _healthFill = AddQuad(eye, "Health Bar", barPosition, new Vector3(_healthBarWidth, _healthBarHeight, 0.0005f), Color.White);
            _healthFillRenderer = _healthFill.GetComponent<MeshRenderer>()!;
        }

        /// <summary>Turns the crosshair red for a moment: longer for a kill.</summary>
        public void ShowHitMarker(bool kill = false) => _hitMarkerTime = kill ? 0.4f : 0.12f;

        protected override void Update()
        {
            _hitMarkerTime -= Time.DeltaTime;
            Color crosshair = _hitMarkerTime > 0f ? _hitColor : _crosshairColor;
            foreach (MeshRenderer line in _crosshair) line.Material.Color = crosshair;

            // Shrinks from the right as health drops, fading from green to red.
            float fraction = Math.Clamp(_player.Health / (float)Player.MaxHealth, 0f, 1f);
            float width = MathF.Max(_healthBarWidth * fraction, 0.0001f);
            _healthFill.Transform.Scale = new Vector3(width, _healthBarHeight, 0.0005f);
            _healthFill.Transform.Position = new Vector3(-_healthBarWidth * 0.5f + width * 0.5f, -0.19f, -_distance);
            _healthFillRenderer.Material.Color = new Color(1f - fraction, 0.25f + 0.65f * fraction, 0.2f);
        }

        private void AddCrosshairLine(GameObject eye, Vector3 position, Vector3 size)
        {
            GameObject line = AddQuad(eye, "Crosshair", position with { Z = -_distance }, size, _crosshairColor);
            _crosshair.Add(line.GetComponent<MeshRenderer>()!);
        }

        private static GameObject AddQuad(GameObject eye, string name, Vector3 position, Vector3 size, Color color)
        {
            var quad = new GameObject(name);
            quad.Transform.Parent = eye.Transform;
            quad.Transform.Position = position;
            quad.Transform.Scale = size;

            MeshRenderer renderer = quad.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Cube;
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = color;
            return quad;
        }
    }
}
