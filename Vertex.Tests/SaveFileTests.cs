using System.Text;

namespace Vertex.Tests
{
    /// <summary>A game's save kept scrambled and signed, so it can't be read or changed by opening it, with backups.</summary>
    public class SaveFileTests : EngineTest
    {
        private const string Secret = "the game's own secret";

        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N"));

        private string SavePath => Path.Combine(_folder, "Saves", "progress.sav");

        public override void Dispose()
        {
            base.Dispose();
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        [Fact]
        public void A_save_reads_back_but_cant_be_read_by_opening_it()
        {
            Assert.Null(SaveFile.Read(SavePath, Secret));
            Assert.False(SaveFile.Exists(SavePath));

            string text = """{ "Coins": 1234, "Unlocked": ["secret-level"] }""";
            SaveFile.Write(SavePath, text, Secret);
            Assert.Equal(text, SaveFile.Read(SavePath, Secret));
            Assert.True(SaveFile.Exists(SavePath));

            string raw = Encoding.Latin1.GetString(File.ReadAllBytes(SavePath));
            Assert.DoesNotContain("Coins", raw);
            Assert.DoesNotContain("1234", raw);
            Assert.DoesNotContain("secret", raw);

            // Another game's secret can't read it.
            Assert.Null(SaveFile.Read(SavePath, "someone else's secret"));
        }

        [Fact]
        public void A_save_thats_been_changed_is_refused_and_the_newest_whole_backup_is_read_instead()
        {
            for (int i = 1; i <= 5; i++) SaveFile.Write(SavePath, $"save {i}", Secret);

            // The last three before this one are kept; the older ones are gone.
            Assert.Equal("save 4", SaveFile.Read(SaveFile.Backup(SavePath, 1), Secret, backups: 0));
            Assert.Equal("save 3", SaveFile.Read(SaveFile.Backup(SavePath, 2), Secret, backups: 0));
            Assert.Equal("save 2", SaveFile.Read(SaveFile.Backup(SavePath, 3), Secret, backups: 0));
            Assert.False(File.Exists(SaveFile.Backup(SavePath, 4)));

            // One byte changed anywhere, and it isn't believed.
            byte[] bytes = File.ReadAllBytes(SavePath);
            bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(SavePath, bytes);
            Assert.Equal("save 4", SaveFile.Read(SavePath, Secret));

            // Nor is one written over with plain text, or one cut short.
            File.WriteAllText(SaveFile.Backup(SavePath, 1), """{ "Coins": 999999 }""");
            Assert.Equal("save 3", SaveFile.Read(SavePath, Secret));
            byte[] whole = File.ReadAllBytes(SaveFile.Backup(SavePath, 2));
            File.WriteAllBytes(SaveFile.Backup(SavePath, 2), whole[..(whole.Length - 5)]);
            Assert.Equal("save 2", SaveFile.Read(SavePath, Secret));

            // Saving again doesn't let the changed save push a good backup out.
            SaveFile.Write(SavePath, "save 6", Secret);
            Assert.Equal("save 6", SaveFile.Read(SavePath, Secret));
            Assert.Equal("save 2", SaveFile.Read(SaveFile.Backup(SavePath, 3), Secret, backups: 0));

            SaveFile.Delete(SavePath);
            Assert.False(SaveFile.Exists(SavePath));
            Assert.Null(SaveFile.Read(SavePath, Secret));
        }

        [Fact]
        public void Nothing_trustworthy_reads_as_nothing_saved()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            File.WriteAllText(SavePath, "{ }");
            Assert.True(SaveFile.Exists(SavePath));
            Assert.Null(SaveFile.Read(SavePath, Secret));
            File.WriteAllBytes(SavePath, []);
            Assert.Null(SaveFile.Read(SavePath, Secret));
        }
    }
}
