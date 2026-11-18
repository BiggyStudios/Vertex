using System.Numerics;

using Vertex.Graphs;

namespace Vertex.Effects
{
    /// <summary>
    /// One particle as a VFX graph works on it. Values in the graph are worked out for each particle: its age, where it
    /// is, its own random number.
    /// </summary>
    public struct VfxParticle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector4 Color;
        public float Size;
        public float Age;
        public float Lifetime;
        public float Rotation;

        /// <summary>A number from 0 to 1 picked when the particle is made, the same for its whole life.</summary>
        public float Random;

        /// <summary>The fraction of its life it's lived, from 0 to 1.</summary>
        public readonly float Life => Lifetime > 0f ? Math.Clamp(Age / Lifetime, 0f, 1f) : 1f;
    }

    /// <summary>What's the same for every particle this frame: the effect's clock, where it is, and its properties.</summary>
    public sealed class VfxFrame
    {
        /// <summary>Seconds since the effect started playing.</summary>
        public float Time;

        public float DeltaTime;

        /// <summary>Where the effect is, and which way it's turned.</summary>
        public Matrix4x4 World = Matrix4x4.Identity;

        /// <summary>The effect's properties (its graph's variables, or what the game set them to), as values.</summary>
        public readonly Dictionary<string, Vector4> Properties = new();
    }

    /// <summary>
    /// A value worked out for one particle: a number is in X, a vector in X, Y and Z, a colour in all four, true/false
    /// as 1 or 0 in X. One shape for every type keeps a graph quick to run for thousands of particles.
    /// </summary>
    public delegate Vector4 VfxValue(ref VfxParticle particle, VfxFrame frame);

    /// <summary>What a node is in a VFX graph: a value worked out from others, or one of the steps a particle goes through.</summary>
    public enum VfxRole
    {
        /// <summary>Works out values from its inputs (maths, shapes, a particle's age).</summary>
        Value,

        /// <summary>How many particles are made, and when: the start of a particle system.</summary>
        Spawn,

        /// <summary>What each particle starts as: how long it lives, where, how fast, how big, what colour.</summary>
        Initialize,

        /// <summary>What happens to each particle every frame: forces, drag, swirling.</summary>
        Update,

        /// <summary>How each particle is drawn.</summary>
        Output
    }

    /// <summary>
    /// A kind of node in a VFX graph. A value node turns the values coming into its inputs into values for its outputs
    /// (<see cref="Compile"/>); the steps (spawn, initialize, update, output) are linked one after another by their
    /// Particles ports, and the effect reads their inputs.
    /// </summary>
    public sealed class VfxNode : NodeType
    {
        public VfxRole Role { get; }

        /// <summary>For a value node: given how to work out each input (in order), how to work out each output (in order).</summary>
        public Func<VfxValue[], VfxValue[]>? Compile { get; init; }

        public VfxNode(string id, string title, string category, VfxRole role, IReadOnlyList<Port> inputs, IReadOnlyList<Port> outputs)
            : base(id, title, category, inputs, outputs)
        {
            Role = role;
        }
    }
}
