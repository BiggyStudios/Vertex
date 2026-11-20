using System.Numerics;
using System.Reflection;

using Vertex.Animation;
using Vertex.Audio;
using Vertex.Graphs;
using Vertex.Physics;
using Vertex.Scripting;

namespace Vertex.AI
{
    /// <summary>
    /// What one node of a behaviour tree does, on one object: it's started when it's first run, run each frame until it
    /// says it's done (succeeded or failed), and stopped then, or when something above it stops it.
    /// </summary>
    internal abstract class BehaviorTask
    {
        public GraphNode Node = null!;
        public BehaviorTreeInstance Tree = null!;
        public BehaviorTask[] Children = [];

        public bool IsRunning { get; private set; }

        /// <summary>Whether it only checks something, straight away: what Recheck checks again.</summary>
        public virtual bool IsCondition => false;

        /// <summary>Runs it for this frame: started first if it wasn't running, and stopped if it's done.</summary>
        public BehaviorStatus Run()
        {
            Tree.Graph.Ran(Node.Id);
            try
            {
                if (!IsRunning)
                {
                    IsRunning = true;
                    Begin();
                }

                BehaviorStatus status = Tick();
                if (status != BehaviorStatus.Running)
                {
                    IsRunning = false;
                    End();
                }

                return status;
            }
            catch (BehaviorException)
            {
                IsRunning = false;
                throw;
            }
            catch (Exception exception)
            {
                IsRunning = false;
                throw new BehaviorException(Node, $"{Title}: {exception.Message}", exception);
            }
        }

        /// <summary>Stops it, and whatever's running under it, before it's done.</summary>
        public void Abort()
        {
            if (!IsRunning) return;
            foreach (BehaviorTask child in Children) child.Abort();
            IsRunning = false;
            End();
        }

        protected virtual void Begin() {}
        protected abstract BehaviorStatus Tick();
        protected virtual void End() {}

        protected string Title => BehaviorNodes.Library.Find(Node.Type)?.Title ?? Node.Type;
        protected GameObject Self => Tree.Self;
        protected float Number(string port) => Tree.Input(Node, port) is float f ? f : 0f;
        protected bool Boolean(string port) => Tree.Input(Node, port) is true;
        protected object? Value(string port) => Tree.Input(Node, port);
        protected string Text(string port) => BehaviorTreeInstance.Text(Node, port);
        protected bool Target(string port, out Vector3 position, out GameObject? target) => Tree.Target(Text(port), out position, out target);
        protected BehaviorException Problem(string message) => new(Node, $"{Title}: {message}");

        protected static BehaviorStatus Result(bool success) => success ? BehaviorStatus.Success : BehaviorStatus.Failure;

        /// <summary>The task for a kind of node.</summary>
        public static BehaviorTask For(string type) => type switch
        {
            BehaviorNodes.Root => new RootTask(),
            BehaviorNodes.Sequence => new SequenceTask(),
            BehaviorNodes.Selector => new SelectorTask(),
            BehaviorNodes.Parallel => new ParallelTask(),
            BehaviorNodes.RandomChild => new RandomTask(),
            BehaviorNodes.Invert => new InvertTask(),
            BehaviorNodes.AlwaysSucceed => new AlwaysSucceedTask(),
            BehaviorNodes.Repeat => new RepeatTask(),
            BehaviorNodes.Retry => new RetryTask(),
            BehaviorNodes.Cooldown => new CooldownTask(),
            BehaviorNodes.TimeLimit => new TimeLimitTask(),
            BehaviorNodes.If => new IfTask(),
            BehaviorNodes.Check => new CheckTask(),
            BehaviorNodes.CanSee => new CanSeeTask(),
            BehaviorNodes.IsNear => new IsNearTask(),
            BehaviorNodes.IsSet => new IsSetTask(),
            BehaviorNodes.Chance => new ChanceTask(),
            BehaviorNodes.MoveTo => new MoveToTask(),
            BehaviorNodes.Wander => new WanderTask(),
            BehaviorNodes.Patrol => new PatrolTask(),
            BehaviorNodes.Flee => new FleeTask(),
            BehaviorNodes.LookAt => new LookAtTask(),
            BehaviorNodes.StopMoving => new StopTask(),
            BehaviorNodes.Wait => new WaitTask(),
            BehaviorNodes.SetVariable => new SetVariableTask(),
            BehaviorNodes.PlayAnimation => new PlayAnimationTask(),
            BehaviorNodes.SetAnimationParameter => new SetAnimationParameterTask(),
            BehaviorNodes.PlaySound => new PlaySoundTask(),
            BehaviorNodes.SendEvent => new SendEventTask(),
            BehaviorNodes.CallMethod => new CallMethodTask(),
            BehaviorNodes.LogText => new LogTask(),
            // A value node that's been linked as a task does nothing.
            _ => new NothingTask()
        };
    }

    internal sealed class NothingTask : BehaviorTask
    {
        protected override BehaviorStatus Tick() => BehaviorStatus.Failure;
    }

    internal sealed class RootTask : BehaviorTask
    {
        protected override BehaviorStatus Tick() => Children.Length > 0 ? Children[0].Run() : BehaviorStatus.Failure;
    }

    // ---------------------------------------------------------------- Composites

    internal sealed class SequenceTask : BehaviorTask
    {
        private int _at;

        private Recheck Recheck => Value("Recheck") is Recheck recheck ? recheck : Recheck.Never;

        protected override void Begin() => _at = 0;

        protected override BehaviorStatus Tick()
        {
            // Conditions already passed, checked again: one that's no longer true stops it.
            if (Recheck is Recheck.WhileRunning or Recheck.Both)
            {
                for (int i = 0; i < _at && i < Children.Length; i++)
                {
                    if (!Children[i].IsCondition || Children[i].Run() == BehaviorStatus.Success) continue;
                    if (_at < Children.Length) Children[_at].Abort();
                    return BehaviorStatus.Failure;
                }
            }

            while (_at < Children.Length)
            {
                BehaviorStatus status = Children[_at].Run();
                if (status != BehaviorStatus.Success) return status;
                _at++;
            }

            return BehaviorStatus.Success;
        }

        /// <summary>
        /// Whether it would take over from what's after it under a Selector, if it's set to: the conditions it starts with
        /// are all true.
        /// </summary>
        public bool Interrupts()
        {
            if (Recheck is not (Recheck.ToInterrupt or Recheck.Both)) return false;
            int checkedCount = 0;
            foreach (BehaviorTask child in Children)
            {
                if (!child.IsCondition) break;
                if (child.Run() != BehaviorStatus.Success) return false;
                checkedCount++;
            }

            return checkedCount > 0;
        }
    }

    internal sealed class SelectorTask : BehaviorTask
    {
        private int _at;

        protected override void Begin() => _at = 0;

        protected override BehaviorStatus Tick()
        {
            // Something before what's running, that's set to, takes over when its conditions say.
            for (int i = 0; i < _at && i < Children.Length; i++)
            {
                if (!Interrupts(Children[i])) continue;
                if (_at < Children.Length) Children[_at].Abort();
                _at = i;
                break;
            }

            while (_at < Children.Length)
            {
                BehaviorStatus status = Children[_at].Run();
                if (status != BehaviorStatus.Failure) return status;
                _at++;
            }

            return BehaviorStatus.Failure;
        }

        private static bool Interrupts(BehaviorTask task) => task switch
        {
            SequenceTask sequence => sequence.Interrupts(),
            IfTask condition => condition.Interrupts(),
            _ => false
        };
    }

    internal sealed class ParallelTask : BehaviorTask
    {
        private BehaviorStatus[] _done = [];

        protected override void Begin()
        {
            _done = new BehaviorStatus[Children.Length];
            Array.Fill(_done, BehaviorStatus.Running);
        }

        protected override BehaviorStatus Tick()
        {
            bool any = Value("Succeeds When") is ParallelPolicy.AnySucceeds;
            int succeeded = 0, failed = 0;
            for (int i = 0; i < Children.Length; i++)
            {
                if (_done[i] == BehaviorStatus.Running) _done[i] = Children[i].Run();
                if (_done[i] == BehaviorStatus.Success) succeeded++;
                if (_done[i] == BehaviorStatus.Failure) failed++;
            }

            BehaviorStatus result = any
                ? succeeded > 0 ? BehaviorStatus.Success : failed == Children.Length ? BehaviorStatus.Failure : BehaviorStatus.Running
                : failed > 0 ? BehaviorStatus.Failure : succeeded == Children.Length ? BehaviorStatus.Success : BehaviorStatus.Running;
            if (result != BehaviorStatus.Running)
            {
                foreach (BehaviorTask child in Children) child.Abort();
            }

            return result;
        }
    }

    internal sealed class RandomTask : BehaviorTask
    {
        private int _pick;

        protected override void Begin() => _pick = Children.Length > 0 ? Tree.Random.Next(Children.Length) : -1;

        protected override BehaviorStatus Tick() => _pick >= 0 ? Children[_pick].Run() : BehaviorStatus.Failure;
    }

    // ---------------------------------------------------------------- Decorators

    internal abstract class DecoratorTask : BehaviorTask
    {
        protected BehaviorTask? Child => Children.Length > 0 ? Children[0] : null;
    }

    internal sealed class InvertTask : DecoratorTask
    {
        public override bool IsCondition => Child?.IsCondition == true;

        protected override BehaviorStatus Tick() => Child?.Run() switch
        {
            BehaviorStatus.Running => BehaviorStatus.Running,
            BehaviorStatus.Success => BehaviorStatus.Failure,
            _ => BehaviorStatus.Success
        };
    }

    internal sealed class AlwaysSucceedTask : DecoratorTask
    {
        protected override BehaviorStatus Tick() => Child?.Run() == BehaviorStatus.Running ? BehaviorStatus.Running : BehaviorStatus.Success;
    }

    internal sealed class RepeatTask : DecoratorTask
    {
        private int _count;

        protected override void Begin() => _count = 0;

        protected override BehaviorStatus Tick()
        {
            if (Child is not { } child) return BehaviorStatus.Failure;
            BehaviorStatus status = child.Run();
            if (status != BehaviorStatus.Success) return status;

            // Again next frame, so something that succeeds straight away doesn't go round for ever in one.
            _count++;
            int times = (int)MathF.Round(Number("Times"));
            return times > 0 && _count >= times ? BehaviorStatus.Success : BehaviorStatus.Running;
        }
    }

    internal sealed class RetryTask : DecoratorTask
    {
        private int _tries;

        protected override void Begin() => _tries = 0;

        protected override BehaviorStatus Tick()
        {
            if (Child is not { } child) return BehaviorStatus.Failure;
            BehaviorStatus status = child.Run();
            if (status != BehaviorStatus.Failure) return status;
            _tries++;
            return _tries >= Math.Max(1, (int)MathF.Round(Number("Times"))) ? BehaviorStatus.Failure : BehaviorStatus.Running;
        }
    }

    internal sealed class CooldownTask : DecoratorTask
    {
        private float _readyAt = float.MinValue;

        protected override BehaviorStatus Tick()
        {
            if (Child is not { } child) return BehaviorStatus.Failure;
            if (!child.IsRunning && Tree.Time < _readyAt) return BehaviorStatus.Failure;
            BehaviorStatus status = child.Run();
            if (status != BehaviorStatus.Running) _readyAt = Tree.Time + Number("Seconds");
            return status;
        }
    }

    internal sealed class TimeLimitTask : DecoratorTask
    {
        private float _until;

        protected override void Begin() => _until = Tree.Time + Number("Seconds");

        protected override BehaviorStatus Tick()
        {
            if (Child is not { } child) return BehaviorStatus.Failure;
            if (Tree.Time >= _until)
            {
                child.Abort();
                return BehaviorStatus.Failure;
            }

            return child.Run();
        }
    }

    internal sealed class IfTask : DecoratorTask
    {
        private Recheck Recheck => Value("Recheck") is Recheck recheck ? recheck : Recheck.WhileRunning;

        public bool Interrupts()
        {
            Tree.Graph.Ran(Node.Id);
            return Recheck is Recheck.ToInterrupt or Recheck.Both && Boolean("Condition");
        }

        protected override BehaviorStatus Tick()
        {
            if (Child is not { } child) return BehaviorStatus.Failure;
            bool condition = Boolean("Condition");
            if (!child.IsRunning && !condition) return BehaviorStatus.Failure;
            if (child.IsRunning && !condition && Recheck is Recheck.WhileRunning or Recheck.Both)
            {
                child.Abort();
                return BehaviorStatus.Failure;
            }

            return child.Run();
        }
    }

    // ---------------------------------------------------------------- Conditions

    internal abstract class ConditionTask : BehaviorTask
    {
        public override bool IsCondition => true;
    }

    internal sealed class CheckTask : ConditionTask
    {
        protected override BehaviorStatus Tick() => Result(Boolean("Condition"));
    }

    internal sealed class CanSeeTask : ConditionTask
    {
        protected override BehaviorStatus Tick()
        {
            if (!Target("Target", out Vector3 position, out GameObject? target)) return BehaviorStatus.Failure;

            // From its eyes to the middle of the target, near enough and in front.
            float eyes = Self.GetComponent<NavMeshAgent>() is { NavMesh: { } navMesh } ? navMesh.AgentHeight * 0.85f : 1.5f;
            Vector3 from = Self.Transform.WorldPosition + Vector3.UnitY * eyes;
            Vector3 to = position + Vector3.UnitY * (target != null ? TargetMiddle(target) : 0.5f);
            Vector3 way = to - from;
            float distance = way.Length();
            if (distance > Number("Distance")) return BehaviorStatus.Failure;
            if (distance < 1e-4f) return BehaviorStatus.Success;

            float angle = Number("Angle");
            if (angle < 360f)
            {
                Vector3 forward = Self.Transform.Forward, flatWay = new(way.X, 0f, way.Z);
                forward.Y = 0f;
                if (forward.LengthSquared() > 1e-6f && flatWay.LengthSquared() > 1e-6f)
                {
                    float cos = Vector3.Dot(Vector3.Normalize(forward), Vector3.Normalize(flatWay));
                    if (cos < MathF.Cos(angle * 0.5f * MathF.PI / 180f)) return BehaviorStatus.Failure;
                }
            }

            // Nothing in the way but the target. Its own body (its colliders, which the eyes may be inside) is looked
            // past, one at a time.
            Vector3 direction = way / distance;
            Collider? past = Self.GetComponent<Collider>();
            for (int look = 0; look < 6; look++)
            {
                if (!Self.Scene.Physics.Raycast(from, direction, distance, out RaycastHit hit, past)) return BehaviorStatus.Success;
                if (target != null && IsUnder(hit.GameObject, target)) return BehaviorStatus.Success;
                if (!IsUnder(hit.GameObject, Self)) return BehaviorStatus.Failure;

                past = hit.Collider;
                float on = MathF.Max(hit.Distance, 0f);
                from += direction * on;
                distance -= on;
            }

            return BehaviorStatus.Failure;
        }

        private static float TargetMiddle(GameObject target) => target.GetComponent<CharacterController>() is { } controller ? controller.Height * 0.5f : 1f;

        private static bool IsUnder(GameObject gameObject, GameObject ancestor)
        {
            for (Transform? at = gameObject.Transform; at != null; at = at.Parent)
            {
                if (at.GameObject == ancestor) return true;
            }

            return false;
        }
    }

    internal sealed class IsNearTask : ConditionTask
    {
        protected override BehaviorStatus Tick() =>
            Result(Target("Target", out Vector3 position, out _) && Vector3.Distance(Self.Transform.WorldPosition, position) <= Number("Distance"));
    }

    internal sealed class IsSetTask : ConditionTask
    {
        protected override BehaviorStatus Tick() => Result(Tree.Variables.GetValueOrDefault(Text("Variable")) switch
        {
            GameObject gameObject => !gameObject.IsDestroyed,
            bool b => b,
            float f => f != 0f,
            string s => s.Length > 0,
            Vector3 v => v != Vector3.Zero,
            null => false,
            _ => true
        });
    }

    internal sealed class ChanceTask : ConditionTask
    {
        protected override BehaviorStatus Tick() => Result(Tree.Random.NextSingle() < Number("Chance"));
    }

    // ---------------------------------------------------------------- Movement

    /// <summary>A task that walks with the object's Nav Mesh Agent, at its own speed if it has one, which goes back after.</summary>
    internal abstract class MovementTask : BehaviorTask
    {
        private float? _speedBefore, _stopBefore;
        protected NavMeshAgent Agent = null!;

        /// <summary>Whether it's been sent somewhere since it started: what it says when it stops walking.</summary>
        protected bool Sent;

        protected override void Begin()
        {
            Agent = Self.GetComponent<NavMeshAgent>() ?? throw Problem($"{Self.Name} needs a Nav Mesh Agent to move");
            Sent = false;
            float speed = Number("Speed");
            if (speed > 0f)
            {
                _speedBefore = Agent.Speed;
                Agent.Speed = speed;
            }
        }

        /// <summary>Stops within <paramref name="distance"/> of where it's going, for as long as it runs.</summary>
        protected void StopWithin(float distance)
        {
            _stopBefore ??= Agent.StoppingDistance;
            Agent.StoppingDistance = MathF.Max(distance * 0.8f, 0.05f);
        }

        protected override void End()
        {
            Agent.Stop();
            if (_speedBefore is float speed) Agent.Speed = speed;
            if (_stopBefore is float stop) Agent.StoppingDistance = stop;
            _speedBefore = _stopBefore = null;
        }

        /// <summary>Sends it to <paramref name="point"/>.</summary>
        protected void Send(Vector3 point) => Sent = Agent.SetDestination(point);

        /// <summary>Running while it walks to where it was sent; success once it's there, failure if it wasn't sent anywhere.</summary>
        protected BehaviorStatus Walking() => !Sent ? BehaviorStatus.Failure : Agent.HasPath ? BehaviorStatus.Running : Result(Agent.Arrived);
    }

    internal sealed class MoveToTask : MovementTask
    {
        private Vector3 _sentTo;
        private float _sentAt;


        protected override void Begin()
        {
            base.Begin();
            _sentAt = float.MinValue;
        }

        protected override BehaviorStatus Tick()
        {
            if (!Target("Target", out Vector3 position, out GameObject? target)) return BehaviorStatus.Failure;
            float within = MathF.Max(Number("Stop Within"), 0.05f);
            StopWithin(within);

            Vector3 at = Self.Transform.WorldPosition;
            if (Flat(position - at).Length() <= within && MathF.Abs(position.Y - at.Y) < (Agent.NavMesh?.AgentHeight ?? 2f) + within)
            {
                Agent.Stop();
                return BehaviorStatus.Success;
            }

            // Got to the end of the way without being there: it can't get nearer, unless what it's following has moved.
            bool sent = _sentAt != float.MinValue;
            float movedBy = Vector3.Distance(position, _sentTo);
            if (sent && !Agent.HasPath && (target == null || movedBy < 0.05f)) return BehaviorStatus.Failure;

            // Sent again when an object it's following has moved on.
            bool moved = sent && target != null && movedBy > MathF.Max(0.3f, within * 0.5f) && Tree.Time - _sentAt > 0.2f;
            if (!sent || moved || !Agent.HasPath)
            {
                if (!Agent.SetDestination(position)) return BehaviorStatus.Failure;
                _sentTo = position;
                _sentAt = Tree.Time;
            }

            return BehaviorStatus.Running;
        }

        private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);
    }

    internal sealed class WanderTask : MovementTask
    {
        protected override void Begin()
        {
            base.Begin();
            if (NavMesh.For(Self.Scene).RandomPoint(Tree.Home, MathF.Max(Number("Radius"), 0.5f), out Vector3 point, Tree.Random)) Send(point);
        }

        protected override BehaviorStatus Tick() => Walking();
    }

    internal sealed class PatrolTask : MovementTask
    {
        private int _next;

        protected override void Begin()
        {
            base.Begin();
            if (!Target("Waypoints", out Vector3 position, out GameObject? waypoints)) return;
            Vector3 point = position;
            if (waypoints != null && waypoints.Transform.Children.Count > 0)
            {
                _next %= waypoints.Transform.Children.Count;
                point = waypoints.Transform.Children[_next].WorldPosition;
            }

            Send(point);
            _next++;
        }

        protected override BehaviorStatus Tick() => Walking();
    }

    internal sealed class FleeTask : MovementTask
    {
        protected override void Begin()
        {
            base.Begin();
            if (!Target("From", out Vector3 from, out _)) return;
            NavMesh navMesh = NavMesh.For(Self.Scene);

            Vector3 at = Self.Transform.WorldPosition;
            Vector3 away = new(at.X - from.X, 0f, at.Z - from.Z);
            away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Self.Transform.Forward;
            float distance = MathF.Max(Number("Distance"), 1f);

            // Straight away if it can, otherwise as near that way as it can get.
            foreach (float turn in (ReadOnlySpan<float>)[0f, 30f, -30f, 60f, -60f, 90f, -90f, 135f, -135f])
            {
                float radians = turn * MathF.PI / 180f;
                var way = new Vector3(away.X * MathF.Cos(radians) - away.Z * MathF.Sin(radians), 0f, away.X * MathF.Sin(radians) + away.Z * MathF.Cos(radians));
                if (!navMesh.SamplePosition(at + way * distance, distance * 0.5f, out Vector3 point) || !navMesh.Connected(at, point)) continue;
                if (Vector3.Distance(point, from) <= Vector3.Distance(at, from)) continue;
                Send(point);
                return;
            }
        }

        protected override BehaviorStatus Tick() => Walking();
    }

    internal sealed class LookAtTask : BehaviorTask
    {
        protected override BehaviorStatus Tick()
        {
            if (!Target("Target", out Vector3 position, out _)) return BehaviorStatus.Failure;
            Vector3 way = position - Self.Transform.WorldPosition;
            way.Y = 0f;
            if (way.LengthSquared() < 1e-6f) return BehaviorStatus.Success;

            Quaternion target = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(-way.X, -way.Z), 0f, 0f);
            Quaternion now = Self.Transform.WorldRotation;
            float angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(now, target)), 0f, 1f)) * 180f / MathF.PI;
            float step = MathF.Max(Number("Turn Speed"), 1f) * Time.DeltaTime;
            Self.Transform.WorldRotation = angle <= step ? target : Quaternion.Slerp(now, target, step / angle);
            return angle <= MathF.Max(step, 2f) ? BehaviorStatus.Success : BehaviorStatus.Running;
        }
    }

    internal sealed class StopTask : BehaviorTask
    {
        protected override BehaviorStatus Tick()
        {
            Self.GetComponent<NavMeshAgent>()?.Stop();
            return BehaviorStatus.Success;
        }
    }

    // ---------------------------------------------------------------- Actions

    internal sealed class WaitTask : BehaviorTask
    {
        private float _until;

        protected override void Begin() => _until = Tree.Time + MathF.Max(Number("Seconds"), 0f) + MathF.Max(Number("Random"), 0f) * Tree.Random.NextSingle();

        protected override BehaviorStatus Tick() => Tree.Time >= _until ? BehaviorStatus.Success : BehaviorStatus.Running;
    }

    internal sealed class SetVariableTask : BehaviorTask
    {
        protected override BehaviorStatus Tick()
        {
            string name = Text("Variable");
            if (Tree.Graph.Variable(name) is not { } variable) throw Problem(name.Length == 0 ? "No variable's chosen" : $"There's no variable called {name}");
            Tree.Variables[name] = variable.Type.Convert(Value("Value"));
            return BehaviorStatus.Success;
        }
    }

    internal sealed class PlayAnimationTask : BehaviorTask
    {
        private float _until;
        private bool _wait;

        protected override void Begin()
        {
            Animator animator = FindAnimator(Self) ?? throw Problem($"{Self.Name} has no Animator to play it");
            if (Value("Animation") is not AnimationClip clip) throw Problem("No animation's chosen");
            animator.Play(clip, MathF.Max(Number("Fade"), 0f));
            _wait = Boolean("Wait Until Done");
            _until = Tree.Time + clip.Length / MathF.Max(MathF.Abs(animator.Speed), 0.01f);
        }

        protected override BehaviorStatus Tick() => !_wait || Tree.Time >= _until ? BehaviorStatus.Success : BehaviorStatus.Running;

        internal static Animator? FindAnimator(GameObject gameObject)
        {
            if (gameObject.GetComponent<Animator>() is { } own) return own;
            foreach (Transform child in gameObject.Transform.Children)
            {
                if (FindAnimator(child.GameObject) is { } found) return found;
            }

            return null;
        }
    }

    internal sealed class SetAnimationParameterTask : BehaviorTask
    {
        protected override BehaviorStatus Tick()
        {
            Animator animator = PlayAnimationTask.FindAnimator(Self) ?? throw Problem($"{Self.Name} has no Animator");
            animator.Set(Text("Parameter"), Number("Value"));
            return BehaviorStatus.Success;
        }
    }

    internal sealed class PlaySoundTask : BehaviorTask
    {
        protected override BehaviorStatus Tick()
        {
            if (Value("Sound") is not AudioClip clip) throw Problem("No sound's chosen");
            Sound.PlayAt(clip, Self.Transform.WorldPosition, Number("Volume"));
            return BehaviorStatus.Success;
        }
    }

    internal sealed class SendEventTask : BehaviorTask
    {
        protected override BehaviorStatus Tick()
        {
            VisualScript.SendEvent(Text("Event"), Value("Value") ?? Self);
            return BehaviorStatus.Success;
        }
    }

    internal sealed class CallMethodTask : BehaviorTask
    {
        private (Component Component, MethodInfo Method)? _found;
        private string? _foundFor;

        protected override BehaviorStatus Tick()
        {
            string name = Text("Method");
            if (_foundFor != name || _found is not { } found || found.Component.GameObject != Self)
            {
                _found = Find(name) ?? throw Problem(name.Length == 0 ? "No method's named" : $"None of {Self.Name}'s components has a method {name}() that takes nothing");
                _foundFor = name;
                found = _found.Value;
            }

            return found.Method.Invoke(found.Component, null) switch
            {
                BehaviorStatus status => status,
                bool success => Result(success),
                _ => BehaviorStatus.Success
            };
        }

        private (Component, MethodInfo)? Find(string name)
        {
            foreach (Component component in Self.Components)
            {
                MethodInfo? method = component.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null) return (component, method);
            }

            return null;
        }
    }

    internal sealed class LogTask : BehaviorTask
    {
        protected override BehaviorStatus Tick()
        {
            Log.Info(Value("Text") as string ?? "");
            return BehaviorStatus.Success;
        }
    }
}
