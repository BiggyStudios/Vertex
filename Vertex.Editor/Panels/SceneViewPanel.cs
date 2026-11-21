using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Graphs;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Editor.SceneEditing;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The scene, seen through the editor's own camera, in the middle of the window. Click to select, drag the handles to
    /// move, turn and scale; see <see cref="EditorCamera"/> for getting around. Lights and cameras, which have nothing to
    /// see, show as icons that can be clicked.
    /// </summary>
    public sealed class SceneViewPanel
    {
        private readonly EditorContext _context;
        private readonly TransformHandles _handles = new();
        private RenderTexture? _texture;
        private RenderTexture? _gameTexture;

        private bool _looking, _panning, _orbiting;
        private bool _pickPending;
        private bool _boxSelecting;

        // What the selection box touches, so lights' and cameras' icons can light up too.
        private readonly HashSet<GameObject> _inBox = new();
        private Vector2 _pressedAt;
        private float _speedChangedAt = -10f;

        // Which tab shows: the editor's view of the scene, or the game's own camera.
        private bool _gameTab;
        private bool _wasPlaying;
        private bool _gameTabBeforePlay;

        // Graphs open in tabs of their own after Scene and Game, and the one showing (null: the scene or the game).
        private readonly List<GraphDocument> _graphs = new();
        private GraphDocument? _graph;
        private GraphDocument? _graphBeforePlay;
        private readonly GraphView _graphView;

        // Where the game's picture was last frame, in UI units, to tell clicks in it from clicks elsewhere.
        private Rect _gameRect;
        private bool _gameRectShown;

        private const float _tabsHeight = 34f;

        public EditorCamera Camera { get; }

        /// <summary>Whether the right mouse is held to look and fly, so WASD belong to the camera, not shortcuts.</summary>
        public bool Looking => _looking;

        /// <summary>Whether the camera has the mouse (looking, panning or orbiting), with the cursor hidden and held.</summary>
        public bool MovingCamera => _looking || _panning || _orbiting;

        /// <summary>The pictures for the renderer to draw this frame: the scene view's, or the game's while its tab shows.</summary>
        public RenderView[] Views
        {
            get
            {
                if (_graph != null) return _graphView.Views(_graph);
                if (_gameTab) return _gameTexture != null && Vertex.Rendering.Camera.Main is { } main ? [new RenderView(main, _gameTexture)] : [];
                Vertex.Rendering.Camera? effects = _context.Preferences.ShowEffects ? Vertex.Rendering.Camera.Main : null;
                return _texture != null ? [new RenderView(Camera.Camera, _texture, _context.Preferences.ShowGrid, Camera.Is2D, effects, Overlay: _context.NavigationOverlay)] : [];
            }
        }

        public SceneViewPanel(EditorContext context)
        {
            _context = context;

            // The camera lives in a scene of the editor's own, which is never run or drawn by itself.
            Camera = new EditorCamera(new Scene());
            Camera.FlySpeed = context.Preferences.FlySpeed;
            _uiEditing = new UIEditing(context);
            CreateMenu.MadeUI += ShowGame;
            _graphView = new GraphView(context);
            context.GraphOpenRequested += OpenGraph;
            context.PrefabOpened += _ =>
            {
                _graph = null;
                _gameTab = false;
            };
        }

        /// <summary>
        /// Decides, before anything reads input this frame, whether the game or the editor has it: Shift+F1 gives it back
        /// to the editor, as does clicking outside the game's picture; clicking the picture gives it to the game.
        /// </summary>
        public void RouteInput()
        {
            PlayMode play = _context.Play;
            if (!play.Playing)
            {
                play.GameFocused = false;
                return;
            }

            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
            if (play.GameFocused && shift && InputSystem.GetKeyDown(Key.F1))
            {
                play.GameFocused = false;
                InputSystem.CursorLocked = false;
                InputSystem.Consume(Key.F1);
                _context.Notify("The editor has the mouse: click the game to give it back");
                return;
            }

            bool pressed = InputSystem.GetMouseButtonDown(MouseButton.Left) || InputSystem.GetMouseButtonDown(MouseButton.Right);
            bool inGame = _gameTab && _graph == null && _gameRectShown && _gameRect.Contains(Canvas.MousePosition);
            if (pressed && !InputSystem.CursorLocked)
            {
                if (play.GameFocused && !inGame) play.GameFocused = false;
                else if (!play.GameFocused && inGame && !Dialog.IsOpen && !Menu.IsAnyOpen) play.GameFocused = true;
            }
        }

        public void Draw(Rect rect)
        {
            PlayMode play = _context.Play;

            // Playing shows the game; stopping goes back to whichever tab was showing before (the game, while arranging UI).
            if (play.Playing && !_wasPlaying)
            {
                _gameTabBeforePlay = _gameTab;
                _graphBeforePlay = _graph;
                _graph = null;
                _gameTab = true;
            }

            if (!play.Playing && _wasPlaying)
            {
                _gameTab = _gameTabBeforePlay;
                if (_graphBeforePlay != null && _graphs.Contains(_graphBeforePlay)) _graph = _graphBeforePlay;
            }
            _wasPlaying = play.Playing;

            FollowPrefab();
            Ui.Rect(rect, Theme.Panel, Theme.PanelRadius);
            DrawTabs(rect.TakeTop(_tabsHeight));
            Rect viewport = rect.CutTop(_tabsHeight).Shrink(1f);
            Ui.Rect(viewport, Theme.Background);

            if (_graph != null) _graphView.Draw(viewport, _graph);
            else if (_gameTab) DrawGame(viewport);
            else DrawScene(viewport);

            // The game runs every frame while playing, whichever tab shows; with the scene showing, its UI goes nowhere.
            if (play.Playing && (_graph != null || !_gameTab)) StepGame(viewport, visible: false);

            Color outline = play.Playing ? Theme.Accent.WithAlpha(0.75f) : Theme.Border;
            Ui.Outline(rect, outline, Theme.PanelRadius, play.Playing ? 1.5f : 1f);

            if (_context.Busy is { } busy) DrawBusy(viewport, busy);
        }

        private void DrawTabs(Rect tabs)
        {
            float x = tabs.X + 8f;
            foreach ((string name, bool game) in new[] { ("Scene", false), ("Game", true) })
            {
                float width = Ui.TextWidth(name) + 40f;
                var tab = new Rect(x, tabs.Y + 5f, width, tabs.Height - 10f);
                int id = Ui.Id("viewTab." + name);
                Interaction interaction = Ui.Interact(id, tab);
                if (interaction.Clicked)
                {
                    _context.ShowPrefab(null);
                    _gameTab = game;
                    _graph = null;
                }

                bool on = _graph == null && _context.Stage == null && _gameTab == game;
                Ui.Rect(tab, Ui.Smooth(id, on ? Theme.ControlHover : interaction.Hovered ? Theme.Control : Theme.Control.WithAlpha(0f), 22f), 6f);
                if (game) Icons.Play(new Vector2(tab.X + 15f, tab.Centre.Y), 13f, on ? Theme.AccentText : Theme.TextFaint);
                else Icons.Cube(new Vector2(tab.X + 15f, tab.Centre.Y), 14f, on ? Theme.AccentText : Theme.TextFaint);
                Ui.Text(tab.CutLeft(28f), name, on ? Theme.Text : Theme.TextDim);
                x += width + 4f;
            }

            // Open graphs, then open prefabs: click to show, the cross (or a middle click) to close.
            GraphDocument? closing = null;
            foreach (GraphDocument doc in _graphs)
            {
                (bool clicked, bool close) = DocumentTab(ref x, tabs, doc, doc.Title, doc.Path, _graph == doc, Icons.Graph, Theme.Hex(0x5FC9B5));
                if (clicked)
                {
                    _context.ShowPrefab(null);
                    _graph = doc;
                }

                if (close) closing = doc;
            }

            if (closing != null) CloseGraph(closing);

            PrefabStage? closingPrefab = null;
            foreach (PrefabStage stage in _context.OpenPrefabs)
            {
                (bool clicked, bool close) = DocumentTab(ref x, tabs, stage, stage.Title, stage.Path, _graph == null && _context.Stage == stage, Icons.Prefab, Theme.Hex(0x6FA8FF));
                if (clicked)
                {
                    _graph = null;
                    _gameTab = false;
                    _context.ShowPrefab(stage);
                }

                if (close) closingPrefab = stage;
            }

            if (closingPrefab != null) _context.ClosePrefab(closingPrefab);

            if (_context.Play.Playing)
            {
                PlayMode play = _context.Play;
                string hint = play.Paused ? "Paused" : play.GameFocused ? "Playing  ·  Shift+F1 frees the mouse" : "Playing  ·  click the game to control it";
                Ui.Text(tabs.Shrink(12f, 0f), hint, play.Paused ? Theme.AccentText : Theme.TextFaint, align: 1f, size: Theme.SmallTextSize);
            }
        }

        /// <summary>A tab for an open file (a graph, a prefab): whether it was clicked to show, and whether to close it.</summary>
        private static (bool Clicked, bool Close) DocumentTab(ref float x, Rect tabs, object doc, string title, string path, bool on, Icon icon, Color color)
        {
            string name = Ui.Fit(title, 160f);
            float width = Ui.TextWidth(name) + 60f;
            var tab = new Rect(x, tabs.Y + 5f, width, tabs.Height - 10f);
            int id = Ui.Id(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(doc), "documentTab");
            Interaction interaction = Ui.Interact(id, tab);
            Rect close = tab.TakeRight(24f).Shrink(3f);
            bool overClose = close.Contains(Ui.Mouse);
            bool clicked = interaction.Clicked && !overClose;
            bool closing = interaction.Clicked && overClose || interaction.Hovered && Ui.MousePressed(MouseButton.Middle);

            Ui.Rect(tab, Ui.Smooth(id, on ? Theme.ControlHover : interaction.Hovered ? Theme.Control : Theme.Control.WithAlpha(0f), 22f), 6f);
            icon(new Vector2(tab.X + 15f, tab.Centre.Y), 14f, on ? color : Theme.TextFaint);
            Ui.Text(tab.CutLeft(28f).CutRight(24f), name, on ? Theme.Text : Theme.TextDim);
            if (on || interaction.Hovered)
            {
                if (overClose) Ui.Rect(close, Theme.Hover.WithAlpha(2f), 4f);
                Icons.Close(close.Centre, 10f, overClose ? Theme.Text : Theme.TextFaint);
            }

            Ui.Tooltip(id, path);
            x += width + 4f;
            return (clicked, closing);
        }

        // Which prefab's tab the camera was last placed for (null: the scene's), and where it was for the scene.
        private PrefabStage? _cameraFor;
        private EditorCamera.Placement? _sceneCamera;

        /// <summary>
        /// Keeps the camera where it was for each tab: switching to a prefab remembers where it was for the scene and goes
        /// to where it was for the prefab (framing it, the first time); switching back puts it back.
        /// </summary>
        private void FollowPrefab()
        {
            PrefabStage? stage = _context.Stage;
            if (stage == _cameraFor) return;

            if (_cameraFor != null) _cameraFor.Camera = Camera.Remember();
            else _sceneCamera = Camera.Remember();
            _cameraFor = stage;

            if (stage != null)
            {
                _graph = null;
                _gameTab = false;
                if (stage.Camera is { } placed) Camera.Restore(placed);
                else
                {
                    Frame([stage.Root]);
                    Camera.Settle();
                }
            }
            else if (_sceneCamera is { } back)
            {
                Camera.Restore(back);
            }
        }

        // ---------------------------------------------------------------- The game

        private void DrawGame(Rect viewport)
        {
            if (!Resize(ref _gameTexture, viewport)) return;

            _gameRect = viewport;
            _gameRectShown = true;

            if (Vertex.Rendering.Camera.Main == null)
            {
                Ui.Text(new Rect(viewport.X, viewport.Centre.Y - 20f, viewport.Width, 20f), "No camera to show the game through", Theme.TextDim, align: 0.5f);
                Ui.Text(new Rect(viewport.X, viewport.Centre.Y + 2f, viewport.Width, 18f), "Add a Camera component to an object", Theme.TextFaint,
                    align: 0.5f, size: Theme.SmallTextSize);
            }
            else
            {
                Canvas.DrawImage(_gameTexture, viewport.Position, viewport.Size, Vector2.Zero, Vector2.One, Color.White);
            }

            // Playing: clicking the picture gives the game the mouse (see RouteInput), and the editor's controls leave it
            // alone. Otherwise the scene's UI is shown, to arrange.
            if (_context.Play.Playing)
            {
                Ui.Block(Ui.Id("gameView"), viewport);
                StepGame(viewport, visible: true);
            }
            else
            {
                if (Ui.MousePressed()) _gameFocused = viewport.Contains(Ui.Mouse);
                _uiEditing.Draw(viewport, _gameFocused);
            }
        }

        private readonly UIEditing _uiEditing;
        private bool _gameFocused;

        /// <summary>Shows the game's picture, where UI is arranged: after a piece of UI is made.</summary>
        public void ShowGame()
        {
            _context.ShowPrefab(null);
            _gameTab = true;
            _graph = null;
        }

        /// <summary>Whether the game's picture is showing, rather than the scene view.</summary>
        public bool ShowingGame => _gameTab && _graph == null;

        /// <summary>Opens a graph file in a tab of its own (or shows the tab it's open in already).</summary>
        public void OpenGraph(string path)
        {
            GraphDocument? doc = _graphs.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
            if (doc == null)
            {
                try
                {
                    doc = new GraphDocument(Assets.LoadGraph(path), path);
                }
                catch (Exception exception)
                {
                    _context.Notify($"Couldn't open {Path.GetFileName(path)}: {exception.Message}");
                    return;
                }

                _graphs.Add(doc);
            }

            _context.ShowPrefab(null);
            _graph = doc;

            // Shown while the game runs: the editor has the mouse, to edit it.
            if (_context.Play.GameFocused)
            {
                _context.Play.GameFocused = false;
                InputSystem.CursorLocked = false;
            }
        }

        public void CloseGraph(GraphDocument doc)
        {
            int index = _graphs.IndexOf(doc);
            if (index < 0) return;
            _graphs.RemoveAt(index);
            if (_graph == doc) _graph = _graphs.Count > 0 ? _graphs[Math.Min(index, _graphs.Count - 1)] : null;
        }

        /// <summary>The graph showing in its tab, if one is.</summary>
        public GraphDocument? ShowingGraph => _graph;

        public IReadOnlyList<GraphDocument> OpenGraphs => _graphs;

        /// <summary>Runs the game's frame, with its 2D layer drawn into the game's picture as if that were the whole screen.</summary>
        private void StepGame(Rect viewport, bool visible)
        {
            PlayMode play = _context.Play;
            Vector2 origin = viewport.Position * Canvas.Scale;
            Canvas.BeginView(origin, Math.Max((int)(viewport.Width * Canvas.Scale), 1), Math.Max((int)(viewport.Height * Canvas.Scale), 1));
            if (!visible) Canvas.PushClip(new Vector2(-10f), Vector2.Zero);

            bool editorMuted = InputSystem.Muted;
            InputSystem.Muted = !play.GameFocused;
            play.Step();
            InputSystem.Muted = editorMuted;

            Canvas.EndView();
        }

        private static bool Resize(ref RenderTexture? texture, Rect viewport)
        {
            int width = (int)MathF.Round(viewport.Width * Canvas.Scale), height = (int)MathF.Round(viewport.Height * Canvas.Scale);
            if (width <= 0 || height <= 0) return false;

            if (texture == null) texture = new RenderTexture(width, height);
            else texture.Resize(width, height);
            return true;
        }

        private static void DrawBusy(Rect viewport, string busy)
        {
            var chip = new Rect(viewport.Centre.X - 130f, viewport.Y + 16f, 260f, 34f);
            Ui.Shadow(chip, 17f, 16f, 0.6f);
            Ui.Rect(chip, Theme.Popup, 17f);
            Ui.Outline(chip, Theme.BorderStrong, 17f);

            // A spinner: an arc going round.
            var centre = new Vector2(chip.X + 22f, chip.Centre.Y);
            Canvas.DrawArc(centre, 7f, Ui.Time * 360f, 270f, 2f, Ui.Fade(Theme.AccentText));
            Ui.Text(chip.CutLeft(38f), busy, Theme.Text);
        }

        // ---------------------------------------------------------------- The scene

        private void DrawScene(Rect viewport)
        {
            _gameRectShown = false;
            Resize(ref _texture, viewport);
            Camera.Smoothing = _context.Preferences.CameraSmoothing;

            int id = Ui.Id("sceneView");
            Interaction interaction = Ui.Interact(id, viewport);
            bool hovered = interaction.Hovered || Ui.HotId == id;

            HandleCamera(id, viewport, hovered);
            Camera.Update(Ui.DeltaTime);
            _context.SpawnPoint = Round(Camera.Pivot, 0.5f);

            // This frame's picture: the renderer draws the views before the 2D layer.
            if (_texture != null) Canvas.DrawImage(_texture, viewport.Position, viewport.Size, Vector2.Zero, Vector2.One, Color.White);

            var view = new ViewProjection(Camera, viewport);
            Ui.PushClip(viewport);

            GameObject? selected = _context.Selection.Object;
            if (selected != null && !_context.InScene(selected)) selected = null;

            // UI isn't in the 3D world: it's arranged on the game's screen.
            if (selected != null && (selected.GetComponent<UIRect>() != null || selected.GetComponent<UICanvas>() != null))
            {
                selected = null;
                const string text = "UI is arranged on the game's screen: open the Game tab";
                float width = Ui.TextWidth(text) + 28f;
                var chip = new Rect(viewport.Centre.X - width * 0.5f, viewport.Bottom - 44f, width, 28f);
                int chipId = Ui.Id("sceneView.uiHint");
                Interaction chipPress = Ui.Interact(chipId, chip);
                Ui.Rect(chip, chipPress.Hovered ? Theme.AccentHover : Theme.Accent, 14f);
                Ui.Text(chip, text, Color.White, align: 0.5f);
                if (chipPress.Clicked) ShowGame();
            }

            // Every collider in the scene, faintly, while they're asked for; the selected objects' show anyway (below).
            if (_context.Preferences.ShowColliders)
            {
                foreach (GameObject gameObject in _context.Scene.GameObjects)
                {
                    if (gameObject.ActiveInHierarchy) ColliderShapes.Draw(view, gameObject, 0.4f);
                }
            }

            // The others selected (not UI) are outlined too, and move with the main one's handles. What's under one of
            // them moves with it, so isn't moved again.
            List<Transform> others = [];
            if (_context.Selection.Count > 1 && selected != null)
            {
                foreach (GameObject other in _context.Selection.Tops())
                {
                    if (other == selected || other.GetComponent<UIRect>() != null || other.GetComponent<UICanvas>() != null) continue;
                    DrawSelectionOutline(view, other);
                    if (!IsUnder(selected, other)) others.Add(other.Transform);
                }
            }

            if (selected != null) DrawSelectionOutline(view, selected);

            GameObject? clickedIcon = DrawObjectIcons(view, hovered && !_looking);

            bool mouseFree = hovered && !_looking && !_panning && !_orbiting && !Alt && !DragDrop.Dragging;
            bool handleTookPress = selected != null && _handles.Draw(selected.Transform, _context.Tool, view, _context.History, _context.Snap, mouseFree, others);

            // A click (pressed and let go without dragging) selects what's under it.
            if (mouseFree && Ui.MousePressed() && !handleTookPress)
            {
                _pickPending = true;
                _pressedAt = Ui.Mouse;
            }

            // Dragged instead: a box, and everything it touches is selected when it's let go (shown outlined meanwhile).
            if (_pickPending && Ui.MouseHeld() && Vector2.Distance(_pressedAt, Ui.Mouse) >= 5f) _boxSelecting = true;
            if (_boxSelecting)
            {
                Ui.ActiveId = id;
                Rect box = BoxFrom(_pressedAt, Ui.Mouse, viewport);
                List<GameObject> inBox = InBox(view, box);
                foreach (GameObject gameObject in inBox) DrawSelectionOutline(view, gameObject);
                _inBox.Clear();
                _inBox.UnionWith(inBox);

                Ui.Rect(box, Theme.Accent.WithAlpha(0.14f));
                Ui.Outline(box, Theme.Accent.WithAlpha(0.9f), 0f, 1f);

                if (Ui.MouseReleased()) SelectBox(inBox);
            }

            if (_pickPending && Ui.MouseReleased())
            {
                _pickPending = false;
                if (!_boxSelecting && Vector2.Distance(_pressedAt, Ui.Mouse) < 5f) Pick(view, clickedIcon);
            }

            if (!Ui.MouseHeld())
            {
                _boxSelecting = false;
                _inBox.Clear();
                if (!Ui.MouseReleased()) _pickPending = false;
            }

            HandleDrop(view, viewport);
            Ui.PopClip();

            DrawToolbar(viewport);
            if (_context.Stage is { } stage) DrawPrefabBar(viewport, stage);
            PreviewEffects(viewport);
            DrawAxes(viewport);
            DrawSpeed(viewport);
        }

        // ---------------------------------------------------------------- Previewing particle effects

        // Particle effects on what's selected play in the scene view while they're edited, so they can be seen as
        // they're tuned. One that finishes (a burst) starts again a moment later.
        private readonly List<IParticleEffect> _previewing = new();
        private readonly Dictionary<IParticleEffect, float> _finishedFor = new();
        private bool _previewPaused;

        /// <summary>The particle effects playing in the scene view now: those on the selected objects and under them.</summary>
        public IReadOnlyList<IParticleEffect> PreviewedEffects => _previewing;

        private void PreviewEffects(Rect viewport)
        {
            var now = new List<IParticleEffect>();
            if (!_context.Play.Playing)
            {
                foreach (GameObject selected in _context.Selection.Objects)
                {
                    if (_context.InScene(selected)) EffectsUnder(selected, now);
                }
            }

            foreach (IParticleEffect gone in _previewing.Where(e => !now.Contains(e)).ToList())
            {
                if (!_context.Play.Playing) gone.Rewind();
                _previewing.Remove(gone);
                _finishedFor.Remove(gone);
            }

            foreach (IParticleEffect emitter in now)
            {
                if (_previewing.Contains(emitter)) continue;
                _previewing.Add(emitter);
                emitter.Rewind();
            }

            if (_previewing.Count == 0) return;

            if (!_previewPaused)
            {
                float deltaTime = MathF.Min(Ui.DeltaTime, 0.1f);
                foreach (IParticleEffect emitter in _previewing)
                {
                    emitter.Simulate(deltaTime);

                    bool finished = emitter.Finished;
                    float waited = finished ? _finishedFor.GetValueOrDefault(emitter) + deltaTime : 0f;
                    _finishedFor[emitter] = waited;
                    if (waited > 0.8f && emitter.PlaysByItself)
                    {
                        emitter.Rewind();
                        _finishedFor[emitter] = 0f;
                    }
                }
            }

            DrawEffectBar(viewport);
        }

        private static void EffectsUnder(GameObject gameObject, List<IParticleEffect> into)
        {
            if (!gameObject.ActiveInHierarchy) return;
            foreach (Component component in gameObject.Components)
            {
                if (component is IParticleEffect effect && component.Enabled && !into.Contains(effect)) into.Add(effect);
            }

            foreach (Transform child in gameObject.Transform.Children) EffectsUnder(child.GameObject, into);
        }

        /// <summary>In the corner while effects play: how many particles there are, how far through it is, and the controls.</summary>
        private void DrawEffectBar(Rect viewport)
        {
            int alive = _previewing.Sum(p => p.Alive ?? 0);
            bool counted = _previewing.All(p => p.Alive != null);
            IParticleEffect first = _previewing[0];
            string label = _previewing.Count == 1 ? "Particle effect" : $"{_previewing.Count} particle effects";
            string stats = (counted ? $"{alive} alive  ·  " : "") + $"{first.PlayTime:0.0} s";

            float width = 22f + Ui.TextWidth(label, bold: true) + 12f + Ui.TextWidth(stats, Theme.SmallTextSize) + 14f + 2f * 30f + 6f;
            var bar = new Rect(viewport.Right - width - 12f, viewport.Bottom - 48f, width, 36f);
            Ui.Shadow(bar, 8f, 14f, 0.5f);
            Ui.Rect(bar, Theme.Panel.WithAlpha(0.94f), 8f);
            Ui.Outline(bar, Theme.Border, 8f);
            Ui.Block(Ui.Id("sceneView.effectBar"), bar);

            float x = bar.X + 12f;
            float labelWidth = Ui.TextWidth(label, bold: true);
            Ui.Text(new Rect(x, bar.Y, labelWidth + 4f, bar.Height), label, Theme.Text, bold: true);
            x += labelWidth + 12f;
            Ui.Text(new Rect(x, bar.Y, 200f, bar.Height), stats, Theme.TextFaint, size: Theme.SmallTextSize);

            var restart = new Rect(bar.Right - 64f, bar.Y + 4f, 28f, 28f);
            var pause = new Rect(bar.Right - 34f, bar.Y + 4f, 28f, 28f);
            if (Widgets.IconButton(Ui.Id("sceneView.effectRestart"), restart, Icons.Rotate, "Play from the start"))
            {
                foreach (IParticleEffect emitter in _previewing) emitter.Rewind();
                _previewPaused = false;
            }

            if (Widgets.IconButton(Ui.Id("sceneView.effectPause"), pause, _previewPaused ? Icons.Play : Icons.Pause, _previewPaused ? "Play" : "Pause"))
            {
                _previewPaused = !_previewPaused;
            }
        }

        /// <summary>Things dragged in from the assets: a model goes where it's dropped, a texture onto the object it's dropped on.</summary>
        private void HandleDrop(ViewProjection view, Rect viewport)
        {
            if (!DragDrop.Dragging || DragDrop.Payload is not AssetEntry asset || !viewport.Contains(Ui.Mouse)) return;

            (Vector3 origin, Vector3 direction) = view.Ray(Ui.Mouse);
            var hit = Picking.Pick(_context.Scene, origin, direction);

            if (asset.Kind == AssetKind.Texture && hit is (GameObject target, _) && target.GetComponent<MeshRenderer>() is { } renderer)
            {
                DrawSelectionOutline(view, target);
                if (DragDrop.Dropped)
                {
                    Texture texture = Assets.LoadTexture(asset.Path);
                    SetValue.Apply(_context.History, "Set Texture", renderer.Material, typeof(Material).GetProperty(nameof(Material.Texture))!, texture);
                    _context.Selection.Select(target);
                }
            }
            else if (asset.Kind == AssetKind.Material && hit is (GameObject surface, _) && surface.GetComponent<MeshRenderer>() is { } painted)
            {
                DrawSelectionOutline(view, surface);
                if (DragDrop.Dropped)
                {
                    SetValue.Apply(_context.History, "Set Material", painted, typeof(MeshRenderer).GetProperty(nameof(MeshRenderer.Material))!, Assets.LoadMaterial(asset.Path));
                    _context.Selection.Select(surface);
                }
            }
            else if (asset.Kind is AssetKind.Model or AssetKind.Prefab && DragDrop.Dropped)
            {
                // On whatever's under the mouse, or on the ground, or in front of the camera.
                Vector3 at = hit is (_, float distance) ? origin + direction * distance
                    : TransformHandles.PlaneHit(Vector3.Zero, Vector3.UnitY, origin, direction) is Vector3 ground && Vector3.Distance(ground, origin) < 500f ? ground
                    : origin + direction * 10f;
                if (asset.Kind == AssetKind.Prefab) _context.AddPrefab(asset.Path, at);
                else AssetsPanel.AddModel(_context, asset.Path, at);
            }
            else if (asset.Kind == AssetKind.Scene && DragDrop.Dropped)
            {
                _context.OpenScene(asset.Path);
            }
        }

        private static bool Alt => InputSystem.GetKey(Key.AltLeft) || InputSystem.GetKey(Key.AltRight);

        private static Vector3 Round(Vector3 v, float step) =>
            new(MathF.Round(v.X / step) * step, MathF.Round(v.Y / step) * step, MathF.Round(v.Z / step) * step);

        // ---------------------------------------------------------------- Getting around

        private void HandleCamera(int id, Rect viewport, bool hovered)
        {
            const float lookSpeed = 0.16f;

            // Right mouse: look around and fly.
            if (hovered && Ui.MousePressed(MouseButton.Right) && !Menu.IsAnyOpen)
            {
                _looking = true;
                Ui.ActiveId = id;
                InputSystem.CursorLocked = true;
            }

            if (_looking)
            {
                if (!Ui.MouseHeld(MouseButton.Right))
                {
                    _looking = false;
                    if (!_panning && !_orbiting) InputSystem.CursorLocked = false;
                    Camera.EndLook();
                }
                else
                {
                    Ui.ActiveId = id;

                    // In 2D there's nothing to look round at: the right mouse drags the view about instead.
                    if (Camera.Is2D) Camera.Pan(Ui.MouseDelta, viewport.Height);
                    else if (Alt) Camera.Zoom(-Ui.MouseDelta.Y * 0.02f);
                    else Camera.Look(-Ui.MouseDelta.X * lookSpeed, -Ui.MouseDelta.Y * lookSpeed);

                    var direction = new Vector3(
                        (Held(Key.D) ? 1f : 0f) - (Held(Key.A) ? 1f : 0f),
                        (Held(Key.E) ? 1f : 0f) - (Held(Key.Q) ? 1f : 0f),
                        (Held(Key.S) ? 1f : 0f) - (Held(Key.W) ? 1f : 0f));
                    Camera.Fly(direction, Ui.DeltaTime, Held(Key.ShiftLeft) || Held(Key.ShiftRight));

                    if (InputSystem.ScrollDelta != 0f)
                    {
                        Camera.FlySpeed = Math.Clamp(Camera.FlySpeed * MathF.Pow(1.25f, InputSystem.ScrollDelta), 0.5f, 500f);
                        _speedChangedAt = Ui.Time;
                    }
                }
            }

            // Middle mouse: pan. Like looking, the cursor is hidden and held (it comes back where it was): the drag can
            // go on past the screen's edge, and the system isn't busy moving a pointer about, which costs frames.
            if (hovered && Ui.MousePressed(MouseButton.Middle) && !_looking && !_orbiting)
            {
                _panning = true;
                InputSystem.CursorLocked = true;
            }

            if (_panning)
            {
                if (!Ui.MouseHeld(MouseButton.Middle))
                {
                    _panning = false;
                    if (!_looking) InputSystem.CursorLocked = false;
                }
                else
                {
                    Ui.ActiveId = id;
                    Camera.Pan(Ui.MouseDelta, viewport.Height);
                }
            }

            // Alt and left mouse: orbit, the same way.
            if (hovered && Alt && Ui.MousePressed() && !_looking && !_panning)
            {
                _orbiting = true;
                InputSystem.CursorLocked = true;
            }

            if (_orbiting)
            {
                if (!Ui.MouseHeld())
                {
                    _orbiting = false;
                    if (!_looking) InputSystem.CursorLocked = false;
                }
                else
                {
                    Ui.ActiveId = id;
                    Camera.Orbit(-Ui.MouseDelta.X * 0.3f, -Ui.MouseDelta.Y * 0.3f);
                }
            }

            // The wheel: zoom.
            if (hovered && !_looking && InputSystem.ScrollDelta != 0f) Camera.Zoom(InputSystem.ScrollDelta);
        }

        private static bool Held(Key key) => InputSystem.GetKey(key);

        /// <summary>Flies to frame the selected object: everything it and its children draw.</summary>
        public void FrameSelection()
        {
            if (_context.Selection.Object is not { } selected || !_context.InScene(selected)) return;
            Frame(_context.Selection.Objects);
        }

        /// <summary>Points the camera at these objects, together: what they draw, or where they are if they draw nothing.</summary>
        private void Frame(IEnumerable<GameObject> objects)
        {
            (Vector3 Min, Vector3 Max)? bounds = null;
            foreach (GameObject gameObject in objects)
            {
                Vector3 at = gameObject.Transform.WorldPosition;
                (Vector3 low, Vector3 high) = Bounds(gameObject) ?? (at, at);
                bounds = bounds is (Vector3 min, Vector3 max) ? (Vector3.Min(min, low), Vector3.Max(max, high)) : (low, high);
            }

            if (bounds is not (Vector3 lowest, Vector3 highest)) return;
            float radius = Vector3.Distance(lowest, highest) * 0.5f;
            Camera.Frame((lowest + highest) * 0.5f, radius > 0.01f ? radius : 1f);
        }

        private static (Vector3 Min, Vector3 Max)? Bounds(GameObject gameObject)
        {
            (Vector3, Vector3)? bounds = null;
            if (gameObject.GetComponent<MeshRenderer>() is { Mesh: { } mesh })
            {
                Culling.WorldBounds(mesh, gameObject.Transform.WorldMatrix, out Vector3 centre, out Vector3 extents);
                bounds = (centre - extents, centre + extents);
            }

            foreach (Transform child in gameObject.Transform.Children)
            {
                if (Bounds(child.GameObject) is not (Vector3 childMin, Vector3 childMax)) continue;
                bounds = bounds is (Vector3 min, Vector3 max) ? (Vector3.Min(min, childMin), Vector3.Max(max, childMax)) : (childMin, childMax);
            }

            return bounds;
        }

        // ---------------------------------------------------------------- Selecting

        private void Pick(in ViewProjection view, GameObject? clickedIcon)
        {
            // Ctrl or Shift: add to what's selected, or take out what already is.
            bool adding = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight) ||
                          InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);

            GameObject? hit = clickedIcon;
            if (hit == null)
            {
                (Vector3 origin, Vector3 direction) = view.Ray(Ui.Mouse);
                if (Picking.Pick(_context.Scene, origin, direction) is (GameObject picked, _)) hit = picked;
            }

            if (hit == null)
            {
                if (!adding) _context.Selection.Clear();
            }
            else if (adding)
            {
                _context.Selection.Toggle(hit);
            }
            else
            {
                _context.Selection.Select(hit);
            }
        }

        /// <summary>The box between two corners, kept inside the view.</summary>
        private static Rect BoxFrom(Vector2 a, Vector2 b, Rect viewport)
        {
            Vector2 min = Vector2.Clamp(Vector2.Min(a, b), viewport.Position, viewport.Position + viewport.Size);
            Vector2 max = Vector2.Clamp(Vector2.Max(a, b), viewport.Position, viewport.Position + viewport.Size);
            return new Rect(min.X, min.Y, max.X - min.X, max.Y - min.Y);
        }

        /// <summary>
        /// The objects a selection box touches, in the scene's order: what they draw overlapping it (like a desktop's
        /// icons), and lights and cameras by their icons. UI isn't in the 3D world, so never is.
        /// </summary>
        private List<GameObject> InBox(in ViewProjection view, Rect box)
        {
            var found = new List<GameObject>();
            if (box.Width < 1f || box.Height < 1f) return found;

            foreach (GameObject gameObject in _context.Scene.GameObjects)
            {
                if (!gameObject.ActiveInHierarchy || gameObject.GetComponent<UIRect>() != null || gameObject.GetComponent<UICanvas>() != null) continue;

                // Something drawn all round the box (the floor it's dragged over) is the background, like a desktop's: left out.
                bool touched = false;
                if (gameObject.GetComponent<MeshRenderer>() is { Mesh: { } mesh } && ScreenBounds(view, mesh, gameObject.Transform.WorldMatrix) is { } drawn)
                {
                    bool around = drawn.X <= box.X && drawn.Y <= box.Y && drawn.Right >= box.Right && drawn.Bottom >= box.Bottom;
                    touched = drawn.Overlaps(box) && !around;
                }

                if (!touched && (gameObject.GetComponent<Light>() != null || gameObject.GetComponent<Camera>() != null) &&
                    view.ToScreen(gameObject.Transform.WorldPosition) is Vector2 icon)
                {
                    touched = box.Grow(12f).Contains(icon);
                }

                if (touched) found.Add(gameObject);
            }

            return found;
        }

        /// <summary>
        /// The rectangle on screen a mesh's bounds cover; null if they're all behind the camera. Bounds reaching behind
        /// the camera (a floor stretching away under it) could cover any of the view, so count as covering all of it.
        /// </summary>
        private static Rect? ScreenBounds(in ViewProjection view, Mesh mesh, Matrix4x4 world)
        {
            Culling.WorldBounds(mesh, world, out Vector3 centre, out Vector3 extents);
            Vector2 min = new(float.MaxValue), max = new(float.MinValue);
            int inFront = 0;
            for (int i = 0; i < 8; i++)
            {
                var corner = centre + new Vector3(i % 2 == 0 ? -extents.X : extents.X, i / 2 % 2 == 0 ? -extents.Y : extents.Y, i / 4 == 0 ? -extents.Z : extents.Z);
                if (view.ToScreen(corner) is not Vector2 at) continue;
                min = Vector2.Min(min, at);
                max = Vector2.Max(max, at);
                inFront++;
            }

            if (inFront == 0) return null;
            if (inFront < 8) return view.Viewport.Grow(100000f);
            return new Rect(min.X, min.Y, max.X - min.X, max.Y - min.Y);
        }

        /// <summary>Selects what a box touched; with Ctrl or Shift, as well as what's already selected (which stays the main one).</summary>
        private void SelectBox(List<GameObject> inBox)
        {
            bool adding = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight) ||
                          InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
            if (!adding)
            {
                _context.Selection.Select(inBox);
                return;
            }

            GameObject? main = _context.Selection.Object;
            List<GameObject> all = _context.Selection.Objects.Concat(inBox).Distinct().ToList();
            if (main != null)
            {
                all.Remove(main);
                all.Add(main);
            }

            _context.Selection.Select(all);
        }

        /// <summary>Whether <paramref name="gameObject"/> is somewhere under <paramref name="ancestor"/>.</summary>
        private static bool IsUnder(GameObject gameObject, GameObject ancestor)
        {
            for (Transform? parent = gameObject.Transform.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.GameObject == ancestor) return true;
            }

            return false;
        }

        private static void DrawSelectionOutline(ViewProjection view, GameObject selected)
        {
            void Box(GameObject gameObject, Color color, bool top)
            {
                if (gameObject.GetComponent<MeshRenderer>() is { Mesh: { } mesh })
                {
                    Edges(view, gameObject.Transform.WorldMatrix, mesh.BoundsCentre, mesh.BoundsExtents, color);
                }

                // Where it's solid, in green: a character's body is often on a child, so theirs show too, fainter.
                if (gameObject.ActiveInHierarchy) ColliderShapes.Draw(view, gameObject, top ? 1f : 0.55f);

                foreach (Transform child in gameObject.Transform.Children) Box(child.GameObject, color.WithAlpha(0.4f), false);
            }

            float pulse = 0.75f + 0.25f * MathF.Sin(Ui.Time * 3f);
            Box(selected, Theme.Accent.WithAlpha(pulse), true);

            // What has no mesh to outline: a fog volume's box, and how far a light reaches.
            if (selected.GetComponent<FogVolume>() != null)
            {
                Edges(view, selected.Transform.WorldMatrix, Vector3.Zero, new Vector3(0.5f), Theme.Hex(0x9FB8D8).WithAlpha(pulse));
            }

            if (selected.GetComponent<Light>() is { Type: not LightType.Directional } light) DrawReach(view, light, Color.Lerp(light.Color, Color.White, 0.4f).WithAlpha(0.7f));
            if (selected.GetComponent<SpringArm>() is { Enabled: true } arm) DrawArm(view, arm, Theme.Hex(0x7AA7FF));
        }

        /// <summary>
        /// A spring arm: a small ball where it turns about, a line out to where it holds the camera when nothing's in the way,
        /// and a ball there as thick as the arm checks for walls. It only moves the camera while the game runs, so this shows
        /// where the camera will be.
        /// </summary>
        private static void DrawArm(ViewProjection view, SpringArm arm, Color color)
        {
            (Vector3 pivot, Vector3 end) = arm.Resting();
            var lines = new List<(Vector3, Vector3)>();
            ColliderShapes.Sphere(lines, pivot, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 0.08f, view.CameraPosition);
            if (arm.Thickness > 0f) ColliderShapes.Sphere(lines, end, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, arm.Thickness, view.CameraPosition);
            foreach ((Vector3 from, Vector3 to) in lines) view.Line(from, to, color.WithAlpha(0.7f), 1.25f);
            view.Line(pivot, end, color, 1.5f);
        }

        /// <summary>The twelve edges of a box, <paramref name="centre"/> and half-size <paramref name="extents"/>, placed by <paramref name="world"/>.</summary>
        private static void Edges(in ViewProjection view, Matrix4x4 world, Vector3 centre, Vector3 extents, Color color)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var local = new Vector3(i % 2 == 0 ? -extents.X : extents.X, i / 2 % 2 == 0 ? -extents.Y : extents.Y, i / 4 == 0 ? -extents.Z : extents.Z);
                corners[i] = Vector3.Transform(centre + local, world);
            }

            // Corners that differ in one axis.
            for (int a = 0; a < 8; a++)
            {
                foreach (int bit in (ReadOnlySpan<int>)[1, 2, 4])
                {
                    if ((a & bit) == 0) view.Line(corners[a], corners[a | bit], color, 1.5f);
                }
            }
        }

        /// <summary>
        /// How far a light reaches: a spot light's cone, out to its range, or for a point light three rings round it
        /// at its range.
        /// </summary>
        private static void DrawReach(ViewProjection view, Light light, Color color)
        {
            Transform t = light.Transform;
            Vector3 at = t.WorldPosition;
            const int segments = 48;

            void Ring(Vector3 centre, Vector3 a, Vector3 b, float radius)
            {
                for (int i = 0; i < segments; i++)
                {
                    float from = i * MathF.Tau / segments, to = (i + 1) * MathF.Tau / segments;
                    view.Line(centre + (a * MathF.Cos(from) + b * MathF.Sin(from)) * radius, centre + (a * MathF.Cos(to) + b * MathF.Sin(to)) * radius, color, 1f);
                }
            }

            if (light.Type == LightType.Spot)
            {
                float half = Math.Clamp(light.SpotAngle, 1f, 179f) * 0.5f * MathF.PI / 180f;
                Vector3 end = at + t.Forward * MathF.Cos(half) * light.Range;
                float radius = MathF.Sin(half) * light.Range;
                Ring(end, t.Right, t.Up, radius);
                foreach (Vector3 side in (ReadOnlySpan<Vector3>)[t.Right, -t.Right, t.Up, -t.Up]) view.Line(at, end + side * radius, color, 1f);
                return;
            }

            Ring(at, Vector3.UnitX, Vector3.UnitZ, light.Range);
            Ring(at, Vector3.UnitX, Vector3.UnitY, light.Range);
            Ring(at, Vector3.UnitZ, Vector3.UnitY, light.Range);
        }

        /// <summary>Icons for lights, cameras and fog volumes. Returns the one under the mouse, if any.</summary>
        private GameObject? DrawObjectIcons(in ViewProjection view, bool mouseFree)
        {
            GameObject? under = null;
            foreach (GameObject gameObject in _context.Scene.GameObjects)
            {
                if (!gameObject.ActiveInHierarchy) continue;
                Light? light = gameObject.GetComponent<Light>();
                Camera? camera = gameObject.GetComponent<Camera>();
                FogVolume? fog = light == null && camera == null ? gameObject.GetComponent<FogVolume>() : null;
                if (light == null && camera == null && fog == null) continue;

                Vector3 position = gameObject.Transform.WorldPosition;
                if (view.ToScreen(position) is not Vector2 at) continue;

                bool selected = _context.Selection.Contains(gameObject) || _inBox.Contains(gameObject);
                bool hovered = mouseFree && Vector2.Distance(at, Ui.Mouse) < 13f;
                if (hovered) under = gameObject;

                Color tint = selected ? Theme.AccentText : light != null ? Color.Lerp(light.Color, Color.White, 0.4f) : Theme.Text;

                // Which way a sun shines.
                if (light is { Type: LightType.Directional })
                {
                    float metres = view.MetresPerUnit(position);
                    view.Line(position, position + gameObject.Transform.Forward * 46f * metres, tint.WithAlpha(0.7f), 1.5f);
                }

                if (camera != null && selected) DrawFrustum(view, camera);

                Ui.Circle(at, hovered || selected ? 13f : 12f, Theme.Popup.WithAlpha(0.9f));
                Vertex.UI.Canvas.DrawCircleOutline(at, hovered || selected ? 13f : 12f, Ui.Fade(selected ? Theme.Accent : Theme.BorderStrong), 1.5f);
                if (light != null) Icons.Light(at, 18f, tint);
                else if (fog != null) Icons.Fog(at, 18f, tint);
                else Icons.Camera(at, 18f, tint);
            }

            return under;
        }

        private static void DrawFrustum(in ViewProjection view, Camera camera)
        {
            Transform t = camera.Transform;
            float depth = MathF.Min(camera.FarPlane, 4f);
            bool flat = camera.Projection == CameraProjection.Orthographic;
            float halfHeight = flat ? camera.OrthographicSize : MathF.Tan(camera.FieldOfView * MathF.PI / 360f) * depth;
            float halfWidth = halfHeight * 16f / 9f;
            Vector3 eye = t.WorldPosition;

            Vector3[] Corners(Vector3 centre) =>
            [
                centre + t.Right * halfWidth + t.Up * halfHeight, centre - t.Right * halfWidth + t.Up * halfHeight,
                centre - t.Right * halfWidth - t.Up * halfHeight, centre + t.Right * halfWidth - t.Up * halfHeight
            ];

            // A pyramid from the eye for perspective; for an orthographic camera, a box as wide at the eye as further on.
            Vector3[] corners = Corners(eye + t.Forward * depth);
            Vector3[] back = flat ? Corners(eye) : [eye, eye, eye, eye];
            Color color = Color.White.WithAlpha(0.55f);
            for (int i = 0; i < 4; i++)
            {
                view.Line(back[i], corners[i], color, 1f);
                view.Line(corners[i], corners[(i + 1) % 4], color, 1f);
                if (flat) view.Line(back[i], back[(i + 1) % 4], color, 1f);
            }
        }

        // ---------------------------------------------------------------- Overlays

        /// <summary>Along the bottom while a prefab's open: which it is, that it's saved as it's changed, and the way back.</summary>
        private void DrawPrefabBar(Rect viewport, PrefabStage stage)
        {
            const string note = "saved as you change it";
            const string back = "Back to Scene";
            float nameWidth = Ui.TextWidth(stage.Title, bold: true);
            float width = 34f + nameWidth + 10f + Ui.TextWidth(note, Theme.SmallTextSize) + 16f + Ui.TextWidth(back) + 26f + 6f;
            var bar = new Rect(viewport.Centre.X - width * 0.5f, viewport.Bottom - 50f, width, 36f);
            Ui.Shadow(bar, 8f, 14f, 0.5f);
            Ui.Rect(bar, Theme.Panel.WithAlpha(0.94f), 18f);
            Ui.Outline(bar, Theme.Hex(0x6FA8FF).WithAlpha(0.45f), 18f);
            Ui.Block(Ui.Id("sceneView.prefabBar"), bar);

            Icons.Prefab(new Vector2(bar.X + 20f, bar.Centre.Y), 14f, Theme.Hex(0x6FA8FF));
            float x = bar.X + 34f;
            Ui.Text(new Rect(x, bar.Y, nameWidth + 4f, bar.Height), stage.Title, Theme.Text, bold: true);
            x += nameWidth + 10f;
            Ui.Text(new Rect(x, bar.Y, 200f, bar.Height), note, Theme.TextFaint, size: Theme.SmallTextSize);

            var button = new Rect(bar.Right - Ui.TextWidth(back) - 26f - 6f, bar.Y + 5f, Ui.TextWidth(back) + 26f, 26f);
            if (Widgets.Button(Ui.Id("sceneView.backToScene"), button, back, ButtonStyle.Normal)) _context.ShowPrefab(null);
        }

        private void DrawToolbar(Rect viewport)
        {
            // Move, turn and scale, world or local axes, snapping and the grid, floating in the corner.
            // Wide enough for: the three tools, World/Local, the magnet and its arrow, grid, effects and colliders, and 2D.
            var bar = new Rect(viewport.X + 10f, viewport.Y + 10f, 4f + 3f * 30f + 8f + 58f + 9f + 28f + 18f + 9f + 3f * 30f - 2f + 9f + 32f + 4f, 36f);
            Ui.Shadow(bar, 8f, 14f, 0.5f);
            Ui.Rect(bar, Theme.Panel.WithAlpha(0.92f), 8f);
            Ui.Outline(bar, Theme.Border, 8f);
            Ui.Block(Ui.Id("sceneView.toolbar"), bar);

            (TransformTool Tool, Icon Icon, string Tip)[] tools =
            [
                (TransformTool.Move, Icons.Move, "Move (W)"),
                (TransformTool.Rotate, Icons.Rotate, "Rotate (E)"),
                (TransformTool.Scale, Icons.Scale, "Scale (R)")
            ];

            int selectedIndex = Array.FindIndex(tools, tool => tool.Tool == _context.Tool);
            float slide = Ui.Smooth(Ui.Id("sceneView.toolHighlight"), selectedIndex * 30f, 22f);
            Ui.Rect(new Rect(bar.X + 4f + slide, bar.Y + 4f, 28f, 28f), Theme.Accent.WithAlpha(0.9f), 6f);

            for (int i = 0; i < tools.Length; i++)
            {
                var button = new Rect(bar.X + 4f + i * 30f, bar.Y + 4f, 28f, 28f);
                bool on = i == selectedIndex;
                if (Widgets.IconButton(Ui.Id("sceneView.tool" + i), button, tools[i].Icon, tools[i].Tip, color: on ? Color.White : null)) _context.Tool = tools[i].Tool;
            }

            Ui.Rect(new Rect(bar.X + 4f + 3f * 30f + 3f, bar.Y + 9f, 1f, 18f), Theme.Border);

            var space = new Rect(bar.X + 4f + 3f * 30f + 8f, bar.Y + 4f, 58f, 28f);
            bool local = _handles.Space == HandleSpace.Local;
            string tip = _context.Tool == TransformTool.Scale ? "Scaling always uses the object's own axes" : local
                ? "Handles follow the object's own axes: click for the world's"
                : "Handles follow the world's axes: click for the object's own";
            if (Widgets.Button(Ui.Id("sceneView.space"), space, local ? "Local" : "World", ButtonStyle.Subtle, tip))
            {
                _handles.Space = local ? HandleSpace.World : HandleSpace.Local;
            }

            // Snapping: the magnet turns it on and off, the arrow beside it opens its steps.
            Ui.Rect(new Rect(space.Right + 4f, bar.Y + 9f, 1f, 18f), Theme.Border);
            SnapSettings snap = _context.Snap;
            var magnet = new Rect(space.Right + 9f, bar.Y + 4f, 28f, 28f);
            string snapTip = snap.Enabled
                ? $"Snapping on: {snap.Move:0.##} m, {snap.Rotate:0.#}°, {snap.Scale:0.##} (Ctrl while dragging for free movement)"
                : "Snapping off (Ctrl while dragging snaps)";
            if (Widgets.IconButton(Ui.Id("sceneView.snap"), magnet, Icons.Magnet, snapTip, selected: snap.Enabled))
            {
                snap.Enabled = !snap.Enabled;
                _context.Preferences.Save();
            }

            int snapMenu = Ui.Id("sceneView.snapSettings");
            var arrow = new Rect(magnet.Right, bar.Y + 4f, 18f, 28f);
            if (Widgets.IconButton(snapMenu, arrow, (c, s, col) => Icons.Chevron(c, s, col, 90f), "Snapping steps")) Popup.Open(snapMenu, arrow);
            Popup.Show(snapMenu, new Vector2(320f, 136f), inner => _snapGrid.Draw(snap, inner.X, inner.Y, inner.Width));

            Ui.Rect(new Rect(arrow.Right + 4f, bar.Y + 9f, 1f, 18f), Theme.Border);
            var grid = new Rect(arrow.Right + 9f, bar.Y + 4f, 28f, 28f);
            bool showGrid = _context.Preferences.ShowGrid;
            if (Widgets.IconButton(Ui.Id("sceneView.grid"), grid, Icons.Grid, showGrid ? "Hide the grid" : "Show the grid", selected: showGrid))
            {
                _context.Preferences.ShowGrid = !showGrid;
            }

            // The game camera's post effects (a dithered look, a colour grade), so the scene looks as the game will.
            var effects = new Rect(grid.Right + 2f, bar.Y + 4f, 28f, 28f);
            bool showEffects = _context.Preferences.ShowEffects;
            if (Widgets.IconButton(Ui.Id("sceneView.effects"), effects, Icons.ScreenEffect,
                showEffects ? "Hide the game camera's post effects" : "Show the game camera's post effects, as the game will look", selected: showEffects))
            {
                _context.Preferences.ShowEffects = !showEffects;
            }

            // Every collider in the scene, not just the selected objects'.
            var colliders = new Rect(effects.Right + 2f, bar.Y + 4f, 28f, 28f);
            bool showColliders = _context.Preferences.ShowColliders;
            if (Widgets.IconButton(Ui.Id("sceneView.colliders"), colliders, Icons.BoxCollider,
                showColliders ? "Show only the selected objects' colliders" : "Show every collider, not just the selected objects'", selected: showColliders))
            {
                _context.Preferences.ShowColliders = !showColliders;
            }

            // 2D: looking flat on at the X-Y plane, without perspective, as a 2D game's camera does.
            Ui.Rect(new Rect(colliders.Right + 4f, bar.Y + 9f, 1f, 18f), Theme.Border);
            var flat = new Rect(colliders.Right + 9f, bar.Y + 4f, 32f, 28f);
            bool is2D = Camera.Is2D;
            if (Widgets.IconButton(Ui.Id("sceneView.2d"), flat, (c, s, col) => Ui.Text(new Rect(c.X - 16f, c.Y - 10f, 32f, 20f), "2D", col, 0.5f, Theme.SmallTextSize, bold: true),
                is2D ? "Back to 3D" : "2D: look flat on at the X-Y plane, for 2D games", selected: is2D))
            {
                Camera.Set2D(!is2D);
            }
        }

        private PropertyGrid _snapGrid => _snapGridValue ??= new PropertyGrid(new UndoHistory(), _context.Notify);
        private PropertyGrid? _snapGridValue;

        private void DrawAxes(Rect viewport)
        {
            // In 2D the view is always along -Z: there's nothing to turn to.
            if (Camera.Is2D) return;

            // Which way the world's axes point from here. Click one to look along it.
            var centre = new Vector2(viewport.Right - 52f, viewport.Y + 52f);
            const float radius = 30f;
            int id = Ui.Id("sceneView.axes");
            var area = new Rect(centre.X - 44f, centre.Y - 44f, 88f, 88f);
            Interaction areaInteraction = Ui.Interact(id, area);
            float glow = Ui.Smooth(id, areaInteraction.Hovered ? 1f : 0f, 14f);
            if (glow > 0.01f) Ui.Circle(centre, 42f, Theme.Panel.WithAlpha(0.6f * glow));

            Transform camera = Camera.Camera.Transform;
            Vector3 right = camera.Right, up = camera.Up, forward = camera.Forward;

            var ends = new List<(Vector3 Axis, Color Color, string Label, float Depth)>();
            foreach ((Vector3 axis, Color color, string label) in new[] { (Vector3.UnitX, Theme.AxisX, "X"), (Vector3.UnitY, Theme.AxisY, "Y"), (Vector3.UnitZ, Theme.AxisZ, "Z") })
            {
                ends.Add((axis, color, label, Vector3.Dot(axis, forward)));
                ends.Add((-axis, color, "", Vector3.Dot(-axis, forward)));
            }

            // Furthest first, so nearer ends are drawn over them.
            ends.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            Vector3? clicked = null;
            foreach ((Vector3 axis, Color color, string label, float _) in ends)
            {
                Vector2 at = centre + new Vector2(Vector3.Dot(axis, right), -Vector3.Dot(axis, up)) * radius;
                bool positive = label.Length > 0;
                float size = positive ? 9f : 6.5f;
                Interaction interaction = Ui.Interact(Ui.Id(id, label + axis), new Rect(at.X - size, at.Y - size, size * 2f, size * 2f));
                if (interaction.Clicked) clicked = axis;

                Color shown = interaction.Hovered ? Color.Lerp(color, Color.White, 0.35f) : color;
                if (positive)
                {
                    Ui.Line(centre, at, shown, 2f);
                    Ui.Circle(at, size, shown);
                    Ui.Text(new Rect(at.X - size, at.Y - size, size * 2f, size * 2f), label, Theme.Background, align: 0.5f, size: 10.5f, bold: true);
                }
                else
                {
                    Ui.Circle(at, size, shown.WithAlpha(0.35f));
                    Vertex.UI.Canvas.DrawCircleOutline(at, size, Ui.Fade(shown.WithAlpha(0.8f)), 1.3f);
                }

                Ui.Tooltip(Ui.Id(id, label + axis), positive ? $"Look along -{label}" : null);
            }

            // Looking from that axis's end, back towards the middle.
            if (clicked is Vector3 from) Camera.LookAlong(-from);
        }

        private void DrawSpeed(Rect viewport)
        {
            float shown = 1f - Math.Clamp((Ui.Time - _speedChangedAt - 0.9f) / 0.35f, 0f, 1f);
            if (shown <= 0f) return;

            string text = $"Fly speed  {Camera.FlySpeed:0.#} m/s";
            float width = Ui.TextWidth(text) + 24f;
            var chip = new Rect(viewport.Centre.X - width * 0.5f, viewport.Bottom - 46f, width, 28f);
            Ui.Opacity = shown;
            Ui.Rect(chip, Theme.Panel.WithAlpha(0.9f), 14f);
            Ui.Text(chip, text, Theme.Text, align: 0.5f);
            Ui.Opacity = 1f;
        }
    }
}
