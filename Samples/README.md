# Samples

Finished versions of the games the [documentation's](../docs/index.html) tutorials build, and a city to walk round
that shows off the renderer. The engine's tests play them headless, so they keep working as the engine changes.

| Sample | What it shows |
| --- | --- |
| [CodeOnly](CodeOnly) | A whole game in one C# file, without the editor: roll a ball and pick up coins before time runs out. |
| [Duel](Duel) | A 1v1 multiplayer shooter made in the editor, twice: once with C# scripts and once with visual scripts. Steam (with Spacewar's app ID, 480) or the local network. |
| [NeonCity](NeonCity) | A rainy street at night between tall buildings, lit by neon signs, street lamps and windows through volumetric fog, with flying cars overhead and a night market to walk into: a ramen bar, a dumpling stall, a bar and arcade machines. |

## Running them

```
dotnet run --project Samples/CodeOnly
dotnet run --project Samples/Duel                # the C# version
dotnet run --project Samples/Duel -- --visual    # the visual scripting version
dotnet run --project Samples/NeonCity
```

To open the duel in the editor, open `Samples/Duel/Duel.vertexproject` (File > Open Project). The C# version's scene is
`Scenes/Duel (CSharp).scene` and the visual scripting version's is `Scenes/Duel (Visual Scripting).scene`.

To play the duel against yourself on one PC, set the Network Manager's Service to Local Network, run two copies, and
press Enter in each: the first hosts and the second joins.

## The city

Click in the window to look round with the mouse, walk with WASD (Shift to hurry), and press F to turn the volumetric
fog off and on and see what it adds. The night market is on the right, a little way down the street from where you start.

Open `Samples/NeonCity/NeonCity.vertexproject` to look at it in the editor: the fog's settings are under Volumetric Fog in
the scene's Lighting & Sky, the boxes of steam and clearer air are Fog Volumes, and each sign has its own light.

Its scene and textures are made by the code in `NeonCity/Builder`. To make them again after changing that code:

```
dotnet run --project Samples/NeonCity -- --build
```

That replaces the scene, so changes made to it in the editor are lost.
