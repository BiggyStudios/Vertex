using System.Numerics;

using Vertex.Effects;
using Vertex.Graphs;
using Vertex.Rendering;

namespace Vertex.Tests
{
    public class VfxTests : EngineTest
    {
        private static VisualEffect Playing(Graph graph, Vector3 at = default)
        {
            var gameObject = new GameObject("Effect");
            gameObject.Transform.Position = at;
            var effect = gameObject.AddComponent<VisualEffect>();
            effect.Effect = graph;
            return effect;
        }

        private static List<ParticleData> Drawn(VisualEffect effect)
        {
            var drawn = new List<ParticleData>();
            effect.Collect(drawn);
            return drawn;
        }

        /// <summary>Spawn → Initialize → Output, the simplest system.</summary>
        private static (Graph Graph, GraphNode Spawn, GraphNode Init, GraphNode Output) Simple()
        {
            var graph = new Graph(VfxNodes.Kind);
            GraphNode spawn = graph.AddNode("Systems/Spawn", Vector2.Zero);
            GraphNode init = graph.AddNode("Systems/Initialize", Vector2.Zero);
            GraphNode output = graph.AddNode("Systems/Output", Vector2.Zero);
            Assert.True(graph.Link(spawn.Id, "Particles", init.Id, "Particles"));
            Assert.True(graph.Link(init.Id, "Particles", output.Id, "Particles"));
            return (graph, spawn, init, output);
        }

        [Fact]
        public void The_starter_effect_plays_and_draws_glowing_particles()
        {
            VisualEffect effect = Playing(VfxNodes.Starter());
            for (int i = 0; i < 60; i++) effect.Simulate(1f / 60f);

            Assert.InRange(effect.Count, 25, 45);
            List<ParticleData> drawn = Drawn(effect);
            Assert.NotEmpty(drawn);
            Assert.All(drawn, d => Assert.Equal(2f, d.Glow));
            Assert.True(effect.Playing);
        }

        [Fact]
        public void A_burst_that_does_not_loop_goes_off_once_and_finishes()
        {
            (Graph graph, GraphNode spawn, GraphNode init, _) = Simple();
            spawn.Values["Rate"] = 0f;
            spawn.Values["Burst"] = 12f;
            spawn.Values["Duration"] = 0.2f;
            spawn.Values["Loop"] = false;
            init.Values["Lifetime"] = 0.5f;

            VisualEffect effect = Playing(graph);
            effect.Simulate(0.1f);
            Assert.Equal(12, effect.Count);
            effect.Simulate(0.2f);
            Assert.Equal(12, effect.Count);
            Assert.False(effect.Playing);

            effect.Simulate(0.4f);
            Assert.Equal(0, effect.Count);
            Assert.True(effect.Finished);
        }

        [Fact]
        public void Initialize_takes_its_values_from_linked_nodes_for_each_particle_and_they_start_where_the_object_is()
        {
            (Graph graph, GraphNode spawn, GraphNode init, _) = Simple();
            spawn.Values["Burst"] = 30f;
            spawn.Values["Rate"] = 0f;
            init.Values["Velocity"] = Vector3.Zero;
            GraphNode sphere = graph.AddNode("Shapes/Point in Sphere", Vector2.Zero);
            sphere.Values["Radius"] = 2f;
            sphere.Values["On Surface"] = true;
            Assert.True(graph.Link(sphere.Id, "Position", init.Id, "Position"));

            VisualEffect effect = Playing(graph, new Vector3(5f, 0f, 0f));
            effect.Simulate(0.01f);

            List<ParticleData> drawn = Drawn(effect);
            Assert.Equal(30, drawn.Count);
            Assert.All(drawn, d => Assert.Equal(2f, Vector3.Distance(d.Position, new Vector3(5f, 0f, 0f)), 3));
        }

        [Fact]
        public void Output_changes_each_particle_as_it_ages()
        {
            (Graph graph, GraphNode spawn, GraphNode init, GraphNode output) = Simple();
            spawn.Values["Burst"] = 1f;
            spawn.Values["Rate"] = 0f;
            init.Values["Lifetime"] = 2f;
            init.Values["Size"] = 1f;
            GraphNode particle = graph.AddNode("Particle/Particle", Vector2.Zero);
            GraphNode fade = graph.AddNode("Math/One Minus", Vector2.Zero);
            Assert.True(graph.Link(particle.Id, "Life", fade.Id, "Value"));
            Assert.True(graph.Link(fade.Id, "Result", output.Id, "Size Scale"));
            output.Values["Look"] = ParticleLook.Ring;

            VisualEffect effect = Playing(graph);
            effect.Simulate(0.5f);

            ParticleData drawn = Assert.Single(Drawn(effect));
            Assert.Equal(0.75f, drawn.Size, 3);
            Assert.Equal(ParticleLook.Ring, drawn.Look);
        }

        [Fact]
        public void Update_pushes_particles_and_kills_those_it_is_told_to()
        {
            (Graph graph, GraphNode spawn, GraphNode init, GraphNode output) = Simple();
            spawn.Values["Burst"] = 1f;
            spawn.Values["Rate"] = 0f;
            init.Values["Lifetime"] = 10f;
            init.Values["Velocity"] = Vector3.Zero;
            GraphNode update = graph.AddNode("Systems/Update", Vector2.Zero);
            graph.Link(init.Id, "Particles", update.Id, "Particles");
            graph.Link(update.Id, "Particles", output.Id, "Particles");
            update.Values["Force"] = new Vector3(0f, -10f, 0f);

            // Killed once it's fallen below a metre down.
            GraphNode particle = graph.AddNode("Particle/Particle", Vector2.Zero);
            GraphNode split = graph.AddNode("Vectors/Split Vector", Vector2.Zero);
            GraphNode below = graph.AddNode("Math/Compare", Vector2.Zero);
            graph.Link(particle.Id, "Position", split.Id, "Vector");
            graph.Link(split.Id, "Y", below.Id, "A");
            Assert.True(graph.Link(below.Id, "Result", update.Id, "Kill"));
            below.Values["B"] = -0.15f;

            VisualEffect effect = Playing(graph);
            effect.Simulate(0.1f);
            effect.Simulate(0.1f);
            Assert.True(Assert.Single(Drawn(effect)).Position.Y < -0.15f);
            effect.Simulate(0.1f);
            Assert.Equal(0, effect.Count);
        }

        [Fact]
        public void A_property_is_its_variables_value_until_the_game_sets_it_on_the_object()
        {
            (Graph graph, GraphNode spawn, GraphNode init, _) = Simple();
            graph.Variables.Add(new GraphVariable("Amount", PortType.Number, 5f));
            spawn.Values["Rate"] = 0f;
            GraphNode get = graph.AddNode("Properties/Get Property", Vector2.Zero);
            get.Values["Variable"] = "Amount";
            Assert.True(graph.Link(get.Id, "Value", spawn.Id, "Burst"));

            VisualEffect first = Playing(graph);
            VisualEffect second = Playing(graph);
            second.SetProperty("Amount", 9f);
            first.Simulate(0.1f);
            second.Simulate(0.1f);

            Assert.Equal(5, first.Count);
            Assert.Equal(9, second.Count);
            _ = init;
        }

        [Fact]
        public void Values_worked_out_in_a_circle_are_reported_on_the_node()
        {
            (Graph graph, _, GraphNode init, _) = Simple();
            GraphNode a = graph.AddNode("Math/Add", Vector2.Zero);
            GraphNode b = graph.AddNode("Math/Add", Vector2.Zero);
            graph.Link(a.Id, "Result", b.Id, "A");
            graph.Link(b.Id, "Result", a.Id, "A");
            graph.Link(a.Id, "Result", init.Id, "Size");

            var logged = new List<LogEntry>();
            Log.Written += logged.Add;
            try
            {
                VisualEffect effect = Playing(graph);
                effect.Simulate(0.1f);
                Assert.Equal(0, effect.Count);
            }
            finally
            {
                Log.Written -= logged.Add;
            }

            Assert.Contains(logged, e => e.Level == LogLevel.Error && e.Message.Contains("circle"));
            Assert.True(graph.Error(a.Id) != null || graph.Error(b.Id) != null);
        }

        [Fact]
        public void A_vfx_file_keeps_its_colours()
        {
            string path = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N") + ".vfx");
            try
            {
                Graph graph = VfxNodes.Starter();
                GraphNode init = graph.Nodes.First(n => n.Type == "Systems/Initialize");
                init.Values["Color"] = new Color(0.2f, 0.4f, 0.6f, 0.8f);
                graph.Save(path);

                Graph loaded = Graph.Load(path);
                GraphNode read = loaded.Nodes.First(n => n.Type == "Systems/Initialize");
                Assert.Equal(new Color(0.2f, 0.4f, 0.6f, 0.8f), PortType.Color.Convert(read.Values["Color"]));
                Assert.Equal(VfxNodes.Kind, loaded.Kind);
                Assert.Equal(graph.ToJson(), loaded.ToJson());
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
