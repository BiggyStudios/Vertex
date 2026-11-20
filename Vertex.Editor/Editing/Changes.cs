using System.Numerics;
using System.Reflection;

namespace Vertex.Editor.Editing
{
    /// <summary>Sets one property or field of one object: anything changed in the inspector.</summary>
    public sealed class SetValue : IUndoable, IMergeable
    {
        private readonly object _target;
        private readonly MemberInfo _member;
        private readonly object? _before;
        private object? _after;

        public string Name { get; }

        public SetValue(string name, object target, MemberInfo member, object? before, object? after)
        {
            Name = name;
            _target = target;
            _member = member;
            _before = before;
            _after = after;
        }

        public void Undo() => Write(_before);
        public void Redo() => Write(_after);

        public bool Merge(IUndoable next)
        {
            if (next is not SetValue other || other._target != _target || other._member != _member) return false;
            _after = other._after;
            return true;
        }

        private void Write(object? value)
        {
            switch (_member)
            {
                case PropertyInfo property: property.SetValue(_target, value); break;
                case FieldInfo field: field.SetValue(_target, value); break;
            }
        }

        public static object? Read(object target, MemberInfo member) => member switch
        {
            PropertyInfo property => property.GetValue(target),
            FieldInfo field => field.GetValue(target),
            _ => null
        };

        /// <summary>Sets the value now and records it in <paramref name="history"/>; a drag of the same value joins in.</summary>
        public static void Apply(UndoHistory history, string name, object target, MemberInfo member, object? value)
        {
            object? before = Read(target, member);
            history.Do(new SetValue(name, target, member, before, value), (target, member));
        }
    }

    /// <summary>An object made in the editor: undoing takes it out of the scene, redoing puts it back.</summary>
    public sealed class CreateObject(GameObject gameObject, string? name = null) : IUndoable
    {
        private readonly Transform? _parent = gameObject.Transform.Parent;
        private int _index = -1;

        public GameObject GameObject => gameObject;
        public string Name { get; } = name ?? $"Create {gameObject.Name}";

        public void Undo()
        {
            gameObject.Transform.Parent = null;
            _index = gameObject.Scene.Detach(gameObject);
        }

        public void Redo()
        {
            // The first time, it's already there.
            if (_index < 0) return;
            gameObject.Scene.Attach(gameObject, _index);
            gameObject.Transform.Parent = _parent;
        }
    }

    /// <summary>Deleting an object (and everything under it), which undo puts back where it was.</summary>
    public sealed class DeleteObject(GameObject gameObject) : IUndoable
    {
        private Transform? _parent;
        private int _index;

        public GameObject GameObject => gameObject;
        public string Name => $"Delete {gameObject.Name}";

        public void Redo()
        {
            _parent = gameObject.Transform.Parent;
            gameObject.Transform.Parent = null;
            _index = gameObject.Scene.Detach(gameObject);
        }

        public void Undo()
        {
            gameObject.Scene.Attach(gameObject, _index);
            gameObject.Transform.Parent = _parent;
        }
    }

    /// <summary>A component added in the inspector.</summary>
    public sealed class AddComponent(GameObject gameObject, Component component) : IUndoable
    {
        private int _index = gameObject.Components.Count;

        public Component Component => component;
        public string Name => $"Add {Inspection.ComponentCatalog.DisplayName(component.GetType())}";

        public void Redo() => gameObject.AttachComponent(component, _index);
        public void Undo() => _index = gameObject.DetachComponent(component);
    }

    /// <summary>A component removed in the inspector, which undo puts back in its place.</summary>
    public sealed class RemoveComponent(GameObject gameObject, Component component) : IUndoable
    {
        private int _index;

        public string Name => $"Remove {Inspection.ComponentCatalog.DisplayName(component.GetType())}";

        public void Redo() => _index = gameObject.DetachComponent(component);
        public void Undo() => gameObject.AttachComponent(component, _index);
    }

    /// <summary>Moving an object under another (or to the top level), keeping where it is in the world.</summary>
    public sealed class Reparent : IUndoable
    {
        private readonly Transform _transform;
        private readonly Transform? _oldParent;
        private readonly Transform? _newParent;
        private readonly (Vector3 Position, Quaternion Rotation, Vector3 Scale) _oldLocal;
        private (Vector3 Position, Quaternion Rotation, Vector3 Scale) _newLocal;
        private bool _done;

        public string Name { get; }

        public Reparent(GameObject gameObject, Transform? newParent)
        {
            _transform = gameObject.Transform;
            _oldParent = _transform.Parent;
            _newParent = newParent;
            _oldLocal = (_transform.Position, _transform.Rotation, _transform.Scale);
            Name = newParent == null ? $"Unparent {gameObject.Name}" : $"Parent {gameObject.Name} to {newParent.GameObject.Name}";
        }

        public void Redo()
        {
            if (_done)
            {
                _transform.Parent = _newParent;
                (_transform.Position, _transform.Rotation, _transform.Scale) = _newLocal;
                return;
            }

            Vector3 position = _transform.WorldPosition;
            Quaternion rotation = _transform.WorldRotation;
            Vector3 scale = _transform.WorldScale;

            _transform.Parent = _newParent;
            _transform.WorldPosition = position;
            _transform.WorldRotation = rotation;
            Vector3 parentScale = _newParent?.WorldScale ?? Vector3.One;
            _transform.Scale = new Vector3(Divide(scale.X, parentScale.X), Divide(scale.Y, parentScale.Y), Divide(scale.Z, parentScale.Z));

            _newLocal = (_transform.Position, _transform.Rotation, _transform.Scale);
            _done = true;
        }

        public void Undo()
        {
            _transform.Parent = _oldParent;
            (_transform.Position, _transform.Rotation, _transform.Scale) = _oldLocal;
        }

        private static float Divide(float a, float b) => MathF.Abs(b) > 1e-6f ? a / b : a;
    }

    /// <summary>
    /// Moving an object to another place in the hierarchy: under <c>newParent</c> (or to the top level), at
    /// <c>index</c> among what's there. It stays where it is in the world.
    /// </summary>
    public sealed class MoveInHierarchy : IUndoable
    {
        private readonly Transform _transform;
        private readonly Transform? _oldParent, _newParent;
        private readonly int _oldIndex, _newIndex;
        private readonly (Vector3, Quaternion, Vector3) _oldLocal;
        private (Vector3, Quaternion, Vector3)? _newLocal;

        public string Name { get; }

        public MoveInHierarchy(GameObject gameObject, Transform? newParent, int index)
        {
            _transform = gameObject.Transform;
            _oldParent = _transform.Parent;
            _newParent = newParent;
            _oldIndex = _transform.SiblingIndex;
            _newIndex = index;
            _oldLocal = SetPose.Of(_transform);
            Name = newParent == _oldParent ? $"Reorder {gameObject.Name}" : newParent == null ? $"Unparent {gameObject.Name}" : $"Parent {gameObject.Name} to {newParent.GameObject.Name}";
        }

        public void Redo()
        {
            if (_newParent != _oldParent)
            {
                if (_newLocal is { } local)
                {
                    _transform.Parent = _newParent;
                    (_transform.Position, _transform.Rotation, _transform.Scale) = local;
                }
                else
                {
                    var keep = new Reparent(_transform.GameObject, _newParent);
                    keep.Redo();
                    _newLocal = SetPose.Of(_transform);
                }
            }

            _transform.SiblingIndex = _newIndex;
        }

        public void Undo()
        {
            if (_newParent != _oldParent)
            {
                _transform.Parent = _oldParent;
                (_transform.Position, _transform.Rotation, _transform.Scale) = _oldLocal;
            }

            _transform.SiblingIndex = _oldIndex;
        }
    }

    /// <summary>
    /// Several changes as one: undone in reverse order. A group soaks up the next group like it (the same changes to the
    /// same things, as dragging a value on several objects at once makes each frame), so a drag is one undo.
    /// </summary>
    public sealed class ChangeGroup(string name, IReadOnlyList<IUndoable> changes) : IUndoable, IMergeable
    {
        public string Name => name;

        public bool Merge(IUndoable next)
        {
            if (next is not ChangeGroup other || other.Changes.Count != changes.Count) return false;
            for (int i = 0; i < changes.Count; i++)
            {
                if (changes[i] is not IMergeable || changes[i].GetType() != other.Changes[i].GetType()) return false;
            }

            foreach ((IUndoable change, IUndoable later) in changes.Zip(other.Changes))
            {
                if (!((IMergeable)change).Merge(later)) return false;
            }

            return true;
        }

        public IReadOnlyList<IUndoable> Changes => changes;

        public void Redo()
        {
            foreach (IUndoable change in changes) change.Redo();
        }

        public void Undo()
        {
            for (int i = changes.Count - 1; i >= 0; i--) changes[i].Undo();
        }
    }

    /// <summary>Sets an object's position, rotation and scale all at once: what the move, rotate and scale tools do.</summary>
    public sealed class SetPose : IUndoable, IMergeable
    {
        private readonly Transform _transform;
        private readonly (Vector3, Quaternion, Vector3) _before;
        private (Vector3, Quaternion, Vector3) _after;

        public string Name { get; }

        public SetPose(string name, Transform transform, (Vector3, Quaternion, Vector3) before, (Vector3, Quaternion, Vector3) after)
        {
            Name = name;
            _transform = transform;
            _before = before;
            _after = after;
        }

        public static (Vector3, Quaternion, Vector3) Of(Transform transform) => (transform.Position, transform.Rotation, transform.Scale);

        public void Undo() => (_transform.Position, _transform.Rotation, _transform.Scale) = _before;
        public void Redo() => (_transform.Position, _transform.Rotation, _transform.Scale) = _after;

        public bool Merge(IUndoable next)
        {
            if (next is not SetPose other || other._transform != _transform) return false;
            _after = other._after;
            return true;
        }
    }

    /// <summary>
    /// A change to an animation's keys, tracks or settings, kept as what the whole animation was before and after (it's
    /// saved to its file as it changes, like a graph).
    /// </summary>
    public sealed class ClipChange(Vertex.Animation.AnimationClip clip, string name, string before, string after) : IUndoable, IMergeable
    {
        private string _after = after;

        public string Name => name;
        public Vertex.Animation.AnimationClip Clip => clip;

        public void Undo() => clip.ReadJson(before);
        public void Redo() => clip.ReadJson(_after);

        public bool Merge(IUndoable next)
        {
            if (next is not ClipChange other || other.Clip != clip) return false;
            _after = other._after;
            return true;
        }

        /// <summary>
        /// Makes a change to <paramref name="clip"/> with <paramref name="change"/>, and remembers it: what was before is
        /// read first. Nothing's remembered if it made no difference.
        /// </summary>
        public static void Make(UndoHistory history, Vertex.Animation.AnimationClip clip, string name, Action change, object? mergeKey = null)
        {
            string before = clip.ToJson();
            change();
            clip.Changed();
            string after = clip.ToJson();
            if (after != before) history.Record(new ClipChange(clip, name, before, after), mergeKey);
        }
    }
}
