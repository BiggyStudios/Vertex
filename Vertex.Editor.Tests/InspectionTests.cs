using Vertex.Editor.Inspection;
using Vertex.Physics;
using Vertex.Rendering;

namespace Vertex.Editor.Tests
{
    /// <summary>Which components can be added, and which of their values the inspector shows.</summary>
    public class InspectionTests : EditorTest
    {
        /// <summary>A script as a game would write one.</summary>
        public class Spinner : Component
        {
            [Range(0f, 720f)]
            [Tooltip("Degrees a second.")]
            public float Speed = 90f;

            public bool Clockwise { get; set; }

            [HideInInspector]
            public float Angle { get; set; }

            public float ReadOnly => Speed * 2f;
        }

        [HideInInspector]
        public class Hidden : Component;

        [Fact]
        public void Add_component_lists_the_engines_components_and_the_games_scripts()
        {
            IReadOnlyList<ComponentType> all = ComponentCatalog.All;

            Assert.Contains(all, type => type.Type == typeof(MeshRenderer) && type.Name == "Mesh Renderer" && type.Category == "Rendering");
            Assert.Contains(all, type => type.Type == typeof(BoxCollider) && type.Category == "Physics");
            Assert.Contains(all, type => type.Type == typeof(Spinner) && type.Category == "Scripts");

            // Abstract, or asked to stay out.
            Assert.DoesNotContain(all, type => type.Type == typeof(Collider));
            Assert.DoesNotContain(all, type => type.Type == typeof(Hidden));
        }

        [Fact]
        public void Every_engine_component_has_an_icon_of_its_own_and_scripts_have_the_code_icon()
        {
            Gui.Icon gear = Gui.Icons.Component;
            List<string> without = ComponentCatalog.All
                .Where(type => type.Type.Assembly == typeof(Component).Assembly && Gui.ComponentIcons.For(type.Type) == gear)
                .Select(type => type.Name)
                .ToList();
            Assert.Empty(without);

            Gui.Icon code = Gui.Icons.Code;
            Assert.True(Gui.ComponentIcons.For(typeof(Spinner)) == code);
        }

        [Theory]
        [InlineData("CastShadows", "Cast Shadows")]
        [InlineData("UIScale", "UI Scale")]
        [InlineData("_speed", "Speed")]
        [InlineData("Layer2Mask", "Layer 2 Mask")]
        [InlineData("fieldOfView", "Field Of View")]
        public void Names_from_code_read_as_words(string name, string words)
        {
            Assert.Equal(words, ComponentCatalog.SplitWords(name));
        }

        [Fact]
        public void A_light_shows_its_values_in_the_order_they_are_written()
        {
            IReadOnlyList<InspectedMember> members = InspectedMember.Of(typeof(Light));

            Assert.Equal(["Type", "Color", "Intensity", "Mode", "Size", "Range", "Spot Angle", "Spot Softness", "Fog Strength", "Cast Shadows"], members.Select(m => m.Name));
            Assert.Equal(MemberKind.Enum, members[0].Kind);
            Assert.Equal(MemberKind.Color, members[1].Kind);
        }

        [Fact]
        public void A_light_only_shows_what_its_type_uses()
        {
            IReadOnlyList<InspectedMember> members = InspectedMember.Of(typeof(Light));
            string[] Shown(Light light) => members.Where(m => m.IsShown(light)).Select(m => m.Name).ToArray();
            var light = new Light();

            Assert.Equal(["Type", "Color", "Intensity", "Mode", "Fog Strength", "Cast Shadows"], Shown(light));

            light.Type = LightType.Point;
            Assert.Equal(["Type", "Color", "Intensity", "Mode", "Range", "Fog Strength", "Cast Shadows"], Shown(light));

            light.Type = LightType.Spot;
            Assert.Equal(["Type", "Color", "Intensity", "Mode", "Range", "Spot Angle", "Spot Softness", "Fog Strength", "Cast Shadows"], Shown(light));

            // A light that bakes has a size, for how soft its baked shadows are.
            light.Mode = LightMode.Baked;
            Assert.Equal(["Type", "Color", "Intensity", "Mode", "Size", "Range", "Spot Angle", "Spot Softness", "Fog Strength", "Cast Shadows"], Shown(light));
        }

        [Fact]
        public void A_scripts_fields_show_with_their_ranges_and_tooltips_and_hidden_ones_do_not()
        {
            IReadOnlyList<InspectedMember> members = InspectedMember.Of(typeof(Spinner));

            Assert.Equal(["Speed", "Clockwise"], members.Select(m => m.Name));
            Assert.Equal(720f, members[0].Range!.Max);
            Assert.Equal("Degrees a second.", members[0].Tooltip);
        }

        [Fact]
        public void A_material_shows_inside_its_mesh_renderer()
        {
            InspectedMember material = InspectedMember.Of(typeof(MeshRenderer)).Single(m => m.Name == "Material");
            Assert.Equal(MemberKind.Nested, material.Kind);

            InspectedMember roughness = InspectedMember.Of(typeof(Material)).Single(m => m.Name == "Roughness");
            Assert.Equal((0f, 1f), (roughness.Range!.Min, roughness.Range.Max));
        }
    }
}
