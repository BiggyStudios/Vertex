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

            Assert.Equal(["Type", "Color", "Intensity", "Range", "Cast Shadows"], members.Select(m => m.Name));
            Assert.Equal(MemberKind.Enum, members[0].Kind);
            Assert.Equal(MemberKind.Color, members[1].Kind);
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
