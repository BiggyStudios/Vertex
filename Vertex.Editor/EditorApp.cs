using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Editor.Panels;
using Vertex.Editor.Platform;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.Rendering.Vulkan;
using Vertex.UI;

namespace Vertex.Editor
{
    /// <summary>What the editor can ask of the window it's in: its own title bar has the buttons, so it does the moving too.</summary>
    public interface IWindowControls
    {
        bool Maximized { get; }
        void Minimize();
        void ToggleMaximize();
        void Close();

        /// <summary>Starts moving the window with the mouse, until the button is let go.</summary>
        void BeginMove();
    }

    /// <summary>
    /// The whole editor window, drawn each frame: the title bar (menus, play controls, the window's own buttons), the
    /// outliner on the left, the scene with the assets under it in the middle, and the inspector on the right, with
    /// dividers between to resize them. Keyboard shortcuts are handled here.
    /// </summary>
    public sealed class EditorApp
    {
        private readonly Renderer? _renderer;
        private readonly IWindowControls? _window;
        private readonly OutlinerPanel _outliner;
        private readonly InspectorPanel _inspector;
        private readonly SceneViewPanel _sceneView;
        private readonly AssetsPanel _assets;

        private readonly List<(string Text, float At)> _notes = new();
        private float _fps;

        private const float _titleBarHeight = 40f;

        private static readonly string[] _menuNames = ["File", "Edit", "Create", "View", "Help"];

        public EditorContext Context { get; }

        /// <summary>The screen's scaling (1.25 at 125%), set by the window; the editor sizes itself by it.</summary>
        public float DisplayScale { get; set; } = 1f;

        /// <summary>How many pixels tall the screen the window is on is, set by the window: for <see cref="Preferences.AutoSize"/>. 0 if unknown.</summary>
        public float ScreenHeight { get; set; }

        /// <summary>
        /// How much bigger to make everything on a big, sharp screen the system doesn't scale up itself: a 1440-pixel-tall
        /// screen (1440p or ultrawide) at 100% gets 125%, a 4K one 150%. A screen the system already scales is left alone.
        /// </summary>
        public static float AutoScale(float displayScale, float screenHeight)
        {
            if (displayScale > 1.05f || screenHeight <= 0f) return 1f;
            return screenHeight >= 2000f ? 1.5f : screenHeight >= 1400f ? 1.25f : 1f;
        }

        /// <summary>Pixels per UI unit for the editor: the screen's scaling, the automatic size, and the size chosen.</summary>
        public float InterfaceScale => DisplayScale * (Context.Preferences.AutoSize ? AutoScale(DisplayScale, ScreenHeight) : 1f) * Context.Preferences.UiScale;

        /// <summary>Set by the window while it's being resized by an edge: the editor's controls leave the mouse alone.</summary>
        public bool InputBlocked { get; set; }

        /// <summary>A mouse pointer the window wants (resize arrows at its edges), over whatever the editor would show.</summary>
        public CursorShape? CursorOverride { get; set; }
        public SceneViewPanel SceneView => _sceneView;

        /// <summary>Where the panels are docked, as tabs round the scene view.</summary>
        public DockLayout Dock => _dock;

        private readonly DockLayout _dock;
        private readonly ConsolePanel _console;

        /// <param name="renderer">For the title bar to say what's drawing; may be null in tests.</param>
        /// <param name="window">The window's controls, for the title bar's buttons; may be null in tests.</param>
        public EditorApp(Renderer? renderer = null, IWindowControls? window = null, Preferences? preferences = null)
        {
            _renderer = renderer;
            _window = window;
            Context = new EditorContext(preferences);
            _outliner = new OutlinerPanel(Context);
            _inspector = new InspectorPanel(Context);
            _sceneView = new SceneViewPanel(Context);
            _assets = new AssetsPanel(Context);
            _console = new ConsolePanel(Context);
            _dock = new DockLayout(Context.Preferences);
            _dock.Add(_outliner, DockArea.Left);
            _dock.Add(_inspector, DockArea.Right);
            _dock.Add(_assets, DockArea.Bottom);
            _dock.Add(_console, DockArea.Bottom);
            Context.ConsoleRequested += () => _dock.Show(_console.Title);
            Context.Notified += text =>
            {
                // The same note again (a value that keeps failing to set) refreshes the one showing.
                if (_notes.Count > 0 && _notes[^1].Text == text)
                {
                    _notes[^1] = (text, Ui.Time - 0.2f);
                    return;
                }

                _notes.Add((text, Ui.Time));
                if (_notes.Count > 3) _notes.RemoveAt(0);
            };

            // The editor's own file browser offers the project and recent projects' folders first.
            FilePicker.Places = () =>
            {
                var places = new List<(string, string)>();
                if (Context.Project is { } project)
                {
                    places.Add((project.Name, project.Directory));
                    places.Add(("Assets", project.AssetsDirectory));
                }

                foreach (string recent in Context.Preferences.RecentProjects.Take(4))
                {
                    string? folder = Path.GetDirectoryName(recent);
                    if (folder != null && Directory.Exists(folder)) places.Add((Path.GetFileNameWithoutExtension(recent), folder));
                }

                return places;
            };
        }

        /// <summary>The pictures of the scene to draw this frame, before the window's 2D layer.</summary>
        public RenderView[] Views => _sceneView.Views;

        /// <summary>Draws the editor for this frame and does whatever was clicked or pressed. Canvas and input must be ready for the frame.</summary>
        public void Update(float deltaTime)
        {
            Preferences preferences = Context.Preferences;

            // The editor's own size; a game's UI in the game view sizes itself (see Canvas.BeginView).
            Canvas.FixedScale = InterfaceScale;

            // While the game has the keyboard and mouse, the editor's controls don't see them (see PlayMode).
            _sceneView.RouteInput();
            InputSystem.Muted = Context.Play.GameFocused || InputBlocked;

            Ui.BeginFrame(deltaTime);
            if (deltaTime > 0f) _fps = _fps <= 0f ? 1f / deltaTime : _fps + (1f / deltaTime - _fps) * MathF.Min(deltaTime * 2f, 1f);

            Context.Update();
            if (_renderer != null) _renderer.ShadowResolution = Context.Project?.Settings.ShadowResolution ?? ShadowResolutionDefault;

            // Changes made while the mouse is held (a drag) count as one; letting go ends it.
            if (!Ui.MouseHeld()) Context.History.EndMerge();

            // Edits show without the scene running: new objects come in, deleted ones go.
            if (!Context.Play.Playing) Context.Scene.ApplyChanges();
            if (Context.Selection.Object is { } selected && !Context.InScene(selected)) Context.Selection.Clear();

            var screen = new Rect(0f, 0f, Canvas.Width, Canvas.Height);
            Ui.Rect(screen, Theme.Background);

            Rect body = screen.CutTop(_titleBarHeight).Shrink(Theme.Gap).CutTop(0f);
            if (body.Width > 100f && body.Height > 100f) DrawBody(body, preferences);

            DrawTitleBar(screen.TakeTop(_titleBarHeight));

            HandleShortcuts();
            DrawNotes(screen);

            // A drag that locked the cursor (a number box) can end anywhere: make sure it comes back.
            if (InputSystem.CursorLocked && !Context.Play.Playing && !Ui.MouseHeld() && !Ui.MouseHeld(MouseButton.Right) && !_sceneView.MovingCamera)
            {
                InputSystem.CursorLocked = false;
            }

            if (CursorOverride is { } cursor) Ui.Cursor = cursor;
            Ui.EndFrame();
            InputSystem.Muted = false;
        }

        private const int ShadowResolutionDefault = 2048;

        private void DrawBody(Rect body, Preferences preferences)
        {
            // The docks: left and right of everything, the bottom one under the scene view. Each takes at most two fifths;
            // on a small window they shrink rather than overlap. A dock with no tabs gives its room to the scene view.
            float maxSide = MathF.Max(body.Width * 0.4f, 120f);
            bool hasLeft = _dock.Has(DockArea.Left), hasRight = _dock.Has(DockArea.Right), hasBottom = _dock.Has(DockArea.Bottom);
            float left = hasLeft ? Math.Clamp(preferences.OutlinerWidth, MathF.Min(180f, maxSide), maxSide) : 0f;
            float right = hasRight ? Math.Clamp(preferences.InspectorWidth, MathF.Min(240f, maxSide), maxSide) : 0f;

            Rect leftRect = body.TakeLeft(left);
            Rect rightRect = body.TakeRight(right);
            Rect middle = body.CutLeft(left, left > 0f ? Theme.Gap : 0f).CutRight(right, right > 0f ? Theme.Gap : 0f);

            float maxBottom = MathF.Max(middle.Height - 200f, 100f);
            float bottom = hasBottom ? Math.Clamp(preferences.AssetsHeight, MathF.Min(120f, maxBottom), maxBottom) : 0f;
            Rect sceneRect = middle.CutBottom(bottom, bottom > 0f ? Theme.Gap : 0f);
            Rect bottomRect = middle.TakeBottom(bottom);

            _sceneView.Draw(sceneRect);
            if (hasBottom) _dock.Draw(DockArea.Bottom, bottomRect);
            if (hasLeft) _dock.Draw(DockArea.Left, leftRect);
            if (hasRight) _dock.Draw(DockArea.Right, rightRect);

            float value;
            if (hasLeft)
            {
                value = left;
                if (Widgets.Splitter(Ui.Id("split.left"), new Rect(leftRect.Right - 2f, body.Y, Theme.Gap + 4f, body.Height), ref value, 180f, maxSide)) preferences.OutlinerWidth = value;
            }

            if (hasRight)
            {
                value = right;
                if (Widgets.Splitter(Ui.Id("split.right"), new Rect(rightRect.X - Theme.Gap - 2f, body.Y, Theme.Gap + 4f, body.Height), ref value, 240f, maxSide, inverted: true))
                {
                    preferences.InspectorWidth = value;
                }
            }

            if (hasBottom)
            {
                value = bottom;
                if (Widgets.Splitter(Ui.Id("split.assets"), new Rect(middle.X, bottomRect.Y - Theme.Gap - 2f, middle.Width, Theme.Gap + 4f), ref value, 120f, maxBottom, vertical: true, inverted: true))
                {
                    preferences.AssetsHeight = value;
                }
            }

            _dock.UpdateDrag(body, middle);
        }

        // ---------------------------------------------------------------- The title bar

        private void DrawTitleBar(Rect bar)
        {
            // The whole bar moves the window, except where there's a control; double-click to maximise.
            int barId = Ui.Id("titlebar");
            Interaction drag = Ui.Interact(barId, bar);
            if (drag.DoubleClicked) _window?.ToggleMaximize();
            else if (drag.Pressed) _window?.BeginMove();

            // The mark.
            var mark = new Rect(bar.X + 12f, bar.Centre.Y - 11f, 22f, 22f);
            Ui.Rect(mark, Theme.Accent, 6f);
            Ui.Line(new Vector2(mark.X + 6.5f, mark.Y + 7f), new Vector2(mark.Centre.X, mark.Bottom - 6.5f), Color.White, 2f);
            Ui.Line(new Vector2(mark.Centre.X, mark.Bottom - 6.5f), new Vector2(mark.Right - 6.5f, mark.Y + 7f), Color.White, 2f);

            // The menus, which open by clicking and follow the mouse across while one's open.
            float x = mark.Right + 10f;
            for (int i = 0; i < _menuNames.Length; i++)
            {
                float width = Ui.TextWidth(_menuNames[i]) + 20f;
                var header = new Rect(x, bar.Y + 7f, width, bar.Height - 14f);
                int id = Ui.Id("menu." + _menuNames[i]);
                bool open = Menu.IsOpen(id);
                bool anotherOpen = !open && _menuNames.Any(name => Menu.IsOpen(Ui.Id("menu." + name)));

                Interaction interaction = Ui.Interact(id, header);
                if (interaction.Pressed && !open || anotherOpen && header.Contains(Ui.Mouse))
                {
                    Menu.Open(id, new Vector2(header.X, header.Bottom + 4f), MenuItems(_menuNames[i]));
                    open = true;
                }

                Color background = Ui.Smooth(id, open ? Theme.ControlHover : interaction.Hovered ? Theme.Control : Theme.Control.WithAlpha(0f), 24f);
                Ui.Rect(header, background, 5f);
                Ui.Text(header, _menuNames[i], open || interaction.Hovered ? Theme.Text : Theme.TextDim, align: 0.5f);
                x += width + 2f;
            }

            // Which project and scene, and whether it's saved.
            string scene = Context.SceneName + (Context.Dirty ? "  •" : "");
            string title = Context.Project != null ? $"{Context.Project.Name}  /  {scene}" : scene;
            Ui.Text(new Rect(x + 14f, bar.Y, 320f, bar.Height), title, Theme.TextFaint, size: Theme.SmallTextSize);

            DrawPlayControls(bar);

            // The window's own buttons.
            float buttonWidth = 46f;
            var close = new Rect(bar.Right - buttonWidth, bar.Y, buttonWidth, bar.Height);
            var maximize = close.Offset(new Vector2(-buttonWidth, 0f));
            var minimize = maximize.Offset(new Vector2(-buttonWidth, 0f));
            if (WindowButton(Ui.Id("window.min"), minimize, Icons.Minimize, danger: false)) _window?.Minimize();
            if (WindowButton(Ui.Id("window.max"), maximize, _window?.Maximized == true ? Icons.Restore : Icons.Maximize, danger: false)) _window?.ToggleMaximize();
            if (WindowButton(Ui.Id("window.close"), close, Icons.Close, danger: true)) RequestExit();

            // What's drawing, and how fast.
            string device = _renderer?.Backend switch
            {
                VulkanRenderBackend vulkan => $"Vulkan · {vulkan.DeviceName}",
                { } backend => backend.Name,
                null => ""
            };

            string status = _fps > 0f ? $"{device}   {_fps:0} fps" : device;
            Ui.Text(new Rect(bar.Centre.X + 90f, bar.Y, minimize.X - bar.Centre.X - 104f, bar.Height), status, Theme.TextFaint, align: 1f, size: Theme.SmallTextSize);
        }

        private static bool WindowButton(int id, Rect rect, Icon icon, bool danger)
        {
            Interaction interaction = Ui.Interact(id, rect);
            Color hover = danger ? Theme.Danger : Theme.ControlHover;
            Ui.Rect(rect, Ui.Smooth(id, interaction.Hovered ? hover : hover.WithAlpha(0f), 24f));
            icon(rect.Centre, 16f, interaction.Hovered && danger ? Color.White : Theme.TextDim);
            return interaction.Clicked;
        }

        private void DrawPlayControls(Rect bar)
        {
            PlayMode play = Context.Play;
            var pill = new Rect(bar.Centre.X - 52f, bar.Y + 6f, 104f, bar.Height - 12f);
            Ui.Rect(pill, Theme.Panel, 7f);
            Ui.Outline(pill, play.Playing ? Theme.Accent.WithAlpha(0.8f) : Theme.Border, 7f);

            bool busy = Context.Busy != null;
            Rect playButton = pill.Shrink(3f).Column(0, 3, 2f), pauseButton = pill.Shrink(3f).Column(1, 3, 2f), stopButton = pill.Shrink(3f).Column(2, 3, 2f);

            Ui.Opacity = busy ? 0.4f : 1f;
            if (Widgets.IconButton(Ui.Id("play.play"), playButton, Icons.Play, play.Playing ? "Stop (Ctrl+P)" : "Play (Ctrl+P)", selected: play.Playing,
                    color: play.Playing ? Theme.AccentText : Theme.Text) && !busy)
            {
                TogglePlay();
            }

            Ui.Opacity = play.Playing ? 1f : 0.4f;
            if (Widgets.IconButton(Ui.Id("play.pause"), pauseButton, Icons.Pause, "Pause (Ctrl+Shift+P)", selected: play.Paused) && play.Playing) play.Paused = !play.Paused;
            if (Widgets.IconButton(Ui.Id("play.stop"), stopButton, Icons.Stop, "Stop") && play.Playing) play.Stop();
            Ui.Opacity = 1f;
        }

        private void TogglePlay()
        {
            if (Context.Play.Playing) Context.Play.Stop();
            else Context.Play.Start();
        }

        // ---------------------------------------------------------------- Menus

        private List<MenuItem> MenuItems(string menu)
        {
            GameObject? selected = Context.Selection.Object;
            bool editing = !Context.Play.Playing;

            switch (menu)
            {
                case "File":
                {
                    var items = new List<MenuItem>
                    {
                        new("New Scene", () => ConfirmDiscard(Context.NewScene)) { Shortcut = "Ctrl+N" },
                        new("Open Scene…", OpenSceneDialog) { Shortcut = "Ctrl+O", Icon = Icons.Folder },
                        new("Save Scene", () => Context.SaveScene()) { Shortcut = "Ctrl+S", Enabled = editing },
                        new("Save Scene As…", () => Context.SaveScene(saveAs: true)) { Shortcut = "Ctrl+Shift+S", Enabled = editing },
                        MenuItem.Separator,
                        new("New Project…", () => ConfirmDiscard(Context.NewProject)),
                        new("Open Project…", () => ConfirmDiscard(() => OpenProjectDialog(Context)))
                    };

                    foreach (string recent in Context.Preferences.RecentProjects.Where(p => p != Context.Project?.FilePath).Take(4))
                    {
                        string path = recent;
                        items.Add(new MenuItem("    " + Path.GetFileNameWithoutExtension(path), () => ConfirmDiscard(() => Context.OpenProject(path))) { Enabled = File.Exists(path) });
                    }

                    items.Add(MenuItem.Separator);
                    items.Add(new MenuItem("Rebuild Game Code", Context.BuildGame) { Shortcut = "Ctrl+R", Enabled = Context.Project?.GameProjectFile != null && Context.Busy == null });
                    items.Add(new MenuItem("Project Settings…", ShowProjectSettings) { Enabled = Context.Project != null });
                    items.Add(MenuItem.Separator);
                    items.Add(new MenuItem("Exit", RequestExit) { Shortcut = "Alt+F4" });
                    return items;
                }

                case "Edit":
                    return
                    [
                        new MenuItem(Context.History.NextUndo is { } undo ? $"Undo {undo}" : "Undo", Context.Undo) { Shortcut = "Ctrl+Z", Enabled = Context.History.CanUndo, Icon = Icons.Undo },
                        new MenuItem(Context.History.NextRedo is { } redo ? $"Redo {redo}" : "Redo", Context.Redo) { Shortcut = "Ctrl+Y", Enabled = Context.History.CanRedo, Icon = Icons.Redo },
                        MenuItem.Separator,
                        new MenuItem("Duplicate", () => Context.Duplicate(selected!)) { Shortcut = "Ctrl+D", Enabled = selected != null },
                        new MenuItem("Rename", () => Context.Rename(selected!)) { Shortcut = "F2", Enabled = selected != null },
                        new MenuItem("Delete", () => Context.Delete(selected!)) { Shortcut = "Del", Enabled = selected != null, Icon = Icons.Trash },
                        new MenuItem("Frame Selected", _sceneView.FrameSelection) { Shortcut = "F", Enabled = selected != null },
                        MenuItem.Separator,
                        new MenuItem("Snapping…", ShowSnapping) { Icon = Icons.Magnet },
                        new MenuItem("Preferences…", ShowPreferences)
                    ];

                case "Create":
                    return CreateMenu.Items(Context, selected?.Transform.Parent);

                case "View":
                    return
                    [
                        new MenuItem("Outliner", () => _dock.Toggle("Outliner")) { Checked = _dock.IsOpen("Outliner") },
                        new MenuItem("Inspector", () => _dock.Toggle("Inspector")) { Checked = _dock.IsOpen("Inspector") },
                        new MenuItem("Assets", () => _dock.Toggle("Assets")) { Checked = _dock.IsOpen("Assets") },
                        new MenuItem("Console", () => _dock.Toggle("Console")) { Checked = _dock.IsOpen("Console"), Shortcut = "Ctrl+Shift+C" },
                        MenuItem.Separator,
                        new MenuItem("Grid", () => Context.Preferences.ShowGrid = !Context.Preferences.ShowGrid) { Checked = Context.Preferences.ShowGrid },
                        MenuItem.Separator,
                        new MenuItem("Bigger Interface", () => ChangeSize(+0.1f)) { Shortcut = "Ctrl+=" },
                        new MenuItem("Smaller Interface", () => ChangeSize(-0.1f)) { Shortcut = "Ctrl+-" },
                        new MenuItem("Normal Interface Size", () => ChangeSize(0f)) { Shortcut = "Ctrl+0" },
                        new MenuItem("Size to Fit the Screen", () => Context.Preferences.AutoSize = !Context.Preferences.AutoSize) { Checked = Context.Preferences.AutoSize },
                        MenuItem.Separator,
                        new MenuItem("Reset Layout", ResetLayout)
                    ];

                default:
                    return
                    [
                        new MenuItem("Controls", ShowControls),
                        new MenuItem("About Vertex", () => Dialog.Message("About Vertex", "Vertex Editor\nA game engine in C#, drawn with Vulkan or OpenGL."))
                    ];
            }
        }

        /// <summary>Makes everything bigger or smaller by <paramref name="by"/> (0 puts it back to normal), and says how big it is now.</summary>
        private void ChangeSize(float by)
        {
            Preferences preferences = Context.Preferences;
            preferences.UiScale = by == 0f ? 1f : Math.Clamp(MathF.Round((preferences.UiScale + by) * 10f) / 10f, 0.6f, 2f);
            Context.Notify($"Interface size {MathF.Round(InterfaceScale / DisplayScale * 100f)}%");
        }

        private void ResetLayout()
        {
            var defaults = new Preferences();
            Context.Preferences.OutlinerWidth = defaults.OutlinerWidth;
            Context.Preferences.InspectorWidth = defaults.InspectorWidth;
            Context.Preferences.AssetsHeight = defaults.AssetsHeight;
            _dock.Reset();
        }

        public static void OpenProjectDialog(EditorContext context)
        {
            FilePicker.Open("Open Project: a .vertexproject, or a game's .csproj", context.Project?.Directory,
                ("Vertex projects and games", "*.vertexproject;*.csproj"), context.OpenProject);
        }

        private void OpenSceneDialog()
        {
            ConfirmDiscard(() =>
            {
                string? folder = Context.Project != null ? Path.Combine(Context.Project.AssetsDirectory, "Scenes") : null;
                FilePicker.Open("Open Scene", folder, ("Scenes", "*.scene"), Context.OpenScene);
            });
        }

        /// <summary>Runs <paramref name="then"/>, first asking to save the scene if it has unsaved changes.</summary>
        private void ConfirmDiscard(Action then)
        {
            if (!Context.Dirty)
            {
                then();
                return;
            }

            Dialog.Ask("Unsaved changes", $"Save the changes to {Context.SceneName} first?",
                ("Save", () =>
                {
                    if (Context.SaveScene(saved: then)) then();
                }),
                ("Don't Save", then),
                ("Cancel", () => { }));
        }

        /// <summary>Closes the editor, asking about unsaved changes first.</summary>
        public void RequestExit()
        {
            ConfirmDiscard(() =>
            {
                Context.Preferences.Save();
                _window?.Close();
            });
        }

        // ---------------------------------------------------------------- Dialogs

        private void ShowProjectSettings()
        {
            if (Context.Project is not { } project) return;

            var history = new UndoHistory();
            var grid = new PropertyGrid(history, Context.Notify);
            Dialog.Show("Project Settings", new Vector2(560f, 380f), rect =>
            {
                Ui.Text(rect.TakeTop(20f), project.FilePath, Theme.TextFaint, size: Theme.SmallTextSize);
                grid.Draw(project.Settings, rect.X, rect.Y + 30f, rect.Width);
            }, closed: () =>
            {
                project.Save();
                Context.Notify("Project settings saved");
            });
        }

        private void ShowPreferences()
        {
            var grid = new PropertyGrid(new UndoHistory(), Context.Notify);
            Dialog.Show("Preferences", new Vector2(520f, 420f), rect =>
            {
                var scroll = ScrollView.Begin(Ui.Id("preferences.scroll"), rect);
                float end = grid.Draw(Context.Preferences, rect.X, scroll.Top, rect.Width - 10f);
                scroll.End(end - scroll.Top);
            }, closed: Context.Preferences.Save);
        }

        private void ShowSnapping()
        {
            var grid = new PropertyGrid(new UndoHistory(), Context.Notify);
            Dialog.Show("Snapping", new Vector2(460f, 230f), rect =>
            {
                grid.Draw(Context.Snap, rect.X, rect.Y, rect.Width);
                Ui.Text(rect.TakeBottom(20f), "Hold Ctrl while dragging a handle for the opposite.", Theme.TextFaint, size: Theme.SmallTextSize);
            }, closed: Context.Preferences.Save);
        }

        private static void ShowControls()
        {
            (string Keys, string What)[] rows =
            [
                ("Right mouse + WASD, Q, E", "Look around and fly (Shift for faster, wheel sets speed)"),
                ("Alt + left mouse", "Orbit round what you're looking at"),
                ("Middle mouse", "Pan"),
                ("Mouse wheel", "Zoom"),
                ("F", "Frame the selection"),
                ("W, E, R", "Move, rotate, scale"),
                ("Ctrl while dragging", "Snap (or not, if snapping's on)"),
                ("Ctrl+D, F2, Del", "Duplicate, rename, delete"),
                ("Ctrl+Z, Ctrl+Y", "Undo, redo"),
                ("Ctrl+P", "Play and stop"),
                ("Shift+F1", "While playing: give the mouse back to the editor")
            ];

            Dialog.Show("Controls", new Vector2(560f, 64f + rows.Length * 26f), rect =>
            {
                float y = rect.Y;
                foreach ((string keys, string what) in rows)
                {
                    Ui.Text(new Rect(rect.X, y, 190f, 24f), keys, Theme.AccentText);
                    Ui.Text(new Rect(rect.X + 200f, y, rect.Width - 200f, 24f), what, Theme.Text);
                    y += 26f;
                }
            });
        }

        // ---------------------------------------------------------------- Keys

        private void HandleShortcuts()
        {
            if (Context.Play.GameFocused || Dialog.IsOpen || Ui.KeyboardTaken || Menu.IsAnyOpen || _sceneView.Looking) return;

            bool ctrl = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
            GameObject? selected = Context.Selection.Object;

            if (ctrl)
            {
                if (InputSystem.GetKeyDown(Key.Z))
                {
                    if (shift) Context.Redo();
                    else Context.Undo();
                }

                if (InputSystem.GetKeyDown(Key.Y)) Context.Redo();
                if (InputSystem.GetKeyDown(Key.S)) Context.SaveScene(saveAs: shift);
                if (InputSystem.GetKeyDown(Key.O)) OpenSceneDialog();
                if (InputSystem.GetKeyDown(Key.N)) ConfirmDiscard(Context.NewScene);
                if (InputSystem.GetKeyDown(Key.D) && selected != null) Context.Duplicate(selected);
                if (InputSystem.GetKeyDown(Key.R)) Context.BuildGame();
                if (InputSystem.GetKeyDown(Key.Equal) || InputSystem.GetKeyDown(Key.KeypadAdd)) ChangeSize(+0.1f);
                if (InputSystem.GetKeyDown(Key.Minus) || InputSystem.GetKeyDown(Key.KeypadSubtract)) ChangeSize(-0.1f);
                if (InputSystem.GetKeyDown(Key.Number0) || InputSystem.GetKeyDown(Key.Keypad0)) ChangeSize(0f);
                if (shift && InputSystem.GetKeyDown(Key.C)) _dock.Toggle("Console");
                if (InputSystem.GetKeyDown(Key.P))
                {
                    if (shift && Context.Play.Playing) Context.Play.Paused = !Context.Play.Paused;
                    else TogglePlay();
                }

                return;
            }

            if (InputSystem.GetKeyDown(Key.W)) Context.Tool = TransformTool.Move;
            if (InputSystem.GetKeyDown(Key.E)) Context.Tool = TransformTool.Rotate;
            if (InputSystem.GetKeyDown(Key.R)) Context.Tool = TransformTool.Scale;
            if (InputSystem.GetKeyDown(Key.F)) _sceneView.FrameSelection();
            if (InputSystem.GetKeyDown(Key.F2) && selected != null) Context.Rename(selected);
            if ((InputSystem.GetKeyDown(Key.Delete) || InputSystem.GetKeyDown(Key.Backspace)) && selected != null) Context.Delete(selected);
            if (InputSystem.GetKeyDown(Key.Escape)) Context.Selection.Clear();
        }

        /// <summary>Short notes along the bottom ("Undo Move Cube"), which rise in and fade away.</summary>
        private void DrawNotes(Rect screen)
        {
            _notes.RemoveAll(note => Ui.Time - note.At > 2.6f);
            float y = screen.Bottom - 24f;
            for (int i = _notes.Count - 1; i >= 0; i--)
            {
                (string text, float at) = _notes[i];
                float age = Ui.Time - at;
                float shown = Ui.EaseOut(age / 0.2f) * (1f - Math.Clamp((age - 2.2f) / 0.4f, 0f, 1f));

                string fitted = Ui.Fit(text, MathF.Min(640f, screen.Width - 80f));
                float width = Ui.TextWidth(fitted) + 32f;
                var note = new Rect(screen.Centre.X - width * 0.5f, y - 32f + (1f - shown) * 10f, width, 32f);
                Ui.Opacity = shown;
                Ui.Shadow(note, 16f, 16f, 0.6f);
                Ui.Rect(note, Theme.Popup, 16f);
                Ui.Outline(note, Theme.BorderStrong, 16f);
                Ui.Text(note, fitted, Theme.Text, align: 0.5f);
                Ui.Opacity = 1f;
                y -= 38f * shown;
            }
        }
    }
}
