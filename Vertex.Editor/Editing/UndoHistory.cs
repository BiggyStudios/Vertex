namespace Vertex.Editor.Editing
{
    /// <summary>A change made in the editor that can be taken back and made again.</summary>
    public interface IUndoable
    {
        /// <summary>What it did, for the Edit menu and the note shown on undo: "Move Cube".</summary>
        string Name { get; }

        void Undo();
        void Redo();
    }

    /// <summary>
    /// Every change made in the editor, in order, so Ctrl+Z can take them back and Ctrl+Y make them again. Changes are
    /// made by passing them to <see cref="Do"/>.
    /// <para>
    /// A change with a merge key (one property of one object) made straight after another with the same key joins it,
    /// so dragging a number takes one undo, not one per frame. <see cref="EndMerge"/> stops that, at the end of a drag.
    /// </para>
    /// </summary>
    public sealed class UndoHistory
    {
        private readonly List<(IUndoable Change, object? MergeKey)> _done = new();
        private readonly List<IUndoable> _undone = new();
        private bool _merging = true;

        /// <summary>The most changes kept: the oldest are forgotten past this.</summary>
        public int Limit { get; set; } = 500;

        public bool CanUndo => _done.Count > 0;
        public bool CanRedo => _undone.Count > 0;

        public string? NextUndo => _done.Count > 0 ? _done[^1].Change.Name : null;
        public string? NextRedo => _undone.Count > 0 ? _undone[^1].Name : null;

        /// <summary>Counts every change, undo and redo, so things that show the scene know to look again.</summary>
        public int Version { get; private set; }

        /// <summary>Raised after each undo or redo, with what was undone or redone and which it was.</summary>
        public event Action<IUndoable, bool>? Reverted;

        /// <summary>Makes <paramref name="change"/> (calls its <see cref="IUndoable.Redo"/>) and remembers it.</summary>
        public void Do(IUndoable change, object? mergeKey = null)
        {
            change.Redo();
            Record(change, mergeKey);
        }

        /// <summary>Remembers a change that's already been made.</summary>
        public void Record(IUndoable change, object? mergeKey = null)
        {
            Version++;
            _undone.Clear();

            if (mergeKey != null && _merging && _done.Count > 0 && Equals(_done[^1].MergeKey, mergeKey) &&
                _done[^1].Change is IMergeable previous && previous.Merge(change))
            {
                return;
            }

            _done.Add((change, mergeKey));
            _merging = true;
            if (_done.Count > Limit) _done.RemoveAt(0);
        }

        /// <summary>
        /// Remembers a change made because of the last one (a key the Animation panel recorded from a move), so the two are
        /// undone and redone as one, under the last one's name.
        /// </summary>
        public void RecordWithLast(IUndoable change)
        {
            if (_done.Count == 0)
            {
                Record(change);
                return;
            }

            Version++;
            _undone.Clear();
            IUndoable last = _done[^1].Change;
            _done[^1] = (new ChangeGroup(last.Name, [last, change]), null);
            _merging = false;
        }

        /// <summary>Stops the next change joining the last one, even with the same merge key: call at the end of a drag.</summary>
        public void EndMerge() => _merging = false;

        public IUndoable? Undo()
        {
            if (_done.Count == 0) return null;

            IUndoable change = _done[^1].Change;
            _done.RemoveAt(_done.Count - 1);
            change.Undo();
            _undone.Add(change);
            _merging = false;
            Version++;
            Reverted?.Invoke(change, true);
            return change;
        }

        public IUndoable? Redo()
        {
            if (_undone.Count == 0) return null;

            IUndoable change = _undone[^1];
            _undone.RemoveAt(_undone.Count - 1);
            change.Redo();
            _done.Add((change, null));
            _merging = false;
            Version++;
            Reverted?.Invoke(change, false);
            return change;
        }

        public void Clear()
        {
            _done.Clear();
            _undone.Clear();
            Version++;
        }
    }

    /// <summary>A change that can soak up the next one like it, so a drag is one undo.</summary>
    public interface IMergeable
    {
        /// <summary>Takes on <paramref name="next"/>'s result, keeping this one's starting point. False if it can't.</summary>
        bool Merge(IUndoable next);
    }
}
