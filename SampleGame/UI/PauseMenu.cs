using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Networking;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Escape opens it: Resume, Change class, Settings, Players (in a room, to vote to kick someone), Leave room (or Main
    /// menu, playing offline) and Quit. The game
    /// keeps running behind it, as in any multiplayer game. Settings are saved as soon as you leave the settings page.
    /// It also looks after the mouse cursor, which is free while a menu or the console is open and locked to the game
    /// otherwise.
    /// </summary>
    public class PauseMenu : Component
    {
        private const float _buttonWidth = 280f;
        private const float _buttonHeight = 46f;
        private const float _gap = 10f;

        public static bool IsOpen { get; private set; }

        public Player Player { get; set; } = null!;
        public MultiplayerManager Multiplayer { get; set; } = null!;
        public ClassMenu ClassMenu { get; set; } = null!;

        /// <summary>Where Leave room and Main menu go. Without one, there's no Main menu button.</summary>
        public MainMenu? MainMenu { get; set; }

        /// <summary>What the Quit button does.</summary>
        public Action? Quit { get; set; }

        private bool _onSettingsPage;
        private bool _onPlayersPage;

        // Why a vote to kick couldn't start, shown on the players page.
        private string? _voteProblem;

        protected override void Update()
        {
            // Chat, the console and the other menus handle Escape themselves (and consume it) while they're open.
            if (InputSystem.GetKeyDown(Key.Escape) && !ChatBox.IsTyping && !DevConsole.IsOpen && !ClassMenu.IsOpen && !global::SampleGame.MainMenu.IsOpen)
            {
                InputSystem.Consume(Key.Escape);
                if (!IsOpen) Open();
                else if (_onSettingsPage) LeaveSettings();
                else if (_onPlayersPage) _onPlayersPage = false;
                else Close();
            }

            InputSystem.CursorLocked = !GameInput.LookBlocked;

            if (!IsOpen) return;

            Canvas.DrawRect(Vector2.Zero, Canvas.Size, new Color(0.02f, 0.03f, 0.05f, 0.6f));
            if (_onSettingsPage)
            {
                if (SettingsPage.Draw(Player)) LeaveSettings();
            }
            else if (_onPlayersPage)
            {
                DrawPlayers();
            }
            else
            {
                DrawMain();
            }
        }

        protected override void OnDestroy() => IsOpen = false;

        private void Open()
        {
            IsOpen = true;
            _onSettingsPage = false;
            _onPlayersPage = false;
        }

        private void Close()
        {
            if (_onSettingsPage) LeaveSettings();
            IsOpen = false;
        }

        private void DrawMain()
        {
            var buttons = new List<(string Label, Action Action)>
            {
                ("Resume", Close),
                ("Change class", () => { Close(); ClassMenu.Open(); }),
                ("Settings", () => _onSettingsPage = true)
            };
            if (Network.InSession) buttons.Add(("Players", () => { _onPlayersPage = true; _voteProblem = null; }));
            if (Network.InSession) buttons.Add(("Leave room", () => { Close(); Multiplayer.Leave(); MainMenu?.Open(); }));
            else if (MainMenu != null) buttons.Add(("Main menu", () => { Close(); MainMenu.Open(); }));
            buttons.Add(("Quit", () => Quit?.Invoke()));

            float height = buttons.Count * (_buttonHeight + _gap) - _gap;
            float x = (Canvas.Width - _buttonWidth) / 2f;
            float y = (Canvas.Height - height) / 2f + 20f;

            MenuText.Title(Network.InSession ? Network.RoomName : "Paused", y - 80f);

            foreach ((string label, Action action) in buttons)
            {
                if (Gui.Button(Fonts.Bold, label, new Vector2(x, y), new Vector2(_buttonWidth, _buttonHeight))) action();
                y += _buttonHeight + _gap;
            }
        }

        /// <summary>Everyone in the room, each with a button to call a vote to kick them (but the host, and you).</summary>
        private void DrawPlayers()
        {
            const float width = 560f;
            const float rowHeight = 46f;
            const float kickWidth = 130f;

            var rows = new List<(PeerId Peer, string Name, Color Color)> { (Network.LocalPeer, Multiplayer.NameOf(Network.LocalPeer), Multiplayer.ColorOf(Network.LocalPeer)) };
            rows.AddRange(Multiplayer.RemotePlayers.Select(remote => (remote.Peer, remote.Name, remote.Color)));

            float height = rows.Count * rowHeight + 40f;
            float x = (Canvas.Width - width) / 2f;
            float y = MathF.Max(110f, (Canvas.Height - (height + 90f + _buttonHeight)) / 2f + 40f);
            MenuText.Title("Players", y - 80f);

            Canvas.DrawRect(new Vector2(x, y), new Vector2(width, height), new Color(0.05f, 0.06f, 0.08f, 0.82f));
            string hint = $"A vote passes when {VoteKick.Threshold:0%} of the others vote yes. The host can't be kicked.";
            Canvas.DrawText(Fonts.Regular, hint, new Vector2(x + 20f, y + 12f), new Color(1f, 1f, 1f, 0.5f), 14f);
            float rowY = y + 40f;

            VoteKick votes = Multiplayer.Votes;
            foreach ((PeerId peer, string name, Color color) in rows)
            {
                Canvas.DrawRect(new Vector2(x + 20f, rowY + 17f), new Vector2(12f, 12f), color);
                float nameWidth = Canvas.DrawText(Fonts.Bold, name, new Vector2(x + 42f, rowY + 11f), Color.White, 18f);
                string tags = (peer == Network.HostPeer ? " · host" : "") + (peer == Network.LocalPeer ? " · you" : "");
                Canvas.DrawText(Fonts.Regular, tags, new Vector2(x + 42f + nameWidth, rowY + 13f), new Color(1f, 1f, 1f, 0.5f), 15f);

                if (peer != Network.LocalPeer && peer != Network.HostPeer)
                {
                    var button = new Vector2(x + width - 20f - kickWidth, rowY + 6f);
                    if (votes.Current is { Outcome: VoteKick.Outcome.Running } vote)
                    {
                        string state = vote.Target == peer ? "Vote on" : "";
                        Canvas.DrawText(Fonts.Regular, state, button + new Vector2(20f, 8f), Gui.AccentColor, 15f);
                    }
                    else if (Gui.Button(Fonts.Bold, "Vote kick", button, new Vector2(kickWidth, 34f), 16f))
                    {
                        if (votes.Call(peer, out _voteProblem)) Close();
                        return;
                    }
                }

                rowY += rowHeight;
            }

            y += height + 14f;
            if (_voteProblem != null) MenuText.Centred(Fonts.Regular, _voteProblem, y, 16f, new Color(1f, 0.45f, 0.4f));
            y += 30f;

            if (Gui.Button(Fonts.Bold, "Back", new Vector2((Canvas.Width - _buttonWidth) / 2f, y), new Vector2(_buttonWidth, _buttonHeight))) _onPlayersPage = false;
        }

        private void LeaveSettings()
        {
            _onSettingsPage = false;
            SettingsPage.Leave(Player);
        }
    }
}
