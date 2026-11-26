using System.Numerics;
using System.Text;

using Vertex.Audio;
using Vertex.Rendering;
using Vertex.Serialization;
using Vertex.UI;

namespace Vertex.Tests
{
    /// <summary>A built game's assets packed into one file, and everything loading from it as from an Assets folder.</summary>
    public class AssetPackTests : EngineTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N"));

        public AssetPackTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (string file in _written) Assets.Forget(file);
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        private readonly List<string> _written = [];

        private string Put(string relative, byte[] data)
        {
            string path = Path.Combine(_folder, "Game", "Assets", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            _written.Add(path);
            return path;
        }

        private string Put(string relative, string text) => Put(relative, Encoding.UTF8.GetBytes(text));

        private string AssetsFolder => Path.Combine(_folder, "Game", "Assets");

        /// <summary>Packs the Assets folder beside it, as a build does, takes the folder away, and reads from the pack.</summary>
        private string PackAndTakeAwayTheFolder()
        {
            string pack = Path.Combine(_folder, "Game", AssetPack.FileName);
            AssetPack.Write(pack, Directory.EnumerateFiles(AssetsFolder, "*", SearchOption.AllDirectories).Select(f => (Path.GetRelativePath(AssetsFolder, f), f)));
            foreach (string file in _written) Assets.Forget(file);
            Directory.Delete(AssetsFolder, recursive: true);
            BuiltGame.OpenAssetPack(Path.Combine(_folder, "Game"));
            return pack;
        }

        [Fact]
        public void A_pack_gives_back_each_file_where_it_was_in_the_assets_folder_and_nothing_in_it_can_be_read_as_it_is()
        {
            string story = string.Concat(Enumerable.Repeat("The secret ending is behind the lighthouse. ", 200));
            string scene = Put("Scenes/Main.scene", story);
            byte[] noise = new byte[5000];
            new Random(3).NextBytes(noise);
            string picture = Put(Path.Combine("Textures", "Brick.png"), noise);
            string empty = Put("Empty.txt", []);

            string pack = PackAndTakeAwayTheFolder();
            Assert.False(File.Exists(scene));
            Assert.True(AssetFiles.Packed);

            // Neither the names nor what's in them show in the pack.
            string raw = Encoding.Latin1.GetString(File.ReadAllBytes(pack));
            Assert.DoesNotContain("secret", raw);
            Assert.DoesNotContain("Brick", raw);
            Assert.DoesNotContain("Scenes", raw);
            Assert.DoesNotContain(Encoding.Latin1.GetString(noise[..64]), raw);
            Assert.True(new FileInfo(pack).Length < noise.Length + story.Length / 4, "Text should be squeezed");

            // Each is found where it was, whichever way the path is written.
            Assert.True(AssetFiles.Exists(scene));
            Assert.True(AssetFiles.Exists(Path.Combine(AssetsFolder, "scenes", "MAIN.scene")));
            Assert.True(AssetFiles.Exists(AssetsFolder + "/Textures/Brick.png"));
            Assert.False(AssetFiles.Exists(Path.Combine(AssetsFolder, "Scenes", "Other.scene")));
            Assert.Equal(story, AssetFiles.ReadAllText(scene));
            Assert.Equal(noise, AssetFiles.ReadAllBytes(picture));
            Assert.Empty(AssetFiles.ReadAllBytes(empty));
            using (Stream stream = AssetFiles.OpenRead(picture))
            {
                Assert.Equal(noise.Length, stream.Length);
                Assert.Equal(noise[0], stream.ReadByte());
            }

            Assert.Equal(3, AssetFiles.Files(AssetsFolder).Count());
            Assert.Equal([Path.GetFullPath(picture)], AssetFiles.Files(Path.Combine(AssetsFolder, "Textures")));
            Assert.ThrowsAny<IOException>(() => AssetFiles.ReadAllBytes(Path.Combine(AssetsFolder, "Missing.png")));

            // Files elsewhere are still read from the disk.
            string beside = Path.Combine(_folder, "Save.json");
            File.WriteAllText(beside, "{}");
            Assert.True(AssetFiles.Exists(beside));
            Assert.Equal("{}", AssetFiles.ReadAllText(beside));

            // Once it's closed, its files are gone.
            AssetFiles.UnmountAll();
            Assert.False(AssetFiles.Exists(scene));
            Assert.False(AssetFiles.Packed);
        }

        [Fact]
        public void A_file_that_isnt_a_pack_is_refused()
        {
            string path = Path.Combine(_folder, AssetPack.FileName);
            File.WriteAllText(path, "PK, a zip file, perhaps, and long enough to have a header's worth");
            Assert.Throws<InvalidDataException>(() => AssetPack.Open(path));
        }

        [Fact]
        public void A_packed_games_scenes_materials_prefabs_textures_sounds_fonts_and_models_load_with_no_assets_folder()
        {
            // A game's assets: a material, a prefab using it, a scene with both, a picture, a sound, a font, and a model
            // (an .obj naming a .mtl beside it, which names the picture).
            string picture = Put("Textures/Red.bmp", Bitmap());
            string materialPath = Put("Materials/Brick.material", "");
            File.WriteAllText(materialPath, SceneFile.SaveMaterial(new Material { Color = new Color(0.5f, 0.2f, 0.1f), Roughness = 0.7f }, AssetsFolder));
            Material brick = Assets.LoadMaterial(materialPath);

            var crate = new GameObject("Crate");
            crate.AddComponent<MeshRenderer>().Material = brick;
            Scene.Active.ApplyChanges();
            string prefabPath = Path.Combine(AssetsFolder, "Prefabs", "Crate.prefab");
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath)!);
            Prefab.Save(crate, prefabPath, AssetsFolder);
            _written.Add(prefabPath);
            string scenePath = Put("Scenes/Main.scene", SceneFile.Save(Scene.Active, new[] { AssetsFolder }));
            Scene.Active.Unload();
            Scene.Active = new Scene();

            string sound = Put("Sounds/Beep.wav", Wave());
            using (Stream font = typeof(Font).Assembly.GetManifestResourceStream("Vertex.Fonts.NotoSans-Regular.ttf")!)
            {
                var memory = new MemoryStream();
                font.CopyTo(memory);
                Put("Fonts/Main.ttf", memory.ToArray());
            }

            Put("Models/Box.mtl", "newmtl Red\nKd 1 0 0\nmap_Kd ../Textures/Red.bmp\n");
            string model = Put("Models/Box.obj", "mtllib Box.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nusemtl Red\nf 1/1/1 2/2/1 3/3/1\n");

            PackAndTakeAwayTheFolder();

            // The scene, and the material and prefab it refers to.
            var loaded = new Scene();
            SceneFile.Load(scenePath, loaded, AssetsFolder);
            loaded.ApplyChanges();
            Material material = loaded.Find("Crate")!.GetComponent<MeshRenderer>()!.Material!;
            Assert.Equal(0.7f, material.Roughness);
            Assert.Equal(Path.GetFullPath(materialPath), material.Source);
            Assert.Equal("Crate", Assets.LoadPrefab(prefabPath).Instantiate(Vector3.Zero).Name);
            Application.AssetFolders = [AssetsFolder];
            Assert.Equal(Path.GetFullPath(materialPath), Assets.FindFile("Materials/Brick.material"));
            Application.AssetFolders = [];

            // A picture, a sound, a font.
            Texture texture = Assets.LoadTexture(picture);
            Assert.Equal((2, 2), (texture.Width, texture.Height));
            AudioClip clip = Assets.LoadSound(sound);
            Assert.True(clip.Duration > 0.05f);
            Assert.NotNull(Assets.LoadFont(Path.Combine(AssetsFolder, "Fonts", "Main.ttf")));

            // The model, its material from the .mtl beside it, and that material's picture.
            GameObject box = Model.Load(model);
            MeshRenderer renderer = box.GetComponentInChildren<MeshRenderer>()!;
            Assert.Equal(3, renderer.Mesh!.Vertices.Length);
            Assert.NotNull(renderer.Material!.Texture);
            Assert.Equal(2, renderer.Material.Texture!.Width);
        }

        /// <summary>A 2 × 2 red picture, as a 24-bit .bmp.</summary>
        private static byte[] Bitmap()
        {
            var memory = new MemoryStream();
            var writer = new BinaryWriter(memory);
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(70);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(2);
            writer.Write(2);
            writer.Write((short)1);
            writer.Write((short)24);
            writer.Write(0);
            writer.Write(16);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);
            for (int row = 0; row < 2; row++)
            {
                for (int x = 0; x < 2; x++) writer.Write(new byte[] { 0, 0, 255 });
                writer.Write((short)0);
            }

            return memory.ToArray();
        }

        /// <summary>A tenth of a second of a tone, as a 16-bit mono .wav.</summary>
        private static byte[] Wave()
        {
            const int rate = 22050, samples = rate / 10;
            var memory = new MemoryStream();
            var writer = new BinaryWriter(memory);
            writer.Write("RIFF"u8);
            writer.Write(36 + samples * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (int i = 0; i < samples; i++) writer.Write((short)(MathF.Sin(i * 0.1f) * 8000f));
            return memory.ToArray();
        }
    }
}
