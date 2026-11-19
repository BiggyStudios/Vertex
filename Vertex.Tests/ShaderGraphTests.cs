using System.Numerics;

using Silk.NET.Shaderc;

using Vertex.Graphs;
using Vertex.Rendering;
using Vertex.Rendering.Shaders;
using Vertex.Rendering.Vulkan;
using Vertex.Serialization;
using Vertex.ShaderGraphs;

namespace Vertex.Tests
{
    /// <summary>Surface shaders on materials, written in GLSL or made in shader graphs, and post effects made in shader graphs.</summary>
    public class ShaderGraphTests : EngineTest
    {
        private const string Lava = """
            // Glowing cracks that crawl.
            property color Glow = #FF6010;
            property float Speed = 1 [0, 4];   // How fast the cracks crawl
            property texture Cracks = "Textures/Cracks.tga";
            property texture Bumps = normal;

            void surface(inout Surface s)
            {
                float crack = sampleData(Cracks, uv + vec2(time * Speed * 0.1, 0.0)).r;
                s.emission = Glow.rgb * crack * 4.0;
                s.normal = surfaceToWorld(sampleNormal(Bumps, uv, 1.0));
                s.roughness = mix(s.roughness, 0.2, crack) + worldPosition.x * 0.0 + objectPosition.x * 0.0 + screenPosition.x * 0.0 +
                              viewDirection.x * 0.0 + worldNormal.x * 0.0 + materialSurface.metallic * 0.0;
            }
            """;

        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;
        private readonly GameObject _camera;
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N"));
        private readonly string[] _assetFolders = Application.AssetFolders;

        public ShaderGraphTests()
        {
            _renderer = new Renderer(_backend);
            _renderer.Resize(320, 180);
            _camera = new GameObject("Camera");
            _camera.AddComponent<Camera>();
            Directory.CreateDirectory(Path.Combine(_folder, "Textures"));
            Application.AssetFolders = [_folder];
        }

        public override void Dispose()
        {
            base.Dispose();
            Application.AssetFolders = _assetFolders;
            Directory.Delete(_folder, recursive: true);
        }

        /// <summary>An uncompressed TGA image of that size, grey: the simplest image file to write.</summary>
        private static byte[] Tga(int width, int height)
        {
            var file = new byte[18 + width * height * 4];
            file[2] = 2;
            file[12] = (byte)width;
            file[13] = (byte)(width >> 8);
            file[14] = (byte)height;
            file[15] = (byte)(height >> 8);
            file[16] = 32;
            file[17] = 0x28;
            for (int i = 18; i < file.Length; i++) file[i] = 128;
            return file;
        }

        private static byte[] CompileForVulkan(string name, string fragment)
        {
            using var compiler = new VkShaderCompiler(null!);
            return compiler.CompileNow(name, ShaderKind.FragmentShader, ShaderLibrary.Build(ShaderTarget.Vulkan, fragment));
        }

        /// <summary>The fragment shader a shader is drawn with: a surface shader's lit one, or a post effect's.</summary>
        private static string Fragment(Shader shader) =>
            shader.IsSurface ? ShaderLibrary.LitFragmentWith(shader) : ShaderLibrary.EffectFragment(shader);

        // ---------------------------------------------------------------- Surface shaders written in GLSL

        [Fact]
        public void A_surface_shader_is_one_for_materials_and_its_textures_are_settings_of_their_own()
        {
            Shader shader = Shader.FromCode(Lava, "Lava.glsl");

            Assert.Null(shader.Problem);
            Assert.True(shader.IsSurface);
            Assert.False(shader.IsPostEffect);
            Assert.Equal(["Glow", "Speed", "Cracks", "Bumps"], shader.Properties.Select(p => p.Name));

            // Values and textures are counted separately.
            Assert.Equal([0, 1, 0, 1], shader.Properties.Select(p => p.Slot));
            ShaderProperty cracks = shader.Property("Cracks")!;
            Assert.Equal(ShaderPropertyType.Texture, cracks.Type);
            Assert.Equal("Textures/Cracks.tga", cracks.DefaultTexture);
            Assert.Equal(TextureFallback.Normal, shader.Property("Bumps")!.Fallback);
            Assert.Contains("#define Cracks shaderTexture0", shader.Body);

            Shader wrong = Shader.FromCode("property texture Grit = speckled;\nvoid surface(inout Surface s) {}", "Grit.glsl");
            Assert.Contains("Grit.glsl:1:", wrong.Problem);
        }

        [Fact]
        public void The_standard_scene_shaders_and_ones_with_a_surface_shader_in_compile_for_Vulkan_and_errors_name_the_line()
        {
            Assert.NotEmpty(CompileForVulkan("lit.frag", ShaderLibrary.LitFragment));
            Assert.NotEmpty(CompileForVulkan("unlit.frag", ShaderLibrary.UnlitFragment));

            Shader lava = Shader.FromCode(Lava, "Lava.glsl");
            Assert.NotEmpty(CompileForVulkan(lava.Name, ShaderLibrary.LitFragmentWith(lava)));
            Assert.NotEmpty(CompileForVulkan(lava.Name, ShaderLibrary.UnlitFragmentWith(lava)));

            // The mistake is on line 3 of the file.
            Shader broken = Shader.FromCode("property float Speed = 1;\nvoid surface(inout Surface s)\n{ s.color = vec3(Sped); }", "Broken.glsl");
            var error = Assert.Throws<InvalidOperationException>(() => CompileForVulkan(broken.Name, ShaderLibrary.LitFragmentWith(broken)));
            Assert.Contains("Broken.glsl:3:", error.Message);
        }

        [Fact]
        public void A_materials_shader_settings_are_handed_over_with_colours_made_linear_and_textures_as_set_or_the_shaders_own()
        {
            File.WriteAllBytes(Path.Combine(_folder, "Textures", "Cracks.tga"), Tga(2, 2));
            var material = new Material { Shader = Shader.FromCode(Lava, "Lava.glsl") };
            material.Set("Speed", 3f);

            MaterialUniforms data = MaterialUniforms.Create(material, material.Shader);
            Assert.Equal(3f, data.Values[1].X);
            Vector3 glow = FrameUniforms.ToLinear(new Color(1f, 0x60 / 255f, 0x10 / 255f));
            Assert.Equal(glow.Y, data.Values[0].Y, 4);

            // Cracks starts as its file; Bumps, with nothing, is a flat normal map; the other two slots are plain.
            Texture[] textures = material.Shader.PackTextures(material.Values);
            Assert.Equal(Shader.MaxTextures, textures.Length);
            Assert.Equal(2, textures[0].Width);
            Assert.Equal([128, 128, 255, 255], textures[1].Pixels);

            var mine = new Texture(1, 1, [9, 9, 9, 255]);
            material.SetTexture("Cracks", mine);
            Assert.Same(mine, material.Shader.PackTextures(material.Values)[0]);
            Assert.Same(mine, material.GetTexture("Cracks"));
        }

        [Fact]
        public void A_material_draws_with_its_surface_shader_only_if_it_is_one_and_says_once_if_not()
        {
            var lava = new Material { Shader = Shader.FromCode(Lava, "Lava.glsl") };
            Assert.Same(lava.Shader, Renderer.SurfaceShader(lava));

            var wrong = new Material { Shader = Shader.FromCode("vec4 effect(vec2 uv) { return picture(uv); }", "Invert.glsl") };
            var logged = new List<LogEntry>();
            Log.Written += logged.Add;
            try
            {
                Assert.Null(Renderer.SurfaceShader(wrong));
                Assert.Null(Renderer.SurfaceShader(wrong));
            }
            finally
            {
                Log.Written -= logged.Add;
            }

            Assert.Single(logged, e => e.Level == LogLevel.Error && e.Message.Contains("Invert.glsl is a post effect"));

            // And the other way round: a surface shader on a camera isn't run.
            _camera.AddComponent<PostEffect>().Shader = lava.Shader;
            Scene.Active.Update();
            _renderer.Render(Scene.Active);
            Assert.Empty(_backend.LastPostEffects);
        }

        [Fact]
        public void A_materials_shader_and_its_settings_are_saved_with_it()
        {
            string path = Path.Combine(_folder, "Lava.glsl");
            File.WriteAllText(path, Lava);
            File.WriteAllBytes(Path.Combine(_folder, "Textures", "Cracks.tga"), Tga(2, 2));
            var material = new Material { Shader = Assets.LoadShader(path) };
            material.Set("Speed", 2f);
            material.SetTexture("Bumps", Assets.LoadTexture(Path.Combine(_folder, "Textures", "Cracks.tga")));

            string json = SceneFile.SaveMaterial(material, _folder);
            var read = new Material();
            SceneFile.LoadMaterial(json, read, _folder);

            Assert.Same(material.Shader, read.Shader);
            Assert.Equal(2f, read.Get("Speed").X);
            Assert.Same(material.GetTexture("Bumps"), read.GetTexture("Bumps"));
            Assert.False(read.IsSet("Glow"));

            // A copy has its own settings.
            Material copy = read.Copy();
            copy.Set("Speed", 4f);
            Assert.Equal(2f, read.Get("Speed").X);
        }

        // ---------------------------------------------------------------- Shader graphs

        /// <summary>A graph: Noise scaled by a Glow property into a Surface's emission, and a Fresnel into its roughness.</summary>
        private static Graph LavaGraph(out GraphNode surface, out GraphNode multiply)
        {
            var graph = new Graph(ShaderNodes.Kind);
            graph.Variables.Add(new GraphVariable("Glow", PortType.Color, new Color(1f, 0.4f, 0.1f)));
            graph.Variables.Add(new GraphVariable("Crack Scale", PortType.Number, 6f));

            surface = graph.AddNode("Output/Surface", new Vector2(600f, 0f));
            GraphNode glow = graph.AddNode("Properties/Get Property", new Vector2(0f, 0f));
            glow.Values["Variable"] = "Glow";
            GraphNode scale = graph.AddNode("Properties/Get Property", new Vector2(0f, 100f));
            scale.Values["Variable"] = "Crack Scale";
            GraphNode noise = graph.AddNode("Noise/Noise", new Vector2(200f, 100f));
            multiply = graph.AddNode("Math/Multiply", new Vector2(400f, 0f));
            GraphNode fresnel = graph.AddNode("Color/Fresnel", new Vector2(400f, 200f));

            Assert.True(graph.Link(scale.Id, "Value", noise.Id, "Scale"));
            Assert.True(graph.Link(glow.Id, "Value", multiply.Id, "A"));
            Assert.True(graph.Link(noise.Id, "Noise", multiply.Id, "B"));
            Assert.True(graph.Link(multiply.Id, "Result", surface.Id, "Emission"));
            Assert.True(graph.Link(fresnel.Id, "Fresnel", surface.Id, "Roughness"));
            return graph;
        }

        [Fact]
        public void A_graph_ending_in_a_surface_is_a_surface_shader_with_the_graphs_properties_and_it_compiles()
        {
            Graph graph = LavaGraph(out _, out _);
            Shader shader = Shader.FromGraph(graph);

            Assert.Null(shader.Problem);
            Assert.True(shader.IsSurface);
            Assert.Same(graph, shader.Graph);
            Assert.Equal(["Glow", "Crack Scale"], shader.Properties.Select(p => p.Name));
            Assert.Equal(new Vector4(1f, 0.4f, 0.1f, 1f), shader.Property("Glow")!.Default);
            Assert.Equal(6f, shader.Property("Crack Scale")!.Default.X);

            // A colour times a number is a colour; only what's linked is written.
            Assert.Contains("void surface(inout Surface s)", shader.Body);
            Assert.Contains("s.emission =", shader.Body);
            Assert.Contains("s.roughness =", shader.Body);
            Assert.DoesNotContain("s.color =", shader.Body);
            Assert.Contains("vec4 n", shader.Body);
            Assert.NotEmpty(CompileForVulkan("Lava.shadergraph", ShaderLibrary.LitFragmentWith(shader)));

            // A material using it gets its properties' values, by name.
            var material = new Material { Shader = shader };
            material.Set("Crack Scale", 2f);
            Assert.Equal(2f, MaterialUniforms.Create(material, shader).Values[1].X);
        }

        [Fact]
        public void A_graphs_shader_follows_the_graph_but_isnt_built_again_when_only_nodes_move()
        {
            Graph graph = LavaGraph(out GraphNode surface, out GraphNode multiply);
            Shader shader = Shader.FromGraph(graph);
            int version = shader.Version;

            surface.Position += new Vector2(40f, 0f);
            graph.Changed();
            Assert.Equal(version, shader.Version);

            graph.Variables[1].Value = 9f;
            graph.Changed();
            Assert.Equal(version, shader.Version);
            Assert.Equal(9f, shader.Property("Crack Scale")!.Default.X);

            graph.Link(multiply.Id, "Result", surface.Id, "Color");
            Assert.True(shader.Version > version);
            Assert.Contains("s.color =", shader.Body);
        }

        [Fact]
        public void A_post_effect_graph_reads_the_picture_and_compiles()
        {
            var graph = new Graph(ShaderNodes.Kind);
            graph.Variables.Add(new GraphVariable("Steps", PortType.Number, 4f));
            GraphNode output = graph.AddNode("Output/Post Effect", Vector2.Zero);
            GraphNode picture = graph.AddNode("Input/Picture", Vector2.Zero);
            GraphNode pixelate = graph.AddNode("UV/Pixelate", Vector2.Zero);
            GraphNode posterize = graph.AddNode("Math/Posterize", Vector2.Zero);
            GraphNode steps = graph.AddNode("Properties/Get Property", Vector2.Zero);
            steps.Values["Variable"] = "Steps";

            Assert.True(graph.Link(pixelate.Id, "UV", picture.Id, "UV"));
            Assert.True(graph.Link(picture.Id, "Color", posterize.Id, "Value"));
            Assert.True(graph.Link(steps.Id, "Value", posterize.Id, "Steps"));
            Assert.True(graph.Link(posterize.Id, "Result", output.Id, "Color"));

            Shader shader = Shader.FromGraph(graph);
            Assert.Null(shader.Problem);
            Assert.True(shader.IsPostEffect);
            Assert.Contains("vec4 effect(vec2 uv)", shader.Body);
            Assert.Contains("picture(", shader.Body);
            Assert.NotEmpty(CompileForVulkan("Retro.shadergraph", ShaderLibrary.EffectFragment(shader)));

            // With nothing linked, the picture goes through as it was.
            var empty = new Graph(ShaderNodes.Kind);
            empty.AddNode("Output/Post Effect", Vector2.Zero);
            Assert.Contains("return picture(uv);", Shader.FromGraph(empty).Body);
        }

        [Fact]
        public void Every_node_writes_GLSL_that_compiles_in_each_kind_of_graph_it_works_in()
        {
            NodeLibrary library = ShaderNodes.Library;
            int compiled = 0;
            foreach (ShaderNode type in library.All.Cast<ShaderNode>().Where(t => !t.IsOutput))
            {
                foreach (ShaderPurpose purpose in new[] { ShaderPurpose.Surface, ShaderPurpose.PostEffect })
                {
                    if (type.Only is { } only && only != purpose) continue;

                    // Each of its outputs linked into the graph's output, through a Branch for true/false ones; the
                    // first input given a vector, to try the maths on more than a number.
                    var graph = new Graph(ShaderNodes.Kind);
                    graph.Variables.Add(new GraphVariable("Tint", PortType.Color, Color.White));
                    GraphNode output = graph.AddNode(purpose == ShaderPurpose.Surface ? "Output/Surface" : "Output/Post Effect", Vector2.Zero);
                    GraphNode node = graph.AddNode(type.Id, Vector2.Zero);
                    if (type.Id == "Properties/Get Property") node.Values["Variable"] = "Tint";

                    Port? first = type.Inputs.FirstOrDefault(p => p.Linkable && p.Type != PortType.Texture && p.Type != PortType.Boolean);
                    if (first != null)
                    {
                        GraphNode vector = graph.AddNode("Constant/Vector 3", Vector2.Zero);
                        Assert.True(graph.Link(vector.Id, "Value", node.Id, first.Name), $"{type.Id}: can't link a vector into {first.Name}");
                    }

                    GraphNode add = graph.AddNode("Math/Add", Vector2.Zero);
                    string into = "A";
                    foreach (Port port in type.Outputs)
                    {
                        GraphNode from = node;
                        string fromPort = port.Name;
                        if (port.Type == PortType.Boolean)
                        {
                            from = graph.AddNode("Logic/Branch", Vector2.Zero);
                            Assert.True(graph.Link(node.Id, port.Name, from.Id, "Condition"));
                            fromPort = "Result";
                        }

                        Assert.True(graph.Link(from.Id, fromPort, add.Id, into), $"{type.Id}: can't link {port.Name} into Add");
                        into = into == "A" ? "B" : "A";
                    }

                    Assert.True(graph.Link(add.Id, "Result", output.Id, purpose == ShaderPurpose.Surface ? "Emission" : "Color"));

                    Shader shader = Shader.FromGraph(graph);
                    Assert.True(shader.Problem == null, $"{type.Id} in a {purpose} graph: {shader.Problem}");
                    try
                    {
                        CompileForVulkan($"{type.Id}.shadergraph", Fragment(shader));
                    }
                    catch (InvalidOperationException exception)
                    {
                        Assert.Fail($"{type.Id} in a {purpose} graph didn't compile:\n{exception.Message}\n{shader.Body}");
                    }

                    compiled++;
                }
            }

            Assert.True(compiled > 70, $"Only {compiled} compiled");
        }

        [Fact]
        public void What_a_graph_gets_wrong_shows_on_its_nodes_and_keeps_the_shader_from_being_drawn()
        {
            // No output at all.
            var none = new Graph(ShaderNodes.Kind);
            none.AddNode("Math/Add", Vector2.Zero);
            Assert.Contains("needs an output", Shader.FromGraph(none).Problem);
            Assert.Null(Shader.FromGraph(none).Purpose);

            // A post effect's node in a material's graph.
            var graph = new Graph(ShaderNodes.Kind);
            GraphNode surface = graph.AddNode("Output/Surface", Vector2.Zero);
            GraphNode picture = graph.AddNode("Input/Picture", Vector2.Zero);
            graph.Link(picture.Id, "Color", surface.Id, "Color");
            Shader shader = Shader.FromGraph(graph);
            Assert.Contains("Picture", shader.Problem);
            Assert.Contains("post effect", graph.Error(picture.Id));
            Assert.Null(Renderer.SurfaceShader(new Material { Shader = shader }));

            // Linked round in a loop.
            var loop = new Graph(ShaderNodes.Kind);
            GraphNode output = loop.AddNode("Output/Surface", Vector2.Zero);
            GraphNode a = loop.AddNode("Math/Add", Vector2.Zero);
            GraphNode b = loop.AddNode("Math/Add", Vector2.Zero);
            loop.Link(a.Id, "Result", b.Id, "A");
            loop.Link(b.Id, "Result", a.Id, "A");
            loop.Link(a.Id, "Result", output.Id, "Emission");
            Assert.Contains("loop", Shader.FromGraph(loop).Problem);

            // Put right, the problems go.
            graph.Unlink(graph.Links.Single());
            Assert.Null(shader.Problem);
            Assert.Null(graph.Error(picture.Id));
        }

        [Fact]
        public void Numbers_vectors_and_colours_link_to_each_other_in_a_shader_graph_and_textures_only_to_textures()
        {
            NodeLibrary library = ShaderNodes.Library;
            Assert.True(library.CanLink(ShaderNodes.Vector2, PortType.Color));
            Assert.True(library.CanLink(PortType.Color, PortType.Number));
            Assert.True(library.CanLink(PortType.Vector, ShaderNodes.Value));
            Assert.True(library.CanLink(PortType.Boolean, PortType.Number));
            Assert.False(library.CanLink(PortType.Boolean, PortType.Color));
            Assert.False(library.CanLink(PortType.Texture, PortType.Color));
            Assert.True(library.CanLink(PortType.Texture, PortType.Texture));
            Assert.Contains(PortType.Texture, library.VariableTypes);
        }

        [Fact]
        public void A_shader_graph_file_loads_as_a_shader_sharing_the_graph_and_scenes_refer_to_the_file()
        {
            string path = Path.Combine(_folder, "Lava.shadergraph");
            LavaGraph(out _, out _).Save(path);

            Shader shader = Assets.LoadShader(path);
            Graph graph = Assets.LoadGraph(path);
            Assert.Same(graph, shader.Graph);
            Assert.Equal("Lava.shadergraph", shader.Name);
            Assert.True(shader.IsSurface);

            // Its saved variables come back with their types, even before anything else has used shader graphs.
            Graph again = Graph.Load(path);
            Assert.Equal(PortType.Color, again.Variable("Glow")!.Type);

            var material = new Material { Shader = shader };
            string json = SceneFile.SaveMaterial(material, _folder);
            Assert.Contains("Lava.shadergraph", json);
            var read = new Material();
            SceneFile.LoadMaterial(json, read, _folder);
            Assert.Same(shader, read.Shader);
        }
    }
}
