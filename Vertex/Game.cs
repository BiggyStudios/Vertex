namespace Vertex
{
    /// <summary>
    /// A game: what it sets up when it starts (<see cref="Load"/>) and what it does every frame (<see cref="Update"/>).
    /// <para>
    /// Made in code, subclass it, build the world in <see cref="Load"/>, and start it from <c>Main</c> with
    /// <c>new MyGame().Run("My Game")</c>. Made in the editor, it starts in <see cref="StartScene"/>; a subclass that sets
    /// it can still do more in code around the scene. Either way the editor can open it: it shows the world
    /// <see cref="Load"/> builds, or the start scene to edit.
    /// </para>
    /// </summary>
    public class Game
    {
        /// <summary>
        /// A scene made in the editor for the game to start in, inside the Assets folder ("Scenes/Main.scene"). The game's
        /// <see cref="Load"/> opens it, unless it's been given a Load of its own (which can call <c>base.Load()</c> first).
        /// In the editor, a game with a start scene opens it to edit, and Play plays whichever scene is open.
        /// </summary>
        public string? StartScene { get; set; }

        /// <summary>Runs once when the game starts, before the first frame: builds the world, or opens <see cref="StartScene"/>.</summary>
        public virtual void Load()
        {
            if (!string.IsNullOrEmpty(StartScene)) Scene.Load(StartScene);
        }

        /// <summary>Runs every frame, before the scene's objects update.</summary>
        public virtual void Update() {}

        /// <summary>Opens the game's window, <paramref name="width"/> by <paramref name="height"/>, and runs it until it's closed.</summary>
        public void Run(string title = "Vertex", int width = 1280, int height = 720) => new VertexWindow(width, height, this, title).Run();
    }
}
