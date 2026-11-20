using Vertex.Platform;

namespace Vertex.Tests
{
    /// <summary>The native libraries the engine comes with are found on every kind of computer, and are the same versions.</summary>
    public class NativeLibraryTests
    {
        [Theory]
        [InlineData("linux-x64", "libshaderc_shared.so")]
        [InlineData("osx-arm64", "libshaderc_shared.dylib")]
        [InlineData("win-x64", "shaderc_shared.dll")]
        public void Libraries_are_found_in_their_systems_folder_whatever_it_calls_itself(string runtimeId, string name)
        {
            string found = Assert.Single(NativeLibraries.Find(name, runtimeId));
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeId, "native", name), found);
        }

        [Theory]
        [InlineData("linux-x64", "libassimp.so.5", "libassimp.so.6")]
        [InlineData("osx-arm64", "libassimp.5.dylib", "libassimp.6.dylib")]
        public void Assimp_6_is_used_where_silk_asks_for_5(string runtimeId, string asked, string given)
        {
            Assert.EndsWith(Path.Combine(runtimeId, "native", given), NativeLibraries.Find(asked, runtimeId).First());
        }

        [Fact]
        public void This_computers_name_is_the_one_its_libraries_folder_has()
        {
            Assert.True(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", NativeLibraries.RuntimeId, "native")), NativeLibraries.RuntimeId);
        }

        [Fact]
        public void The_assimp_that_loads_is_version_6_on_every_system()
        {
            _ = Rendering.Primitives.Cube; // the engine's loaded, so its library finding is in place
            Silk.NET.Assimp.Assimp assimp = Silk.NET.Assimp.Assimp.GetApi();
            Assert.Equal(6u, assimp.GetVersionMajor());
        }
    }
}
