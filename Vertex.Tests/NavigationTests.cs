using System.Numerics;

using Vertex.AI;
using Vertex.Physics;
using Vertex.Rendering;

namespace Vertex.Tests
{
    /// <summary>Navmeshes: where they let characters walk, the ways they find, and agents walking them.</summary>
    public class NavigationTests : EngineTest
    {
        private static GameObject Box(string name, Vector3 centre, Vector3 size)
        {
            var box = new GameObject(name);
            box.Transform.Position = centre;
            box.AddComponent<BoxCollider>().Size = size;
            return box;
        }

        /// <summary>A floor 20 across, its top at y = 0.</summary>
        private static void Floor() => Box("Floor", new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        /// <summary>A wall across the middle along z, from the far side to z = 4, so the way across goes round its end.</summary>
        private static void Wall() => Box("Wall", new Vector3(0f, 1.5f, -3f), new Vector3(1f, 3f, 14f));

        private static NavMesh Build()
        {
            Scene.Active.ApplyChanges();
            return NavMesh.Build(Scene.Active);
        }

        /// <summary>Whether a way of corners passes through the wall (widened by a little less than a character's radius).</summary>
        private static bool CrossesWall(IReadOnlyList<Vector3> corners, float radius)
        {
            for (int i = 1; i < corners.Count; i++)
            {
                for (int s = 0; s <= 50; s++)
                {
                    Vector3 p = Vector3.Lerp(corners[i - 1], corners[i], s / 50f);
                    if (MathF.Abs(p.X) < 0.5f + radius * 0.9f && p.Z < 4f + radius * 0.9f) return true;
                }
            }

            return false;
        }

        [Fact]
        public void A_floor_is_walkable_but_not_near_its_edges()
        {
            Floor();
            NavMesh navMesh = Build();

            Assert.False(navMesh.IsEmpty);
            Assert.True(navMesh.SamplePosition(new Vector3(0f, 0f, 0f), 0.5f, out Vector3 middle));
            Assert.Equal(0f, middle.Y, 0.02f);

            // Its edge is at 10: the nearest a character's middle can get is its radius in from it.
            Assert.True(navMesh.SamplePosition(new Vector3(9.9f, 0f, 0f), 2f, out Vector3 edge));
            Assert.InRange(edge.X, 9.5f, 9.65f);
            Assert.True(navMesh.AreaCount < navMesh.CellCount / 50, $"{navMesh.CellCount} cells in {navMesh.AreaCount} rectangles");
        }

        [Fact]
        public void The_way_round_a_wall_goes_round_its_end_and_keeps_clear_of_it()
        {
            Floor();
            Wall();
            NavMesh navMesh = Build();
            var path = new NavMeshPath();

            Assert.True(navMesh.FindPath(new Vector3(-5f, 0f, -5f), new Vector3(5f, 0f, -5f), path));
            Assert.Equal(NavMeshPathStatus.Complete, path.Status);
            Assert.False(CrossesWall(path.Corners, navMesh.AgentRadius), string.Join(" ", path.Corners));

            // Round the end at z = 4, and pulled tight: straight to the corners there (two each side, as the navmesh is
            // bevelled round the wall's corners) and back.
            Assert.Contains(path.Corners, c => c.Z > 4f);
            Assert.True(path.Corners.Count is >= 3 and <= 6, string.Join(" ", path.Corners));
            float shortest = 2f * Vector2.Distance(new Vector2(-5f, -5f), new Vector2(-1f, 4.4f)) + 2f;
            Assert.InRange(path.Length, shortest - 0.3f, shortest + 0.6f);
            Assert.Equal(new Vector3(5f, path.Corners[^1].Y, -5f), path.Corners[^1]);
        }

        [Fact]
        public void A_ramp_leads_up_to_a_platform_and_a_ledge_too_high_cant_be_climbed()
        {
            Floor();

            // A platform 1.5 high, with a ramp up to it on one side (about 27 degrees).
            Box("Platform", new Vector3(6f, 0.75f, 6f), new Vector3(4f, 1.5f, 4f));
            var ramp = Box("Ramp", new Vector3(6f, 0.7f, 2.5f), new Vector3(2f, 0.2f, 3.4f));
            ramp.Transform.EulerAngles = new Vector3(-27f, 0f, 0f);

            // And one with no way up.
            Box("Ledge", new Vector3(-6f, 0.75f, 6f), new Vector3(4f, 1.5f, 4f));
            NavMesh navMesh = Build();
            var path = new NavMeshPath();

            Assert.True(navMesh.FindPath(new Vector3(0f, 0f, -6f), new Vector3(6f, 1.5f, 6.5f), path));
            Assert.Equal(NavMeshPathStatus.Complete, path.Status);
            Assert.Equal(1.5f, path.Corners[^1].Y, 0.2f);

            Assert.True(navMesh.FindPath(new Vector3(0f, 0f, -6f), new Vector3(-6f, 1.5f, 6f), path));
            Assert.Equal(NavMeshPathStatus.Partial, path.Status);
            Assert.True(path.Corners[^1].Y < 0.2f, "It stays on the ground, as near the ledge as it gets");
        }

        /// <summary>A floor 60 across with pillars all over it, so its navmesh is many rectangles.</summary>
        private static void Field()
        {
            Box("Field", new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f));
            var random = new Random(3);
            for (int i = 0; i < 70; i++)
            {
                var at = new Vector3(random.Next(-27, 28) + 0.5f, 1f, random.Next(-27, 28) + 0.5f);
                if (MathF.Abs(at.X) < 4f && MathF.Abs(at.Z) < 4f) continue;
                Box("Pillar", at, new Vector3(1f, 2f, 1f));
            }
        }

        [Fact]
        public void A_goal_up_where_it_cant_be_walked_is_gone_to_from_beside_it_without_searching_everywhere()
        {
            Field();
            Box("Ledge", new Vector3(0f, 0.75f, 8f), new Vector3(3f, 1.5f, 3f));
            NavMesh navMesh = Build();
            var path = new NavMeshPath();

            Assert.True(navMesh.FindPath(new Vector3(0f, 0f, 0f), new Vector3(0f, 1.5f, 8f), path));
            Assert.Equal(NavMeshPathStatus.Partial, path.Status);
            Assert.True(path.Corners[^1].Y < 0.2f, "It stays on the ground");
            Assert.InRange(Vector2.Distance(new Vector2(path.Corners[^1].X, path.Corners[^1].Z), new Vector2(0f, 8f)), 1f, 3f);

            // Only the way there was looked at, not the whole field for somewhere nearer.
            Assert.True(navMesh.LastSearched < navMesh.AreaCount / 4, $"{navMesh.LastSearched} of {navMesh.AreaCount} rectangles searched");
        }

        [Fact]
        public void A_way_from_right_on_the_edge_between_rectangles_goes_straight_and_never_back()
        {
            Box("Field", new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f));
            NavMesh navMesh = Build();
            var path = new NavMeshPath();

            // Across the field, from every cell's edge in turn: some are the edges between rectangles.
            for (float x = -20f; x <= 20f; x += navMesh.CellSize * 0.5f)
            {
                Assert.True(navMesh.FindPath(new Vector3(x, 0f, -25f), new Vector3(x, 0f, 25f), path));
                for (int i = 1; i < path.Corners.Count; i++)
                {
                    Assert.True(path.Corners[i].Z >= path.Corners[i - 1].Z - 0.01f, $"From x {x}, the way goes back: {string.Join(" ", path.Corners)}");
                }

                Assert.InRange(path.Length, 49.9f, 50.3f);
            }
        }

        [Fact]
        public void A_search_stops_at_its_limit_and_goes_as_near_as_it_got()
        {
            Field();
            NavMesh navMesh = Build();
            var path = new NavMeshPath();
            Vector3 from = new(-26f, 0f, -26f), to = new(26f, 0f, 26f);

            Assert.True(navMesh.FindPath(from, to, path));
            Assert.Equal(NavMeshPathStatus.Complete, path.Status);

            navMesh.SearchLimit = 6;
            Assert.True(navMesh.FindPath(from, to, path));
            Assert.Equal(NavMeshPathStatus.Partial, path.Status);
            Assert.True(navMesh.LastSearched <= 7);
            Assert.True(Vector3.Distance(path.Corners[^1], to) < Vector3.Distance(from, to) - 2f, "It should get some of the way");
        }

        [Fact]
        public void A_step_can_be_walked_up_but_a_wall_as_high_as_a_knee_cant()
        {
            Floor();
            Box("Step", new Vector3(0f, 0.15f, 5f), new Vector3(20f, 0.3f, 10f));
            NavMesh navMesh = Build();
            var path = new NavMeshPath();
            Assert.True(navMesh.FindPath(new Vector3(0f, 0f, -5f), new Vector3(0f, 0.3f, 5f), path));
            Assert.Equal(NavMeshPathStatus.Complete, path.Status);

            Scene.Active = new Scene();
            Floor();
            Box("Kerb", new Vector3(0f, 0.3f, 5f), new Vector3(20f, 0.6f, 10f));
            navMesh = Build();
            Assert.True(navMesh.FindPath(new Vector3(0f, 0f, -5f), new Vector3(0f, 0.6f, 5f), path));
            Assert.Equal(NavMeshPathStatus.Partial, path.Status);
        }

        [Fact]
        public void Static_meshes_and_scenery_make_it_and_what_moves_doesnt()
        {
            var ground = new GameObject("Ground");
            ground.Transform.Scale = new Vector3(20f, 1f, 20f);
            MeshRenderer renderer = ground.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Plane;
            renderer.Static = true;

            // A crate physics moves leaves no hole; a pillar of scenery does.
            Box("Crate", new Vector3(-4f, 0.5f, 0f), new Vector3(1f, 1f, 1f)).AddComponent<Rigidbody>();
            Box("Pillar", new Vector3(4f, 1f, 0f), new Vector3(1f, 2f, 1f));
            NavMeshBuilder builder = NavMeshBuilder.Prepare(Scene.Active);
            Assert.Equal(2, builder.Objects);

            NavMesh navMesh = builder.Build();
            Assert.True(navMesh.HeightAt(new Vector3(-4f, 0f, 0f), out _));
            Assert.False(navMesh.HeightAt(new Vector3(4f, 0f, 0f), out _));
        }

        [Fact]
        public void A_characters_body_under_it_leaves_no_hole()
        {
            Floor();
            var guard = new GameObject("Guard");
            guard.Transform.Position = new Vector3(3f, 0f, 3f);
            guard.AddComponent<NavMeshAgent>();
            var body = new GameObject("Body");
            body.Transform.Parent = guard.Transform;
            body.Transform.Position = new Vector3(0f, 0.9f, 0f);
            body.AddComponent<BoxCollider>().Size = new Vector3(0.6f, 1.8f, 0.6f);

            NavMesh navMesh = Build();
            Assert.True(navMesh.HeightAt(new Vector3(3f, 0f, 3f), out _));
        }

        [Fact]
        public void Moving_into_a_wall_slides_along_it()
        {
            Floor();
            Wall();
            NavMesh navMesh = Build();

            Vector3 to = navMesh.MoveAlong(new Vector3(-2f, 0f, -2f), new Vector3(2f, 0f, -1f));
            Assert.InRange(to.X, -1.1f, -0.85f);
            Assert.Equal(-1f, to.Z, 0.05f);
            Assert.True(navMesh.Raycast(new Vector3(-2f, 0f, -2f), new Vector3(2f, 0f, -2f), out Vector3 hit));
            Assert.InRange(hit.X, -1.1f, -0.85f);
            Assert.False(navMesh.Raycast(new Vector3(-2f, 0f, 6f), new Vector3(2f, 0f, 6f), out _));
        }

        [Fact]
        public void It_reads_back_from_its_file_as_it_was_saved()
        {
            Floor();
            Wall();
            NavMesh navMesh = Build();
            string file = Path.Combine(Path.GetTempPath(), $"vertex-{Guid.NewGuid():N}{NavMesh.Extension}");
            try
            {
                navMesh.Save(file);
                NavMesh loaded = NavMesh.Load(file);
                Assert.Equal(navMesh.CellCount, loaded.CellCount);
                Assert.Equal(navMesh.AreaCount, loaded.AreaCount);
                Assert.Equal(navMesh.AgentRadius, loaded.AgentRadius);

                var before = new NavMeshPath();
                var after = new NavMeshPath();
                navMesh.FindPath(new Vector3(-5f, 0f, -5f), new Vector3(5f, 0f, -5f), before);
                loaded.FindPath(new Vector3(-5f, 0f, -5f), new Vector3(5f, 0f, -5f), after);
                Assert.Equal(before.Corners, after.Corners);

                File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6]);
                Assert.Throws<InvalidDataException>(() => NavMesh.Load(file));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void A_scenes_baked_navmesh_is_named_in_its_file_and_its_settings_kept()
        {
            Floor();
            Scene.Active.Navigation.AgentRadius = 0.6f;
            Scene.Active.Navigation.NavMeshFile = "Levels/Arena.navmesh";
            string json = Serialization.SceneFile.Save(Scene.Active);

            var loaded = new Scene();
            Serialization.SceneFile.LoadJson(json, loaded);
            Assert.Equal(0.6f, loaded.Navigation.AgentRadius);
            Assert.Equal("Levels/Arena.navmesh", loaded.Navigation.NavMeshFile);
        }

        // ---------------------------------------------------------------- Agents

        private static NavMeshAgent Agent(Vector3 at)
        {
            var walker = new GameObject("Walker");
            walker.Transform.Position = at;
            return walker.AddComponent<NavMeshAgent>();
        }

        [Fact]
        public void An_agent_walks_round_the_wall_and_stops_where_it_was_sent()
        {
            Floor();
            Wall();
            NavMeshAgent agent = Agent(new Vector3(-5f, 0f, -5f));
            var loop = new GameLoop(new EmptyGame());
            RunFrames(loop, 1);

            Assert.True(agent.SetDestination(new Vector3(5f, 0f, -5f)));
            bool crossed = false;
            var previous = agent.Transform.WorldPosition;
            for (int frame = 0; frame < 60 * 10 && !agent.Arrived; frame++)
            {
                RunFrames(loop, 1);
                Vector3 now = agent.Transform.WorldPosition;
                crossed |= CrossesWall([previous, now], 0.3f);
                previous = now;
            }

            Assert.True(agent.Arrived, $"It's at {agent.Transform.WorldPosition}");
            Assert.False(crossed);
            Assert.True(Vector3.Distance(agent.Transform.WorldPosition, new Vector3(5f, 0f, -5f)) < 0.3f, $"It's at {agent.Transform.WorldPosition}");

            // Facing the way it last walked: away from the wall's end, towards -z.
            Assert.True(agent.Transform.Forward.Z < -0.5f, $"It faces {agent.Transform.Forward}");

            // And it slows down at the end: it's stopped.
            RunFrames(loop, 5);
            Assert.True(agent.Velocity.Length() < 0.05f);
        }

        [Fact]
        public void Agents_going_through_each_other_step_round_instead()
        {
            Floor();
            NavMeshAgent a = Agent(new Vector3(-4f, 0f, 0f)), b = Agent(new Vector3(4f, 0f, 0.05f));
            var loop = new GameLoop(new EmptyGame());
            RunFrames(loop, 1);
            a.SetDestination(new Vector3(4f, 0f, 0f));
            b.SetDestination(new Vector3(-4f, 0f, 0f));

            float nearest = float.MaxValue;
            for (int frame = 0; frame < 60 * 6; frame++)
            {
                RunFrames(loop, 1);
                nearest = MathF.Min(nearest, Vector3.Distance(a.Transform.WorldPosition, b.Transform.WorldPosition));
            }

            Assert.True(nearest > 0.45f, $"They came {nearest} apart");
            Assert.True(a.Transform.WorldPosition.X > 3f && b.Transform.WorldPosition.X < -3f, $"{a.Transform.WorldPosition} {b.Transform.WorldPosition}");
        }

        [Fact]
        public void An_agent_with_a_character_controller_moves_it()
        {
            Floor();
            NavMeshAgent agent = Agent(new Vector3(-4f, 0.05f, 0f));
            agent.AddComponent<CharacterController>();
            var loop = new GameLoop(new EmptyGame());
            RunFrames(loop, 5);

            agent.SetDestination(new Vector3(4f, 0f, 0f));
            RunFrames(loop, 60 * 5);
            Assert.True(agent.Arrived || agent.Transform.WorldPosition.X > 3.5f, $"It's at {agent.Transform.WorldPosition}");
            Assert.InRange(agent.Transform.WorldPosition.Y, -0.1f, 0.2f);
        }

        [Fact]
        public void A_visual_script_sends_its_agent_somewhere()
        {
            Floor();
            var graph = new Graphs.Graph(Scripting.ScriptNodes.Kind);
            Graphs.GraphNode start = graph.AddNode("Events/On Start", Vector2.Zero);
            Graphs.GraphNode go = graph.AddNode("AI/Set Destination", new Vector2(250f, 0f));
            go.Values["Position"] = new Vector3(4f, 0f, 2f);
            graph.Link(start.Id, "Then", go.Id, "In");

            NavMeshAgent agent = Agent(new Vector3(-4f, 0f, 0f));
            agent.AddComponent<Scripting.VisualScript>().Script = graph;
            RunFrames(new GameLoop(new EmptyGame()), 60 * 5);
            Assert.True(agent.Arrived);
            Assert.True(Vector3.Distance(agent.Transform.WorldPosition, new Vector3(4f, 0f, 2f)) < 0.3f, $"It's at {agent.Transform.WorldPosition}");
        }

        [Fact]
        public void Without_a_baked_navmesh_one_is_made_from_the_scene_when_its_needed()
        {
            Floor();
            NavMeshAgent agent = Agent(new Vector3(0f, 0f, 0f));
            var loop = new GameLoop(new EmptyGame());
            RunFrames(loop, 1);

            Assert.NotNull(agent.NavMesh);
            Assert.Same(agent.NavMesh, Scene.Active.Navigation.NavMesh);
            Assert.False(agent.NavMesh.IsEmpty);
        }
    }
}
