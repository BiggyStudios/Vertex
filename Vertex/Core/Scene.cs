using Vertex.Physics;
using Vertex.Rendering;

namespace Vertex
{
    /// <summary>
    /// Holds every GameObject and drives their Start/Update lifecycle.
    /// <para>
    /// To switch levels, make a new scene, set it as <see cref="Active"/>, build it, then <see cref="Unload"/> the old
    /// one. Scenes that aren't active keep their objects but don't update or render.
    /// </para>
    /// </summary>
    public class Scene
    {
        /// <summary>
        /// The scene that is currently updated and rendered. New GameObjects are added to it, unless they're given a
        /// scene of their own (see <see cref="GameObject(string, Scene)"/>).
        /// </summary>
        public static Scene Active { get; set; } = new Scene();

        /// <summary>
        /// Opens a scene made in the editor in place of the active one, and returns it: <c>Scene.Load("Scenes/Arena.scene")</c>.
        /// The path is inside the game's Assets folder (or a full path). The scene that was active is unloaded; the new
        /// one's objects start on its first update.
        /// </summary>
        /// <exception cref="FileNotFoundException">There's no such scene.</exception>
        public static Scene Load(string path)
        {
            if (Opening?.Invoke(path) is { } open) return open;

            string file = Assets.FindFile(path) ?? throw new FileNotFoundException($"There's no scene at {path} in the game's Assets folder.", path);
            var scene = new Scene();
            Serialization.SceneFile.Load(file, scene);

            Scene before = Active;
            Active = scene;
            if (before != scene) before.Unload();
            return scene;
        }

        /// <summary>
        /// Opens a scene at the start of the next frame, in place of the active one: what a component's Update (a menu's Play
        /// button, a door to the next level) should use, as <see cref="Load"/> there would pull the scene out from under the
        /// rest of the frame. Asking again before then opens the last asked for.
        /// </summary>
        public static void LoadSoon(string path) => Pending = path;

        /// <summary>The scene <see cref="LoadSoon"/> asked for, not yet opened: null to change its mind.</summary>
        public static string? Pending { get; set; }

        /// <summary>Opens the scene <see cref="LoadSoon"/> asked for, if any: at the start of a frame.</summary>
        internal static void LoadPending()
        {
            if (Pending is not { } path) return;
            Pending = null;
            Load(path);
        }

        /// <summary>
        /// Adds the objects saved in a scene file to this scene, and returns them: a room made in the editor, put into a
        /// world built in code. The file's lighting replaces this scene's.
        /// </summary>
        public List<GameObject> Add(string path)
        {
            string file = Assets.FindFile(path) ?? throw new FileNotFoundException($"There's no scene at {path} in the game's Assets folder.", path);
            return Serialization.SceneFile.Load(file, this);
        }

        /// <summary>
        /// Lets an editor hand back the scene it has open when the game opens that file, so Play plays what's being edited,
        /// unsaved changes and all. Null, or giving back null, opens the file as usual.
        /// </summary>
        internal static Func<string, Scene?>? Opening { get; set; }

        private readonly List<GameObject> _objects = new();
        private readonly List<GameObject> _toAdd = new();
        private readonly List<GameObject> _toDestroy = new();
        private PhysicsWorld? _physics;

        /// <summary>The sky, the soft light it gives, fog and shadow settings.</summary>
        public Lighting Lighting { get; } = new();

        /// <summary>How characters find their way round the scene: what the navmesh is made for, and the one baked for it.</summary>
        public AI.NavigationSettings Navigation { get; } = new();

        /// <summary>
        /// Lights that light the scene without being in it (they belong to another scene): an editor's light for a prefab
        /// shown on its own, which isn't part of the prefab.
        /// </summary>
        public List<Rendering.Light> OutsideLights { get; } = new();

        /// <summary>This scene's physics simulation. Created the first time it is used, or when the scene first has a collider.</summary>
        public PhysicsWorld Physics => _physics ??= new PhysicsWorld(this);

        public IReadOnlyList<GameObject> GameObjects => _objects;

        internal void Register(GameObject gameObject) => _toAdd.Add(gameObject);

        public void Destroy(GameObject gameObject)
        {
            if (!_toDestroy.Contains(gameObject)) _toDestroy.Add(gameObject);
        }

        public GameObject? Find(string name)
        {
            return _objects.FirstOrDefault(o => o.Name == name) ?? _toAdd.FirstOrDefault(o => o.Name == name);
        }

        /// <summary>All enabled components of type T on active objects.</summary>
        public IEnumerable<T> FindComponents<T>() where T : Component
        {
            // Only the lists of the types that are T, not every object in the scene: this runs many times a frame (every
            // renderer, light, collider, animator...) and a big scene has thousands of objects that are none of them.
            List<Component>[] lists = ListsFor(typeof(T));
            for (int l = 0; l < lists.Length; l++)
            {
                List<Component> list = lists[l];
                for (int i = 0; i < list.Count; i++)
                {
                    Component component = list[i];
                    if (component.Tracked && component.Enabled && component.GameObject.ActiveInHierarchy) yield return (T)component;
                }
            }
        }

        public T? FindComponent<T>() where T : Component => FindComponents<T>().FirstOrDefault();

        /// <summary>Runs FixedUpdate on every started object, then steps the physics simulation.</summary>
        internal void FixedUpdate(float deltaTime)
        {
            foreach (GameObject gameObject in Live())
            {
                if (!gameObject.Started || gameObject.IsDestroyed || !gameObject.ActiveInHierarchy) continue;

                for (int c = 0; c < gameObject.Components.Count; c++)
                {
                    Component component = gameObject.Components[c];
                    if (component.Enabled && component.Started) component.FixedUpdate();
                }

                gameObject.FixedUpdate();
            }

            // Don't start Jolt for scenes that never use physics.
            if (_physics == null && FindComponent<Collider>() == null) return;

            Physics.Step(deltaTime);
        }

        /// <summary>Called before a frame's fixed steps: undoes interpolation, so they start from the real simulated positions.</summary>
        internal void BeginFixedSteps() => _physics?.RestorePoses();

        /// <summary>Called after a frame's fixed steps: draws physics objects <paramref name="alpha"/> of the way between their last two steps.</summary>
        internal void InterpolatePhysics(float alpha)
        {
            Time.FixedAlpha = alpha;
            _physics?.Interpolate(alpha);
        }

        /// <summary>
        /// Destroys every object in the scene straight away (each gets its OnDestroy) and frees the physics simulation.
        /// The scene is left empty and can be filled again.
        /// </summary>
        public void Unload()
        {
            ForgetComponents();

            // OnDestroy may create new objects (an effect where something was), which would land back in here, so
            // keep going until nothing's left, but not forever.
            for (int pass = 0; pass < 8 && (_objects.Count > 0 || _toAdd.Count > 0); pass++)
            {
                GameObject[] objects = [.. _objects, .. _toAdd];
                _objects.Clear();
                _toAdd.Clear();

                foreach (GameObject gameObject in objects)
                {
                    gameObject.DestroyImmediate();
                }
            }

            _objects.Clear();
            _toAdd.Clear();
            _toDestroy.Clear();

            _physics?.Dispose();
            _physics = null;
        }

        /// <summary>
        /// Empties the scene without running any of its objects' code, not even OnDestroy: for a scene that was never
        /// run (an editor's, being edited), whose scripts haven't started and so have nothing to clean up.
        /// </summary>
        internal void Discard()
        {
            ForgetComponents();
            _objects.Clear();
            _toAdd.Clear();
            _toDestroy.Clear();
            _physics?.Dispose();
            _physics = null;
        }

        /// <summary>
        /// Brings in objects made since the last update and removes destroyed ones, without running any of their code:
        /// how an editor shows changes to a scene that isn't playing.
        /// </summary>
        internal void ApplyChanges()
        {
            JoinNew();
            DestroyPending();
        }

        /// <summary>
        /// Takes <paramref name="gameObject"/> and everything under it out of the scene without destroying them, so they
        /// can be put back with <see cref="Attach"/>: an editor's delete, which can be undone. Returns where it was among
        /// the scene's objects.
        /// </summary>
        internal int Detach(GameObject gameObject)
        {
            ApplyChanges();
            int index = _objects.IndexOf(gameObject);
            foreach (GameObject member in Subtree(gameObject))
            {
                if (_objects.Remove(member)) Left(member);
            }

            return index;
        }

        /// <summary>Puts back an object taken out by <see cref="Detach"/>, at <paramref name="index"/> among the scene's objects, with everything under it.</summary>
        internal void Attach(GameObject gameObject, int index)
        {
            ApplyChanges();
            List<GameObject> members = Subtree(gameObject).Where(member => !_objects.Contains(member)).ToList();
            _objects.InsertRange(Math.Clamp(index, 0, _objects.Count), members);
            foreach (GameObject member in members) Joined(member);
        }

        /// <summary>Where a top-level object comes among the scene's top-level objects. See <see cref="Transform.SiblingIndex"/>.</summary>
        internal int RootIndex(GameObject gameObject)
        {
            int index = 0;
            foreach (GameObject other in _objects.Concat(_toAdd))
            {
                if (other == gameObject) return index;
                if (other.Transform.Parent == null) index++;
            }

            return -1;
        }

        /// <summary>Moves a top-level object to <paramref name="index"/> among the top-level objects. See <see cref="Transform.SiblingIndex"/>.</summary>
        internal void SetRootIndex(GameObject gameObject, int index)
        {
            ApplyChanges();
            if (!_objects.Remove(gameObject)) return;
            _liveChanged = true;

            int roots = 0;
            for (int i = 0; i < _objects.Count; i++)
            {
                if (_objects[i].Transform.Parent != null) continue;
                if (roots == index)
                {
                    _objects.Insert(i, gameObject);
                    return;
                }

                roots++;
            }

            _objects.Add(gameObject);
        }

        private static IEnumerable<GameObject> Subtree(GameObject root)
        {
            yield return root;
            foreach (Transform child in root.Transform.Children)
            {
                foreach (GameObject member in Subtree(child.GameObject)) yield return member;
            }
        }

        internal void Update()
        {
            JoinNew();

            // Objects created during this loop are picked up next frame.
            List<GameObject> live = Live();
            int count = live.Count;
            for (int i = 0; i < count; i++)
            {
                GameObject gameObject = live[i];
                if (gameObject.IsDestroyed || !gameObject.ActiveInHierarchy) continue;

                if (!gameObject.Started)
                {
                    gameObject.Started = true;
                    gameObject.Start();
                }

                for (int c = 0; c < gameObject.Components.Count; c++)
                {
                    Component component = gameObject.Components[c];
                    if (!component.Enabled) continue;

                    if (!component.Started)
                    {
                        component.Started = true;
                        component.Start();
                    }

                    component.Update();
                }

                gameObject.Update();
            }

            // Animators pose their objects after everything's Update, so what the game set this frame is seen this frame.
            Animation.Animator.AnimateAll(this, Time.DeltaTime);

            for (int i = 0; i < count; i++)
            {
                GameObject gameObject = live[i];
                if (gameObject.IsDestroyed || !gameObject.ActiveInHierarchy) continue;

                List<Component> components = gameObject.ComponentList;
                for (int c = 0; c < components.Count; c++)
                {
                    Component component = components[c];
                    if (component.Enabled && component.Started) component.LateUpdate();
                }
            }

            DestroyPending();
        }

        /// <summary>Brings the objects made since the last update into the scene.</summary>
        private void JoinNew()
        {
            if (_toAdd.Count == 0) return;
            _objects.AddRange(_toAdd);
            foreach (GameObject gameObject in _toAdd) Joined(gameObject);
            _toAdd.Clear();
        }

        private void DestroyPending()
        {
            if (_toDestroy.Count == 0) return;

            // Round again for anything an OnDestroy destroys (a component cleaning up what it made): those go now too.
            while (_toDestroy.Count > 0)
            {
                GameObject[] batch = _toDestroy.ToArray();
                _toDestroy.Clear();
                foreach (GameObject gameObject in batch) gameObject.DestroyImmediate();
            }

            _objects.RemoveAll(o =>
            {
                if (!o.IsDestroyed) return false;
                Left(o);
                return true;
            });
            _toAdd.RemoveAll(o => o.IsDestroyed);
            TidyLists();
        }

        // ---------------------------------------------------------------- Finding components quickly

        // Every component on an object in the scene, by its exact type, in the order they came into the scene.
        private readonly Dictionary<Type, List<Component>> _byType = new();

        // For each type asked for, the lists of the types that are it (a Collider search reads the box, sphere and mesh
        // collider lists). Worked out again when a type the scene hasn't had before turns up.
        private readonly Dictionary<Type, List<Component>[]> _listsFor = new();

        // How many components have been taken off since the lists were last tidied.
        private int _untracked;

        // The objects with code to run each frame (Start, Update, LateUpdate, FixedUpdate), in the scene's order: most of a
        // big scene is scenery that has none, and needn't be gone through. Remade when objects or components come or go.
        private List<GameObject> _live = new();
        private bool _liveChanged = true;

        private List<Component>[] ListsFor(Type type)
        {
            if (_listsFor.TryGetValue(type, out List<Component>[]? lists)) return lists;

            lists = _byType.Where(pair => type.IsAssignableFrom(pair.Key)).Select(pair => pair.Value).ToArray();
            _listsFor[type] = lists;
            return lists;
        }

        /// <summary>An object came into the scene's list of objects: its components can be found from now on.</summary>
        private void Joined(GameObject gameObject)
        {
            if (gameObject.Joined) return;
            gameObject.Joined = true;
            _liveChanged = true;

            // A plain object with nothing to start has, in effect, started.
            if (gameObject.GetType() == typeof(GameObject)) gameObject.Started = true;
            List<Component> components = gameObject.ComponentList;
            for (int i = 0; i < components.Count; i++) Track(components[i]);
        }

        /// <summary>An object left the scene's list of objects (destroyed, or taken out by an editor).</summary>
        private void Left(GameObject gameObject)
        {
            if (!gameObject.Joined) return;
            gameObject.Joined = false;
            _liveChanged = true;
            List<Component> components = gameObject.ComponentList;
            for (int i = 0; i < components.Count; i++) Untrack(components[i]);
        }

        internal void ComponentAdded(Component component)
        {
            if (!component.GameObject.Joined) return;
            _liveChanged = true;
            Track(component);
        }

        internal void ComponentRemoved(Component component)
        {
            if (!component.GameObject.Joined) return;
            _liveChanged = true;
            Untrack(component);
        }

        private void Track(Component component)
        {
            // One with no code to start has, in effect, started (what Animators and IK check before working).
            if (!Hooks.Has(component.GetType())) component.Started = true;

            if (component.Tracked) return;
            component.Tracked = true;
            if (component.Listed) return;
            component.Listed = true;

            Type type = component.GetType();
            if (!_byType.TryGetValue(type, out List<Component>? list))
            {
                list = new List<Component>();
                _byType[type] = list;
                _listsFor.Clear();
            }

            list.Add(component);
        }

        private void Untrack(Component component)
        {
            if (!component.Tracked) return;
            component.Tracked = false;
            _untracked++;
        }

        /// <summary>
        /// Takes components no longer in the scene out of the lists, once enough have gone that skipping them costs more
        /// than a tidy: at the end of a frame, when nothing's going through the lists.
        /// </summary>
        private void TidyLists()
        {
            if (_untracked < 256) return;
            _untracked = 0;
            foreach (List<Component> list in _byType.Values)
            {
                list.RemoveAll(component =>
                {
                    if (component.Tracked) return false;
                    component.Listed = false;
                    return true;
                });
            }
        }

        private void ForgetComponents()
        {
            foreach (List<Component> list in _byType.Values)
            {
                foreach (Component component in list)
                {
                    component.Tracked = false;
                    component.Listed = false;
                }
            }

            _byType.Clear();
            _listsFor.Clear();
            _untracked = 0;
            foreach (GameObject gameObject in _objects) gameObject.Joined = false;
            _live = new List<GameObject>();
            _liveChanged = true;
        }

        /// <summary>The objects with code to run each frame, in the scene's order (see <c>_live</c>).</summary>
        private List<GameObject> Live()
        {
            if (!_liveChanged) return _live;
            _liveChanged = false;

            // A new list rather than refilling the old: a loop going through the old one carries on unbothered.
            var live = new List<GameObject>(_live.Count);
            for (int i = 0; i < _objects.Count; i++)
            {
                GameObject gameObject = _objects[i];
                if (gameObject.GetType() != typeof(GameObject))
                {
                    live.Add(gameObject);
                    continue;
                }

                List<Component> components = gameObject.ComponentList;
                for (int c = 0; c < components.Count; c++)
                {
                    if (!Hooks.Has(components[c].GetType())) continue;
                    live.Add(gameObject);
                    break;
                }
            }

            _live = live;
            return live;
        }

        /// <summary>Which component types have code of their own to run each frame or step.</summary>
        private static class Hooks
        {
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _has = new();

            private static readonly string[] _names = ["Start", "Update", "LateUpdate", "FixedUpdate"];

            public static bool Has(Type type) => _has.GetOrAdd(type, static type =>
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                foreach (string name in _names)
                {
                    if (type.GetMethod(name, flags, Type.EmptyTypes)?.DeclaringType != typeof(Component)) return true;
                }

                return false;
            });
        }
    }
}
