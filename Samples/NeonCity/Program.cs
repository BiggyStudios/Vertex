using Vertex;

// Walks round the city. "--build" makes its scene and textures again from the code in Builder, over what's in Assets:
// run it after changing the builder, but not after changing the scene in the editor, or those changes are lost.
if (args.Contains("--build"))
{
    string assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets"));
    NeonCity.Builder.CityBuilder.Build(assets);
    return;
}

new Game { StartScene = "Scenes/Neon City.scene" }.Run("Neon City");
