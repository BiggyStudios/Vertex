namespace Vertex.Editor
{
    internal static class Program
    {
        public static void Main()
        {
            Vertex.Platform.Terminal.Attach();

            // Started before anything is logged, so the Console has it all from the start.
            _ = Editing.EditorLog.Shared;
            new EditorWindow().Run();
        }
    }
}
