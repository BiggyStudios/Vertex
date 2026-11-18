using System.Collections.Concurrent;
using System.Text;

namespace Vertex.Editor.Editing
{
    /// <summary>
    /// Everything written to the <see cref="Log"/> while the editor runs, for its Console: the engine's messages, the
    /// game's, build errors, and anything printed to the terminal with <see cref="Console.WriteLine()"/> (which would
    /// otherwise go nowhere, as the editor has no terminal window). Messages can arrive on any thread; they're taken
    /// in on the main one by <see cref="Update"/>.
    /// </summary>
    public sealed class EditorLog
    {
        /// <summary>A message as the Console lists it: the same one repeated straight after is counted rather than listed again.</summary>
        public sealed class Item
        {
            public required LogLevel Level { get; init; }
            public required string Message { get; init; }
            public string? Details { get; init; }
            public DateTime Time { get; set; }
            public int Count { get; set; } = 1;
        }

        private static EditorLog? _shared;

        /// <summary>The editor's log, started the first time it's asked for: there's one per editor, however many scenes come and go.</summary>
        public static EditorLog Shared => _shared ??= new EditorLog();

        private readonly ConcurrentQueue<LogEntry> _incoming = new();
        private readonly List<Item> _items = new();

        private const int _limit = 5000;

        public IReadOnlyList<Item> Items => _items;
        public int Infos { get; private set; }
        public int Warnings { get; private set; }
        public int Errors { get; private set; }

        /// <summary>Counts up whenever the list changes, so the Console can tell.</summary>
        public int Version { get; private set; }

        /// <summary>Raised (on the main thread, from <see cref="Update"/>) for each error taken in.</summary>
        public event Action<Item>? ErrorAdded;

        private EditorLog()
        {
            Log.Written += _incoming.Enqueue;

            // What's printed to the terminal is kept too, still going on to the terminal if there is one.
            Console.SetOut(new Capture(Console.Out, LogLevel.Info, _incoming));
            Console.SetError(new Capture(Console.Error, LogLevel.Error, _incoming));
        }

        /// <summary>Takes in what's been written since last time. Called once a frame.</summary>
        public void Update()
        {
            while (_incoming.TryDequeue(out LogEntry entry)) Add(entry);
        }

        private void Add(LogEntry entry)
        {
            if (_items.Count > 0 && _items[^1] is { } last && last.Level == entry.Level && last.Message == entry.Message && last.Details == entry.Details)
            {
                last.Count++;
                last.Time = entry.Time;
            }
            else
            {
                _items.Add(new Item { Level = entry.Level, Message = entry.Message, Details = entry.Details, Time = entry.Time });
                if (_items.Count > _limit) _items.RemoveRange(0, _items.Count - _limit);
            }

            switch (entry.Level)
            {
                case LogLevel.Info: Infos++; break;
                case LogLevel.Warning: Warnings++; break;
                case LogLevel.Error:
                    Errors++;
                    ErrorAdded?.Invoke(_items[^1]);
                    break;
            }

            Version++;
        }

        public void Clear()
        {
            Update();
            _items.Clear();
            Infos = Warnings = Errors = 0;
            Version++;
        }

        /// <summary>
        /// Stands in for the terminal's output: passes everything on, and turns each whole line into a message, guessing
        /// from its words whether it's a warning or an error.
        /// </summary>
        private sealed class Capture(TextWriter inner, LogLevel level, ConcurrentQueue<LogEntry> into) : TextWriter
        {
            private readonly StringBuilder _line = new();
            private readonly object _lock = new();

            public override Encoding Encoding => inner.Encoding;

            public override void Write(char value)
            {
                Pass(() => inner.Write(value));
                lock (_lock)
                {
                    if (value == '\n') Finish();
                    else if (value != '\r') _line.Append(value);
                }
            }

            public override void Write(string? value)
            {
                if (value == null) return;
                Pass(() => inner.Write(value));
                lock (_lock)
                {
                    foreach (char c in value)
                    {
                        if (c == '\n') Finish();
                        else if (c != '\r') _line.Append(c);
                    }
                }
            }

            public override void WriteLine(string? value)
            {
                Write(value);
                Write('\n');
            }

            public override void Flush() => Pass(inner.Flush);

            private static void Pass(Action write)
            {
                try
                {
                    write();
                }
                catch (Exception)
                {
                    // No terminal: kept here only.
                }
            }

            private void Finish()
            {
                string text = _line.ToString();
                _line.Clear();

                // The log's own echo is already in the list.
                if (text.Trim().Length == 0 || Log.Echoing) return;
                into.Enqueue(new LogEntry(Guess(text), text, null, DateTime.Now));
            }

            private LogLevel Guess(string text)
            {
                if (text.Contains("warning", StringComparison.OrdinalIgnoreCase)) return LogLevel.Warning;
                if (level == LogLevel.Error || text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("exception", StringComparison.OrdinalIgnoreCase)) return LogLevel.Error;
                return LogLevel.Info;
            }
        }
    }
}
