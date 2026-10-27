using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Networking;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// The menu the game opens on, over the live map: find a room to join, host one (on Steam or this network, with
    /// an optional password), practise offline, change settings or quit. Leaving a room comes back here. Joining waits
    /// on a page that says what went wrong if it doesn't work, and a room with a password asks for it first.
    /// </summary>
    public class MainMenu : Component
    {
        private enum Page { Main, Rooms, Host, Password, Joining, Settings }

        /// <summary>Where rooms are found and hosted.</summary>
        private enum Place { Steam, LocalNetwork }

        private const float _buttonWidth = 300f;
        private const float _buttonHeight = 46f;
        private const float _gap = 10f;
        private const float _panelWidth = 720f;
        private const float _formWidth = 460f;
        private const float _rowHeight = 54f;
        private const int _visibleRows = 6;

        // How long a search of the local network listens for answers, and how long to wait on Steam's list or a join
        // before giving up. The transports give up on a silent host after 10 seconds themselves.
        private static readonly TimeSpan _localSearchTime = TimeSpan.FromSeconds(0.8);
        private const float _steamSearchTimeout = 10f;
        private const float _joinTimeout = 15f;

        private static readonly Color _panel = new(0.05f, 0.06f, 0.08f, 0.82f);
        private static readonly Color _dim = new(1f, 1f, 1f, 0.6f);
        private static readonly Color _faint = new(1f, 1f, 1f, 0.35f);
        private static readonly Color _errorColor = new(1f, 0.45f, 0.4f);
        private static readonly Color _onlineColor = new(0.35f, 0.85f, 0.45f);

        public static bool IsOpen { get; private set; }

        public Player Player { get; set; } = null!;
        public MultiplayerManager Multiplayer { get; set; } = null!;
        public ClassMenu ClassMenu { get; set; } = null!;

        /// <summary>What the Quit button does.</summary>
        public Action? Quit { get; set; }

        /// <summary>Open the menu when the game starts.</summary>
        public bool OpenOnStart { get; set; } = true;

        private Page _page;
        private Place _place = Place.Steam;

        // What went wrong, shown on the page it went wrong on.
        private string? _message;

        private IReadOnlyList<RoomInfo> _rooms = [];
        private bool _searching;
        private float _searchStarted;
        private Task<IReadOnlyList<RoomInfo>>? _localSearch;
        private int _scroll;

        // The room being joined (or asking for its password): its name, whether it has a password, how to join it
        // given one, and the page to go back to if that doesn't work.
        private string _targetName = "";
        private Func<string, bool>? _join;
        private Page _returnPage = Page.Main;
        private bool _gavePassword;
        private string _joiningText = "";
        private float _joinStarted;

        private readonly TextField _roomName = new() { MaxLength = RoomSettings.MaxNameLength };
        private readonly TextField _hostPassword = new() { MaxLength = RoomSettings.MaxPasswordLength, PasswordChar = '•' };
        private readonly TextField _joinPassword = new() { MaxLength = RoomSettings.MaxPasswordLength, PasswordChar = '•' };
        private readonly TextField _address = new() { MaxLength = 100 };
        private float _maxPlayers = 8f;
        private Place _hostPlace = Place.Steam;
        private bool _private;

        // Kicked out of the room, which the menu says once the room's gone, with how long until they can rejoin.
        private bool _kicked;
        private TimeSpan? _kickedFor;

        // The click that opened the menu (from the pause menu) mustn't also press whatever's under the pointer.
        private bool _ignoreClick;

        protected override void Start()
        {
            Network.SessionStarted += OnSessionStarted;
            Network.SessionEnded += OnSessionEnded;
            Network.JoinFailed += OnJoinFailed;
            Network.Kicked += OnKicked;
            Multiplayer.SteamRoomsFound += OnSteamRoomsFound;

            if (OpenOnStart) Open();
        }

        protected override void OnDestroy()
        {
            Network.SessionStarted -= OnSessionStarted;
            Network.SessionEnded -= OnSessionEnded;
            Network.JoinFailed -= OnJoinFailed;
            Network.Kicked -= OnKicked;
            Multiplayer.SteamRoomsFound -= OnSteamRoomsFound;
            IsOpen = false;
        }

        public void Open()
        {
            // Behind the menu, the game's back where it starts, wherever you'd got to.
            Player.StartOver();
            IsOpen = true;
            _page = Page.Main;
            _message = null;
            _ignoreClick = InputSystem.GetMouseButton(MouseButton.Left);
            Gui.Focused = null;
            ClassMenu.Close();
        }

        /// <summary>Into the game: the menu goes, and the class menu comes up to pick a class.</summary>
        private void Play()
        {
            IsOpen = false;
            _searching = false;
            Gui.Focused = null;
            ClassMenu.Open();
        }

        protected override void Update()
        {
            if (!IsOpen) return;

            if (!InputSystem.GetMouseButton(MouseButton.Left)) _ignoreClick = false;
            UpdateSearch();

            if (_page == Page.Joining && Time.TotalTime - _joinStarted > _joinTimeout)
            {
                Multiplayer.Leave();
                ShowProblem(MultiplayerManager.JoinErrorText(JoinError.NoAnswer));
            }

            // The console takes the keyboard while it's open.
            if (DevConsole.IsOpen)
            {
                Gui.Focused = null;
            }
            else
            {
                if (InputSystem.GetKeyDown(Key.Escape))
                {
                    InputSystem.Consume(Key.Escape);
                    Back();
                    if (!IsOpen) return;
                }

                if (InputSystem.GetKeyDown(Key.Tab)) FocusNext();
            }

            Canvas.DrawRect(Vector2.Zero, Canvas.Size, new Color(0.02f, 0.03f, 0.05f, _page == Page.Main ? 0.25f : 0.6f));

            switch (_page)
            {
                case Page.Main: DrawMain(); break;
                case Page.Rooms: DrawRooms(); break;
                case Page.Host: DrawHost(); break;
                case Page.Password: DrawPassword(); break;
                case Page.Joining: DrawJoining(); break;
                case Page.Settings:
                    if (SettingsPage.Draw(Player) && !_ignoreClick) Back();
                    break;
            }
        }

        private void Back()
        {
            switch (_page)
            {
                case Page.Settings:
                    SettingsPage.Leave(Player);
                    GoTo(Page.Main);
                    break;
                case Page.Rooms or Page.Host:
                    GoTo(Page.Main);
                    break;
                case Page.Password:
                    GoTo(_returnPage);
                    break;
                case Page.Joining:
                    Multiplayer.Leave();
                    GoTo(_returnPage);
                    break;
            }
        }

        private void GoTo(Page page)
        {
            _page = page;
            _message = null;
            Gui.Focused = null;
        }

        /// <summary>Tab moves to the page's next text box.</summary>
        private void FocusNext()
        {
            TextField[] fields = _page switch
            {
                Page.Host when _private => [_roomName, _hostPassword],
                Page.Host => [_roomName],
                Page.Rooms when _place == Place.LocalNetwork => [_address],
                Page.Password => [_joinPassword],
                _ => []
            };
            if (fields.Length == 0) return;

            int current = Gui.Focused == null ? -1 : Array.IndexOf(fields, Gui.Focused);
            Gui.Focused = fields[(current + 1) % fields.Length];
        }

        // ---- The main page ----

        private void DrawMain()
        {
            // Darker down the left, behind the title and buttons, so the map shows through on the right. Bands that
            // meet exactly, so no edge is drawn twice.
            const int bands = 48;
            float fade = MathF.Min(Canvas.Width * 0.55f, 720f);
            for (int i = 0; i < bands; i++)
            {
                float left = fade * i / bands;
                float alpha = 0.7f * (1f - i / (float)bands);
                Canvas.DrawRect(new Vector2(left, 0f), new Vector2(fade * (i + 1) / bands - left, Canvas.Height), new Color(0.02f, 0.03f, 0.05f, alpha));
            }

            float x = 90f;
            float y = Canvas.Height / 2f - 230f;
            Canvas.DrawText(Fonts.Bold, "VERTEX", new Vector2(x - 4f, y), Color.White, 84f, shadow: true);
            y += Canvas.LineHeight(Fonts.Bold, 84f) - 4f;
            Canvas.DrawRect(new Vector2(x, y), new Vector2(72f, 4f), Gui.AccentColor);
            y += 44f;

            var buttons = new (string Label, Action Action)[]
            {
                ("Find a room", OpenRooms),
                ("Host a room", OpenHost),
                ("Practice offline", Play),
                ("Settings", () => GoTo(Page.Settings)),
                ("Quit", () => Quit?.Invoke())
            };

            foreach ((string label, Action action) in buttons)
            {
                if (Button(label, new Vector2(x, y), new Vector2(_buttonWidth, _buttonHeight)))
                {
                    action();
                    return;
                }

                y += _buttonHeight + _gap;
            }

            if (_message != null) Canvas.DrawText(Fonts.Regular, _message, new Vector2(x, y + 8f), _errorColor, 17f, shadow: true);

            // The version, in the bottom-right corner: players only join rooms on the same one.
            string version = $"Version {Network.GameVersion}";
            float versionWidth = Canvas.MeasureText(Fonts.Regular, version, 16f).X;
            Canvas.DrawText(Fonts.Regular, version, new Vector2(Canvas.Width - 40f - versionWidth, Canvas.Height - 52f), _faint, 16f, shadow: true);

            // Whether Steam's rooms are available, in the bottom-left corner.
            var status = new Vector2(x, Canvas.Height - 52f);
            if (Multiplayer.SteamName is string name)
            {
                Canvas.DrawRect(status + new Vector2(0f, 7f), new Vector2(9f, 9f), _onlineColor);
                Canvas.DrawText(Fonts.Regular, $"Signed in to Steam as {name}", status + new Vector2(18f, 0f), _dim, 16f, shadow: true);
            }
            else
            {
                string text = Multiplayer.SteamError != null ? "Steam isn't running: only rooms on your network are available." : "Not connected to Steam.";
                Canvas.DrawText(Fonts.Regular, text, status, _faint, 16f, shadow: true);
            }
        }

        // ---- Finding rooms ----

        private void OpenRooms()
        {
            // Steam's rooms if Steam's there; it's only tried again from its tab.
            GoTo(Page.Rooms);
            _place = Multiplayer.SteamConnected ? Place.Steam : Place.LocalNetwork;
            Search();
        }

        private void Search()
        {
            _rooms = [];
            _scroll = 0;
            _message = null;
            _searchStarted = Time.TotalTime;

            if (_place == Place.Steam)
            {
                _localSearch = null;
                _searching = Multiplayer.FindSteamRooms();
            }
            else
            {
                _localSearch = UdpTransport.FindRoomsAsync(_localSearchTime);
                _searching = true;
            }
        }

        private void UpdateSearch()
        {
            if (!_searching) return;

            if (_place == Place.LocalNetwork && _localSearch is { IsCompleted: true } search)
            {
                _rooms = search.IsCompletedSuccessfully ? Sorted(search.Result) : [];
                _localSearch = null;
                _searching = false;
            }
            else if (_place == Place.Steam && Time.TotalTime - _searchStarted > _steamSearchTimeout)
            {
                _searching = false;
            }
        }

        private void OnSteamRoomsFound(IReadOnlyList<RoomInfo> rooms)
        {
            if (_place != Place.Steam || !_searching) return;

            _rooms = Sorted(rooms);
            _searching = false;
        }

        /// <summary>Rooms on this version with space first, the busiest first, then by name.</summary>
        private static List<RoomInfo> Sorted(IEnumerable<RoomInfo> rooms) => rooms
            .OrderBy(room => !room.IsSameVersion)
            .ThenBy(room => room.IsFull)
            .ThenByDescending(room => room.Players)
            .ThenBy(room => room.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        private void DrawRooms()
        {
            float listHeight = _visibleRows * _rowHeight;
            float footer = _place == Place.LocalNetwork ? 64f : 0f;
            float total = 64f + 52f + listHeight + 16f + footer + 30f + _buttonHeight;
            float x = (Canvas.Width - _panelWidth) / 2f;
            float y = MathF.Max(30f, (Canvas.Height - total) / 2f);

            MenuText.Title("Find a room", y);
            y += 64f;

            var tab = new Vector2(170f, 38f);
            if (Tab("Steam", _place == Place.Steam, new Vector2(x, y), tab)) SwitchPlace(Place.Steam);
            if (Tab("Local network", _place == Place.LocalNetwork, new Vector2(x + tab.X + 8f, y), tab)) SwitchPlace(Place.LocalNetwork);
            if (Button("Refresh", new Vector2(x + _panelWidth - 140f, y), new Vector2(140f, tab.Y), 18f)) Search();
            y += 52f;

            var listPosition = new Vector2(x, y);
            var listSize = new Vector2(_panelWidth, listHeight);
            Canvas.DrawRect(listPosition, listSize, _panel);
            DrawRoomList(listPosition, listSize);
            y += listHeight + 16f;

            if (_place == Place.LocalNetwork)
            {
                const float joinWidth = 140f;
                bool enter = Gui.TextBox(Fonts.Regular, _address, new Vector2(x, y), new Vector2(_panelWidth - joinWidth - 10f, _buttonHeight),
                    "Or type an address to join, like 192.168.1.20");
                if (Button("Join", new Vector2(x + _panelWidth - joinWidth, y), new Vector2(joinWidth, _buttonHeight)) || enter) JoinAddress();
                y += footer;
            }

            if (_message != null) MenuText.Centred(Fonts.Regular, _message, y, 17f, _errorColor);
            y += 30f;

            if (Button("Back", new Vector2((Canvas.Width - _buttonWidth) / 2f, y), new Vector2(_buttonWidth, _buttonHeight))) Back();
        }

        private void SwitchPlace(Place place)
        {
            _place = place;
            Search();
        }

        private void DrawRoomList(Vector2 position, Vector2 size)
        {
            Vector2 centre = position + size / 2f;

            if (_place == Place.Steam && !Multiplayer.SteamConnected && !_searching)
            {
                DrawNote(centre, "Steam isn't running.", "Start Steam and press Refresh, or find rooms on your local network.");
                return;
            }

            if (_rooms.Count == 0)
            {
                if (_searching)
                {
                    Spinner(centre - new Vector2(0f, 18f));
                    MenuText.Centred(Fonts.Regular, "Looking for rooms...", centre.Y + 14f, 18f, _dim);
                }
                else
                {
                    DrawNote(centre, "No rooms found.", _place == Place.Steam ? "Host one, or press Refresh in a moment."
                        : "Host one, or check the other game is on the same network.");
                }

                return;
            }

            // The mouse wheel scrolls a long list.
            int hidden = Math.Max(_rooms.Count - _visibleRows, 0);
            if (Contains(position, size, Canvas.MousePosition) && InputSystem.ScrollDelta != 0f)
            {
                _scroll = Math.Clamp(_scroll - Math.Sign(InputSystem.ScrollDelta), 0, hidden);
            }

            _scroll = Math.Clamp(_scroll, 0, hidden);
            for (int i = 0; i < _visibleRows && _scroll + i < _rooms.Count; i++)
            {
                RoomInfo room = _rooms[_scroll + i];
                if (DrawRoom(room, position + new Vector2(0f, i * _rowHeight), new Vector2(size.X, _rowHeight)) && CanJoin(room))
                {
                    JoinRoom(room);
                    return;
                }
            }

            if (hidden > 0)
            {
                // A scroll bar down the right edge.
                float barHeight = size.Y * _visibleRows / _rooms.Count;
                float barY = position.Y + (size.Y - barHeight) * _scroll / hidden;
                Canvas.DrawRect(new Vector2(position.X + size.X - 5f, barY), new Vector2(3f, barHeight), _faint);
            }
        }

        /// <summary>A room on this version with space: one that can be clicked to join.</summary>
        private static bool CanJoin(RoomInfo room) => room.IsSameVersion && !room.IsFull;

        /// <summary>One room in the list. True when it's clicked.</summary>
        private bool DrawRoom(RoomInfo room, Vector2 position, Vector2 size)
        {
            bool joinable = CanJoin(room);
            bool hovered = Contains(position, size, Canvas.MousePosition) && joinable;
            if (hovered) Canvas.DrawRect(position, size, Gui.HoverColor);
            Canvas.DrawRect(new Vector2(position.X + 16f, position.Y + size.Y - 1f), new Vector2(size.X - 32f, 1f), new Color(1f, 1f, 1f, 0.06f));

            Color text = joinable ? Color.White : _faint;
            float left = position.X + 20f;
            if (room.HasPassword)
            {
                Padlock(new Vector2(left + 7f, position.Y + size.Y / 2f), text);
                left += 26f;
            }

            Canvas.DrawText(Fonts.Bold, room.Name, new Vector2(left, position.Y + 7f), text, 19f);
            string where = room.EndPoint is { } endPoint ? $"{endPoint.Address}:{endPoint.Port}" : "Steam";
            if (room.HasPassword) where += " · private";
            if (!room.IsSameVersion) where += room.Version.Length > 0 ? $" · version {room.Version}" : " · an older version";
            Canvas.DrawText(Fonts.Regular, where, new Vector2(left, position.Y + 31f), joinable ? _dim : _faint, 14f);

            float right = position.X + size.X - 20f;
            string tag = !room.IsSameVersion ? "Other version" : room.IsFull ? "Full" : hovered ? "Join ›" : "";
            float tagWidth = Canvas.MeasureText(Fonts.Bold, tag, 17f).X;
            if (tag.Length > 0) Canvas.DrawText(Fonts.Bold, tag, new Vector2(right - tagWidth, position.Y + 16f), joinable ? Gui.AccentColor : _faint, 17f);

            // The player count, clear of the tag however long it is.
            string players = $"{room.Players} / {room.MaxPlayers}";
            float playersWidth = Canvas.MeasureText(Fonts.Regular, players, 17f).X;
            float playersX = right - MathF.Max(80f, tagWidth + 20f) - playersWidth;
            Canvas.DrawText(Fonts.Regular, players, new Vector2(playersX, position.Y + 16f), text, 17f);

            // How long a message takes there and back, coloured like the scoreboard's; blank if it couldn't be told.
            if (room.Ping is int ping)
            {
                string shown = $"{ping} ms";
                float pingWidth = Canvas.MeasureText(Fonts.Regular, shown, 17f).X;
                Color color = joinable ? Scoreboard.PingColor(ping) : _faint;
                Canvas.DrawText(Fonts.Regular, shown, new Vector2(playersX - 36f - pingWidth, position.Y + 16f), color, 17f);
            }

            return hovered && InputSystem.GetMouseButtonDown(MouseButton.Left) && !_ignoreClick && !DevConsole.IsOpen;
        }

        private void JoinRoom(RoomInfo room)
        {
            Func<string, bool> join = room.EndPoint is { } endPoint
                ? password => Multiplayer.JoinLocal(endPoint.Address.ToString(), endPoint.Port, password)
                : password => JoinSteam(room.LobbyId, password);

            Target(room.Name, join, Page.Rooms);
            if (room.HasPassword) AskPassword(problem: null);
            else Join("");
        }

        /// <summary>Joins whatever's typed in the address box: an address or name, with or without a :port.</summary>
        private void JoinAddress()
        {
            string address = _address.Text.Trim();
            if (address.Length == 0)
            {
                _message = "Type the address of the PC hosting the room.";
                return;
            }

            int port = UdpTransport.DefaultPort;
            int colon = address.LastIndexOf(':');
            if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out int typedPort))
            {
                port = typedPort;
                address = address[..colon];
            }

            Target(address, password => Multiplayer.JoinLocal(address, port, password), Page.Rooms);
            Join("");
        }

        private bool JoinSteam(ulong lobby, string password)
        {
            Multiplayer.JoinSteam(lobby, password);
            return Multiplayer.SteamConnected;
        }

        private void Target(string name, Func<string, bool> join, Page returnPage)
        {
            _targetName = name;
            _join = join;
            _returnPage = returnPage;
        }

        private void Join(string password)
        {
            if (_join == null) return;

            _gavePassword = password.Length > 0;
            if (!_join(password))
            {
                ShowProblem(Multiplayer.SteamError != null && _place == Place.Steam ? "Steam isn't running." : $"Couldn't join {_targetName}.");
                return;
            }

            ShowJoining($"Joining {_targetName}...");
        }

        private void ShowJoining(string text)
        {
            GoTo(Page.Joining);
            _joiningText = text;
            _joinStarted = Time.TotalTime;
        }

        /// <summary>Back to where the join started, saying what went wrong.</summary>
        private void ShowProblem(string problem)
        {
            GoTo(_returnPage);
            _message = problem;
        }

        // ---- Passwords ----

        private void AskPassword(string? problem)
        {
            GoTo(Page.Password);
            _message = problem;
            _joinPassword.Clear();
            Gui.Focused = _joinPassword;
        }

        private void DrawPassword()
        {
            float x = (Canvas.Width - _formWidth) / 2f;
            float y = Canvas.Height / 2f - 150f;

            MenuText.Title(_targetName, y);
            y += 76f;
            Canvas.DrawRect(new Vector2(x - 28f, y - 22f), new Vector2(_formWidth + 56f, 236f), _panel);
            Padlock(new Vector2(x + 7f, y + 11f), _dim);
            Canvas.DrawText(Fonts.Regular, "This room is private. Enter its password.", new Vector2(x + 24f, y), _dim, 17f);
            y += 34f;

            bool enter = Gui.TextBox(Fonts.Regular, _joinPassword, new Vector2(x, y), new Vector2(_formWidth, _buttonHeight), "Password");
            y += _buttonHeight + 14f;
            if (_message != null) Canvas.DrawText(Fonts.Regular, _message, new Vector2(x, y), _errorColor, 17f);
            y += 52f;

            float half = (_formWidth - _gap) / 2f;
            if (Button("Join", new Vector2(x, y), new Vector2(half, _buttonHeight)) || enter)
            {
                if (_joinPassword.Text.Length == 0) _message = "Type the room's password.";
                else Join(_joinPassword.Text);
            }
            else if (Button("Back", new Vector2(x + half + _gap, y), new Vector2(half, _buttonHeight)))
            {
                Back();
            }
        }

        // ---- Hosting ----

        private void OpenHost()
        {
            GoTo(Page.Host);
            if (_roomName.Text.Length == 0) _roomName.SetText(Multiplayer.SteamName is string name ? $"{name}'s room" : "Vertex room");
            _hostPlace = Multiplayer.SteamConnected ? Place.Steam : Place.LocalNetwork;
        }

        private void DrawHost()
        {
            // The hint at the bottom says who can join and whether cheats work, then how others find the room.
            string hint = (_private
                    ? "Only players with the password can join, and you can turn on sv_cheats. "
                    : "Anyone can join, and sv_cheats stays off. ")
                + (_hostPlace == Place.Steam
                    ? "Listed for everyone on Steam; friends can also join from the Steam overlay."
                    : "Players on your network, or other copies of the game on this PC, find it under Local network.");
            List<string> hintLines = MenuText.Wrap(Fonts.Regular, hint, 15f, _formWidth);
            float hintLineHeight = Canvas.LineHeight(Fonts.Regular, 15f);

            float panelHeight = 22f + 82f + 36f + (_private ? 56f : 0f) + 64f + 74f + hintLines.Count * hintLineHeight + 18f;
            float x = (Canvas.Width - _formWidth) / 2f;
            float y = MathF.Max(30f, (Canvas.Height - (76f + panelHeight + 42f + _buttonHeight)) / 2f);

            MenuText.Title("Host a room", y);
            y += 76f;
            float panelTop = y - 22f;
            Canvas.DrawRect(new Vector2(x - 28f, panelTop), new Vector2(_formWidth + 56f, panelHeight), _panel);

            Label("Room name", x, ref y);
            bool enter = Gui.TextBox(Fonts.Regular, _roomName, new Vector2(x, y), new Vector2(_formWidth, 42f), "Vertex room");
            y += 56f;

            // Private rooms need a password, so the box for it only shows for one.
            bool wasPrivate = _private;
            _private = Gui.Toggle(Fonts.Regular, "Private room", _private, new Vector2(x, y), _formWidth);
            if (_private && !wasPrivate) Gui.Focused = _hostPassword;
            else if (!_private && Gui.Focused == _hostPassword) Gui.Focused = null;
            y += 36f;

            if (_private)
            {
                enter |= Gui.TextBox(Fonts.Regular, _hostPassword, new Vector2(x, y), new Vector2(_formWidth, 42f), "Password");
                y += 56f;
            }

            _maxPlayers = MathF.Round(Gui.Slider(Fonts.Regular, "Max players", _maxPlayers, 2f, 16f, new Vector2(x, y), _formWidth, "0"));
            y += 64f;

            Label("Host on", x, ref y);
            float half = (_formWidth - 8f) / 2f;
            if (Tab("Steam", _hostPlace == Place.Steam, new Vector2(x, y), new Vector2(half, 38f))) _hostPlace = Place.Steam;
            if (Tab("Local network", _hostPlace == Place.LocalNetwork, new Vector2(x + half + 8f, y), new Vector2(half, 38f))) _hostPlace = Place.LocalNetwork;
            y += 48f;

            foreach (string line in hintLines)
            {
                Canvas.DrawText(Fonts.Regular, line, new Vector2(x, y), _faint, 15f);
                y += hintLineHeight;
            }

            y = panelTop + panelHeight + 10f;
            if (_message != null) MenuText.Centred(Fonts.Regular, _message, y, 17f, _errorColor);
            y += 32f;

            float buttonHalf = (_formWidth - _gap) / 2f;
            if (Button("Host room", new Vector2(x, y), new Vector2(buttonHalf, _buttonHeight)) || enter) Host();
            else if (Button("Back", new Vector2(x + buttonHalf + _gap, y), new Vector2(buttonHalf, _buttonHeight))) Back();
        }

        private void Host()
        {
            if (_private && _hostPassword.Text.Length == 0)
            {
                _message = "A private room needs a password.";
                Gui.Focused = _hostPassword;
                return;
            }

            string name = _roomName.Text.Trim();
            var room = new RoomSettings(name.Length > 0 ? name : "Vertex room", _private ? _hostPassword.Text : "", (int)_maxPlayers);
            _targetName = room.Name;
            _returnPage = Page.Host;

            if (_hostPlace == Place.Steam)
            {
                Multiplayer.HostSteam(room);
                if (!Multiplayer.SteamConnected)
                {
                    _message = "Steam isn't running. Start it, or host on the local network.";
                    return;
                }
            }
            else if (!Multiplayer.HostLocal(room))
            {
                _message = $"Couldn't host: ports {UdpTransport.DefaultPort} to {UdpTransport.DefaultPort + UdpTransport.SearchPorts - 1} are all in use.";
                return;
            }

            ShowJoining($"Starting {room.Name}...");
        }

        // ---- Joining ----

        private void DrawJoining()
        {
            Vector2 centre = Canvas.Size / 2f;
            float width = MathF.Max(_formWidth, Canvas.MeasureText(Fonts.Bold, _joiningText, 24f).X + 80f);
            Canvas.DrawRect(new Vector2(centre.X - width / 2f, centre.Y - 96f), new Vector2(width, 214f), _panel);
            Spinner(centre - new Vector2(0f, 50f));
            MenuText.Centred(Fonts.Bold, _joiningText, centre.Y - 4f, 24f, Color.White);

            if (Button("Cancel", new Vector2(centre.X - _buttonWidth / 2f, centre.Y + 50f), new Vector2(_buttonWidth, _buttonHeight))) Back();
        }

        private void OnSessionStarted()
        {
            if (IsOpen) Play();
        }

        private void OnSessionEnded()
        {
            // Leaving on purpose opens the menu itself; this is the room closing, or the connection dropping.
            if (IsOpen) return;

            Open();
            _message = _kicked ? MultiplayerManager.KickedText(_kickedFor) : "Disconnected from the room.";
            _kicked = false;
        }

        private void OnKicked(TimeSpan? banFor)
        {
            _kicked = true;
            _kickedFor = banFor;
        }

        private void OnJoinFailed(JoinError error)
        {
            if (!IsOpen) Open();

            // Not joined from here: a Steam invite, or the console. A Steam room with a password asks for it, then
            // tries that room again.
            if (_page is not (Page.Joining or Page.Password))
            {
                if (Network.Transport is not SteamTransport)
                {
                    _returnPage = Page.Main;
                    ShowProblem(MultiplayerManager.JoinErrorText(error));
                    return;
                }

                ulong lobby = Multiplayer.SteamJoinTarget;
                Target("Steam room", password => JoinSteam(lobby, password), Page.Main);
            }

            if (error == JoinError.WrongPassword)
            {
                AskPassword(_page == Page.Joining && _gavePassword ? "Wrong password." : null);
                return;
            }

            ShowProblem(_returnPage == Page.Host && error == JoinError.NoAnswer ? "Steam couldn't create the room. Try again." : MultiplayerManager.JoinErrorText(error));
        }

        // ---- Pieces ----

        private bool Button(string label, Vector2 position, Vector2 size, float textSize = 20f) =>
            Gui.Button(Fonts.Bold, label, position, size, textSize) && !_ignoreClick && !DevConsole.IsOpen;

        /// <summary>One of a row of choices: the chosen one is lit and underlined. True when another is clicked.</summary>
        private bool Tab(string label, bool selected, Vector2 position, Vector2 size)
        {
            if (!selected) return Button(label, position, size, 18f);

            Canvas.DrawRect(position, size, new Color(1f, 1f, 1f, 0.16f));
            Canvas.DrawRect(new Vector2(position.X, position.Y + size.Y - 3f), new Vector2(size.X, 3f), Gui.AccentColor);
            Vector2 textSize = Canvas.MeasureText(Fonts.Bold, label, 18f);
            Canvas.DrawText(Fonts.Bold, label, position + (size - textSize) / 2f, Color.White, 18f);
            return false;
        }

        private static void Label(string text, float x, ref float y)
        {
            Canvas.DrawText(Fonts.Regular, text, new Vector2(x, y), _dim, 16f);
            y += 26f;
        }

        private static void DrawNote(Vector2 centre, string title, string detail)
        {
            MenuText.Centred(Fonts.Bold, title, centre.Y - 24f, 20f, Color.White);
            MenuText.Centred(Fonts.Regular, detail, centre.Y + 6f, 16f, _dim);
        }

        /// <summary>A turning arc, for waiting.</summary>
        private static void Spinner(Vector2 centre)
        {
            Canvas.DrawArc(centre, 16f, Time.TotalTime * 300f % 360f, 270f, 4f, Gui.AccentColor);
        }

        /// <summary>A small padlock centred on <paramref name="centre"/>: a body with a shackle over it.</summary>
        private static void Padlock(Vector2 centre, Color color)
        {
            Canvas.DrawRect(centre + new Vector2(-6f, -2f), new Vector2(12f, 10f), color);
            Canvas.DrawArc(centre + new Vector2(0f, -3f), 4f, 180f, 180f, 2f, color);
            Canvas.DrawRect(centre + new Vector2(-5f, -3f), new Vector2(2f, 2f), color);
            Canvas.DrawRect(centre + new Vector2(3f, -3f), new Vector2(2f, 2f), color);
        }

        private static bool Contains(Vector2 position, Vector2 size, Vector2 point) =>
            point.X >= position.X && point.Y >= position.Y && point.X < position.X + size.X && point.Y < position.Y + size.Y;
    }
}
