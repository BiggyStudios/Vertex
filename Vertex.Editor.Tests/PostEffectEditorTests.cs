using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Post effects and their shaders in the editor.</summary>
    public class PostEffectEditorTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));

        public PostEffectEditorTests()
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
        public void A_new_post_effect_is_a_shader_that_works_with_settings_to_start_from()
        {
            var context = new EditorContext();
            string path = context.CreatePostEffect(_folder);

            Assert.Equal(".glsl", Path.GetExtension(path));
            Assert.Equal(AssetKind.Shader, AssetDatabase.KindOf(path));
            Shader shader = Assets.LoadShader(path);
            Assert.Null(shader.Problem);
            Assert.True(shader.IsPostEffect);
            Assert.Equal(["Brightness", "Saturation", "Tint"], shader.Properties.Select(p => p.Name));
        }

        [Fact]
        public void A_shaders_setting_typed_into_the_inspector_is_kept_on_the_effect_and_undone_in_one_step()
        {
            var context = new EditorContext();
            PostEffect effect = new GameObject("Camera").AddComponent<PostEffect>();
            effect.Shader = Shader.FromCode("property float Lines = 360;  // Rows of big pixels\nvec4 effect(vec2 uv) { return picture(uv) * Lines; }", "Dots.glsl");
            var grid = new PropertyGrid(context);
            void Draw() => grid.Draw(effect, 0f, 0f, 400f);

            int id = Ui.Id(RuntimeHelpers.GetHashCode(effect), "shader:Lines");
            UiFrame(Draw);
            NumberField.BeginTyping(id);
            UiFrame(Draw);
            foreach (char c in "240") InputSystem.TypeCharacter(c);
            UiFrame(Draw);
            InputSystem.PressKey(Key.Enter);
            UiFrame(Draw);

            Assert.Equal(240f, effect.Get("Lines").X);
            context.History.Undo();
            Assert.False(effect.IsSet("Lines"));
            Assert.Equal(360f, effect.Get("Lines").X);
            context.History.Redo();
            Assert.Equal(240f, effect.Get("Lines").X);
        }

        [Fact]
        public void The_list_of_settings_is_kept_out_of_the_inspectors_own_rows()
        {
            Assert.DoesNotContain(InspectedMember.Of(typeof(PostEffect)), m => m.Name == "Values");
            Assert.Equal(MemberKind.Asset, InspectedMember.Of(typeof(PostEffect)).Single(m => m.Name == "Shader").Kind);
            Assert.Equal(AssetKind.Shader, PropertyGrid.AssetKindOf(typeof(Shader))!.Value.Kind);
        }

        [Fact]
        public void The_scene_view_shows_the_game_cameras_effects_unless_theyre_switched_off()
        {
            var app = new EditorApp();
            GameObject camera = app.Context.Create("Main Camera");
            camera.AddComponent<Camera>();
            Frame(app, 2);

            RenderView view = Assert.Single(app.Views);
            Assert.Same(camera.GetComponent<Camera>(), view.EffectsFrom);

            app.Context.Preferences.ShowEffects = false;
            Assert.Null(Assert.Single(app.Views).EffectsFrom);
        }
    }
}
