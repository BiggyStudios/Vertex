Vertex $(VertexVersion)
======================

A 3D game engine in C#. The documentation, with tutorials: https://biggystudios.github.io/Vertex/


What's here
-----------

  Editor/                    The editor: run $(VertexEditor).
  Engine/                    The engine as a NuGet package, for games written in code.
  Samples/Duel/              A 1v1 multiplayer shooter made in the editor, with C# scripts and with visual scripts.
  Samples/CodeOnly/          Coin Grab: a whole game in one C# file.
  Starter/                   A blank game written in code, to copy and make your own.


What you need
-------------

  - 64-bit Windows 10 or 11, 64-bit Linux, or a Mac with Apple silicon.
  - A graphics card with Vulkan or OpenGL 3.3. On a Mac, Vertex renders with OpenGL.
  - The .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
    The editor runs on .NET 10, and builds C# scripts and games written in code with the SDK.
$(VertexPlatformNotes)

Trying things
-------------

  - The editor: run $(VertexEditor).
  - The duel in the editor: File > Open Project, and pick Samples/Duel/Duel.vertexproject. Press Play.
  - A game written in code: open a terminal in its folder (Samples/CodeOnly, or Starter) and run:  dotnet run
  - The duel on its own, against yourself on one computer: in Samples/Duel run  dotnet run -- -lan  twice, and press
    Enter in each window. The first hosts and the second joins.


Games written in code, outside this folder
------------------------------------------

The nuget.config here tells the projects in this folder where the engine's package is. To use the engine from a
project anywhere else, tell .NET where the package is, once:

  dotnet nuget add source "<this folder>/Engine" --name Vertex

then add it to your project:

  dotnet add package Vertex.Engine --version $(VertexVersion)
