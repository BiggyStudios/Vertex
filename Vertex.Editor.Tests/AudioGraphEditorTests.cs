using System.Numerics;

using Vertex.Audio;
using Vertex.Editor.Editing;
using Vertex.Editor.Graphs;
using Vertex.Editor.Gui;
using Vertex.Graphs;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Making audio graphs in their tab: sounds dropped in from the assets, listening to it, new ones.</summary>
    public class AudioGraphEditorTests : EditorTest
    {
        private static readonly Rect _area = new(0f, 0f, 1200f, 800f);

        private readonly EditorContext _context = new();
        private readonly Graph _graph = AudioNodes.Starter();
        private readonly GraphDocument _doc;
        private readonly GraphView _view;
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));

        public AudioGraphEditorTests()
        {
            Directory.CreateDirectory(_folder);
            _doc = new GraphDocument(_graph, Path.Combine(_folder, "Steps.audiograph"));
            _view = new GraphView(_context);
        }

        public override void Dispose()
        {
            Inspection.SoundPreview.Stop();
            base.Dispose();
            try { Directory.Delete(_folder, true); } catch (IOException) { }
        }

        private void Frame(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                Time.Advance(1.0 / 60.0);
                Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
                Ui.BeginFrame(1f / 60f);
                _view.Draw(_area, _doc);
                Ui.EndFrame();
                Canvas.EndFrame();
                InputSystem.EndFrame();
            }
        }

        private GraphNode Node(string type) => _graph.Nodes.Single(n => n.Type == type);

        /// <summary>Drags a sound file in from the assets (as the Assets panel starts it) and lets it go at <paramref name="to"/>.</summary>
        private void DropSound(string file, Vector2 to)
        {
            var from = new Vector2(600f, 780f);
            InputSystem.MoveMouse(from);
            Frame();
            InputSystem.PressButton(MouseButton.Left);
            Frame();
            DragDrop.Begin(new AssetEntry(file, AssetKind.Sound), Path.GetFileName(file));
            for (int i = 1; i <= 4; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(from, to, i / 4f));
                Frame();
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Frame();
        }

        private string Wav(string name)
        {
            string path = Path.Combine(_folder, name + ".wav");
            AudioClip clip = AudioClip.FromSamples(new float[800], 8000);
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + clip.Samples.Length * 2); writer.Write("WAVE"u8);
                writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(clip.Samples.Length * 2);
                foreach (short sample in clip.Samples) writer.Write(sample);
            }

            return path;
        }

        [Fact]
        public void Sounds_dropped_onto_a_random_node_are_linked_into_it_one_after_another_with_a_free_input_after()
        {
            Frame(2);
            GraphNode random = Node("Choose/Random");
            Vector2 onRandom() => _view.PortOnScreen(_doc, random.Id, "Sound 1", output: false)!.Value + new Vector2(40f, 0f);

            DropSound(Wav("Step 1"), onRandom());
            DropSound(Wav("Step 2"), onRandom());
            Frame();

            List<GraphNode> sounds = _graph.Nodes.Where(n => n.Type == "Sounds/Sound").ToList();
            Assert.Equal(2, sounds.Count);
            Assert.EndsWith("Step 1.wav", (string)sounds[0].Values["Sound"]!);
            Assert.Single(_graph.LinksTo(random.Id, "Sound 1"), l => l.FromNode == sounds[0].Id);
            Assert.Single(_graph.LinksTo(random.Id, "Sound 2"), l => l.FromNode == sounds[1].Id);

            // A third input is there for the next, and the new nodes sit to the Random's left.
            Assert.NotNull(_view.PortOnScreen(_doc, random.Id, "Sound 3", output: false));
            Assert.All(sounds, s => Assert.True(s.Position.X < random.Position.X));

            // One undo each.
            _context.Undo();
            Assert.Single(_graph.Nodes, n => n.Type == "Sounds/Sound");
            Assert.Empty(_graph.LinksTo(random.Id, "Sound 2"));

            // On empty space, it's just a node, linked to nothing.
            DropSound(Wav("Thud"), new Vector2(900f, 650f));
            GraphNode loose = _graph.Nodes.Last();
            Assert.Equal("Sounds/Sound", loose.Type);
            Assert.DoesNotContain(_graph.Links, l => l.FromNode == loose.Id);
        }

        [Fact]
        public void The_play_button_plays_the_graph_and_its_nodes_light_up_as_they_play()
        {
            _graph.Save(_doc.Path);
            Frame(2);
            Assert.Null(_graph.LastRan(Node("Output/Output").Id));

            // The toolbar's play button, left of the Properties button.
            InputSystem.MoveMouse(new Vector2(330f, 28f));
            Frame(2);
            InputSystem.PressButton(MouseButton.Left);
            Frame();
            InputSystem.ReleaseButton(MouseButton.Left);
            Frame();

            Assert.NotNull(Assets.LoadGraph(_doc.Path).LastRan(Node("Output/Output").Id));
        }

        [Fact]
        public void A_new_audio_graph_is_a_file_of_its_own_kind_ready_for_sounds()
        {
            string path = _context.CreateAudioGraph(_folder);

            Assert.EndsWith(".audiograph", path);
            Assert.Equal(AssetKind.AudioGraph, AssetDatabase.KindOf(path));
            Graph graph = Graph.Load(path);
            Assert.Equal(AudioNodes.Kind, graph.Kind);
            Assert.Contains(graph.Nodes, n => n.Type == "Choose/Random");
            Assert.Contains(graph.Nodes, n => n.Type == "Output/Output");
        }
    }
}
