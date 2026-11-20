using System.Numerics;

using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.Animation
{
    /// <summary>
    /// Animates its object and the objects under it: plays an <see cref="AnimationClip"/> (made in the editor's Animation
    /// panel, or a model's own walk and run), or an animation graph that blends clips by what the game tells it
    /// (<see cref="Set"/> a speed, and it blends from walking to running). A model with bones moves its mesh with them
    /// (see <see cref="Rendering.SkinnedMeshRenderer"/>).
    /// <para>
    /// Animation happens each frame after every component's Update, so what the game sets in Update is seen the same frame;
    /// change what the animation put somewhere in <see cref="Component.LateUpdate"/>.
    /// </para>
    /// </summary>
    public sealed class Animator : Component
    {
        [Tooltip("The animation it plays. An Animation Graph plays instead, if one's chosen")]
        public AnimationClip? Clip { get; set; }

        [Tooltip("An animation graph (a .animgraph file, made in the editor) that blends animations by what the game sets: walking into running, an attack over the top")]
        [GraphKind(AnimationNodes.Kind)]
        public Graph? AnimationGraph { get; set; }

        [Tooltip("Plays as soon as the game starts")]
        public bool PlayOnStart { get; set; } = true;

        [Tooltip("How fast it plays: 1 as made, 2 twice as fast")]
        [Range(0f, 3f)]
        public float Speed { get; set; } = 1f;

        [Tooltip("The animations move the character through the world (a walk walks it forward) instead of playing on the spot. With a Character Controller or Rigidbody, it sets their velocity along the ground instead")]
        public bool RootMotion { get; set; }

        [Tooltip("The object whose movement is the character's, by name (a model's hips or root bone). Empty for the highest one the animations move")]
        public string RootMotionFrom { get; set; } = "";

        /// <summary>With <see cref="RootMotion"/> on, how fast the animation moved the character last frame, in the world (units a second).</summary>
        [HideInInspector]
        [Serialization.DontSave]
        public Vector3 RootMotionVelocity { get; private set; }

        /// <summary>With <see cref="RootMotion"/> on, how much the animation turned the character last frame.</summary>
        [HideInInspector]
        [Serialization.DontSave]
        public Quaternion RootMotionTurn { get; private set; } = Quaternion.Identity;

        /// <summary>This object's own starting values for the animation graph's parameters, in place of the graph's. Set in the inspector, under the animator.</summary>
        [HideInInspector]
        public List<ScriptVariableValue> Parameters { get; set; } = new();

        /// <summary>Raised when a clip it's playing passes one of its events: a footstep, a sword's swing. Visual scripts on the object hear it as an event of the same name.</summary>
        public event Action<AnimationEvent>? EventReached;

        /// <summary>Something playing, and how much of the pose is its: the graph at the bottom, clips played over it.</summary>
        private sealed class Layer
        {
            public AnimationClip? Clip;
            public AnimationGraphPlayer? Graph;
            public float Time;
            public float Weight;
            public float Target = 1f;
            public float FadeRate;
        }

        private readonly List<Layer> _layers = new();
        private readonly Dictionary<string, object?> _parameters = new();
        private readonly List<string> _triggers = new();
        private AnimationRig? _rig;
        private Pose? _pose, _layerPose;
        private Graph? _parametersFrom;

        /// <summary>What it animates, looked up the first time it's needed.</summary>
        internal AnimationRig Rig => _rig ??= new AnimationRig(GameObject);

        private int? _motionRoot;

        /// <summary>The object root motion comes from, as a place in the rig; -1 with root motion off. Looked up once a frame, the first time a clip's sampled.</summary>
        internal int MotionRoot => _motionRoot ??= RootMotion ? Rig.MotionRoot(RootMotionFrom) : -1;

        /// <summary>A clip sampled from <paramref name="from"/> to <paramref name="to"/> seconds of playing: with root motion, how far it moved the character, into the pose.</summary>
        internal void Moved(AnimationClip clip, float from, float to, Pose pose)
        {
            if (RootMotion) Rig.Move(clip, from, to, MotionRoot, pose);
        }

        /// <summary>The state its animation graph's state machine is in ("Idle", "Jump"); null without one.</summary>
        public string? State => _layers.Count > 0 ? _layers[0].Graph?.CurrentState : null;

        /// <summary>Whether it's playing anything.</summary>
        public bool IsPlaying => _layers.Count > 0;

        /// <summary>The clip played last with <see cref="Play(AnimationClip, float)"/> (or <see cref="Clip"/>), while it's still playing.</summary>
        public AnimationClip? Playing => _layers.LastOrDefault(l => l.Clip != null)?.Clip;

        /// <summary>How far into <see cref="Playing"/> it is, in seconds. Set it to jump.</summary>
        [HideInInspector]
        [Serialization.DontSave]
        public float Time
        {
            get => _layers.LastOrDefault(l => l.Clip != null) is { } layer ? layer.Clip!.Wrap(layer.Time) : 0f;
            set
            {
                if (_layers.LastOrDefault(l => l.Clip != null) is { } layer) layer.Time = value;
            }
        }

        protected internal override void Start()
        {
            if (PlayOnStart) Play();
        }

        /// <summary>Plays its <see cref="AnimationGraph"/>, or if it has none, its <see cref="Clip"/>, from the start.</summary>
        public void Play()
        {
            _layers.Clear();
            if (AnimationGraph != null) _layers.Add(new Layer { Graph = new AnimationGraphPlayer(AnimationGraph, this), Weight = 1f });
            else if (Clip != null) _layers.Add(new Layer { Clip = Clip, Weight = 1f });
        }

        /// <summary>
        /// Plays <paramref name="clip"/> from the start, blending into it over <paramref name="fade"/> seconds from whatever's
        /// playing. Over an animation graph, a clip that doesn't loop blends back into the graph when it ends; one that loops
        /// plays until <see cref="Stop"/>.
        /// </summary>
        public void Play(AnimationClip clip, float fade = 0.2f)
        {
            if (_layers.Count == 0 && AnimationGraph != null) _layers.Add(new Layer { Graph = new AnimationGraphPlayer(AnimationGraph, this), Weight = 1f });
            var layer = new Layer { Clip = clip, Weight = fade > 0f && _layers.Count > 0 ? 0f : 1f, FadeRate = fade > 0f ? 1f / fade : 0f };
            _layers.Add(layer);
        }

        /// <summary>Stops the clips played with <see cref="Play(AnimationClip, float)"/>: the graph carries on, if there is one. Everything stays where it was.</summary>
        public void Stop()
        {
            _layers.RemoveAll(l => l.Clip != null);
        }

        // ---------------------------------------------------------------- Parameters

        /// <summary>
        /// Sets one of the animation graph's parameters: <c>animator.Set("Speed", 4f)</c>, <c>animator.Set("Crouching", true)</c>.
        /// The graph blends to match from this frame on.
        /// </summary>
        public void Set(string parameter, object? value)
        {
            FillParameters();
            _parameters[parameter] = AnimationGraph?.Variable(parameter) is { } variable && variable.Type != AnimationNodes.Trigger ? variable.Type.Convert(value) : value;
        }

        /// <summary>Sets a Trigger parameter for one frame: what starts a Play Once node (a jump, an attack).</summary>
        public void SetTrigger(string parameter)
        {
            FillParameters();
            _parameters[parameter] = true;
            if (!_triggers.Contains(parameter)) _triggers.Add(parameter);
        }

        /// <summary>One of the animation graph's parameters as it is now: as set, or this object's own starting value, or the graph's.</summary>
        public object? Get(string parameter)
        {
            FillParameters();
            return _parameters.GetValueOrDefault(parameter);
        }

        private void FillParameters()
        {
            if (AnimationGraph == _parametersFrom) return;
            _parametersFrom = AnimationGraph;
            if (AnimationGraph == null) return;

            foreach (GraphVariable variable in AnimationGraph.Variables)
            {
                if (_parameters.ContainsKey(variable.Name)) continue;
                ScriptVariableValue? own = Parameters.FirstOrDefault(p => p.Name == variable.Name);
                _parameters[variable.Name] = variable.Type == AnimationNodes.Trigger ? false : own != null ? own.Get(variable.Type) : variable.Value;
            }
        }

        /// <summary>A parameter for the graph, as a number (a true/false is 1 or 0).</summary>
        internal float NumberParameter(string name)
        {
            FillParameters();
            return _parameters.GetValueOrDefault(name) switch { float f => f, bool b => b ? 1f : 0f, int i => i, double d => (float)d, _ => 0f };
        }

        // ---------------------------------------------------------------- Posing

        /// <summary>
        /// Poses the objects as <paramref name="clip"/> has them <paramref name="time"/> seconds in, without playing it: what the
        /// editor shows as the timeline's scrubbed. What the clip doesn't animate goes back to how it was.
        /// </summary>
        public void Sample(AnimationClip clip, float time)
        {
            AnimationRig rig = Rig;
            _pose ??= rig.NewPose();
            _pose.CopyFrom(rig.Rest);
            rig.Sample(clip, Math.Clamp(time, 0f, Math.Max(clip.Length, 0f)), _pose);
            rig.Apply(_pose);
        }

        /// <summary>Puts everything it has animated back where it was before it first played (or was last <see cref="Rebind"/>-ed).</summary>
        public void ResetPose() => _rig?.ResetPose();

        /// <summary>
        /// Looks the objects under it up again, after some were added, removed or renamed, taking how they are now as how
        /// they rest (what's left where an animation doesn't move something).
        /// </summary>
        public void Rebind()
        {
            _rig = null;
            _motionRoot = null;
            _pose = _layerPose = null;
            foreach (Layer layer in _layers) layer.Graph?.Rebind();
        }

        /// <summary>Every playing animator in <paramref name="scene"/> moves on by <paramref name="deltaTime"/> seconds: after the scene's Update.</summary>
        internal static void AnimateAll(Scene scene, float deltaTime)
        {
            foreach (Animator animator in scene.FindComponents<Animator>())
            {
                if (animator.Started) animator.Animate(deltaTime);
            }

            // Then IK bends what they posed: a hand to a handle, a head to look, feet onto the ground.
            InverseKinematics.SolveAll(scene);
        }

        /// <summary>Moves what's playing on by <paramref name="deltaTime"/> seconds and poses the objects.</summary>
        internal void Animate(float deltaTime)
        {
            if (_layers.Count == 0) return;
            float step = deltaTime * Speed;
            AnimationRig rig = Rig;
            _motionRoot = null;

            // Fades, and clips over a graph that are ending.
            for (int i = 0; i < _layers.Count; i++)
            {
                Layer layer = _layers[i];
                if (layer.Clip is { } clip && i > 0 && !clip.Loop && layer.Target > 0f)
                {
                    float fade = layer.FadeRate > 0f ? 1f / layer.FadeRate : 0f;
                    if (layer.Time + step >= clip.Length - fade && _layers[0].Graph != null)
                    {
                        layer.Target = 0f;
                        if (layer.FadeRate <= 0f) layer.Weight = 0f;
                    }
                }

                if (layer.Weight != layer.Target)
                {
                    float change = layer.FadeRate > 0f ? layer.FadeRate * deltaTime : 1f;
                    layer.Weight = layer.Target > layer.Weight ? MathF.Min(layer.Target, layer.Weight + change) : MathF.Max(layer.Target, layer.Weight - change);
                }
            }

            // A layer faded out goes, and so does everything under one that's fully in (except the graph, which carries on
            // from where it is when what's over it ends).
            _layers.RemoveAll(l => l.Weight <= 0f && l.Target <= 0f);
            int top = _layers.FindLastIndex(l => l.Weight >= 1f);
            for (int i = top - 1; i >= 0; i--)
            {
                if (_layers[i].Clip != null) _layers.RemoveAt(i);
            }

            top = Math.Max(0, _layers.FindLastIndex(l => l.Weight >= 1f));
            if (_layers.Count == 0) return;

            _pose ??= rig.NewPose();
            _layerPose ??= rig.NewPose();
            _pose.CopyFrom(rig.Rest);

            for (int i = top; i < _layers.Count; i++)
            {
                Layer layer = _layers[i];
                Pose target = i == top ? _pose : _layerPose;
                if (i != top) target.CopyFrom(rig.Rest);

                if (layer.Graph != null)
                {
                    layer.Graph.Evaluate(step, target, layer.Weight);
                }
                else if (layer.Clip is { } clip)
                {
                    float before = layer.Time;
                    layer.Time += step;
                    rig.Sample(clip, clip.Wrap(layer.Time), target);
                    Moved(clip, before, layer.Time, target);
                    if (layer.Weight >= 0.5f) Passed(clip, before, layer.Time);
                }

                if (i != top) _pose.Blend(_layerPose, layer.Weight);
            }

            if (RootMotion) MoveByRoot(_pose, deltaTime);
            rig.Apply(_pose);

            foreach (string trigger in _triggers) _parameters[trigger] = false;
            _triggers.Clear();
        }

        /// <summary>
        /// Root motion: moves and turns the object by what the animations moved their root this frame, and keeps the root in
        /// place in the pose. A Character Controller or Rigidbody on the object is given the movement as its velocity along
        /// the ground instead (its fall kept), so it still collides.
        /// </summary>
        private void MoveByRoot(Pose pose, float deltaTime)
        {
            int root = MotionRoot;
            RootMotionVelocity = default;
            RootMotionTurn = Quaternion.Identity;
            if (root < 0) return;

            Transform transform = Transform;
            // The movement is in the character's own space: its own object's moves along the way it faces now; a bone's
            // through the object, scaled with it.
            Vector3 local = root == 0
                ? Vector3.Transform(pose.Move, transform.Rotation)
                : Vector3.Transform(pose.Move * transform.Scale, transform.Rotation);
            Vector3 world = transform.Parent is { } parent ? Vector3.TransformNormal(local, parent.WorldMatrix) : local;
            RootMotionVelocity = deltaTime > 0f ? world / deltaTime : default;
            RootMotionTurn = pose.Turn;

            Vector3 velocity = RootMotionVelocity;
            if (GetComponent<Physics.CharacterController>() is { } controller)
            {
                controller.Velocity = new Vector3(velocity.X, controller.Velocity.Y, velocity.Z);
            }
            else if (GetComponent<Physics.Rigidbody>() is { IsKinematic: false } body)
            {
                body.Velocity = new Vector3(velocity.X, body.Velocity.Y, velocity.Z);
            }
            else
            {
                transform.Position += local;
            }

            transform.Rotation = Quaternion.Normalize(transform.Rotation * pose.Turn);

            if (root == 0)
            {
                // The object's own tracks are its movement now, not where it's put.
                pose.Positions[0] = transform.Position;
                pose.Rotations[0] = transform.Rotation;
            }
            else
            {
                Rig.KeepInPlace(root, pose);
            }
        }

        /// <summary>Sends the events <paramref name="clip"/> passed going from <paramref name="from"/> to <paramref name="to"/> seconds of playing.</summary>
        internal void Passed(AnimationClip clip, float from, float to)
        {
            if (clip.Events.Count == 0 || to <= from || clip.Length <= 0f) return;
            foreach (AnimationEvent animationEvent in clip.Events)
            {
                if (!Crossed(clip, animationEvent.Time, from, to)) continue;
                EventReached?.Invoke(animationEvent);
                VisualScript.SendEvent(animationEvent.Name, clip.Name, GameObject);
            }
        }

        private static bool Crossed(AnimationClip clip, float at, float from, float to)
        {
            if (!clip.Loop) return at > from && at <= to || (from == 0f && at == 0f);
            float length = clip.Length;
            // Each time round the loop from the one 'from' is in, until 'to'.
            for (float start = MathF.Floor(from / length) * length; start <= to; start += length)
            {
                float t = start + at;
                if (t > from && t <= to) return true;
                if (from == 0f && t == 0f) return true;
            }

            return false;
        }
    }
}
