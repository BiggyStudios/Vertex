using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>Whether a <see cref="FlatRenderer"/> turns to face the camera.</summary>
    public enum Billboard
    {
        /// <summary>It faces the way its object does.</summary>
        None,

        /// <summary>It always faces the camera square on, as a name over a player's head should.</summary>
        FaceCamera,

        /// <summary>It turns to face the camera but stays upright: a tree or a character drawn flat in a 3D world.</summary>
        Upright
    }

    /// <summary>Where the camera drawing this view is and which way it looks: what billboards turn to face.</summary>
    internal readonly record struct ViewAxes(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward);

    /// <summary>
    /// Something drawn as flat pictures in the world: a <see cref="SpriteRenderer"/>, or <see cref="WorldText"/>. It lies
    /// in its object's X-Y plane, X to the right and Y up, the right way round for a camera looking the way cameras do
    /// unturned (along -Z), and see-through parts show what's behind. Where they overlap, a higher
    /// <see cref="SortingOrder"/> draws on top; with the same order, the nearer one does.
    /// </summary>
    public abstract class FlatRenderer : Component
    {
        [Tooltip("Which draws over which where they overlap: higher on top, however far away. With the same order, the nearer is on top")]
        public int SortingOrder { get; set; }

        [Tooltip("Turns to face the camera: square on, or only about its up axis, staying upright")]
        public Billboard Billboard { get; set; }

        /// <summary>Adds what it draws this view, in the world, to <paramref name="into"/>.</summary>
        internal abstract void Collect(List<WorldQuad> into, in ViewAxes view);

        /// <summary>Its object's world matrix, turned to face the camera if it's a billboard.</summary>
        private protected Matrix4x4 DrawMatrix(in ViewAxes view)
        {
            if (Billboard == Billboard.None) return Transform.WorldMatrix;

            Vector3 position = Transform.WorldPosition;
            Vector3 right, up, back;
            if (Billboard == Billboard.FaceCamera)
            {
                right = view.Right;
                up = view.Up;
                back = -view.Forward;
            }
            else
            {
                up = Vector3.UnitY;
                Vector3 toCamera = view.Position - position;
                toCamera.Y = 0f;
                back = toCamera.LengthSquared() > 1e-8f ? Vector3.Normalize(toCamera) : -new Vector3(view.Forward.X, 0f, view.Forward.Z);
                if (back.LengthSquared() < 1e-8f) back = Vector3.UnitZ;
                back = Vector3.Normalize(back);
                right = Vector3.Cross(up, back);
            }

            var facing = new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                back.X, back.Y, back.Z, 0f,
                position.X, position.Y, position.Z, 1f);
            return Matrix4x4.CreateScale(Transform.WorldScale) * facing;
        }
    }
}
