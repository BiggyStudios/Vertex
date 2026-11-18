using Vertex;

// Runs the duel outside the editor, in the scene made with C# scripts; "--visual" plays the one made with visual
// scripts. In the editor, Play plays whichever scene is open.
string scene = args.Contains("--visual") ? "Scenes/Duel (Visual Scripting).scene" : "Scenes/Duel (CSharp).scene";
new Game { StartScene = scene }.Run("Duel");
