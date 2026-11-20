using System.Numerics;

using Vertex.Physics;

namespace Vertex.Animation
{
    /// <summary>
    /// What IK components share: each bends bones to reach for something, after animation has posed them and before
    /// <see cref="Component.LateUpdate"/>, as much as its <see cref="Weight"/> says.
    /// </summary>
    public abstract class InverseKinematics : Component
    {
        [Tooltip("How much it bends the bones: 0 leaves the animation as it is, 1 reaches all the way")]
        [Range(0f, 1f)]
        public float Weight { get; set; } = 1f;

        /// <summary>Bends the bones, as posed this frame.</summary>
        internal abstract void Solve();

        // The bones' rotations before it last bent them, and after: bones no animation moves stay as it left them, so it
        // puts them back first, or it would bend them further each frame.
        private Transform[] _bones = [];
        private Quaternion[] _before = [], _after = [];

        /// <summary>Puts <paramref name="bones"/> back as they were before it last bent them, if nothing's moved them since; then notes how they are.</summary>
        private protected void Unbend(Transform[] bones)
        {
            bool same = bones.Length == _bones.Length;
            for (int i = 0; i < bones.Length && same; i++) same = bones[i] == _bones[i];
            if (same)
            {
                bool untouched = true;
                for (int i = 0; i < bones.Length && untouched; i++) untouched = bones[i].Rotation == _after[i];
                if (untouched)
                {
                    for (int i = 0; i < bones.Length; i++) bones[i].Rotation = _before[i];
                }
            }
            else
            {
                _bones = bones;
                _before = new Quaternion[bones.Length];
                _after = new Quaternion[bones.Length];
            }

            for (int i = 0; i < bones.Length; i++) _before[i] = bones[i].Rotation;
        }

        /// <summary>Notes how the bones are after bending them, for <see cref="Unbend"/>.</summary>
        private protected void Bent()
        {
            for (int i = 0; i < _bones.Length; i++) _after[i] = _bones[i].Rotation;
        }

        /// <summary>Which go first: a spine turned to look before the arms reach from it.</summary>
        internal virtual int Order => 0;

        /// <summary>Every IK component in <paramref name="scene"/> bends its bones: after the animators.</summary>
        internal static void SolveAll(Scene scene)
        {
            List<InverseKinematics>? solvers = null;
            foreach (InverseKinematics solver in scene.FindComponents<InverseKinematics>())
            {
                if (!solver.Started || solver.Weight <= 0f) continue;
                (solvers ??= new()).Add(solver);
            }

            if (solvers == null) return;
            if (solvers.Count > 1) solvers.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (InverseKinematics solver in solvers)
            {
                try
                {
                    solver.Solve();
                }
                catch (Exception exception)
                {
                    Log.Error($"{solver.GameObject.Name}'s {solver.GetType().Name}: {exception.Message}");
                    solver.Enabled = false;
                }
            }
        }

        /// <summary>The rotation that turns direction <paramref name="from"/> to <paramref name="to"/>, the shortest way.</summary>
        internal static Quaternion Between(Vector3 from, Vector3 to)
        {
            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);
            float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
            if (dot > 0.99999f) return Quaternion.Identity;
            if (dot < -0.99999f)
            {
                // Opposite: half a turn round any axis at right angles.
                Vector3 axis = Vector3.Cross(Vector3.UnitX, from);
                if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, from);
                return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
            }

            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(from, to)), MathF.Acos(dot));
        }

        /// <summary>Turns <paramref name="transform"/> by <paramref name="rotation"/> in the world, round its own position.</summary>
        internal static void TurnInWorld(Transform transform, Quaternion rotation) =>
            transform.WorldRotation = Quaternion.Normalize(rotation * transform.WorldRotation);
    }

    /// <summary>
    /// Bends a limb so its end reaches a target: put it on a hand (or a foot), and it bends the elbow and turns the
    /// shoulder (the bones above it) so the hand is at <see cref="Target"/>, as far as the arm reaches. A
    /// <see cref="Pole"/> says which way the elbow points. With <see cref="Ground"/> on, a foot finds the ground under it
    /// instead, so it stands on steps and slopes.
    /// </summary>
    public sealed class TwoBoneIK : InverseKinematics
    {
        [Tooltip("What the end of the limb reaches for: an object to hold, a handle, a ledge")]
        public Transform? Target { get; set; }

        [Tooltip("Which way the middle joint points: an object in front of the knee, or out from the elbow. Without one, it bends the way the animation has it")]
        public Transform? Pole { get; set; }

        [Tooltip("The end of the limb also turns to match the target's rotation (a hand gripping a handle); otherwise it keeps the animation's")]
        public bool MatchTargetRotation { get; set; }

        [Tooltip("Stands this foot on the ground under it (found with a ray), lifted as much as the animation lifts it: for steps and slopes. A Target, if there is one, is reached for instead")]
        public bool Ground { get; set; }

        [Tooltip("With Ground on, how far the foot's sole is below the bone: so it rests on the ground rather than in it")]
        public float FootHeight { get; set; } = 0.08f;

        [Tooltip("With Ground on, how far above and below the foot the ground is looked for")]
        public float GroundReach { get; set; } = 0.5f;

        [Tooltip("With Ground on, the foot also tilts to lie flat on a slope")]
        public bool AlignToGround { get; set; } = true;

        /// <summary>The limb: the bone two above (a shoulder), one above (an elbow), and this one (a hand).</summary>
        private (Transform Root, Transform Middle, Transform End)? Limb() =>
            Transform.Parent is { } middle && middle.Parent is { } root ? (root, middle, Transform) : null;

        internal override int Order => 1;

        internal override void Solve()
        {
            if (Limb() is not { } limb) return;
            if (_limb.Length != 3 || _limb[0] != limb.Root || _limb[1] != limb.Middle) _limb = [limb.Root, limb.Middle, limb.End];
            Unbend(_limb);
            try
            {
                Bend(limb);
            }
            finally
            {
                Bent();
            }
        }

        private Transform[] _limb = [];

        private void Bend((Transform Root, Transform Middle, Transform End) limb)
        {
            Vector3 end = limb.End.WorldPosition;
            Quaternion endRotation = limb.End.WorldRotation;

            Vector3 goal;
            Quaternion? goalRotation = null;
            if (Target != null)
            {
                goal = Target.WorldPosition;
                if (MatchTargetRotation) goalRotation = Target.WorldRotation;
            }
            else if (Ground && GroundUnder(end) is { } ground)
            {
                goal = ground.Point;
                if (AlignToGround) goalRotation = Between(Vector3.UnitY, ground.Normal) * endRotation;
            }
            else
            {
                return;
            }

            float weight = Math.Clamp(Weight, 0f, 1f);
            Reach(limb.Root, limb.Middle, limb.End, Vector3.Lerp(end, goal, weight), Pole?.WorldPosition, weight);

            // The end keeps the way the animation turned it in the world (bending the arm turned it), or turns to the target's.
            Quaternion final = goalRotation is { } wanted ? Quaternion.Slerp(endRotation, wanted, weight) : endRotation;
            limb.End.WorldRotation = Quaternion.Normalize(final);
        }

        /// <summary>Where the foot stands with Ground on: the ground under it, raised by as much as the animation has it above the character's own feet.</summary>
        private (Vector3 Point, Vector3 Normal)? GroundUnder(Vector3 foot)
        {
            Scene? scene = GameObject.Scene;
            if (scene == null) return null;

            // The character's feet are where the animator's object is: how high the animation lifts the foot is from there.
            Transform character = Transform;
            for (Transform? t = Transform; t != null; t = t.Parent)
            {
                if (t.GameObject.GetComponent<Animator>() != null)
                {
                    character = t;
                    break;
                }
            }

            float lift = MathF.Max(0f, foot.Y - character.WorldPosition.Y - FootHeight);
            Collider? own = character.GameObject.GetComponent<Collider>();
            var from = new Vector3(foot.X, character.WorldPosition.Y + GroundReach, foot.Z);
            if (!scene.Physics.Raycast(from, -Vector3.UnitY, GroundReach * 2f, out RaycastHit hit, own)) return null;
            return (hit.Point + Vector3.UnitY * (lift + FootHeight), hit.Normal);
        }

        /// <summary>
        /// Bends root, middle and end so the end is at <paramref name="goal"/> (or as near as the limb reaches), keeping the
        /// bend in the plane it was in, then turned round so the middle points at the pole.
        /// </summary>
        internal static void Reach(Transform root, Transform middle, Transform end, Vector3 goal, Vector3? pole, float poleWeight = 1f)
        {
            Vector3 a = root.WorldPosition, b = middle.WorldPosition, c = end.WorldPosition;
            float upper = Vector3.Distance(a, b), lower = Vector3.Distance(b, c);
            if (upper < 1e-5f || lower < 1e-5f) return;

            float distance = Math.Clamp(Vector3.Distance(a, goal), MathF.Abs(upper - lower) + 1e-4f, upper + lower - 1e-4f);

            // The plane the limb bends in: as it is, or (when it's straight) towards the pole, or any.
            Vector3 axis = Vector3.Cross(c - a, b - a);
            if (axis.LengthSquared() < 1e-10f && pole is { } towards) axis = Vector3.Cross(c - a, towards - a);
            if (axis.LengthSquared() < 1e-10f) axis = Vector3.Cross(c - a, MathF.Abs(Vector3.Normalize(c - a).Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            axis = Vector3.Normalize(axis);

            // The angles at the root and the middle now, and as they need to be for the end to be that far from the root.
            float rootNow = Angle(c - a, b - a), middleNow = Angle(a - b, c - b);
            float rootWanted = MathF.Acos(Math.Clamp((lower * lower - upper * upper - distance * distance) / (-2f * upper * distance), -1f, 1f));
            float middleWanted = MathF.Acos(Math.Clamp((distance * distance - upper * upper - lower * lower) / (-2f * upper * lower), -1f, 1f));

            TurnInWorld(middle, Quaternion.CreateFromAxisAngle(axis, middleWanted - middleNow));
            TurnInWorld(root, Quaternion.CreateFromAxisAngle(axis, rootNow - rootWanted));

            // Then the whole limb round to point at the goal.
            TurnInWorld(root, Between(end.WorldPosition - a, goal - a));

            // And round the line to the goal, so the middle joint points at the pole.
            if (pole is not { } p || poleWeight <= 0f) return;
            Vector3 line = goal - a;
            if (line.LengthSquared() < 1e-10f) return;
            line = Vector3.Normalize(line);
            Vector3 bend = Flatten(middle.WorldPosition - a, line), wanted = Flatten(p - a, line);
            if (bend.LengthSquared() < 1e-10f || wanted.LengthSquared() < 1e-10f) return;
            float angle = Angle(bend, wanted) * poleWeight;
            if (Vector3.Dot(Vector3.Cross(bend, wanted), line) < 0f) angle = -angle;
            TurnInWorld(root, Quaternion.CreateFromAxisAngle(line, angle));
        }

        private static float Angle(Vector3 a, Vector3 b)
        {
            float lengths = a.Length() * b.Length();
            return lengths < 1e-10f ? 0f : MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / lengths, -1f, 1f));
        }

        private static Vector3 Flatten(Vector3 vector, Vector3 normal) => vector - normal * Vector3.Dot(vector, normal);
    }

    /// <summary>
    /// Turns a bone to look at something: put it on a head, and it turns the head (and, with <see cref="Bones"/> more than
    /// 1, the neck and spine above it, each a share) to face <see cref="Target"/>, no further round than
    /// <see cref="MaxAngle"/> from the way the character faces.
    /// </summary>
    public sealed class LookAtIK : InverseKinematics
    {
        [Tooltip("What it looks at")]
        public Transform? Target { get; set; }

        [Tooltip("How many bones share the turn, from this one up: 1 for the head alone, 3 for the head, neck and upper back")]
        [Range(1f, 6f)]
        public int Bones { get; set; } = 2;

        [Tooltip("The furthest it turns from the way the character faces, in degrees: past that it looks as far round as it can")]
        [Range(0f, 180f)]
        public float MaxAngle { get; set; } = 75f;

        [Tooltip("Which way the bone faces, in its own space. All zero works it out from the way the character faces when the game starts")]
        public Vector3 Forward { get; set; }

        private Vector3? _forward;

        internal override int Order => -1;

        internal override void Solve()
        {
            if (Target == null) return;
            Transform head = Transform;
            Transform character = CharacterOf(head);
            Vector3 facing = character.Forward;

            // The bones from the highest down, put back first if no animation's moved them since last frame.
            var chain = new List<Transform>();
            for (Transform? t = head; t != null && chain.Count < Math.Max(1, Bones) && t != character; t = t.Parent) chain.Add(t);
            chain.Reverse();
            if (!chain.SequenceEqual(_chain)) _chain = chain.ToArray();
            Unbend(_chain);

            // The bone's own forward: as set, or the way the character faced when this first ran, in the bone's space.
            Vector3 forward = Forward.LengthSquared() > 1e-8f ? Vector3.Normalize(Forward) : _forward ??= Vector3.Transform(facing, Quaternion.Inverse(head.WorldRotation));

            // Where to look, kept within MaxAngle of the character's facing.
            Vector3 wanted = Target.WorldPosition - head.WorldPosition;
            if (wanted.LengthSquared() < 1e-8f)
            {
                Bent();
                return;
            }

            wanted = Vector3.Normalize(wanted);
            float limit = MaxAngle * MathF.PI / 180f;
            float off = MathF.Acos(Math.Clamp(Vector3.Dot(facing, wanted), -1f, 1f));
            if (off > limit && off > 1e-4f) wanted = Vector3.Transform(facing, Quaternion.Slerp(Quaternion.Identity, Between(facing, wanted), limit / off));

            // Each bone takes its share of what's left to turn.
            float weight = Math.Clamp(Weight, 0f, 1f);
            for (int i = 0; i < chain.Count; i++)
            {
                Vector3 looking = Vector3.Transform(forward, head.WorldRotation);
                Quaternion turn = Between(looking, wanted);
                float share = weight / (chain.Count - i);
                TurnInWorld(chain[i], Quaternion.Slerp(Quaternion.Identity, turn, share));
            }

            Bent();
        }

        private Transform[] _chain = [];

        private static Transform CharacterOf(Transform bone)
        {
            for (Transform? t = bone.Parent; t != null; t = t.Parent)
            {
                if (t.GameObject.GetComponent<Animator>() != null) return t;
            }

            return bone.Parent ?? bone;
        }
    }
}
