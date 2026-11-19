using System.Numerics;

using Silk.NET.Shaderc;

using Vertex.Rendering;
using Vertex.Rendering.Shaders;
using Vertex.Rendering.Vulkan;
using Vertex.Serialization;

namespace Vertex.Tests
{
    /// <summary>Shaders written in GLSL for a game, and post effects made with them.</summary>
    public class PostEffectTests : EngineTest
    {
        private const string Invert = """
            // Turns the picture into its negative.
            property float Amount = 1 [0, 1];   // How far to go
            property color Tint = #FF8000;
            property vec2 Shift = (0.5, 0.25);
            property bool Flip = true;
            property int Steps = 4;

            vec4 effect(vec2 uv)
            {
                vec3 colour = picture(uv + Shift * 0.0).rgb;
                if (Flip) colour = mix(colour, 1.0 - colour, Amount);
                return vec4(colour * Tint.rgb + float(Steps) * 0.0 + distanceAt(uv) * 0.0 + time * 0.0 + screenSize.x * 0.0, 1.0);
            }
            """;

        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;
        private readonly GameObject _camera;
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N"));
        private readonly string[] _assetFolders = Application.AssetFolders;

        public PostEffectTests()
        {
            _renderer = new Renderer(_backend);
            _renderer.Resize(1280, 720);
            _camera = new GameObject("Camera");
            _camera.AddComponent<Camera>();
            Directory.CreateDirectory(Path.Combine(_folder, "Shaders"));
            Application.AssetFolders = [_folder];
        }

        public override void Dispose()
        {
            base.Dispose();
            Application.AssetFolders = _assetFolders;
            Directory.Delete(_folder, recursive: true);
        }

        private void Render()
        {
            Scene.Active.Update();
            _renderer.Render(Scene.Active);
        }

        [Fact]
        public void A_shader_reads_its_settings_with_their_types_starting_values_ranges_and_tooltips()
        {
            Shader shader = Shader.FromCode(Invert, "Invert.glsl");

            Assert.Null(shader.Problem);
            Assert.True(shader.IsPostEffect);
            Assert.Equal(["Amount", "Tint", "Shift", "Flip", "Steps"], shader.Properties.Select(p => p.Name));

            ShaderProperty amount = shader.Properties[0];
            Assert.Equal(ShaderPropertyType.Float, amount.Type);
            Assert.Equal(1f, amount.Default.X);
            Assert.Equal(0f, amount.Min);
            Assert.Equal(1f, amount.Max);
            Assert.Equal("How far to go", amount.Tooltip);

            Assert.Equal(new Vector4(1f, 128f / 255f, 0f, 1f), shader.Properties[1].Default);
            Assert.Equal(new Vector4(0.5f, 0.25f, 0f, 0f), shader.Properties[2].Default);
            Assert.Equal(1f, shader.Properties[3].Default.X);
            Assert.Equal(ShaderPropertyType.Int, shader.Properties[4].Type);
            Assert.Equal(Enumerable.Range(0, 5), shader.Properties.Select(p => p.Slot));
        }

        [Fact]
        public void A_setting_it_cant_read_or_a_missing_effect_function_is_a_problem_naming_the_line()
        {
            Shader shader = Shader.FromCode("// A broken one.\nproperty float = 2;\nproperty wobble Size = 1;\nvec4 effect(vec2 uv) { return vec4(1.0); }", "Broken.glsl");
            Assert.Contains("Broken.glsl:2:", shader.Problem);
            Assert.Contains("Broken.glsl:3:", shader.Problem);
            Assert.Contains("wobble", shader.Problem);

            Shader notAnEffect = Shader.FromCode("float twice(float x) { return x * 2.0; }", "Helper.glsl");
            Assert.False(notAnEffect.IsPostEffect);
            Assert.Contains("vec4 effect(vec2 uv)", notAnEffect.Problem);
        }

        [Fact]
        public void Settings_become_names_for_their_values_without_moving_the_lines_below()
        {
            Shader shader = Shader.FromCode(Invert, "Invert.glsl");
            string[] written = Invert.Replace("\r\n", "\n").Split('\n');
            string[] body = shader.Body.Split('\n');

            Assert.Equal("#define Amount (effectValues[0].x)", body[1]);
            Assert.Equal("#define Flip (effectValues[3].x > 0.5)", body[4]);
            Assert.Equal("#define Steps int(effectValues[4].x)", body[5]);
            Assert.Equal(written[7], body[7]);
        }

        [Fact]
        public void An_effect_gives_its_shaders_settings_as_set_or_their_starting_values_in_order()
        {
            PostEffect effect = _camera.AddComponent<PostEffect>();
            effect.Shader = Shader.FromCode(Invert, "Invert.glsl");
            effect.Set("Amount", 0.25f);
            effect.Set("Flip", false);

            Render();

            PostEffectData data = Assert.Single(_backend.LastPostEffects);
            Assert.Same(effect.Shader, data.Shader);
            Assert.Equal(0.25f, data.Values[0].X);
            Assert.Equal(new Vector4(1f, 128f / 255f, 0f, 1f), data.Values[1]);
            Assert.Equal(0f, data.Values[3].X);
            Assert.Equal(4f, data.Values[4].X);
            Assert.Equal(Shader.MaxProperties, data.Values.Length);

            effect.Reset("Amount");
            Assert.False(effect.IsSet("Amount"));
            Assert.Equal(1f, effect.Get("Amount").X);
        }

        [Fact]
        public void Only_enabled_effects_with_shaders_that_work_run_in_the_order_theyre_on_the_camera()
        {
            Shader first = Shader.FromCode(Invert, "First.glsl");
            Shader second = Shader.FromCode(Invert, "Second.glsl");
            _camera.AddComponent<PostEffect>().Shader = first;
            _camera.AddComponent<PostEffect>();
            _camera.AddComponent<PostEffect>().Shader = Shader.FromCode("property nonsense;\nvec4 effect(vec2 uv) { return vec4(1.0); }");
            _camera.AddComponent<PostEffect>(effect => { effect.Shader = second; effect.Enabled = false; });
            _camera.AddComponent<PostEffect>().Shader = second;

            Render();

            Assert.Equal([first, second], _backend.LastPostEffects.Select(e => e.Shader));
        }

        [Fact]
        public void A_shader_that_didnt_compile_is_left_out_until_its_code_changes()
        {
            PostEffect effect = _camera.AddComponent<PostEffect>();
            effect.Shader = Shader.FromCode(Invert, "Invert.glsl");
            effect.Shader.CompileProblem = "Invert.glsl:9: error";

            Render();
            Assert.Empty(_backend.LastPostEffects);

            int version = effect.Shader.Version;
            effect.Shader.SetCode(Invert);
            Assert.True(effect.Shader.Version > version);

            Render();
            Assert.Single(_backend.LastPostEffects);
        }

        [Fact]
        public void An_editors_view_can_show_the_effects_of_the_games_camera()
        {
            _camera.AddComponent<PostEffect>().Shader = Shader.FromCode(Invert, "Invert.glsl");
            Scene.Active.Update();
            Camera editorCamera = new GameObject("Editor camera", new Scene()).AddComponent<Camera>();
            var target = new RenderTexture(320, 180);

            // The view is drawn, then the screen (with no camera, here).
            _renderer.Render(Scene.Active, null, [new RenderView(editorCamera, target)]);
            Assert.Equal(target, _backend.Frames[^2].Target);
            Assert.Equal(0, _backend.EffectCounts[^2]);

            _renderer.Render(Scene.Active, null, [new RenderView(editorCamera, target, EffectsFrom: _camera.GetComponent<Camera>())]);
            Assert.Equal(1, _backend.EffectCounts[^2]);
        }

        [Fact]
        public void An_effects_shader_and_settings_are_saved_with_the_scene_and_the_file_is_loaded_once()
        {
            string path = Path.Combine(_folder, "Shaders", "Invert.glsl");
            File.WriteAllText(path, Invert);
            PostEffect effect = _camera.AddComponent<PostEffect>();
            effect.Shader = Assets.LoadShader(path);
            effect.Set("Tint", new Color(0.1f, 0.2f, 0.3f, 1f));

            string json = SceneFile.Save(Scene.Active);
            Assert.Contains("Shaders/Invert.glsl", json);
            var loaded = new Scene();
            SceneFile.LoadJson(json, loaded, _folder);
            loaded.ApplyChanges();

            PostEffect read = loaded.FindComponents<PostEffect>().Single();
            Assert.Same(effect.Shader, read.Shader);
            Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1f), read.Get("Tint"));
            Assert.False(read.IsSet("Amount"));
        }

        [Fact]
        public void A_changed_shader_file_is_read_again_in_place()
        {
            string path = Path.Combine(_folder, "Shaders", "Invert.glsl");
            File.WriteAllText(path, Invert);
            Shader shader = Assets.LoadShader(path);
            int version = shader.Version;

            File.WriteAllText(path, Invert.Replace("property int Steps = 4;", "property int Steps = 8;"));
            Assets.Reload(path);

            Assert.Same(shader, Assets.LoadShader(path));
            Assert.True(shader.Version > version);
            Assert.Equal(8f, shader.Property("Steps")!.Default.X);
        }

        [Fact]
        public void An_effect_compiles_for_Vulkan_and_its_errors_give_the_line_in_its_own_file()
        {
            using var compiler = new VkShaderCompiler(null!);
            Shader shader = Shader.FromCode(Invert, "Invert.glsl");
            byte[] spirv = compiler.CompileNow(shader.Name, ShaderKind.FragmentShader, ShaderLibrary.Build(ShaderTarget.Vulkan, ShaderLibrary.EffectFragment(shader)));
            Assert.NotEmpty(spirv);

            // The mistake is on line 4.
            Shader broken = Shader.FromCode("property float Amount = 1;\nvec4 effect(vec2 uv)\n{\n    return vec4(picture(uv).rgb * Amont, 1.0);\n}", "Broken.glsl");
            var error = Assert.Throws<InvalidOperationException>(() =>
                compiler.CompileNow(broken.Name, ShaderKind.FragmentShader, ShaderLibrary.Build(ShaderTarget.Vulkan, ShaderLibrary.EffectFragment(broken))));
            Assert.Contains("Broken.glsl:4:", error.Message);
            Assert.Contains("Amont", error.Message);
        }
    }
}
