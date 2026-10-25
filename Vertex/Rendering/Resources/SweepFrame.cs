using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// One point along a <see cref="Primitives.CreateSweep"/> path: where the cross-section goes, and which ways its X
    /// (<see cref="Right"/>) and Y (<see cref="Up"/>) point there. Both should be unit length and at right angles.
    /// </summary>
    public readonly record struct SweepFrame(Vector3 Position, Vector3 Right, Vector3 Up)
    {
        /// <summary>The way the path heads at this frame: Right and Up's third direction, as a camera looks along -Z.</summary>
        public Vector3 Forward => Vector3.Cross(Up, Right);

        /// <summary>Where a corner of the cross-section ends up at this frame.</summary>
        public Vector3 Place(Vector2 corner) => Position + Right * corner.X + Up * corner.Y;
    }
}
