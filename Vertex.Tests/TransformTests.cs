using System.Numerics;

namespace Vertex.Tests
{
    public class TransformTests : EngineTest
    {
        private static void AssertClose(Vector3 expected, Vector3 actual) =>
            Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"Expected {expected}, got {actual}");

        [Fact]
        public void A_child_moves_and_turns_with_its_parent()
        {
            var parent = new GameObject();
            parent.Transform.Position = new Vector3(10f, 0f, 0f);
            parent.Transform.EulerAngles = new Vector3(0f, 90f, 0f);

            var child = new GameObject();
            child.Transform.Parent = parent.Transform;
            child.Transform.Position = new Vector3(0f, 0f, -1f);

            // Forward is -Z; turned 90 degrees left (about +Y), the parent's forward is -X.
            AssertClose(new Vector3(9f, 0f, 0f), child.Transform.WorldPosition);
            AssertClose(-Vector3.UnitX, child.Transform.Forward);
        }

        [Fact]
        public void Setting_world_position_under_a_parent_lands_there()
        {
            var parent = new GameObject();
            parent.Transform.Position = new Vector3(3f, 2f, 1f);
            parent.Transform.EulerAngles = new Vector3(20f, 45f, 0f);
            parent.Transform.Scale = new Vector3(2f);

            var child = new GameObject();
            child.Transform.Parent = parent.Transform;
            child.Transform.WorldPosition = new Vector3(-5f, 7f, 4f);

            AssertClose(new Vector3(-5f, 7f, 4f), child.Transform.WorldPosition);
        }

        [Fact]
        public void A_transform_cannot_be_its_own_ancestor()
        {
            var parent = new GameObject();
            var child = new GameObject();
            child.Transform.Parent = parent.Transform;

            Assert.Throws<InvalidOperationException>(() => parent.Transform.Parent = child.Transform);
            Assert.Throws<InvalidOperationException>(() => parent.Transform.Parent = parent.Transform);
        }

        [Fact]
        public void A_transform_cannot_be_parented_into_another_scene()
        {
            var here = new GameObject();
            var elsewhere = new GameObject("Elsewhere", new Scene());

            Assert.Throws<InvalidOperationException>(() => here.Transform.Parent = elsewhere.Transform);
        }

        [Fact]
        public void A_childs_place_in_the_world_follows_every_change_above_it()
        {
            var root = new GameObject("Root");
            var middle = new GameObject("Middle");
            var leaf = new GameObject("Leaf");
            middle.Transform.Parent = root.Transform;
            leaf.Transform.Parent = middle.Transform;
            leaf.Transform.Position = new Vector3(1f, 0f, 0f);
            AssertClose(new Vector3(1f, 0f, 0f), leaf.Transform.WorldPosition);
            int version = leaf.Transform.Version;

            // Read, then something above moves: read again, it's moved too.
            root.Transform.Position = new Vector3(0f, 5f, 0f);
            AssertClose(new Vector3(1f, 5f, 0f), leaf.Transform.WorldPosition);
            Assert.NotEqual(version, leaf.Transform.Version);

            middle.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            AssertClose(new Vector3(0f, 5f, -1f), leaf.Transform.WorldPosition);
            middle.Transform.Scale = new Vector3(2f);
            AssertClose(new Vector3(0f, 5f, -2f), leaf.Transform.WorldPosition);
            AssertClose(new Vector3(2f), leaf.Transform.WorldScale);

            // Moved under another parent, it's where that one puts it.
            var other = new GameObject("Other");
            other.Transform.Position = new Vector3(10f, 0f, 0f);
            version = leaf.Transform.Version;
            leaf.Transform.Parent = other.Transform;
            AssertClose(new Vector3(11f, 0f, 0f), leaf.Transform.WorldPosition);
            Assert.NotEqual(version, leaf.Transform.Version);

            // Nothing moving, nothing changes.
            version = leaf.Transform.Version;
            _ = leaf.Transform.WorldMatrix;
            leaf.Transform.Position = leaf.Transform.Position;
            Assert.Equal(version, leaf.Transform.Version);
        }
    }
}
