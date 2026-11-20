using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Graphs;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.ShaderGraphs;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Shader graphs and surface shaders in the editor.</summary>
    public class ShaderGraphEditorTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));

        public ShaderGraphEditorTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (string file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories)) Assets.Forget(file);
            Directory.Delete(_folder, recursive: true);
        }

        private static void UiFrame(Action draw)
        {
            Time.Advance(1.0 / 60.0);
            Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
            Ui.BeginFrame(1f / 60f);
            draw();
            Ui.EndFrame();
            Canvas.EndFrame();
            InputSystem.EndFrame();
        }

        [Fact]
        public void New_shader_graphs_start_as_working_shaders_for_a_material_and_for_a_camera()
        {
            var context = new EditorContext();
            string surface = context.CreateShaderGraph(_folder);
            string effect = context.CreatePostEffectGraph(_folder);

            Assert.Equal(AssetKind.ShaderGraph, AssetDatabase.KindOf(surface));
            Shader material = Assets.LoadShader(surface);
            Assert.Null(material.Problem);
            Assert.True(material.IsSurface);
            Assert.Equal(["Tint", "Rim"], material.Properties.Select(p => p.Name));

            Shader camera = Assets.LoadShader(effect);
            Assert.Null(camera.Problem);
            Assert.True(camera.IsPostEffect);
            Assert.Equal(["Brightness", "Saturation"], camera.Properties.Select(p => p.Name));
        }

        [Fact]
        public void A_shader_graph_opens_in_a_tab_whose_preview_the_renderer_draws_with_the_graphs_own_shader()
        {
            var app = new EditorApp();
            string path = app.Context.CreateShaderGraph(_folder);
            app.Context.OpenGraph(path);
            Frame(app, 2);

            Assert.NotNull(app.SceneView.ShowingGraph);
            RenderView preview = Assert.Single(app.Views);
            Assert.NotNull(preview.Scene);
            MeshRenderer shape = preview.Scene!.FindComponents<MeshRenderer>().Single(r => r.GameObject.ActiveInHierarchy);
            Assert.Same(Assets.LoadShader(path), shape.Material.Shader);

            // A change to the graph is what the preview draws next.
            Graph graph = app.SceneView.ShowingGraph!.Graph;
            int version = Assets.LoadShader(path).Version;
            GraphNode surface = graph.Nodes.Single(n => n.Type == "Output/Surface");
            GraphNode noise = graph.AddNode("Noise/Noise", Vector2.Zero);
            graph.Link(noise.Id, "Noise", surface.Id, "Roughness");
            Assert.True(Assets.LoadShader(path).Version > version);
        }

        [Fact]
        public void A_shader_slot_offers_only_shaders_for_what_it_is_on()
        {
            var context = new EditorContext();
            string surface = context.CreateShaderGraph(_folder);
            string effect = context.CreatePostEffect(_folder);

            var material = InspectedMember.Of(typeof(Material)).Single(m => m.Name == "Shader");
            var camera = InspectedMember.Of(typeof(PostEffect)).Single(m => m.Name == "Shader");
            Assert.Equal(ShaderPurpose.Surface, material.Member.GetCustomAttribute<ShaderForAttribute>()!.Purpose);
            Assert.Equal(ShaderPurpose.PostEffect, camera.Member.GetCustomAttribute<ShaderForAttribute>()!.Purpose);
            Assert.True(Assets.LoadShader(surface).IsSurface);
            Assert.True(Assets.LoadShader(effect).IsPostEffect);
        }

        [Fact]
        public void A_materials_shader_settings_show_under_it_and_a_change_is_undone_in_one_step()
        {
            var context = new EditorContext();
            string path = context.CreateShaderGraph(_folder);
            var material = new Material { Shader = Assets.LoadShader(path) };
            material.Shader.Graph!.Variables.Add(new GraphVariable("Speed", PortType.Number, 2f));
            material.Shader.Graph.Changed();
            var grid = new PropertyGrid(context);
            void Draw() => grid.Draw(material, 0f, 0f, 400f);

            int id = Ui.Id(RuntimeHelpers.GetHashCode(material), "shader:Speed");
            UiFrame(Draw);
            NumberField.BeginTyping(id);
            UiFrame(Draw);
            foreach (char c in "5") InputSystem.TypeCharacter(c);
            UiFrame(Draw);
            InputSystem.PressKey(Key.Enter);
            UiFrame(Draw);

            Assert.Equal(5f, material.Get("Speed").X);
            context.History.Undo();
            Assert.False(material.IsSet("Speed"));
            Assert.Equal(2f, material.Get("Speed").X);
        }

        [Fact]
        public void In_a_shader_graph_a_vector_links_into_a_number_and_a_surfaces_unlinked_inputs_keep_the_materials_values()
        {
            var graph = new Graph(ShaderNodes.Kind);
            GraphNode vector = graph.AddNode("Constant/Vector 3", Vector2.Zero);
            GraphNode add = graph.AddNode("Math/Add", Vector2.Zero);
            GraphNode surface = graph.AddNode("Output/Surface", Vector2.Zero);
            Assert.True(graph.Link(vector.Id, "Value", add.Id, "A"));
            Assert.True(graph.Link(add.Id, "Result", surface.Id, "Roughness"));

            Port color = ShaderNodes.Library.Find("Output/Surface")!.Input("Color")!;
            Assert.Equal("Material's", color.WhenUnlinked);
        }
    }
}
