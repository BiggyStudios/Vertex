# Samples

Finished versions of the games the [documentation's](../docs/index.html) tutorials build. The engine's tests play
them headless, so they keep working as the engine changes.

| Sample | What it shows |
| --- | --- |
| [CodeOnly](CodeOnly) | A whole game in one C# file, without the editor: roll a ball and pick up coins before time runs out. |
| [Duel](Duel) | A 1v1 multiplayer shooter made in the editor, twice: once with C# scripts and once with visual scripts. Steam (with Spacewar's app ID, 480) or the local network. |

## Running them

```
dotnet run --project Samples/CodeOnly
dotnet run --project Samples/Duel                # the C# version
dotnet run --project Samples/Duel -- --visual    # the visual scripting version
```

To open the duel in the editor, open `Samples/Duel/Duel.vertexproject` (File > Open Project). The C# version's scene is
`Scenes/Duel (CSharp).scene` and the visual scripting version's is `Scenes/Duel (Visual Scripting).scene`.

To play the duel against yourself on one PC, set the Network Manager's Service to Local Network, run two copies, and
press Enter in each: the first hosts and the second joins.
