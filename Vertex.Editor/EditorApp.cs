using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Editor.Panels;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.Rendering.Vulkan;
using Vertex.UI;

namespace Vertex.Editor
{
    /// <summary>
    /// The whole editor window, drawn each frame: a bar along the top, the outliner on the left, the scene in the middle
    /// and the inspector on the right, with dividers between to resize them. Keyboard shortcuts are handled here.
    /// </summary>
    public sealed class EditorApp
    {
        private readonly Renderer? _renderer;
        private readonly OutlinerPanel _outliner;
        private readonly InspectorPanel _inspector;
        private readonly SceneViewPanel _sceneView;

        private float _leftWidth = 270f;
        private float _rightWidth = 340f;

        private readonly List<(string Text, float At)> _notes = new();
        private float _fps;

        private const float _topBarHeight = 42f;

        public EditorContext Context { get; } = new();
        public SceneViewPanel SceneView => _sceneView;

        /// <param name="renderer">For the top bar to say what's drawing; may be null in tests.</param>
        public EditorApp(Renderer? renderer = null)
        {
            _renderer = renderer;
            _outliner = new OutlinerPanel(Context);
            _inspector = new InspectorPanel(Context);
            _sceneView = new SceneViewPanel(Context);
            Context.Notified += text =>
            {
                _notes.Add((text, Ui.Time));
                if (_notes.Count > 3) _notes.RemoveAt(0);
            };
        }

        /// <summary>The pictures of the scene to draw this frame, before the window's 2D layer.</summary>
        public RenderView[] Views => _sceneView.View is RenderView view ? [view] : [];

        /// <summary>Draws the editor for this frame and does whatever was clicked or pressed. Canvas and input must be ready for the frame.</summary>
        public void Update(float deltaTime)
        {
            Ui.BeginFrame(deltaTime);
            if (deltaTime > 0f) _fps = _fps <= 0f ? 1f / deltaTime : _fps + (1f / deltaTime - _fps) * MathF.Min(deltaTime * 2f, 1f);

            // Changes made while the mouse is held (a drag) count as one; letting go ends it.
            if (!Ui.MouseHeld()) Context.History.EndMerge();

            // Edits show without the scene running: new objects come in, deleted ones go.
            Context.Scene.ApplyChanges();
            if (Context.Selection.Object is { } selected && !Context.InScene(selected)) Context.Selection.Clear();

            var screen = new Rect(0f, 0f, Canvas.Width, Canvas.Height);
            Ui.Rect(screen, Theme.Background);

            DrawTopBar(screen.TakeTop(_topBarHeight));

            Rect body = screen.CutTop(_topBarHeight).Shrink(Theme.Gap).CutTop(0f);
            float maxSide = MathF.Max(body.Width * 0.4f, 200f);
            _leftWidth = Math.Clamp(_leftWidth, 180f, maxSide);
            _rightWidth = Math.Clamp(_rightWidth, 240f, maxSide);

            Rect left = body.TakeLeft(_leftWidth);
            Rect right = body.TakeRight(_rightWidth);
            Rect middle = body.CutLeft(_leftWidth, Theme.Gap).CutRight(_rightWidth, Theme.Gap);

            _sceneView.Draw(middle);
            _outliner.Draw(left);
            _inspector.Draw(right);

            Widgets.Splitter(Ui.Id("split.left"), new Rect(left.Right - 2f, body.Y, Theme.Gap + 4f, body.Height), ref _leftWidth, 180f, maxSide);
            Widgets.Splitter(Ui.Id("split.right"), new Rect(right.X - Theme.Gap - 2f, body.Y, Theme.Gap + 4f, body.Height), ref _rightWidth, 240f, maxSide, inverted: true);

            HandleShortcuts();
            DrawNotes(screen);

            // A drag that locked the cursor (a number box) can end anywhere: make sure it comes back.
            if (InputSystem.CursorLocked && !Ui.MouseHeld() && !Ui.MouseHeld(MouseButton.Right) && !_sceneView.Looking) InputSystem.CursorLocked = false;

            Ui.EndFrame();
        }

        private void DrawTopBar(Rect bar)
        {
            Rect inner = bar.Shrink(Theme.Gap + 6f, 0f);

            // The mark and name.
            var mark = new Rect(inner.X, bar.Centre.Y - 11f, 22f, 22f);
            Ui.Rect(mark, Theme.Accent, 6f);
            Ui.Line(new Vector2(mark.X + 6f, mark.Y + 7f), new Vector2(mark.Centre.X, mark.Bottom - 6f), Color.White, 2f);
            Ui.Line(new Vector2(mark.Centre.X, mark.Bottom - 6f), new Vector2(mark.Right - 6f, mark.Y + 7f), Color.White, 2f);
            float x = mark.Right + 10f;
            x += Ui.Text("Vertex", new Vector2(x, bar.Y + (bar.Height - Ui.LineHeight(14f, true)) * 0.5f), Theme.Text, 14f, bold: true);
            Ui.Text(new Rect(x + 12f, bar.Y, 300f, bar.Height), Context.SceneName, Theme.TextDim);

            // Undo and redo, on the right.
            Rect redo = new(inner.Right - 30f, bar.Centre.Y - 15f, 30f, 30f);
            Rect undo = redo.Offset(new Vector2(-34f, 0f));
            string? nextUndo = Context.History.NextUndo, nextRedo = Context.History.NextRedo;
            Ui.Opacity = nextUndo != null ? 1f : 0.4f;
            if (Widgets.IconButton(Ui.Id("top.undo"), undo, Icons.Undo, nextUndo != null ? $"Undo {nextUndo} (Ctrl+Z)" : "Nothing to undo") && nextUndo != null) Context.Undo();
            Ui.Opacity = nextRedo != null ? 1f : 0.4f;
            if (Widgets.IconButton(Ui.Id("top.redo"), redo, Icons.Redo, nextRedo != null ? $"Redo {nextRedo} (Ctrl+Y)" : "Nothing to redo") && nextRedo != null) Context.Redo();
            Ui.Opacity = 1f;

            // What's drawing, and how fast.
            string device = _renderer?.Backend switch
            {
                VulkanRenderBackend vulkan => $"Vulkan · {vulkan.DeviceName}",
                { } backend => backend.Name,
                null => ""
            };

            string status = _fps > 0f ? $"{device}   {_fps:0} fps" : device;
            Ui.Text(new Rect(inner.X, bar.Y, undo.X - inner.X - 14f, bar.Height), status, Theme.TextFaint, align: 1f, size: Theme.SmallTextSize);
        }

        private void HandleShortcuts()
        {
            if (Ui.KeyboardTaken || Menu.IsAnyOpen || _sceneView.Looking) return;

            bool ctrl = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);

            if (ctrl && InputSystem.GetKeyDown(Key.Z))
            {
                if (shift) Context.Redo();
                else Context.Undo();
                return;
            }

            if (ctrl && InputSystem.GetKeyDown(Key.Y))
            {
                Context.Redo();
                return;
            }

            if (ctrl) return;

            if (InputSystem.GetKeyDown(Key.W)) Context.Tool = TransformTool.Move;
            if (InputSystem.GetKeyDown(Key.E)) Context.Tool = TransformTool.Rotate;
            if (InputSystem.GetKeyDown(Key.R)) Context.Tool = TransformTool.Scale;
            if (InputSystem.GetKeyDown(Key.F)) _sceneView.FrameSelection();

            if ((InputSystem.GetKeyDown(Key.Delete) || InputSystem.GetKeyDown(Key.Backspace)) && Context.Selection.Object is { } selected)
            {
                Context.Delete(selected);
            }

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

                float width = Ui.TextWidth(text) + 32f;
                var note = new Rect(screen.Centre.X - width * 0.5f, y - 32f + (1f - shown) * 10f, width, 32f);
                Ui.Opacity = shown;
                Ui.Shadow(note, 16f, 16f, 0.6f);
                Ui.Rect(note, Theme.Popup, 16f);
                Ui.Outline(note, Theme.BorderStrong, 16f);
                Ui.Text(note, text, Theme.Text, align: 0.5f);
                Ui.Opacity = 1f;
                y -= 38f * shown;
            }
        }
    }
}
