using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Networking;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Votes to kick someone out of the room, called from the pause menu's Players page or the votekick console
    /// command. Everyone but the player in question gets a say, F1 for yes and F2 for no, and whoever called it votes
    /// yes. If at least <see cref="Threshold"/> of them vote yes, they're kicked: kept out of the room for
    /// <see cref="Network.FirstKickBan"/> the first time, and for good the second. The host counts the votes, so the host
    /// can't be voted out: it's their room. One vote at a time, for 30 seconds.
    /// </summary>
    public class VoteKick : Component
    {
        /// <summary>The share of the other players who have to vote yes.</summary>
        public const float Threshold = 0.7f;

        private const float _duration = 30f;
        private const float _resultTime = 4f;

        // After a vote they called fails, how long before someone can call another.
        private const float _callCooldown = 30f;

        private const float _width = 300f;

        private static readonly Color _panel = new(0.05f, 0.06f, 0.08f, 0.82f);
        private static readonly Color _dim = new(1f, 1f, 1f, 0.6f);
        private static readonly Color _yesColor = new(0.35f, 0.85f, 0.45f);
        private static readonly Color _noColor = new(1f, 0.45f, 0.4f);

        public enum Outcome : byte { Running, Passed, Failed, Left }

        /// <summary>A vote as the host last described it.</summary>
        public readonly record struct State(PeerId Target, PeerId Caller, int Yes, int No, int Needed, float SecondsLeft, Outcome Outcome);

        internal MultiplayerManager Multiplayer { get; set; } = null!;

        /// <summary>The vote going on, or just ended, as last heard from the host. Null when there isn't one.</summary>
        public State? Current { get; private set; }

        public bool Running => Current is { Outcome: Outcome.Running };

        // When the current state arrived, and how this machine voted in it.
        private float _stateTime;
        private bool? _myVote;

        // The host only: the vote it's counting.
        private PeerId? _target;
        private PeerId _caller;
        private float _endsAt;
        private readonly Dictionary<PeerId, bool> _votes = new();
        private readonly Dictionary<PeerId, float> _canCallAgainAt = new();

        protected override void Start()
        {
            Network.PeerLeft += OnPeerLeft;
            Network.SessionEnded += OnSessionEnded;
        }

        protected override void OnDestroy()
        {
            Network.PeerLeft -= OnPeerLeft;
            Network.SessionEnded -= OnSessionEnded;
        }

        /// <summary>Starts a vote to kick <paramref name="target"/>, or asks the host to. False, saying why, if it can't.</summary>
        public bool Call(PeerId target, out string? problem)
        {
            problem = Problem(Network.LocalPeer, target);
            if (problem != null) return false;

            if (Network.IsHost)
            {
                Begin(Network.LocalPeer, target);
            }
            else
            {
                var call = new MessageWriter(stackalloc byte[16], MessageType.VoteCall);
                call.Write(target.Value);
                Network.Send(Network.HostPeer, call.Data, SendMode.Reliable);
            }

            return true;
        }

        /// <summary>Votes yes or no in the vote going on, once. The player it's about doesn't get a vote.</summary>
        public void Cast(bool yes)
        {
            if (Current is not { Outcome: Outcome.Running } vote || vote.Target == Network.LocalPeer || _myVote != null) return;

            _myVote = yes;
            if (Network.IsHost)
            {
                Count(Network.LocalPeer, yes);
            }
            else
            {
                var cast = new MessageWriter(stackalloc byte[8], MessageType.VoteCast);
                cast.Write(yes);
                Network.Send(Network.HostPeer, cast.Data, SendMode.Reliable);
            }
        }

        /// <summary>Why <paramref name="caller"/> can't start a vote to kick <paramref name="target"/>, or null if they can.</summary>
        private string? Problem(PeerId caller, PeerId target)
        {
            if (!Network.InSession) return "You're not in a room.";
            if (Running) return "There's already a vote going on.";
            if (target == caller) return "You can't vote to kick yourself.";
            if (target == Network.HostPeer) return "The host can't be voted out: it's their room.";
            if (target != Network.LocalPeer && !Network.Peers.Contains(target)) return "They're not in the room.";
            if (_canCallAgainAt.TryGetValue(caller, out float at) && Time.TotalTime < at) return "Wait a little before calling another vote.";
            return null;
        }

        // ---- The host ----

        /// <summary>Someone asked the host for a vote.</summary>
        internal void OnCall(PeerId caller, PeerId target)
        {
            if (Network.IsHost && Problem(caller, target) == null) Begin(caller, target);
        }

        /// <summary>Someone voted, by message to the host.</summary>
        internal void OnCast(PeerId voter, bool yes)
        {
            if (Network.IsHost) Count(voter, yes);
        }

        private void Begin(PeerId caller, PeerId target)
        {
            _target = target;
            _caller = caller;
            _endsAt = Time.TotalTime + _duration;
            _votes.Clear();
            _votes[caller] = true;
            Evaluate();
        }

        private void Count(PeerId voter, bool yes)
        {
            if (_target is not PeerId target || voter == target || _votes.ContainsKey(voter)) return;

            _votes[voter] = yes;
            Evaluate();
        }

        /// <summary>The host adds up the votes, tells everyone, and kicks the player if the vote passed.</summary>
        private void Evaluate()
        {
            if (_target is not PeerId target) return;

            var members = new HashSet<PeerId>(Network.Peers) { Network.LocalPeer };
            members.Remove(target);
            foreach (PeerId gone in _votes.Keys.Where(voter => !members.Contains(voter)).ToList()) _votes.Remove(gone);

            int yes = _votes.Values.Count(v => v);
            int no = _votes.Count - yes;
            int needed = Math.Max(1, (int)MathF.Ceiling(members.Count * Threshold - 0.001f));
            bool targetHere = target == Network.LocalPeer || Network.Peers.Contains(target);

            Outcome outcome = !targetHere ? Outcome.Left
                : yes >= needed ? Outcome.Passed
                : no > members.Count - needed || Time.TotalTime >= _endsAt ? Outcome.Failed
                : Outcome.Running;

            var state = new State(target, _caller, yes, no, needed, MathF.Max(_endsAt - Time.TotalTime, 0f), outcome);
            var message = new MessageWriter(stackalloc byte[32], MessageType.VoteState);
            message.Write(state.Target.Value);
            message.Write(state.Caller.Value);
            message.Write((byte)Math.Min(yes, byte.MaxValue));
            message.Write((byte)Math.Min(no, byte.MaxValue));
            message.Write((byte)Math.Min(needed, byte.MaxValue));
            message.Write((byte)MathF.Ceiling(state.SecondsLeft));
            message.Write((byte)outcome);
            Network.Broadcast(message.Data, SendMode.Reliable);
            Show(state);

            if (outcome == Outcome.Running) return;

            _target = null;
            if (outcome == Outcome.Failed) _canCallAgainAt[_caller] = Time.TotalTime + _callCooldown;
            if (outcome == Outcome.Passed) Network.Kick(target);
        }

        // ---- Everyone ----

        /// <summary>The host's word on the vote.</summary>
        internal void OnState(ref MessageReader message)
        {
            var state = new State(message.ReadPeer(), message.ReadPeer(), message.ReadByte(), message.ReadByte(), message.ReadByte(), message.ReadByte(),
                (Outcome)message.ReadByte());
            if (Enum.IsDefined(state.Outcome)) Show(state);
        }

        private void Show(State state)
        {
            bool isNew = state.Outcome == Outcome.Running && (Current is not { Outcome: Outcome.Running } before || before.Target != state.Target);
            bool ended = state.Outcome != Outcome.Running && Current is { Outcome: Outcome.Running };

            Current = state;
            _stateTime = Time.TotalTime;

            string target = state.Target == Network.LocalPeer ? "you" : Multiplayer.NameOf(state.Target);
            if (isNew)
            {
                _myVote = state.Caller == Network.LocalPeer ? true : null;
                Multiplayer.Chat.AddSystemMessage(state.Target == Network.LocalPeer
                    ? $"{Multiplayer.NameOf(state.Caller)} called a vote to kick you."
                    : $"{Multiplayer.NameOf(state.Caller)} called a vote to kick {target}. F1 yes, F2 no.");
            }
            else if (ended)
            {
                Multiplayer.Chat.AddSystemMessage(state.Outcome switch
                {
                    Outcome.Passed => $"Vote passed: {target} {(state.Target == Network.LocalPeer ? "were" : "was")} kicked.",
                    Outcome.Left => $"{Multiplayer.NameOf(state.Target)} left before the vote ended.",
                    _ => $"The vote to kick {target} failed."
                });
            }
        }

        protected override void Update()
        {
            // The host ends a vote that's run out of time.
            if (_target != null && Time.TotalTime >= _endsAt) Evaluate();

            if (Current is not State vote) return;

            float age = Time.TotalTime - _stateTime;
            float secondsLeft = vote.SecondsLeft - age;

            // An ended vote shows its result for a moment. A running one that the host never finished (it left, and
            // someone else took over) goes once its time is well up.
            if ((vote.Outcome != Outcome.Running && age > _resultTime) || (vote.Outcome == Outcome.Running && secondsLeft < -3f))
            {
                Current = null;
                return;
            }

            bool canVote = vote.Outcome == Outcome.Running && vote.Target != Network.LocalPeer && _myVote == null;
            if (canVote && !GameInput.Blocked)
            {
                if (InputSystem.GetKeyDown(Key.F1)) Cast(true);
                else if (InputSystem.GetKeyDown(Key.F2)) Cast(false);
            }

            if (!MainMenu.IsOpen) Draw(vote, MathF.Max(secondsLeft, 0f));
        }

        private void Draw(State vote, float secondsLeft)
        {
            const float padding = 16f;
            var position = new Vector2(24f, Canvas.Height * 0.3f);
            float x = position.X + padding;
            float y = position.Y + padding;
            bool aboutYou = vote.Target == Network.LocalPeer;
            string target = aboutYou ? "You" : Multiplayer.NameOf(vote.Target);

            Canvas.DrawRect(position, new Vector2(_width, vote.Outcome == Outcome.Running ? 150f : 74f), _panel);

            if (vote.Outcome != Outcome.Running)
            {
                (string title, Color color) = vote.Outcome switch
                {
                    Outcome.Passed => ("Vote passed", _yesColor),
                    Outcome.Left => ("Vote over", _dim),
                    _ => ("Vote failed", _noColor)
                };
                Canvas.DrawText(Fonts.Bold, title, new Vector2(x, y - 2f), color, 18f);
                string detail = vote.Outcome switch
                {
                    Outcome.Passed => $"{target} {(aboutYou ? "were" : "was")} kicked.",
                    Outcome.Left => $"{target} left.",
                    _ => $"{target} stay{(aboutYou ? "" : "s")}."
                };
                Canvas.DrawText(Fonts.Regular, detail, new Vector2(x, y + 24f), _dim, 15f);
                return;
            }

            Canvas.DrawText(Fonts.Regular, "Vote to kick", new Vector2(x, y - 2f), _dim, 14f);
            Canvas.DrawText(Fonts.Bold, target, new Vector2(x, y + 16f), aboutYou ? Color.White : Multiplayer.ColorOf(vote.Target), 20f);
            Canvas.DrawText(Fonts.Regular, $"called by {Multiplayer.NameOf(vote.Caller)}", new Vector2(x, y + 44f), _dim, 14f);

            float keysY = y + 70f;
            if (aboutYou)
            {
                Canvas.DrawText(Fonts.Regular, "You don't get a vote in this one.", new Vector2(x, keysY + 4f), _dim, 15f);
            }
            else if (_myVote is bool voted)
            {
                Canvas.DrawText(Fonts.Bold, voted ? "You voted yes" : "You voted no", new Vector2(x, keysY + 4f), voted ? _yesColor : _noColor, 16f);
            }
            else
            {
                float after = KeyCap("F1", x, keysY);
                after += Canvas.DrawText(Fonts.Bold, "Yes", new Vector2(after + 8f, keysY + 4f), _yesColor, 16f) + 28f;
                after = KeyCap("F2", after, keysY);
                Canvas.DrawText(Fonts.Bold, "No", new Vector2(after + 8f, keysY + 4f), _noColor, 16f);
            }

            string tally = $"{vote.Yes} yes · {vote.No} no · {vote.Needed} yes needed";
            Canvas.DrawText(Fonts.Regular, tally, new Vector2(x, y + 104f), _dim, 14f);

            // Time left, as a bar along the bottom.
            Canvas.DrawRect(new Vector2(position.X, position.Y + 146f), new Vector2(_width * MathF.Min(secondsLeft / _duration, 1f), 4f), Gui.AccentColor);
        }

        /// <summary>A key cap with <paramref name="key"/> on it. Returns where it ends.</summary>
        private static float KeyCap(string key, float x, float y)
        {
            Vector2 size = Canvas.MeasureText(Fonts.Bold, key, 14f);
            float width = MathF.Max(size.X + 12f, 26f);
            Canvas.DrawRect(new Vector2(x, y), new Vector2(width, 26f), new Color(1f, 1f, 1f, 0.9f));
            Canvas.DrawText(Fonts.Bold, key, new Vector2(x + (width - size.X) / 2f, y + (26f - size.Y) / 2f), new Color(0.08f, 0.09f, 0.11f), 14f);
            return x + width;
        }

        private void OnPeerLeft(PeerId peer)
        {
            if (_target != null) Evaluate();
        }

        private void OnSessionEnded()
        {
            _target = null;
            _votes.Clear();
            _canCallAgainAt.Clear();
            Current = null;
        }
    }
}
