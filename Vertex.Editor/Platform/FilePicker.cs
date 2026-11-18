using System.Diagnostics;
using System.Numerics;

using Vertex.Editor.Gui;

namespace Vertex.Editor.Platform
{
    /// <summary>
    /// Asks for a file to open or a place to save one, the best way the system allows: its own dialog on Windows, zenity
    /// or kdialog on Linux, the Finder's on macOS, and otherwise the editor's own <see cref="FileBrowser"/>. The answer
    /// comes back through a callback, as on Linux and macOS the dialog is another program that's waited for while the
    /// editor keeps drawing.
    /// </summary>
    public static class FilePicker
    {
        /// <summary>Places offered in the editor's own browser, besides home and the drives: the project, recent projects.</summary>
        public static Func<IEnumerable<(string Name, string Path)>>? Places { get; set; }

        /// <summary>Asks for a file to open. <paramref name="filter"/> is a name and patterns: ("Scenes", "*.scene;*.json").</summary>
        public static void Open(string title, string? folder, (string Name, string Patterns) filter, Action<string> picked) =>
            Ask(title, folder, null, filter, save: false, picked);

        /// <summary>Asks where to save a file, suggesting <paramref name="fileName"/>.</summary>
        public static void Save(string title, string? folder, string fileName, (string Name, string Patterns) filter, Action<string> picked) =>
            Ask(title, folder, fileName, filter, save: true, picked);

        private static void Ask(string title, string? folder, string? fileName, (string Name, string Patterns) filter, bool save, Action<string> picked)
        {
            if (folder != null && !Directory.Exists(folder)) folder = null;
            string[] patterns = filter.Patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // VERTEX_FILE_BROWSER=editor always uses the editor's own browser.
            if (Environment.GetEnvironmentVariable("VERTEX_FILE_BROWSER") == "editor")
            {
                BrowseInEditor(title, folder, fileName, patterns, save, picked);
                return;
            }

            if (NativeDialogs.Available)
            {
                string? path = save ? NativeDialogs.SaveFile(title, folder, fileName ?? "", filter) : NativeDialogs.OpenFile(title, folder, filter);
                if (path != null) picked(path);
                return;
            }

            if (Command(title, folder, fileName, filter, patterns, save) is { } command && Start(command) is { } process)
            {
                Wait(title, process, folder, fileName, patterns, save, picked);
                return;
            }

            BrowseInEditor(title, folder, fileName, patterns, save, picked);
        }

        private static void BrowseInEditor(string title, string? folder, string? fileName, string[] patterns, bool save, Action<string> picked) =>
            FileBrowser.Show(title, folder, patterns, save, fileName ?? "", picked, Places?.Invoke());

        /// <summary>The program to run for the system's dialog, with its arguments, or null if there's none.</summary>
        private static ProcessStartInfo? Command(string title, string? folder, string? fileName, (string Name, string Patterns) filter, string[] patterns, bool save)
        {
            string start = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (OperatingSystem.IsMacOS())
            {
                string prompt = title.Replace("\"", "'");
                string script = save
                    ? $"POSIX path of (choose file name with prompt \"{prompt}\" default name \"{fileName}\" default location POSIX file \"{start}\")"
                    : $"POSIX path of (choose file with prompt \"{prompt}\" default location POSIX file \"{start}\")";
                var mac = new ProcessStartInfo("osascript");
                mac.ArgumentList.Add("-e");
                mac.ArgumentList.Add(script);
                return mac;
            }

            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD()) return null;

            // KDE's own dialog on KDE, otherwise zenity (GNOME's, and the usual one elsewhere), whichever there is.
            bool kde = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase);
            string? zenity = FindProgram("zenity"), kdialog = FindProgram("kdialog");
            if (kde && kdialog != null || zenity == null && kdialog != null)
            {
                var kd = new ProcessStartInfo(kdialog!);
                kd.ArgumentList.Add(save ? "--getsavefilename" : "--getopenfilename");
                kd.ArgumentList.Add(save ? Path.Combine(start, fileName ?? "") : start);
                kd.ArgumentList.Add($"{filter.Name} ({string.Join(' ', patterns)})");
                kd.ArgumentList.Add("--title");
                kd.ArgumentList.Add(title);
                return kd;
            }

            if (zenity == null) return null;

            var z = new ProcessStartInfo(zenity);
            z.ArgumentList.Add("--file-selection");
            z.ArgumentList.Add($"--title={title}");
            if (save)
            {
                z.ArgumentList.Add("--save");
                z.ArgumentList.Add("--confirm-overwrite");
            }

            z.ArgumentList.Add($"--filename={Path.Combine(start, fileName ?? "")}{(save ? "" : "/")}");
            z.ArgumentList.Add($"--file-filter={filter.Name} | {string.Join(' ', patterns)}");
            z.ArgumentList.Add("--file-filter=All files | *");
            return z;
        }

        private static string? FindProgram(string name)
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }

            return null;
        }

        private static Process? Start(ProcessStartInfo command)
        {
            command.RedirectStandardOutput = true;
            command.RedirectStandardError = true;
            command.UseShellExecute = false;
            try
            {
                return Process.Start(command);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Shows that the system's dialog is open, until it's answered: the editor carries on drawing meanwhile.</summary>
        private static void Wait(string title, Process process, string? folder, string? fileName, string[] patterns, bool save, Action<string> picked)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            bool done = false;

            void Finish()
            {
                done = true;
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch (Exception)
                {
                    // Already gone.
                }

                process.Dispose();
            }

            Dialog.Show(title, new Vector2(460f, 150f), rect =>
            {
                if (done) return;

                if (process.HasExited && output.IsCompleted)
                {
                    int code = process.ExitCode;
                    string path = output.Result.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
                    Finish();
                    Dialog.Close();

                    // 0 with a path is an answer, 1 is cancelled; anything else means the dialog couldn't run (no display for
                    // it, say), so the editor's own is used instead.
                    if (code == 0 && path.Length > 0) picked(path);
                    else if (code > 1) BrowseInEditor(title, folder, fileName, patterns, save, picked);
                    return;
                }

                Canvas_Spinner(new Vector2(rect.X + 10f, rect.Y + 14f));
                Ui.Text(new Rect(rect.X + 28f, rect.Y + 4f, rect.Width - 28f, 20f), "Choose in the window that opened…", Theme.Text);

                Rect buttons = rect.TakeBottom(30f);
                if (Widgets.Button(Ui.Id("picker.cancel"), buttons.TakeRight(90f), "Cancel"))
                {
                    Finish();
                    Dialog.Close();
                }

                if (Widgets.Button(Ui.Id("picker.builtin"), buttons.CutRight(98f).TakeRight(190f), "Use the editor's browser"))
                {
                    Finish();
                    BrowseInEditor(title, folder, fileName, patterns, save, picked);
                }
            }, closed: () =>
            {
                if (!done) Finish();
            });
        }

        private static void Canvas_Spinner(Vector2 centre) => Vertex.UI.Canvas.DrawArc(centre, 7f, Ui.Time * 360f, 270f, 2f, Ui.Fade(Theme.AccentText));
    }
}
