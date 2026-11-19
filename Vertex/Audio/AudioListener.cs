namespace Vertex.Audio
{
    /// <summary>
    /// Where sound is heard from: sounds in the world are louder nearer it and come from their direction as it turns.
    /// Without one, sound is heard from the main camera. Put it on the player's head in a third-person game, so footsteps
    /// sound as near as the player is, not the camera behind them. If there are several, the first switched on is used.
    /// </summary>
    public sealed class AudioListener : Component;
}
