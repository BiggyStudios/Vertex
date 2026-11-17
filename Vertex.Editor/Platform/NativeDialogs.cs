using System.Runtime.InteropServices;

namespace Vertex.Editor.Platform
{
    /// <summary>
    /// The system's own Open and Save dialogs, on Windows. Elsewhere these return null, and the editor asks for a path in
    /// a dialog of its own.
    /// </summary>
    public static class NativeDialogs
    {
        public static bool Available => OperatingSystem.IsWindows();

        /// <summary>Asks for a file to open. <paramref name="filter"/> is pairs of a name and patterns: ("Scenes", "*.scene").</summary>
        public static string? OpenFile(string title, string? folder, params (string Name, string Patterns)[] filter) =>
            Show(title, folder, null, filter, save: false);

        /// <summary>Asks where to save a file, suggesting <paramref name="fileName"/>.</summary>
        public static string? SaveFile(string title, string? folder, string fileName, params (string Name, string Patterns)[] filter) =>
            Show(title, folder, fileName, filter, save: true);

        private static string? Show(string title, string? folder, string? fileName, (string Name, string Patterns)[] filter, bool save)
        {
            if (!Available) return null;

            const int bufferLength = 2048;
            IntPtr buffer = Marshal.AllocHGlobal(bufferLength * 2);
            try
            {
                char[] initial = new char[bufferLength];
                (fileName ?? "").CopyTo(0, initial, 0, Math.Min((fileName ?? "").Length, bufferLength - 1));
                Marshal.Copy(initial, 0, buffer, bufferLength);

                string filterText = string.Concat(filter.Select(f => $"{f.Name} ({f.Patterns})\0{f.Patterns}\0")) + "All files (*.*)\0*.*\0\0";
                string? extension = filter.Length > 0 ? filter[0].Patterns.Split(';')[0].TrimStart('*', '.') : null;

                var dialog = new OpenFileName
                {
                    StructSize = Marshal.SizeOf<OpenFileName>(),
                    Filter = filterText,
                    File = buffer,
                    MaxFile = bufferLength,
                    InitialDir = folder,
                    Title = title,
                    DefaultExt = extension,
                    Flags = save ? OverwritePrompt | NoChangeDir | PathMustExist : FileMustExist | PathMustExist | NoChangeDir
                };

                bool picked = save ? GetSaveFileName(ref dialog) : GetOpenFileName(ref dialog);
                return picked ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Shows a file or folder in the system's file browser.</summary>
        public static void ShowInFolder(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    System.Diagnostics.Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"");
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
                }
            }
            catch (Exception)
            {
                // Nothing to show it with.
            }
        }

        private const int OverwritePrompt = 0x2, NoChangeDir = 0x8, PathMustExist = 0x800, FileMustExist = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int StructSize;
            public IntPtr Owner;
            public IntPtr Instance;
            public string Filter;
            public IntPtr CustomFilter;
            public int MaxCustomFilter;
            public int FilterIndex;
            public IntPtr File;
            public int MaxFile;
            public IntPtr FileTitle;
            public int MaxFileTitle;
            public string? InitialDir;
            public string Title;
            public int Flags;
            public short FileOffset;
            public short FileExtension;
            public string? DefaultExt;
            public IntPtr CustomData;
            public IntPtr Hook;
            public IntPtr TemplateName;
            public IntPtr Reserved;
            public int ReservedFlags;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
        private static extern bool GetOpenFileName(ref OpenFileName dialog);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetSaveFileNameW", SetLastError = true)]
        private static extern bool GetSaveFileName(ref OpenFileName dialog);
    }
}
