using System.Numerics;

using Vertex.Rendering;

namespace Vertex.Editor.SceneEditing
{
    /// <summary>Finds what's under the mouse in the scene view: the nearest mesh a ray hits, by its actual triangles.</summary>
    public static class Picking
    {
        /// <summary>The object whose mesh the ray hits first, and how far along the ray, or null if it hits nothing.</summary>
        public static (GameObject GameObject, float Distance)? Pick(Scene scene, Vector3 origin, Vector3 direction)
        {
            (GameObject, float)? nearest = null;
            float best = float.MaxValue;

            foreach (MeshRenderer renderer in scene.FindComponents<MeshRenderer>())
            {
                if (renderer.Mesh is not { } mesh) continue;

                Matrix4x4 world = renderer.Transform.WorldMatrix;
                Culling.WorldBounds(mesh, world, out Vector3 centre, out Vector3 extents);
                if (RayBox(origin, direction, centre - extents, centre + extents) is not float boxDistance || boxDistance > best) continue;

                if (RayMesh(mesh, world, origin, direction) is float distance && distance < best)
                {
                    best = distance;
                    nearest = (renderer.GameObject, distance);
                }
            }

            return nearest;
        }

        /// <summary>How far along the ray it enters the box (0 if it starts inside), or null if it misses.</summary>
        public static float? RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max)
        {
            float near = 0f, far = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = origin[axis], d = direction[axis];
                if (MathF.Abs(d) < 1e-8f)
                {
                    if (o < min[axis] || o > max[axis]) return null;
                    continue;
                }

                float t1 = (min[axis] - o) / d, t2 = (max[axis] - o) / d;
                if (t1 > t2) (t1, t2) = (t2, t1);
                near = MathF.Max(near, t1);
                far = MathF.Min(far, t2);
                if (near > far) return null;
            }

            return near;
        }

        /// <summary>How far along the (world) ray it first hits one of the mesh's triangles, placed by <paramref name="world"/>.</summary>
        public static float? RayMesh(Mesh mesh, Matrix4x4 world, Vector3 origin, Vector3 direction)
        {
            // Into the mesh's own space, where its triangles are.
            if (!Matrix4x4.Invert(world, out Matrix4x4 inverse)) return null;
            Vector3 localOrigin = Vector3.Transform(origin, inverse);
            Vector3 localDirection = Vector3.TransformNormal(direction, inverse);

            float best = float.MaxValue;
            MeshVertex[] vertices = mesh.Vertices;
            uint[] indices = mesh.Indices;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                Vector3 a = vertices[indices[i]].Position, b = vertices[indices[i + 1]].Position, c = vertices[indices[i + 2]].Position;
                if (RayTriangle(localOrigin, localDirection, a, b, c) is float t && t < best) best = t;
            }

            if (best == float.MaxValue) return null;

            // Back to a world distance: the local direction isn't unit length if the object is scaled.
            Vector3 hit = Vector3.Transform(localOrigin + localDirection * best, world);
            return Vector3.Distance(origin, hit);
        }

        /// <summary>Möller–Trumbore: how far along the ray it hits the triangle (either side), or null.</summary>
        public static float? RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 edge1 = b - a, edge2 = c - a;
            Vector3 p = Vector3.Cross(direction, edge2);
            float determinant = Vector3.Dot(edge1, p);
            if (MathF.Abs(determinant) < 1e-10f) return null;

            float inverse = 1f / determinant;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, p) * inverse;
            if (u < 0f || u > 1f) return null;

            Vector3 q = Vector3.Cross(s, edge1);
            float v = Vector3.Dot(direction, q) * inverse;
            if (v < 0f || u + v > 1f) return null;

            float t = Vector3.Dot(edge2, q) * inverse;
            return t > 0f ? t : null;
        }
    }
}
