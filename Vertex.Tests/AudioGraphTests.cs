using System.Numerics;
using System.Text;

using Vertex.Audio;
using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.Tests
{
    /// <summary>
    /// Audio graphs: which sounds they play, when, and how, heard through a stand-in for the audio device that keeps
    /// what each voice was asked to play.
    /// </summary>
    public class AudioGraphTests : EngineTest
    {
        /// <summary>The audio device, for tests: sounds "play" for as long as their clip lasts, by its own clock.</summary>
        private sealed class TestVoices : IVoices
        {
            public sealed class Voice
            {
                public required AudioClip Clip;
                public Vector3? Position;
                public SoundPlacement Placement;
                public float Volume, Pitch, StartedAt, Ends;
                public bool Loop, Stopped;
                public string Name => Path.GetFileNameWithoutExtension(Clip.Source)!;
            }

            public readonly List<Voice> Started = new();
            public float Now;

            public PlayingSound Start(AudioClip clip, in SoundPlacement placement, float volume, float pitch, bool loop)
            {
                Started.Add(new Voice { Clip = clip, Position = placement.Position, Placement = placement, Volume = volume, Pitch = pitch, Loop = loop, StartedAt = Now, Ends = Now + clip.Duration / pitch });
                return new PlayingSound(Started.Count - 1, 1, clip);
            }

            public bool IsPlaying(int voice, int generation) => voice < Started.Count && !Started[voice].Stopped && (Started[voice].Loop || Now < Started[voice].Ends - 1e-4f);
            public float Offset(int voice, int generation) => Now - Started[voice].StartedAt;
            public void Seek(int voice, int generation, float seconds) { }

            public void Set(int voice, int generation, in SoundPlacement placement, float volume, float pitch)
            {
                Voice playing = Started[voice];
                if (playing.Position != null) playing.Position = placement.Position;
                playing.Placement = placement;
                playing.Volume = volume;
                playing.Pitch = pitch;
            }

            public void Stop(int voice, int generation) => Started[voice].Stopped = true;

            public void StopAll()
            {
                foreach (Voice voice in Started) voice.Stopped = true;
            }

            public IEnumerable<string> Names => Started.Select(v => v.Name);
        }

        private readonly TestVoices _voices = new();
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexAudioTests", Guid.NewGuid().ToString("N"));

        public AudioGraphTests()
        {
            Sound.Voices = _voices;
            Directory.CreateDirectory(_folder);
        }

        public override void Dispose()
        {
            Sound.StopAll();
            Sound.Voices = new TestVoicesGone();
            base.Dispose();
            try { Directory.Delete(_folder, true); } catch (IOException) { }
        }

        /// <summary>Puts back a device that plays nothing, as the tests' engine has none.</summary>
        private sealed class TestVoicesGone : IVoices
        {
            public PlayingSound Start(AudioClip clip, in SoundPlacement placement, float volume, float pitch, bool loop) => default;
            public bool IsPlaying(int voice, int generation) => false;
            public float Offset(int voice, int generation) => 0f;
            public void Seek(int voice, int generation, float seconds) { }
            public void Set(int voice, int generation, in SoundPlacement placement, float volume, float pitch) { }
            public void Stop(int voice, int generation) { }
            public void StopAll() { }
        }

        /// <summary>A WAV file of silence <paramref name="seconds"/> long, named <paramref name="name"/>.</summary>
        private string Wav(string name, float seconds = 0.5f)
        {
            const int rate = 8000;
            int samples = (int)(rate * seconds);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVE"u8);
                writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(samples * 2); writer.Write(new byte[samples * 2]);
            }

            string path = Path.Combine(_folder, name + ".wav");
            File.WriteAllBytes(path, stream.ToArray());
            return path;
        }

        private static GraphNode SoundNode(Graph graph, string file, float volume = 1f)
        {
            GraphNode node = graph.AddNode("Sounds/Sound", Vector2.Zero);
            node.Values["Sound"] = file;
            node.Values["Volume"] = volume;
            return node;
        }

        /// <summary>A graph with an Output, and <paramref name="into"/> linked into it.</summary>
        private static Graph WithOutput(Graph graph, GraphNode into)
        {
            GraphNode output = graph.AddNode("Output/Output", Vector2.Zero);
            Assert.True(graph.Link(into.Id, "Sound", output.Id, "Sound"));
            return graph;
        }

        /// <summary>A Random (or Switch, Sequence, Layer) node with a Sound node linked into each of its inputs.</summary>
        private GraphNode Choosing(Graph graph, string type, params string[] names)
        {
            GraphNode node = graph.AddNode(type, Vector2.Zero);
            for (int i = 0; i < names.Length; i++) Assert.True(graph.Link(SoundNode(graph, Wav(names[i])).Id, "Sound", node.Id, $"Sound {i + 1}"));
            return node;
        }

        private void Step(float seconds, float deltaTime = 0.01f)
        {
            for (float t = 0f; t < seconds - 1e-5f; t += deltaTime)
            {
                _voices.Now += deltaTime;
                Sound.UpdatePlaybacks(deltaTime);
            }
        }

        [Fact]
        public void A_node_with_a_repeated_input_has_one_more_than_is_linked()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode random = graph.AddNode("Choose/Random", Vector2.Zero);
            NodeType type = graph.Library!.Find(random.Type)!;
            Assert.Equal(["Mode", "Sound 1", "Sound 2"], type.InputsOf(graph, random).Select(p => p.Name));

            for (int i = 1; i <= 5; i++) Assert.True(graph.Link(graph.AddNode("Sounds/Sound", Vector2.Zero).Id, "Sound", random.Id, $"Sound {i}"));
            Assert.Equal(6, type.RepeatedCount(graph, random));
            Assert.Equal(AudioNodes.Audio, type.Input("Sound 6")!.Type);
            Assert.Null(type.Input("Sound 0"));
            Assert.False(graph.Link(graph.AddNode("Sounds/Sound", Vector2.Zero).Id, "Sound", random.Id, "Sounds 2"));

            // Saved and loaded, the links to the fifth are kept.
            Graph loaded = Graph.FromJson(graph.ToJson());
            Assert.Contains(loaded.Links, l => l.ToNode == random.Id && l.ToPort == "Sound 5");
            Assert.Equal(6, type.RepeatedCount(loaded, loaded.Node(random.Id)!));
        }

        [Fact]
        public void Random_with_no_repeats_plays_each_of_its_sounds_but_never_the_same_twice_running()
        {
            var graph = new Graph(AudioNodes.Kind);
            WithOutput(graph, Choosing(graph, "Choose/Random", "A", "B", "C"));

            for (int i = 0; i < 60; i++) Sound.Play(graph);

            List<string> played = _voices.Names.ToList();
            Assert.Equal(60, played.Count);
            Assert.Equal(["A", "B", "C"], played.Distinct().Order());
            for (int i = 1; i < played.Count; i++) Assert.NotEqual(played[i - 1], played[i]);
        }

        [Fact]
        public void Shuffle_plays_every_sound_once_before_any_plays_again()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode random = Choosing(graph, "Choose/Random", "A", "B", "C", "D");
            random.Values["Mode"] = "Shuffle";
            WithOutput(graph, random);

            for (int i = 0; i < 12; i++) Sound.Play(graph);

            List<string> played = _voices.Names.ToList();
            for (int round = 0; round < 3; round++) Assert.Equal(["A", "B", "C", "D"], played.Skip(round * 4).Take(4).Order());
            Assert.NotEqual(played[3], played[4]);
        }

        [Fact]
        public void In_turn_goes_round_them_in_order()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode random = Choosing(graph, "Choose/Random", "A", "B", "C");
            random.Values["Mode"] = "InTurn";
            WithOutput(graph, random);

            for (int i = 0; i < 7; i++) Sound.Play(graph);

            Assert.Equal(["A", "B", "C", "A", "B", "C", "A"], _voices.Names);
        }

        [Fact]
        public void Volumes_multiply_through_the_graph_and_random_pitch_differs_each_time_within_its_range()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode file = SoundNode(graph, Wav("Step"), volume: 0.5f);
            GraphNode volume = graph.AddNode("Change/Volume", Vector2.Zero);
            volume.Values["Volume"] = 0.5f;
            GraphNode pitch = graph.AddNode("Change/Random Pitch", Vector2.Zero);
            pitch.Values["Min"] = 0.8f;
            pitch.Values["Max"] = 1.2f;
            graph.Link(file.Id, "Sound", volume.Id, "Sound");
            graph.Link(volume.Id, "Sound", pitch.Id, "Sound");
            WithOutput(graph, pitch);

            for (int i = 0; i < 20; i++) Sound.Play(graph, volume: 0.8f);

            Assert.All(_voices.Started, v => Assert.Equal(0.2f, v.Volume, 4));
            Assert.All(_voices.Started, v => Assert.InRange(v.Pitch, 0.8f, 1.2f));
            Assert.True(_voices.Started.Select(v => v.Pitch).Distinct().Count() > 10);
        }

        [Fact]
        public void A_sequence_plays_each_sound_after_the_one_before_has_finished_with_its_gap_between()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode sequence = graph.AddNode("Combine/Sequence", Vector2.Zero);
            sequence.Values["Gap"] = 0.1f;
            graph.Link(SoundNode(graph, Wav("Long", 0.5f)).Id, "Sound", sequence.Id, "Sound 1");
            graph.Link(SoundNode(graph, Wav("Short", 0.3f)).Id, "Sound", sequence.Id, "Sound 2");
            WithOutput(graph, sequence);

            AudioPlayback playback = Sound.Play(graph);
            Assert.Equal(["Long"], _voices.Names);

            Step(0.55f);
            Assert.Equal(["Long"], _voices.Names);
            Step(0.1f);
            Assert.Equal(["Long", "Short"], _voices.Names);
            Assert.InRange(_voices.Started[1].StartedAt, 0.59f, 0.63f);

            Assert.True(playback.IsPlaying);
            Step(0.33f);
            Assert.False(playback.IsPlaying);
            Assert.DoesNotContain(playback, Sound.Playbacks);
        }

        [Fact]
        public void A_layer_plays_its_sounds_together_and_a_delay_waits_first()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode layer = Choosing(graph, "Combine/Layer", "Crack", "Body");
            GraphNode delay = graph.AddNode("Timing/Delay", Vector2.Zero);
            delay.Values["Seconds"] = 0.2f;
            graph.Link(SoundNode(graph, Wav("Echo")).Id, "Sound", delay.Id, "Sound");
            graph.Link(delay.Id, "Sound", layer.Id, "Sound 3");
            WithOutput(graph, layer);

            Sound.Play(graph);
            Assert.Equal(["Crack", "Body"], _voices.Names);
            Step(0.21f);
            Assert.Equal(["Crack", "Body", "Echo"], _voices.Names);
        }

        [Fact]
        public void A_loop_chooses_again_each_time_round_and_one_sound_looped_forever_loops_on_its_voice()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode random = Choosing(graph, "Choose/Random", "A", "B");
            random.Values["Mode"] = "InTurn";
            GraphNode loop = graph.AddNode("Timing/Loop", Vector2.Zero);
            loop.Values["Times"] = 3f;
            graph.Link(random.Id, "Sound", loop.Id, "Sound");
            WithOutput(graph, loop);

            AudioPlayback playback = Sound.Play(graph);
            Step(2f);
            Assert.Equal(["A", "B", "A"], _voices.Names);
            Assert.False(playback.IsPlaying);

            // Forever, with a single sound: one voice, looping.
            var hum = new Graph(AudioNodes.Kind);
            GraphNode forever = hum.AddNode("Timing/Loop", Vector2.Zero);
            hum.Link(SoundNode(hum, Wav("Hum")).Id, "Sound", forever.Id, "Sound");
            WithOutput(hum, forever);
            AudioPlayback humming = Sound.Play(hum);
            Step(3f);
            Assert.Single(_voices.Started, v => v.Name == "Hum");
            Assert.True(_voices.Started.Last().Loop);
            Assert.True(humming.IsPlaying);

            humming.Stop();
            Assert.True(_voices.Started.Last().Stopped);
            Assert.False(humming.IsPlaying);
        }

        [Fact]
        public void A_loops_gap_linked_to_a_random_number_is_different_each_time_round()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode loop = graph.AddNode("Timing/Loop", Vector2.Zero);
            loop.Values["Times"] = 6f;
            GraphNode gap = graph.AddNode("Math/Random Number", Vector2.Zero);
            gap.Values["Min"] = 0.2f;
            gap.Values["Max"] = 2f;
            graph.Link(SoundNode(graph, Wav("Drip", 0.1f)).Id, "Sound", loop.Id, "Sound");
            Assert.True(graph.Link(gap.Id, "Value", loop.Id, "Gap"));
            WithOutput(graph, loop);

            Sound.Play(graph);
            Step(15f);

            List<float> starts = _voices.Started.Select(v => v.StartedAt).ToList();
            Assert.Equal(6, starts.Count);
            List<float> gaps = starts.Zip(starts.Skip(1), (a, b) => b - a - 0.1f).ToList();
            Assert.All(gaps, g => Assert.InRange(g, 0.18f, 2.03f));
            Assert.True(gaps.Select(g => MathF.Round(g, 2)).Distinct().Count() > 2);
        }

        [Fact]
        public void Chance_plays_its_sound_only_as_often_as_it_says()
        {
            foreach ((float chance, bool plays) in new[] { (0f, false), (1f, true) })
            {
                _voices.Started.Clear();
                var graph = new Graph(AudioNodes.Kind);
                GraphNode node = graph.AddNode("Choose/Chance", Vector2.Zero);
                node.Values["Chance"] = chance;
                graph.Link(SoundNode(graph, Wav("Rattle")).Id, "Sound", node.Id, "Sound");
                WithOutput(graph, node);

                for (int i = 0; i < 20; i++) Sound.Play(graph);
                Assert.Equal(plays ? 20 : 0, _voices.Started.Count);
            }
        }

        [Fact]
        public void A_switch_plays_the_sound_a_property_chooses()
        {
            var graph = new Graph(AudioNodes.Kind);
            graph.Variables.Add(new GraphVariable("Ground", PortType.Number, 1f));
            GraphNode which = graph.AddNode("Properties/Get Property", Vector2.Zero);
            which.Values["Variable"] = "Ground";
            GraphNode switcher = Choosing(graph, "Choose/Switch", "Grass", "Metal");
            Assert.True(graph.Link(which.Id, "Value", switcher.Id, "Which"));
            WithOutput(graph, switcher);

            Sound.Play(graph);
            AudioPlayback metal = new(graph);
            metal.SetProperty("Ground", 2f);
            Sound.Start(metal);
            AudioPlayback beyond = new(graph);
            beyond.SetProperty("Ground", 7f);
            Sound.Start(beyond);

            Assert.Equal(["Grass", "Metal", "Metal"], _voices.Names);
        }

        [Fact]
        public void A_fade_in_starts_silent_and_comes_up_to_full_volume()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode fade = graph.AddNode("Change/Fade In", Vector2.Zero);
            fade.Values["Seconds"] = 1f;
            graph.Link(SoundNode(graph, Wav("Wind", 3f)).Id, "Sound", fade.Id, "Sound");
            WithOutput(graph, fade);

            Sound.Play(graph);
            Assert.Equal(0f, _voices.Started[0].Volume, 3);
            Step(0.5f);
            Assert.InRange(_voices.Started[0].Volume, 0.45f, 0.55f);
            Step(1f);
            Assert.Equal(1f, _voices.Started[0].Volume, 3);
        }

        [Fact]
        public void A_sound_node_with_no_sound_is_marked_and_the_rest_still_play()
        {
            var graph = new Graph(AudioNodes.Kind);
            GraphNode layer = graph.AddNode("Combine/Layer", Vector2.Zero);
            GraphNode empty = graph.AddNode("Sounds/Sound", Vector2.Zero);
            graph.Link(empty.Id, "Sound", layer.Id, "Sound 1");
            graph.Link(SoundNode(graph, Wav("Fine")).Id, "Sound", layer.Id, "Sound 2");
            WithOutput(graph, layer);

            Sound.Play(graph);

            Assert.Equal(["Fine"], _voices.Names);
            Assert.Equal("No sound chosen", graph.Error(empty.Id));
            Assert.NotNull(graph.LastRan(layer.Id));
        }

        [Fact]
        public void An_audio_source_plays_on_start_follows_its_object_and_uses_its_own_property_values()
        {
            var graph = new Graph(AudioNodes.Kind);
            graph.Variables.Add(new GraphVariable("Ground", PortType.Number, 1f));
            GraphNode which = graph.AddNode("Properties/Get Property", Vector2.Zero);
            which.Values["Variable"] = "Ground";
            GraphNode switcher = Choosing(graph, "Choose/Switch", "Grass", "Metal");
            graph.Link(which.Id, "Value", switcher.Id, "Which");
            GraphNode loop = graph.AddNode("Timing/Loop", Vector2.Zero);
            graph.Link(switcher.Id, "Sound", loop.Id, "Sound");
            WithOutput(graph, loop);

            var walker = new GameObject("Walker");
            walker.Transform.Position = new Vector3(1f, 0f, 0f);
            AudioSource source = walker.AddComponent<AudioSource>(s =>
            {
                s.AudioGraph = graph;
                s.Volume = 0.5f;
                s.Properties = [new ScriptVariableValue("Ground", PortType.Number, 2f)];
            });

            var loop1 = new GameLoop(new EmptyGame());
            RunFrames(loop1, 1);
            Assert.Equal(["Metal"], _voices.Names);
            Assert.Equal(new Vector3(1f, 0f, 0f), _voices.Started[0].Position);
            Assert.Equal(0.5f, _voices.Started[0].Volume, 3);
            Assert.True(source.IsPlaying);

            // It follows the object, and a change of volume reaches what's playing.
            walker.Transform.Position = new Vector3(5f, 0f, 0f);
            source.Volume = 0.25f;
            RunFrames(loop1, 1);
            Assert.Equal(new Vector3(5f, 0f, 0f), _voices.Started[0].Position);
            Assert.Equal(0.25f, _voices.Started[0].Volume, 3);

            source.Stop();
            Assert.True(_voices.Started.All(v => v.Stopped));
            Assert.False(source.IsPlaying);
        }

        [Fact]
        public void An_audio_source_with_just_a_sound_loops_it_on_its_voice_and_a_visual_script_can_play_it()
        {
            var clip = AudioClip.Load(Wav("Click"));
            var speaker = new GameObject("Speaker");
            AudioSource source = speaker.AddComponent<AudioSource>(s =>
            {
                s.Sound = clip;
                s.PlayOnStart = false;
                s.InWorld = false;
            });

            var loop = new GameLoop(new EmptyGame());
            RunFrames(loop, 2);
            Assert.Empty(_voices.Started);

            // A script's Play Audio node plays it.
            var script = new Graph();
            GraphNode start = script.AddNode("Events/On Start", Vector2.Zero);
            GraphNode play = script.AddNode("Sound/Play Audio", Vector2.Zero);
            script.Link(start.Id, "Then", play.Id, "In");
            speaker.AddComponent<VisualScript>(s => s.Script = script);
            RunFrames(loop, 1);

            Assert.Single(_voices.Started);
            Assert.Null(_voices.Started[0].Position);
            Assert.False(_voices.Started[0].Loop);

            source.Loop = true;
            source.Play();
            Assert.True(_voices.Started[1].Loop);
        }

        [Fact]
        public void An_audio_sources_spatial_settings_movement_and_facing_reach_its_voice()
        {
            var clip = AudioClip.Load(Wav("Engine", 2f));
            var car = new GameObject("Car");
            car.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            car.AddComponent<AudioSource>(s =>
            {
                s.Sound = clip;
                s.Loop = true;
                s.MaxDistance = 40f;
                s.Rolloff = Rolloff.Linear;
                s.Doppler = 1f;
                s.ConeAngle = 90f;
                s.OuterConeAngle = 180f;
                s.OuterConeVolume = 0.2f;
            });

            var loop = new GameLoop(new EmptyGame());
            RunFrames(loop, 1);
            SpatialSettings heard = _voices.Started[0].Placement.Spatial;
            Assert.Equal(40f, heard.MaxDistance);
            Assert.Equal(Rolloff.Linear, heard.Rolloff);
            Assert.Equal(1f, heard.Doppler);
            Assert.Equal((90f, 180f, 0.2f), (heard.ConeAngle, heard.OuterConeAngle, heard.OuterConeVolume));
            Assert.True(Vector3.Distance(car.Transform.Forward, _voices.Started[0].Placement.Forward) < 1e-4f);

            // Driving along X at 6 metres a second: how fast it moves goes with it, for its Doppler shift.
            for (int i = 0; i < 3; i++)
            {
                car.Transform.Position += new Vector3(0.1f, 0f, 0f);
                RunFrames(loop, 1);
            }

            Assert.Equal(6f, _voices.Started[0].Placement.Velocity.X, 0.01f);
        }

        [Fact]
        public void A_sound_that_isnt_in_the_world_is_panned_and_has_no_position()
        {
            var clip = AudioClip.Load(Wav("Music"));
            new GameObject("Radio").AddComponent<AudioSource>(s =>
            {
                s.Sound = clip;
                s.InWorld = false;
                s.Pan = -0.5f;
            });

            RunFrames(new GameLoop(new EmptyGame()), 1);

            Assert.Null(_voices.Started[0].Placement.Position);
            Assert.Equal(-0.5f, _voices.Started[0].Placement.Pan);
        }

        [Fact]
        public void Each_rolloff_fades_with_distance_as_it_says()
        {
            var natural = new SpatialSettings { FullVolumeDistance = 3f, MaxDistance = 30f };
            Assert.Equal(1f, natural.VolumeAt(2f));
            Assert.Equal(0.5f, natural.VolumeAt(6f), 4);
            Assert.Equal(0.1f, natural.VolumeAt(100f), 4);

            var linear = natural with { Rolloff = Rolloff.Linear, MaxDistance = 13f };
            Assert.Equal(0.5f, linear.VolumeAt(8f), 4);
            Assert.Equal(0f, linear.VolumeAt(20f));

            Assert.Equal(1f, (natural with { Rolloff = Rolloff.None }).VolumeAt(500f));

            // A one-off sound takes them too.
            var clip = AudioClip.Load(Wav("Boom"));
            Sound.PlayAt(clip, new Vector3(1f, 2f, 3f), linear);
            Assert.Equal(linear, _voices.Started[0].Placement.Spatial);
            Assert.Equal(new Vector3(1f, 2f, 3f), _voices.Started[0].Placement.Position);
        }

        [Fact]
        public void Sound_is_heard_from_the_audio_listener_or_else_the_main_camera()
        {
            Assert.Null(Sound.Listener);

            var camera = new GameObject("Camera");
            camera.AddComponent<Rendering.Camera>();
            Scene.Active.ApplyChanges();
            Assert.Same(camera.Transform, Sound.Listener);

            var player = new GameObject("Player");
            AudioListener ears = player.AddComponent<AudioListener>();
            Scene.Active.ApplyChanges();
            Assert.Same(player.Transform, Sound.Listener);

            ears.Enabled = false;
            Assert.Same(camera.Transform, Sound.Listener);
        }
    }
}
