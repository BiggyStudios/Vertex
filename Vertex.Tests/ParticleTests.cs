using System.Numerics;

using Vertex.Rendering;

namespace Vertex.Tests
{
    public class ParticleTests : EngineTest
    {
        private static ParticleSystem Made(Action<ParticleSystem> setUp)
        {
            var particles = new GameObject("Effect").AddComponent<ParticleSystem>();
            particles.Lifetime = 10f;
            particles.Speed = 0f;
            setUp(particles);
            return particles;
        }

        private static List<ParticleData> Drawn(ParticleSystem particles)
        {
            var drawn = new List<ParticleData>();
            particles.Collect(drawn);
            return drawn;
        }

        [Fact]
        public void A_burst_goes_off_when_it_starts_and_again_each_time_it_loops()
        {
            ParticleSystem particles = Made(p =>
            {
                p.Burst = 20;
                p.Duration = 1f;
                p.MaxParticles = 1000;
            });

            particles.Simulate(0.1f);
            Assert.Equal(20, particles.Count);
            particles.Simulate(0.5f);
            Assert.Equal(20, particles.Count);
            particles.Simulate(0.5f);
            Assert.Equal(40, particles.Count);
            Assert.True(particles.Playing);

            // Not looping, it bursts once and is done.
            particles.Loop = false;
            particles.Restart();
            particles.Simulate(0.1f);
            particles.Simulate(2f);
            Assert.Equal(20, particles.Count);
            Assert.False(particles.Playing);
        }

        [Fact]
        public void A_stream_stops_once_its_duration_is_up_unless_it_loops()
        {
            ParticleSystem particles = Made(p =>
            {
                p.Rate = 10f;
                p.Duration = 1f;
                p.Loop = false;
            });

            for (int i = 0; i < 30; i++) particles.Simulate(0.1f);

            Assert.InRange(particles.Count, 9, 11);
            Assert.False(particles.Playing);
        }

        [Fact]
        public void Particles_start_on_the_shape_asked_for()
        {
            ParticleSystem sphere = Made(p =>
            {
                p.Shape = EmitterShape.Sphere;
                p.Radius = 2f;
                p.FromEdge = true;
            });
            sphere.Emit(50);
            Assert.All(Drawn(sphere), d => Assert.Equal(2f, d.Position.Length(), 3));

            // A circle across the way they head (up): flat, within its radius.
            ParticleSystem circle = Made(p =>
            {
                p.Shape = EmitterShape.Circle;
                p.Radius = 1f;
            });
            circle.Emit(50);
            Assert.All(Drawn(circle), d =>
            {
                Assert.Equal(0f, d.Position.Y, 4);
                Assert.True(new Vector2(d.Position.X, d.Position.Z).Length() <= 1.0001f);
            });

            // Heading out from the centre, like an explosion.
            ParticleSystem burst = Made(p =>
            {
                p.Shape = EmitterShape.Sphere;
                p.FromEdge = true;
                p.Outward = true;
                p.Speed = 5f;
                p.Stretch = 1f;
            });
            burst.Emit(30);
            Assert.All(Drawn(burst), d => Assert.True(Vector3.Dot(Vector3.Normalize(d.Velocity), Vector3.Normalize(d.Position)) > 0.999f));
        }

        [Fact]
        public void Particles_turn_and_are_drawn_as_the_look_chosen()
        {
            ParticleSystem particles = Made(p =>
            {
                p.Look = ParticleLook.Square;
                p.Rotation = 90f;
                p.Spin = 180f;
            });

            particles.Emit(1);
            particles.Simulate(0.5f);

            ParticleData drawn = Assert.Single(Drawn(particles));
            Assert.Equal(ParticleLook.Square, drawn.Look);
            Assert.Equal(MathF.PI, drawn.Rotation, 3);
        }

        [Fact]
        public void Fading_in_starts_them_see_through()
        {
            ParticleSystem particles = Made(p =>
            {
                p.FadeIn = 0.5f;
                p.StartColor = Color.White;
                p.EndColor = Color.White;
            });

            particles.Emit(1);
            particles.Simulate(1f);
            Assert.Equal(0.2f, Assert.Single(Drawn(particles)).Color.A, 3);
            particles.Simulate(5f);
            Assert.Equal(1f, Assert.Single(Drawn(particles)).Color.A, 3);
        }

        [Fact]
        public void In_local_space_particles_move_with_the_object()
        {
            ParticleSystem particles = Made(p => p.LocalSpace = true);
            particles.Emit(1);
            particles.Simulate(0.1f);

            particles.Transform.Position = new Vector3(3f, 0f, 0f);
            particles.Simulate(0.1f);
            Assert.Equal(new Vector3(3f, 0f, 0f), Assert.Single(Drawn(particles)).Position);

            // In the world (the usual), they stay where they were let go.
            ParticleSystem world = Made(_ => { });
            world.Emit(1);
            world.Simulate(0.1f);
            world.Transform.Position = new Vector3(3f, 0f, 0f);
            world.Simulate(0.1f);
            Assert.Equal(Vector3.Zero, Assert.Single(Drawn(world)).Position);
        }

        [Fact]
        public void Prewarmed_a_stream_is_already_full_on_its_first_frame()
        {
            ParticleSystem particles = Made(p =>
            {
                p.Rate = 20f;
                p.Lifetime = 1f;
                p.Prewarm = true;
            });

            particles.Simulate(1f / 60f);

            Assert.InRange(particles.Count, 17, 22);
        }

        [Fact]
        public void Turbulence_pushes_still_particles_about()
        {
            ParticleSystem particles = Made(p => p.Turbulence = 4f);
            particles.Emit(5);
            for (int i = 0; i < 10; i++) particles.Simulate(0.05f);

            Assert.Contains(Drawn(particles), d => d.Position.Length() > 0.01f);
        }

        [Fact]
        public void Ranges_and_the_new_settings_are_saved_with_the_scene()
        {
            ParticleSystem particles = Made(p =>
            {
                p.Lifetime = new FloatRange(2f, 3f);
                p.Size = 0.25f;
                p.Look = ParticleLook.Ring;
                p.Burst = 12;
            });

            var loaded = new Scene();
            Serialization.SceneFile.LoadJson(Serialization.SceneFile.Save(Scene.Active), loaded);
            var copy = loaded.Find("Effect")!.GetComponent<ParticleSystem>()!;

            Assert.Equal(new FloatRange(2f, 3f), copy.Lifetime);
            Assert.Equal(new FloatRange(0.25f, 0.25f), copy.Size);
            Assert.Equal(ParticleLook.Ring, copy.Look);
            Assert.Equal(12, copy.Burst);
            _ = particles;
        }
    }
}
