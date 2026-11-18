using Vertex.Rendering;

namespace Vertex.Editor.Tests
{
    /// <summary>Particle effects playing in the scene view while they're selected.</summary>
    public class EffectPreviewTests : EditorTest
    {
        [Fact]
        public void A_selected_effect_plays_in_the_scene_view_and_stops_when_it_is_not()
        {
            var app = new EditorApp();
            GameObject torch = app.Context.Create("Torch");
            GameObject flame = app.Context.Create("Flame", parent: torch.Transform);
            var particles = flame.AddComponent<ParticleSystem>();
            particles.Rate = 60f;
            app.Context.Selection.Clear();
            Frame(app, 10);
            Assert.Equal(0, particles.Count);

            // Selecting the torch plays what's under it too.
            app.Context.Selection.Select(torch);
            Frame(app, 30);
            Assert.True(particles.Count > 10);
            Assert.Same(particles, Assert.Single(app.SceneView.PreviewedEffects));
            Assert.True(particles.Emitting);

            app.Context.Selection.Clear();
            Frame(app, 2);
            Assert.Equal(0, particles.Count);
            Assert.Empty(app.SceneView.PreviewedEffects);
        }

        [Fact]
        public void A_burst_shown_on_its_own_goes_off_again_once_it_has_finished_without_its_settings_changing()
        {
            var app = new EditorApp();
            var particles = app.Context.Create("Boom").AddComponent<ParticleSystem>();
            particles.Burst = 25;
            particles.Lifetime = 0.2f;
            particles.Loop = false;
            particles.Duration = 0.1f;

            app.Context.Selection.Select(particles.GameObject);
            Frame(app, 3);
            Assert.Equal(25, particles.Count);

            // Gone after its short life, then back a moment later.
            Frame(app, 30);
            Assert.Equal(0, particles.Count);
            bool back = false;
            for (int i = 0; i < 60 && !back; i++)
            {
                Frame(app);
                back = particles.Count > 0;
            }

            Assert.True(back);
            Assert.Equal(0f, particles.Rate);
            Assert.False(particles.Loop);
        }
    }
}
