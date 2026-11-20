using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vertex.Rendering
{
    /// <summary>One of the view's point or spot light shadows, as the shaders read it (see <see cref="LocalShadows"/>).</summary>
    public readonly record struct LocalShadowData(int FirstLayer, bool Point, Matrix4x4 SpotMatrix, float Near, float Far, float TexelSize);

    /// <summary>A layer of the local shadow map to draw again: how the light sees it, and what casts shadows into it.</summary>
    public readonly record struct LocalShadowDraw(int Layer, Matrix4x4 ViewProjection, int FirstCaster, int CasterCount);

    /// <summary>
    /// Shadows for point and spot lights. Each light that casts them gets layers of one shadow map that's kept from
    /// frame to frame (a spot light one, a point light six, one for each way it shines), and they're only drawn again
    /// when something changes: the light moves or changes, or something that casts a shadow in its reach moves, changes
    /// or comes or goes. So a still scene's lamps cost a lookup or four per pixel, and nothing to draw. The nearest
    /// <see cref="MaxPerView"/> lights to the camera get them.
    /// </summary>
    internal sealed class LocalShadows
    {
        /// <summary>The most lights with shadows in one view.</summary>
        public const int MaxPerView = 16;

        /// <summary>How many layers the shadow map has.</summary>
        public const int Layers = 48;

        /// <summary>How many texels across each layer is.</summary>
        public const int Resolution = 512;

        /// <summary>
        /// How far a point light's face looks to its side, as the tangent of half its width: a little more than a quarter
        /// turn, so lookups near its edges stay on it. The shaders have one over it (LOCAL_FACE_SCALE).
        /// </summary>
        public const float FaceTangent = 1f + 4f / Resolution;

        /// <summary>The ways a point light's faces look, and which way is up in each (as the shaders have them).</summary>
        public static readonly (Vector3 Forward, Vector3 Up)[] Faces =
        [
            (Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY)
        ];

        private sealed class Slot
        {
            public required object Owner;
            public int FirstLayer;
            public int LayerCount;
            public ulong Drawn;
            public bool HasDrawn;
            public long LastUsed;
        }

        private readonly List<Slot> _slots = new();
        private readonly bool[] _taken = new bool[Layers];
        private long _frame;

        /// <summary>A new frame: slots used last frame can be given away from now if needed.</summary>
        public void NextFrame() => _frame++;

        /// <summary>Forgets everything drawn, so it's all drawn again (the backend's map was made again).</summary>
        public void Forget()
        {
            foreach (Slot slot in _slots) slot.HasDrawn = false;
        }

        /// <summary>
        /// Picks the lights in <paramref name="frame"/>'s view that get shadows, gives each its layers, and lists in the
        /// frame what has to be drawn again. <paramref name="casters"/> is everything that casts shadows, with its bounds.
        /// </summary>
        public void Plan(RenderFrame frame, IReadOnlyList<(DrawCall Draw, Vector3 Centre, Vector3 Extents)> casters)
        {
            frame.LocalShadowList.Clear();
            frame.LocalShadowDrawList.Clear();
            frame.LocalShadowCasterList.Clear();

            // The shadow-casting lights, nearest the camera first.
            List<PointLightData> lights = frame.PointLightList;
            var wanted = new List<int>();
            for (int i = 0; i < lights.Count && i < Shaders.ShaderLibrary.MaxPointLights; i++)
            {
                if (lights[i].CastsShadows && !lights[i].Baked && lights[i].Owner != null) wanted.Add(i);
            }

            Vector3 eye = frame.CameraPosition;
            wanted.Sort((a, b) => (Vector3.Distance(lights[a].Position, eye) - lights[a].Range).CompareTo(Vector3.Distance(lights[b].Position, eye) - lights[b].Range));
            if (wanted.Count > MaxPerView) wanted.RemoveRange(MaxPerView, wanted.Count - MaxPerView);

            var inReach = new List<DrawCall>();
            foreach (int index in wanted)
            {
                PointLightData light = lights[index];
                bool point = !light.IsSpot;
                int layers = point ? 6 : 1;
                Slot? slot = SlotFor(light.Owner!, layers);
                if (slot == null) continue;
                slot.LastUsed = _frame;

                float near = Math.Clamp(light.Range * 0.01f, 0.03f, 0.5f);
                float far = MathF.Max(light.Range, near * 2f);

                // What casts shadows within its reach, and a fingerprint of it all with the light, to tell whether it's changed.
                inReach.Clear();
                ulong hash = 14695981039346656037UL;
                void Add(float value) => hash = (hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211UL;
                void AddVector(Vector3 v) { Add(v.X); Add(v.Y); Add(v.Z); }
                AddVector(light.Position);
                AddVector(light.Direction);
                Add(light.Range);
                Add(light.SpotOuter);
                foreach ((DrawCall draw, Vector3 centre, Vector3 extents) in casters)
                {
                    Vector3 nearest = Vector3.Clamp(light.Position, centre - extents, centre + extents);
                    if (Vector3.DistanceSquared(nearest, light.Position) > light.Range * light.Range) continue;
                    inReach.Add(draw);
                    hash = (hash ^ (uint)RuntimeHelpers.GetHashCode(draw.Mesh)) * 1099511628211UL;
                    Add(draw.Mesh.Version);
                    Matrix4x4 m = draw.WorldMatrix;
                    Add(m.M11); Add(m.M12); Add(m.M13); Add(m.M21); Add(m.M22); Add(m.M23); Add(m.M31); Add(m.M32); Add(m.M33); Add(m.M41); Add(m.M42); Add(m.M43);
                }

                Matrix4x4 spotMatrix = Matrix4x4.Identity;
                float texel;
                if (point)
                {
                    texel = 2f * FaceTangent / Resolution;
                }
                else
                {
                    float half = MathF.Acos(Math.Clamp(light.SpotOuter, -1f, 1f));
                    float fov = Math.Clamp(half * 2f + 0.05f, 0.05f, MathF.PI * 0.95f);
                    Vector3 forward = Vector3.Normalize(light.Direction);
                    Vector3 up = MathF.Abs(forward.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ;
                    spotMatrix = Matrix4x4.CreateLookAt(light.Position, light.Position + forward, up) * Matrix4x4.CreatePerspectiveFieldOfView(fov, 1f, near, far);
                    texel = 2f * MathF.Tan(fov * 0.5f) / Resolution;
                }

                int shadow = frame.LocalShadowList.Count;
                frame.LocalShadowList.Add(new LocalShadowData(slot.FirstLayer, point, spotMatrix, near, far, texel));
                frame.Clusters.SetShadow(index, shadow);

                if (slot.HasDrawn && slot.Drawn == hash) continue;
                slot.Drawn = hash;
                slot.HasDrawn = true;

                for (int face = 0; face < layers; face++)
                {
                    Matrix4x4 viewProjection = point ? FaceMatrix(light.Position, face, near, far) : spotMatrix;
                    var frustum = new Frustum(viewProjection);
                    int first = frame.LocalShadowCasterList.Count;
                    foreach (DrawCall draw in inReach)
                    {
                        Culling.WorldBounds(draw.Mesh, draw.WorldMatrix, out Vector3 centre, out Vector3 extents);
                        if (frustum.Intersects(centre, extents)) frame.LocalShadowCasterList.Add(draw);
                    }

                    frame.LocalShadowDrawList.Add(new LocalShadowDraw(slot.FirstLayer + face, viewProjection, first, frame.LocalShadowCasterList.Count - first));
                }
            }
        }

        /// <summary>How a point light at <paramref name="position"/> sees through its face <paramref name="face"/> (see <see cref="Faces"/>).</summary>
        public static Matrix4x4 FaceMatrix(Vector3 position, int face, float near, float far)
        {
            (Vector3 forward, Vector3 up) = Faces[face];
            float fov = 2f * MathF.Atan(FaceTangent);
            return Matrix4x4.CreateLookAt(position, position + forward, up) * Matrix4x4.CreatePerspectiveFieldOfView(fov, 1f, near, far);
        }

        /// <summary>The layers <paramref name="owner"/> has, or new ones: free ones, or those of a light not used this frame that waited longest.</summary>
        private Slot? SlotFor(object owner, int count)
        {
            foreach (Slot existing in _slots)
            {
                if (existing.Owner == owner && existing.LayerCount == count) return existing;
            }

            // A light that changed between point and spot gives its old layers back.
            for (int i = _slots.Count - 1; i >= 0; i--)
            {
                if (_slots[i].Owner == owner) Free(i);
            }

            while (true)
            {
                int first = FreeRun(count);
                if (first >= 0)
                {
                    for (int i = 0; i < count; i++) _taken[first + i] = true;
                    var slot = new Slot { Owner = owner, FirstLayer = first, LayerCount = count };
                    _slots.Add(slot);
                    return slot;
                }

                int oldest = -1;
                for (int i = 0; i < _slots.Count; i++)
                {
                    if (_slots[i].LastUsed >= _frame) continue;
                    if (oldest < 0 || _slots[i].LastUsed < _slots[oldest].LastUsed) oldest = i;
                }

                if (oldest < 0) return null;
                Free(oldest);
            }
        }

        private void Free(int index)
        {
            Slot slot = _slots[index];
            for (int i = 0; i < slot.LayerCount; i++) _taken[slot.FirstLayer + i] = false;
            _slots.RemoveAt(index);
        }

        private int FreeRun(int count)
        {
            for (int first = 0; first + count <= Layers; first++)
            {
                bool free = true;
                for (int i = 0; i < count && free; i++) free = !_taken[first + i];
                if (free) return first;
            }

            return -1;
        }
    }
}
