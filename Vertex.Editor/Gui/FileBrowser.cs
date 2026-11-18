using System.Numerics;

using Vertex.Input;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// The editor's own Open and Save dialog, for systems without one it can call: folders down the left (the project,
    /// home, recent places, drives), the folder's contents in the middle, and a path that can be typed into. Double-click
    /// a folder to go in and a file to pick it; Backspace goes up a folder.
    /// </summary>
    public static class FileBrowser
    {
        private sealed class State
        {
            public required string Title;
            public required string[] Patterns;
            public required bool Save;
            public required Action<string> Picked;
            public string Folder = "";
            public string PathText = "";
            public string FileName = "";
            public string? Selected;
            public List<(string Path, bool IsFolder)> Entries = new();
            public List<(string Name, string Path)> Places = new();
            public readonly List<string> Back = new();
            public string? Problem;
        }

        private static State? _state;

        /// <summary>
        /// Opens the browser in <paramref name="folder"/>, showing folders and files matching <paramref name="patterns"/>
        /// ("*.scene"). <paramref name="picked"/> gets the chosen file's full path; nothing happens if it's cancelled.
        /// </summary>
        public static void Show(string title, string? folder, string[] patterns, bool save, string fileName, Action<string> picked,
            IEnumerable<(string Name, string Path)>? places = null)
        {
            var state = new State { Title = title, Patterns = patterns, Save = save, Picked = picked, FileName = fileName };
            foreach ((string name, string path) in places ?? [])
            {
                if (Directory.Exists(path) && !state.Places.Any(p => SamePath(p.Path, path))) state.Places.Add((name, path));
            }

            AddStandardPlaces(state.Places);

            string start = folder != null && Directory.Exists(folder) ? folder : state.Places.FirstOrDefault().Path ?? Environment.CurrentDirectory;
            _state = state;
            Go(state, start, remember: false);

            Dialog.Show(title, new Vector2(820f, 540f), Draw, closed: () =>
            {
                if (_state == state) _state = null;
            });
        }

        private static void AddStandardPlaces(List<(string Name, string Path)> places)
        {
            void Add(string name, string? path)
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path) || places.Any(p => SamePath(p.Path, path))) return;
                places.Add((name, path));
            }

            Add("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Add("Downloads", Path.Combine(home, "Downloads"));

            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType is DriveType.Ram or DriveType.Unknown) continue;

                    // On Linux every mount is a "drive" (/proc, /sys...): only the root and real disks under /mnt, /media and /run/media.
                    string root = drive.RootDirectory.FullName;
                    if (!OperatingSystem.IsWindows() && root != "/" && !root.StartsWith("/mnt/") && !root.StartsWith("/media/") && !root.StartsWith("/run/media/")) continue;
                    Add(OperatingSystem.IsWindows() ? root.TrimEnd('\\') : root == "/" ? "Computer" : Path.GetFileName(root.TrimEnd('/')), root);
                }
            }
            catch (Exception)
            {
                // Drives that can't be listed aren't offered.
            }
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static void Go(State state, string folder, bool remember = true)
        {
            try
            {
                folder = Path.GetFullPath(folder);
                var entries = new List<(string, bool)>();
                entries.AddRange(Directory.GetDirectories(folder).Where(Visible).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).Select(d => (d, true)));
                entries.AddRange(Directory.GetFiles(folder).Where(f => Visible(f) && Matches(state, f)).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .Select(f => (f, false)));

                if (remember && state.Folder.Length > 0) state.Back.Add(state.Folder);
                state.Folder = folder;
                state.PathText = folder;
                state.Entries = entries;
                state.Selected = null;
                state.Problem = null;
                ScrollView.Reveal(Ui.Id("files.list"), 0f, 0f, 1f);
            }
            catch (Exception exception)
            {
                state.Problem = exception is UnauthorizedAccessException ? $"Can't open {Path.GetFileName(folder)}: no permission" : exception.Message;
                state.PathText = state.Folder;
            }
        }

        private static bool Visible(string path)
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith('.')) return false;
            try
            {
                return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Matches(State state, string file)
        {
            if (state.Patterns.Length == 0) return true;
            string name = Path.GetFileName(file);
            foreach (string pattern in state.Patterns)
            {
                if (pattern is "*" or "*.*") return true;
                if (pattern.StartsWith("*.") && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static void Pick(State state, string path)
        {
            if (Directory.Exists(path))
            {
                Go(state, path);
                return;
            }

            if (!state.Save && !File.Exists(path))
            {
                state.Problem = $"{Path.GetFileName(path)} isn't there";
                return;
            }

            // A name typed without its extension gets the first one offered.
            if (state.Save && Path.GetExtension(path).Length == 0 && state.Patterns.FirstOrDefault(p => p.StartsWith("*.") && p.Length > 2) is { } pattern)
            {
                path += pattern[1..];
            }

            _state = null;
            Dialog.Close();
            state.Picked(path);
        }

        private static void Draw(Rect rect)
        {
            if (_state is not { } state) return;

            const float rowHeight = 26f;
            Rect bottom = rect.TakeBottom(32f);
            Rect body = rect.CutBottom(44f);

            // The way back and up, and the folder's path, which can be typed into.
            Rect bar = body.TakeTop(28f);
            body = body.CutTop(36f);
            if (Widgets.IconButton(Ui.Id("files.back"), bar.TakeLeft(28f), (c, s, col) => Icons.Chevron(c, s, col, 180f), "Back") && state.Back.Count > 0)
            {
                string previous = state.Back[^1];
                state.Back.RemoveAt(state.Back.Count - 1);
                Go(state, previous, remember: false);
            }

            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(state.Folder));
            if (Widgets.IconButton(Ui.Id("files.up"), bar.CutLeft(32f).TakeLeft(28f), (c, s, col) => Icons.Chevron(c, s, col, -90f), "Up a folder") && parent != null)
            {
                Go(state, parent);
            }

            string typed = state.PathText;
            if (TextInput.Draw(Ui.Id("files.path"), bar.CutLeft(68f), ref typed, "Folder or file"))
            {
                state.PathText = typed;
                string path = typed.Trim().Trim('"');
                if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
                if (Directory.Exists(path)) Go(state, path);
                else if (File.Exists(path) || state.Save) Pick(state, path);
                else state.Problem = "No such folder";
            }

            // Places down the left.
            Rect places = body.TakeLeft(170f);
            Rect list = body.CutLeft(178f);
            Ui.Rect(places, Theme.Field, 6f);
            float y = places.Y + 6f;
            foreach ((string name, string path) in state.Places)
            {
                var row = new Rect(places.X + 4f, y, places.Width - 8f, rowHeight);
                int id = Ui.Id("files.place:" + path);
                Interaction interaction = Ui.Interact(id, row);
                bool here = SamePath(path, state.Folder);
                if (here) Ui.Rect(row, Theme.Selection, 4f);
                else if (interaction.Hovered) Ui.Rect(row, Theme.Hover.WithAlpha(2f), 4f);
                if (interaction.Clicked) Go(state, path);

                Icons.Folder(new Vector2(row.X + 14f, row.Centre.Y), 14f, here ? Theme.AccentText : Theme.TextDim);
                Ui.Text(row.CutLeft(28f), name, here ? Theme.Text : Theme.TextDim);
                Ui.Tooltip(id, path);
                y += rowHeight;
                if (y > places.Bottom - rowHeight) break;
            }

            // The folder's contents.
            Ui.Rect(list, Theme.Field, 6f);
            int listId = Ui.Id("files.list");
            var scroll = ScrollView.Begin(listId, list.Shrink(4f));
            y = scroll.Top;
            int selectedIndex = -1;
            for (int i = 0; i < state.Entries.Count; i++)
            {
                (string path, bool isFolder) = state.Entries[i];
                var row = new Rect(list.X + 4f, y, list.Width - 16f, rowHeight);
                y += rowHeight;
                if (row.Bottom < list.Y || row.Y > list.Bottom) continue;

                int id = Ui.Id("files.entry:" + path);
                Interaction interaction = Ui.Interact(id, row);
                bool selected = state.Selected == path;
                if (selected) selectedIndex = i;
                if (selected) Ui.Rect(row, Theme.Accent.WithAlpha(0.85f), 4f);
                else if (interaction.Hovered) Ui.Rect(row, Theme.Hover.WithAlpha(2f), 4f);

                if (interaction.Pressed)
                {
                    state.Selected = path;
                    if (!isFolder) state.FileName = Path.GetFileName(path);
                }

                if (interaction.DoubleClicked)
                {
                    Pick(state, path);
                    return;
                }

                Vector2 icon = new(row.X + 14f, row.Centre.Y);
                if (isFolder) Icons.Folder(icon, 15f, selected ? Color.White : Theme.Hex(0xD9B25F));
                else Icons.File(icon, 15f, selected ? Color.White : Theme.TextDim);
                Ui.Text(row.CutLeft(30f), Path.GetFileName(path), selected ? Color.White : Theme.Text);
            }

            if (state.Entries.Count == 0) Ui.Text(new Rect(list.X, list.Y + 16f, list.Width, 20f), "Nothing here", Theme.TextFaint, align: 0.5f);
            scroll.End(y - scroll.Top);

            // The keyboard, when not typing: arrows move, Enter opens, Backspace goes up.
            if (Ui.FocusId == 0 && state.Entries.Count > 0)
            {
                int step = InputSystem.GetKeyDown(Key.Down) ? 1 : InputSystem.GetKeyDown(Key.Up) ? -1 : 0;
                if (step != 0)
                {
                    int index = Math.Clamp(selectedIndex + step, 0, state.Entries.Count - 1);
                    state.Selected = state.Entries[index].Path;
                    if (!state.Entries[index].IsFolder) state.FileName = Path.GetFileName(state.Selected);
                    ScrollView.Reveal(listId, index * rowHeight, (index + 1) * rowHeight + 8f, list.Height - 8f);
                }

                if (InputSystem.GetKeyDown(Key.Enter) && state.Selected != null)
                {
                    InputSystem.Consume(Key.Enter);
                    Pick(state, state.Selected);
                    return;
                }
            }

            if (Ui.FocusId == 0 && InputSystem.GetKeyDown(Key.Backspace) && parent != null) Go(state, parent);

            // The name (when saving), what's shown, and the buttons.
            bool canPick;
            if (state.Save)
            {
                Ui.Text(bottom.TakeLeft(80f), "Name", Theme.TextDim);
                string name = state.FileName;
                Rect nameRect = bottom.CutLeft(80f).CutRight(220f);
                if (TextInput.Draw(Ui.Id("files.name"), nameRect, ref name, "File name", live: true)) state.FileName = name;
                canPick = state.FileName.Trim().Length > 0;
                if (Ui.FocusId == Ui.Id("files.name") && InputSystem.GetKeyDown(Key.Enter) && canPick)
                {
                    InputSystem.Consume(Key.Enter);
                    Pick(state, Path.Combine(state.Folder, state.FileName.Trim()));
                    return;
                }
            }
            else
            {
                string shows = state.Patterns.Length == 0 ? "All files" : string.Join(", ", state.Patterns);
                Ui.Text(bottom.CutRight(220f), state.Problem ?? $"Showing {shows}", state.Problem != null ? Theme.Danger : Theme.TextFaint, size: Theme.SmallTextSize);
                canPick = state.Selected != null;
            }

            Rect buttons = bottom.TakeRight(210f);
            if (Widgets.Button(Ui.Id("files.cancel"), buttons.TakeLeft(100f), "Cancel"))
            {
                _state = null;
                Dialog.Close();
                return;
            }

            string action = state.Save ? "Save" : state.Selected != null && Directory.Exists(state.Selected) ? "Open Folder" : "Open";
            if (Widgets.Button(Ui.Id("files.ok"), buttons.TakeRight(100f), action, canPick ? ButtonStyle.Primary : ButtonStyle.Normal) && canPick)
            {
                Pick(state, state.Save ? Path.Combine(state.Folder, state.FileName.Trim()) : state.Selected!);
            }
        }
    }
}
