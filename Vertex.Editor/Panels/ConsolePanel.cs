using System.Numerics;
using System.Text.RegularExpressions;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Input;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// What's been logged: the engine's messages, the game's, build errors and errors while playing. Errors, warnings
    /// and messages can each be hidden, and the search narrows it down. Click one to see all of it (with its stack trace),
    /// double-click one that points at code to open the code there. It keeps to the newest unless scrolled up.
    /// </summary>
    public sealed partial class ConsolePanel : IDockPanel
    {
        private readonly EditorContext _context;
        private string _search = "";
        private bool _showInfo = true, _showWarnings = true, _showErrors = true;
        private EditorLog.Item? _selected;

        // Keeping to the newest: whether the list was scrolled to the end, and what had been seen of the log then.
        private bool _atEnd = true;
        private int _shownVersion = -1;
        private float _contentHeight;

        // Errors that came in since the Console was last looked at, for the badge on its tab.
        private int _seenErrors, _seenWarnings;

        private const float _rowHeight = 24f;

        public ConsolePanel(EditorContext context)
        {
            _context = context;
        }

        public string Title => "Console";
        public Icon Icon => Icons.Console;

        public (string Text, Color Color)? Badge
        {
            get
            {
                EditorLog log = _context.ConsoleLog;
                if (log.Errors > _seenErrors) return ((log.Errors - _seenErrors).ToString(), Theme.Danger);
                if (log.Warnings > _seenWarnings) return ((log.Warnings - _seenWarnings).ToString(), Theme.Warning);
                return null;
            }
        }

        public void Draw(Rect rect)
        {
            EditorLog log = _context.ConsoleLog;
            Rect content = PanelFrame.Draw(rect, "Console", Icons.Console);
            (_seenErrors, _seenWarnings) = (log.Errors, log.Warnings);
            // Its controls go along the header, right of the tabs, or on a row of their own when that's too narrow.
            Rect bar = new(PanelFrame.TitleRight, rect.Y, rect.Right - PanelFrame.TitleRight, PanelFrame.HeaderHeight);
            if (bar.Width < 400f)
            {
                bar = content.TakeTop(34f).Shrink(4f, 0f);
                content = content.CutTop(32f);
            }

            DrawHeader(bar, log);

            List<EditorLog.Item> shown = log.Items.Where(Shows).ToList();
            if (_selected != null && !shown.Contains(_selected)) _selected = null;

            // The list, and under it what the selected message says in full.
            Rect list = content.Shrink(4f, 4f);
            Rect details = default;
            if (_selected != null)
            {
                float detailsHeight = MathF.Min(MathF.Max(list.Height * 0.42f, 80f), list.Height - 60f);
                details = list.TakeBottom(detailsHeight);
                list = list.CutBottom(detailsHeight, 4f);
            }

            DrawList(list, shown, log);
            if (_selected != null) DrawDetails(details, _selected);
        }

        private bool Shows(EditorLog.Item item)
        {
            bool level = item.Level switch { LogLevel.Error => _showErrors, LogLevel.Warning => _showWarnings, _ => _showInfo };
            if (!level) return false;
            string search = _search.Trim();
            return search.Length == 0 || item.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   item.Details?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
        }

        private void DrawHeader(Rect bar, EditorLog log)
        {
            // From the right: options, clear, search, then a switch for each kind with how many there are.
            var options = new Rect(bar.Right - Theme.Padding - 26f, bar.Centre.Y - 13f, 26f, 26f);
            int optionsId = Ui.Id("console.options");
            if (Widgets.IconButton(optionsId, options, Icons.More, "Options"))
            {
                Preferences preferences = _context.Preferences;
                Menu.Open(optionsId, new Vector2(options.X, options.Bottom + 4f),
                [
                    new MenuItem("Clear When Play Starts", () => preferences.ClearConsoleOnPlay = !preferences.ClearConsoleOnPlay) { Checked = preferences.ClearConsoleOnPlay },
                    new MenuItem("Copy Everything", () => InputSystem.ClipboardText = string.Join("\n", log.Items.Select(Text)))
                ], flipBy: options.Height + 8f);
            }

            Rect clear = options.Offset(new Vector2(-28f, 0f));
            if (Widgets.IconButton(Ui.Id("console.clear"), clear, Icons.Trash, "Clear the Console"))
            {
                log.Clear();
                _selected = null;
            }

            float searchWidth = Math.Clamp(bar.Width - 260f, 90f, 200f);
            var search = new Rect(clear.X - 8f - searchWidth, bar.Centre.Y - 13f, searchWidth, 26f);
            TextInput.Draw(Ui.Id("console.search"), search, ref _search, "Search", live: true, icon: Icons.Search);

            float x = search.X - 8f;
            x = Toggle("console.errors", x, bar, ref _showErrors, log.Errors, LogLevel.Error, "errors");
            x = Toggle("console.warnings", x, bar, ref _showWarnings, log.Warnings, LogLevel.Warning, "warnings");
            Toggle("console.info", x, bar, ref _showInfo, log.Infos, LogLevel.Info, "messages");
        }

        /// <summary>A switch showing or hiding one kind of message, with how many there are. Drawn leftwards from <paramref name="right"/>.</summary>
        private static float Toggle(string name, float right, Rect bar, ref bool on, int count, LogLevel level, string what)
        {
            string text = count > 999 ? "999+" : count.ToString();
            float width = Ui.TextWidth(text, Theme.SmallTextSize) + 34f;
            var button = new Rect(right - width, bar.Centre.Y - 12f, width, 24f);
            int id = Ui.Id(name);
            Interaction interaction = Ui.Interact(id, button);
            if (interaction.Clicked) on = !on;

            Ui.Rect(button, Ui.Smooth(id, on ? Theme.ControlHover : interaction.Hovered ? Theme.Control : Theme.Control.WithAlpha(0f), 22f), 12f);
            LevelIcon(level, new Vector2(button.X + 13f, button.Centre.Y), on ? 1f : 0.4f);
            Ui.Text(button.CutLeft(24f), text, on ? Theme.Text : Theme.TextFaint, size: Theme.SmallTextSize);
            Ui.Tooltip(id, on ? $"Hide {what}" : $"Show {what}");
            return button.X - 4f;
        }

        /// <summary>A grey dot for a message, a yellow triangle for a warning, a red circle for an error.</summary>
        public static void LevelIcon(LogLevel level, Vector2 centre, float alpha = 1f)
        {
            switch (level)
            {
                case LogLevel.Error:
                    Ui.Circle(centre, 6.5f, Theme.Danger.WithAlpha(alpha));
                    Icons.Close(centre, 9f, Color.White.WithAlpha(alpha));
                    break;
                case LogLevel.Warning:
                    Vertex.UI.Canvas.DrawTriangle(centre + new Vector2(0f, -7f), centre + new Vector2(7.5f, 6f), centre + new Vector2(-7.5f, 6f),
                        Ui.Fade(Theme.Warning.WithAlpha(alpha)));
                    Ui.Rect(new Rect(centre.X - 0.8f, centre.Y - 2.5f, 1.6f, 4.5f), Color.Black.WithAlpha(0.8f * alpha), 0.8f);
                    Ui.Circle(centre + new Vector2(0f, 3.8f), 0.9f, Color.Black.WithAlpha(0.8f * alpha));
                    break;
                default:
                    Vertex.UI.Canvas.DrawCircleOutline(centre, 6f, Ui.Fade(Theme.TextDim.WithAlpha(alpha)), 1.3f);
                    Ui.Rect(new Rect(centre.X - 0.7f, centre.Y - 1f, 1.4f, 4.5f), Theme.TextDim.WithAlpha(alpha), 0.7f);
                    Ui.Circle(centre + new Vector2(0f, -3f), 0.9f, Theme.TextDim.WithAlpha(alpha));
                    break;
            }
        }

        private void DrawList(Rect list, List<EditorLog.Item> shown, EditorLog log)
        {
            int scrollId = Ui.Id("console.scroll");
            var scroll = ScrollView.Begin(scrollId, list);

            // Keeps to the newest while it was already at the end: something new scrolls it down; scrolling up stops that.
            if (log.Version != _shownVersion && _atEnd && shown.Count > 0)
            {
                ScrollView.Reveal(scrollId, (shown.Count - 1) * _rowHeight, shown.Count * _rowHeight + 4f, list.Height);
            }

            _shownVersion = log.Version;
            _atEnd = scroll.Offset >= _contentHeight - list.Height - 6f;

            int first = Math.Max((int)((list.Y - scroll.Top) / _rowHeight), 0);
            int last = Math.Min((int)((list.Bottom - scroll.Top) / _rowHeight) + 1, shown.Count - 1);
            for (int i = first; i <= last; i++)
            {
                EditorLog.Item item = shown[i];
                var row = new Rect(list.X, scroll.Top + i * _rowHeight, list.Width - 8f, _rowHeight);
                int id = Ui.Id(scrollId, i);
                Interaction interaction = Ui.Interact(id, row, rightClick: true);
                bool selected = item == _selected;

                if (selected) Ui.Rect(row, Theme.Accent.WithAlpha(0.85f), 4f);
                else if (interaction.Hovered) Ui.Rect(row, Theme.Hover.WithAlpha(2f), 4f);
                else if (i % 2 == 1) Ui.Rect(row, Color.White.WithAlpha(0.015f));

                if (interaction.Pressed) _selected = selected ? null : item;
                if (interaction.DoubleClicked)
                {
                    _selected = item;
                    OpenSource(item);
                }

                if (interaction.RightClicked)
                {
                    _selected = item;
                    Menu.Open(Ui.Id(id, "menu"), Ui.Mouse,
                    [
                        new MenuItem("Copy", () => InputSystem.ClipboardText = Text(item)),
                        new MenuItem("Open the Code There", () => OpenSource(item)) { Enabled = Source(item) != null }
                    ]);
                }

                LevelIcon(item.Level, new Vector2(row.X + 14f, row.Centre.Y));
                Color faint = selected ? Color.White.WithAlpha(0.75f) : Theme.TextFaint;
                Ui.Text(new Rect(row.X + 28f, row.Y, 64f, row.Height), item.Time.ToString("HH:mm:ss"), faint, size: Theme.SmallTextSize);

                float countWidth = 0f;
                if (item.Count > 1)
                {
                    string count = item.Count > 999 ? "999+" : item.Count.ToString();
                    countWidth = Ui.TextWidth(count, Theme.SmallTextSize) + 14f;
                    var chip = new Rect(row.Right - countWidth - 6f, row.Y + 4f, countWidth, row.Height - 8f);
                    Ui.Rect(chip, selected ? Color.White.WithAlpha(0.25f) : Theme.Control, 8f);
                    Ui.Text(chip, count, selected ? Color.White : Theme.TextDim, align: 0.5f, size: Theme.SmallTextSize);
                    countWidth += 10f;
                }

                string firstLine = item.Message.Split('\n')[0];
                Color text = selected ? Color.White : item.Level == LogLevel.Error ? Theme.Hex(0xF29B95) : item.Level == LogLevel.Warning ? Theme.Hex(0xEBCB7E) : Theme.Text;
                Ui.Text(new Rect(row.X + 92f, row.Y, row.Width - 98f - countWidth, row.Height), firstLine, text);
            }

            if (shown.Count == 0)
            {
                string empty = log.Items.Count == 0 ? "Nothing logged yet. Messages, warnings and errors from the engine and the game show here." : "Nothing matches";
                Ui.Text(new Rect(list.X, list.Y + 14f, list.Width, 20f), empty, Theme.TextFaint, align: 0.5f);
            }

            _contentHeight = shown.Count * _rowHeight + 4f;
            scroll.End(_contentHeight);
        }

        private void DrawDetails(Rect rect, EditorLog.Item item)
        {
            Ui.Rect(rect, Theme.Field, 6f);
            Rect buttons = rect.TakeTop(30f).Shrink(6f, 3f);
            if (Widgets.Button(Ui.Id("console.copy"), buttons.TakeRight(70f), "Copy", ButtonStyle.Subtle)) InputSystem.ClipboardText = Text(item);
            if (Source(item) is { } source &&
                Widgets.Button(Ui.Id("console.open"), buttons.CutRight(76f).TakeRight(MathF.Min(220f, buttons.Width * 0.5f)), $"Open {Path.GetFileName(source.File)}:{source.Line}", ButtonStyle.Subtle))
            {
                OpenSource(item);
            }

            LevelIcon(item.Level, new Vector2(buttons.X + 8f, buttons.Centre.Y));
            Ui.Text(new Rect(buttons.X + 22f, buttons.Y, buttons.Width * 0.4f, buttons.Height), $"{item.Level} at {item.Time:HH:mm:ss}" + (item.Count > 1 ? $", {item.Count} times" : ""),
                Theme.TextDim, size: Theme.SmallTextSize);

            Rect body = rect.CutTop(30f).Shrink(10f, 4f);
            int scrollId = Ui.Id("console.details");
            var scroll = ScrollView.Begin(scrollId, body);
            float y = scroll.Top;
            foreach (string line in Ui.Wrap(Text(item), body.Width - 14f, Theme.SmallTextSize))
            {
                Ui.Text(new Rect(body.X, y, body.Width - 14f, 17f), line, Theme.Text, size: Theme.SmallTextSize);
                y += 17f;
            }

            scroll.End(y - scroll.Top + 6f);
        }

        private static string Text(EditorLog.Item item) => item.Details != null ? $"{item.Message}\n{item.Details}" : item.Message;

        // ---------------------------------------------------------------- Opening the code a message points at

        // A C# file and a line: "C:\Game\Player.cs(12)", "/home/me/Game/Player.cs(12,5)", or a stack trace's "in C:\Game\Player.cs:line 12".
        [GeneratedRegex(@"(?<file>(?:[A-Za-z]:)?[\\/][^\s:*?""<>|()]+\.cs)(?:\((?<line>\d+)|:line (?<line>\d+))")]
        private static partial Regex SourcePattern();

        /// <summary>The first file and line of the game's code a message mentions that's still there, if any.</summary>
        public static (string File, int Line)? Source(EditorLog.Item item)
        {
            foreach (Match match in SourcePattern().Matches(Text(item)))
            {
                string file = match.Groups["file"].Value;
                if (File.Exists(file)) return (file, int.Parse(match.Groups["line"].Value));
            }

            return null;
        }

        /// <summary>Opens the code a message points at: in VS Code at the line if it's installed, otherwise however the system opens .cs files.</summary>
        private void OpenSource(EditorLog.Item item)
        {
            if (Source(item) is not { } source) return;

            try
            {
                string code = OperatingSystem.IsWindows() ? "code.cmd" : "code";
                string? vscode = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(folder => Path.Combine(folder, code)).FirstOrDefault(File.Exists);

                var start = vscode != null
                    ? new System.Diagnostics.ProcessStartInfo(vscode) { ArgumentList = { "-g", $"{source.File}:{source.Line}" }, UseShellExecute = false, CreateNoWindow = true }
                    : new System.Diagnostics.ProcessStartInfo(source.File) { UseShellExecute = true };
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't open {Path.GetFileName(source.File)}: {exception.Message}");
            }
        }
    }
}
