using System.Numerics;

using Vertex.Rendering;

namespace Vertex.Tests
{
    public class GpuParticleTests : EngineTest
    {
        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;

        public GpuParticleTests()
        {
            _renderer = new Renderer(_backend);
            _renderer.Resize(640, 360);
            new GameObject("Camera").AddComponent<Camera>().Transform.Position = new Vector3(0f, 0f, 10f);
        }

        private static GpuParticleSystem AddSystem(string name, int max = 100, float lifetime = 1f)
        {
            GpuParticleSystem system = new GameObject(name).AddComponent<GpuParticleSystem>();
            system.MaxParticles = max;
            system.Lifetime = lifetime;
            system.Speed = 0f;
            return system;
        }

        // A frame as the window runs one: the scene updates, then it's drawn.
        private void Frame(float deltaTime = 1f / 60f)
        {
            Time.Advance(deltaTime);
            Scene.Active.Update();
            _renderer.Render(Scene.Active);
        }

        [Fact]
        public void Without_compute_shaders_they_are_simulated_here_and_drawn_as_ordinary_particles()
        {
            GpuParticleSystem system = AddSystem("Sparks");
            system.Area = new Vector3(2f);
            Frame();

            system.Emit(30, new Vector3(5f, 0f, 0f), Vector3.UnitY);
            Frame();

            Assert.Equal(30, _backend.LastParticles.Count);
            Assert.All(_backend.LastParticles, p => Assert.InRange(p.Position.X, 4f, 6f));
            Assert.Empty(_backend.ParticleUpdates);

            // A second later they've all lived out their lifetime.
            for (int i = 0; i < 61; i++) Frame();
            Assert.Empty(_backend.LastParticles);
        }

        [Fact]
        public void They_move_the_same_as_cpu_particles()
        {
            GpuParticleSystem system = AddSystem("Falling", lifetime: 10f);
            system.Gravity = 9.8f;
            Frame();

            system.Emit(1, Vector3.Zero, Vector3.UnitY);
            Frame();
            for (int i = 0; i < 60; i++) Frame();

            // Sixty steps of v += g dt, p += v dt: a little further than the exact half g t squared, as on the CPU.
            float expected = 0f, velocity = 0f;
            for (int i = 0; i < 60; i++)
            {
                velocity -= 9.8f / 60f;
                expected += velocity / 60f;
            }

            Assert.Equal(expected, _backend.LastParticles.Single().Position.Y, 0.001f);
        }

        [Fact]
        public void With_compute_shaders_the_backend_gets_one_update_a_frame()
        {
            _backend.GpuParticles = true;
            GpuParticleSystem system = AddSystem("Sparks");
            Frame();

            system.Emit(10);
            system.Emit(5, Vector3.One, Vector3.UnitX);
            Frame();

            GpuParticleUpdate update = _backend.ParticleUpdates[^1];
            Assert.Equal(2, _backend.ParticleUpdates.Count);
            Assert.Equal(2, update.Emissions.Count);
            Assert.Equal((0, 10), (update.Emissions[0].First, update.Emissions[0].Count));
            Assert.Equal((10, 5), (update.Emissions[1].First, update.Emissions[1].Count));
            Assert.Equal(100, update.PoolSize);
            Assert.Empty(_backend.LastParticles);
            Assert.Equal(_backend.Begins, _backend.Ends);
        }

        [Fact]
        public void A_burst_past_the_limit_wraps_round_over_the_oldest()
        {
            _backend.GpuParticles = true;
            GpuParticleSystem system = AddSystem("Small", max: 8);
            Frame();

            system.Emit(6);
            system.Emit(20);
            Frame();

            IReadOnlyList<GpuParticleEmission> emissions = _backend.ParticleUpdates[^1].Emissions;
            Assert.Equal((0, 6), (emissions[0].First, emissions[0].Count));
            Assert.Equal((6, 8), (emissions[1].First, emissions[1].Count));
        }

        [Fact]
        public void Systems_share_the_pool_and_give_their_share_back()
        {
            _backend.GpuParticles = true;
            GpuParticleSystem first = AddSystem("First", max: 100);
            GpuParticleSystem second = AddSystem("Second", max: 50);
            Frame();
            first.Emit(1);
            second.Emit(1);
            Frame();

            IReadOnlyList<GpuParticleEmission> emissions = _backend.ParticleUpdates[^1].Emissions;
            Assert.Equal(0, emissions[0].RangeOffset);
            Assert.Equal(100, emissions[1].RangeOffset);
            Assert.Equal(150, _backend.ParticleUpdates[^1].PoolSize);

            first.GameObject.Destroy();
            Frame();
            Assert.Contains((0, 100), _backend.ParticleUpdates[^1].Clears);

            // The freed share is used again rather than the pool growing.
            AddSystem("Third", max: 60).Emit(1);
            Frame();
            Assert.Equal(0, _backend.ParticleUpdates[^1].Emissions.Single().RangeOffset);
            Assert.Equal(150, _backend.ParticleUpdates[^1].PoolSize);
        }

        [Fact]
        public void A_disabled_system_keeps_its_particles_but_is_not_drawn()
        {
            GpuParticleSystem system = AddSystem("Sparks", lifetime: 10f);
            Frame();
            system.Emit(5);
            Frame();
            Assert.Equal(5, _backend.LastParticles.Count);

            system.Enabled = false;
            Frame();
            Assert.Empty(_backend.LastParticles);

            system.Enabled = true;
            Frame();
            Assert.Equal(5, _backend.LastParticles.Count);
        }

        [Fact]
        public void Clear_removes_what_is_out()
        {
            GpuParticleSystem system = AddSystem("Sparks", lifetime: 10f);
            Frame();
            system.Emit(5);
            Frame();

            system.Clear();
            Frame();

            Assert.Empty(_backend.LastParticles);
        }

        [Fact]
        public void A_one_off_effect_destroys_itself_when_its_particles_are_gone()
        {
            GpuParticleSystem system = AddSystem("Burst", lifetime: 0.5f);
            system.DestroyWhenFinished = true;
            Frame();
            Assert.False(system.GameObject.IsDestroyed);

            system.Emit(10);
            for (int i = 0; i < 40; i++) Frame();

            Assert.True(system.GameObject.IsDestroyed);
        }

        [Fact]
        public void The_shared_random_numbers_are_spread_from_0_to_1()
        {
            uint state = ParticleRandom.Start(12345u, 7u);
            float min = 1f, max = 0f, sum = 0f;
            for (int i = 0; i < 10000; i++)
            {
                float value = ParticleRandom.Next(ref state);
                min = MathF.Min(min, value);
                max = MathF.Max(max, value);
                sum += value;
            }

            Assert.InRange(min, 0f, 0.01f);
            Assert.InRange(max, 0.99f, 0.99999995f);
            Assert.InRange(sum / 10000f, 0.48f, 0.52f);
            Assert.NotEqual(ParticleRandom.Start(1u, 0u), ParticleRandom.Start(1u, 1u));
        }
    }
}
