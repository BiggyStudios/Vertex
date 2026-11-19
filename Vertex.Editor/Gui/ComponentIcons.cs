using Vertex.Audio;
using Vertex.Effects;
using Vertex.Editor.Inspection;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;
using Vertex.Scripting;
using Vertex.UI;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// Each kind of component's icon, and the colour it's drawn in: a soft colour for each part of the engine (physics
    /// green, sound amber, UI violet...), so a glance down an object's components shows what it's made of. A component
    /// without an icon of its own has its base class's; the game's own scripts have the code icon.
    /// </summary>
    public static class ComponentIcons
    {
        private static readonly Dictionary<Type, Icon> _icons = new()
        {
            [typeof(Transform)] = Icons.Move,
            [typeof(Camera)] = Icons.Camera,
            [typeof(Light)] = Icons.Light,
            [typeof(MeshRenderer)] = Icons.Cube,
            [typeof(SpriteRenderer)] = Icons.Sprite,
            [typeof(SpriteAnimator)] = Icons.Animation,
            [typeof(WorldText)] = Icons.WorldText,
            [typeof(ParticleEmitter)] = Icons.Particles,
            [typeof(VisualEffect)] = Icons.Sparkle,

            [typeof(Rigidbody)] = Icons.Rigidbody,
            [typeof(BoxCollider)] = Icons.BoxCollider,
            [typeof(SphereCollider)] = Icons.SphereCollider,
            [typeof(CapsuleCollider)] = Icons.CapsuleCollider,
            [typeof(ConvexCollider)] = Icons.ConvexCollider,
            [typeof(MeshCollider)] = Icons.MeshCollider,
            [typeof(CharacterController)] = Icons.Character,
            [typeof(FixedJoint)] = Icons.FixedJoint,
            [typeof(HingeJoint)] = Icons.HingeJoint,
            [typeof(SliderJoint)] = Icons.SliderJoint,
            [typeof(BallJoint)] = Icons.BallJoint,
            [typeof(DistanceJoint)] = Icons.DistanceJoint,
            [typeof(SpringJoint)] = Icons.SpringJoint,

            [typeof(AudioSource)] = Icons.Speaker,
            [typeof(AudioListener)] = Icons.Headphones,

            [typeof(NetworkManager)] = Icons.Network,
            [typeof(NetworkObject)] = Icons.NetworkObject,
            [typeof(NetworkTransform)] = Icons.Sync,
            [typeof(SpawnPoint)] = Icons.Pin,

            [typeof(UICanvas)] = Icons.UI,
            [typeof(UIRect)] = Icons.UIRect,
            [typeof(UIPanel)] = Icons.Panel,
            [typeof(UIImage)] = Icons.Image,
            [typeof(UIText)] = Icons.Letter,
            [typeof(UIButton)] = Icons.Button,
            [typeof(UIToggle)] = Icons.Toggle,
            [typeof(UISlider)] = Icons.Slider,
            [typeof(UIProgressBar)] = Icons.ProgressBar,
            [typeof(UIInputField)] = Icons.TextBox,
            [typeof(UIVerticalLayout)] = Icons.Column,
            [typeof(UIHorizontalLayout)] = Icons.Row,
            [typeof(UIGridLayout)] = Icons.GridLayout,
            [typeof(UIMask)] = Icons.Mask,

            [typeof(VisualScript)] = Icons.Graph,
        };

        private static readonly Color _rendering = Theme.Hex(0x7AA7FF);
        private static readonly Color _effects = Theme.Hex(0xFF9A5C);
        private static readonly Color _physics = Theme.Hex(0x7DCB5A);
        private static readonly Color _audio = Theme.Hex(0xF0B848);
        private static readonly Color _ui = Theme.Hex(0xB792FF);
        private static readonly Color _network = Theme.Hex(0x48C6C0);
        private static readonly Color _scripts = Theme.Hex(0xA9B0BC);

        /// <summary>The icon of a kind of component: its own, its nearest base class's, or the code icon for a script.</summary>
        public static Icon For(Type type)
        {
            for (Type? at = type; at != null && at != typeof(Component); at = at.BaseType)
            {
                if (_icons.TryGetValue(at, out Icon? icon)) return icon;
            }

            return type.Assembly == typeof(Component).Assembly ? Icons.Component : Icons.Code;
        }

        public static Icon For(Component component) => For(component.GetType());

        /// <summary>The colour its icon is drawn in: its part of the engine's.</summary>
        public static Color ColorFor(Type type)
        {
            if (type == typeof(Transform) || type == typeof(UIRect)) return Theme.TextDim;
            if (typeof(ParticleEmitter).IsAssignableFrom(type) || typeof(VisualEffect).IsAssignableFrom(type)) return _effects;

            return ComponentCatalog.Category(type) switch
            {
                "Rendering" => _rendering,
                "Physics" => _physics,
                "Audio" => _audio,
                "UI" => _ui,
                "Networking" => _network,
                _ => _scripts
            };
        }
    }
}
