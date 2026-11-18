namespace Vertex.Rendering
{
    /// <summary>
    /// Something that makes particles (<see cref="ParticleEmitter"/>, <see cref="Effects.VisualEffect"/>) and can be played
    /// on by hand: for an editor to show it while it's selected.
    /// </summary>
    public interface IParticleEffect
    {
        /// <summary>Seconds since it started playing.</summary>
        float PlayTime { get; }

        /// <summary>How many particles are alive, if it can tell (ones on the GPU can't).</summary>
        int? Alive { get; }

        /// <summary>Whether it's done: it won't make any more by itself, and those it made are gone.</summary>
        bool Finished { get; }

        /// <summary>Whether it plays by itself when the game starts, rather than waiting for code to start it.</summary>
        bool PlaysByItself { get; }

        /// <summary>Moves it on by <paramref name="deltaTime"/> seconds, as a frame of the game does. Never destroys anything.</summary>
        void Simulate(float deltaTime);

        /// <summary>Takes its particles away and goes back to the start, without changing any of its settings.</summary>
        void Rewind();
    }
}
