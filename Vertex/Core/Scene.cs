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
            // Indexed, not foreach: this runs several times a frame over every object, and enumerating each object's
            // components through an interface would allocate thousands of enumerators a frame for the GC to collect.
            for (int i = 0; i < _objects.Count; i++)
            {
                GameObject gameObject = _objects[i];
                if (!gameObject.ActiveInHierarchy) continue;

                List<Component> components = gameObject.ComponentList;
                for (int c = 0; c < components.Count; c++)
                {
                    if (components[c] is T match && match.Enabled) yield return match;
                }
            }
        }

        public T? FindComponent<T>() where T : Component => FindComponents<T>().FirstOrDefault();

        /// <summary>Runs FixedUpdate on every started object, then steps the physics simulation.</summary>
        internal void FixedUpdate(float deltaTime)
        {
            foreach (GameObject gameObject in _objects)
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
            _objects.AddRange(_toAdd);
            _toAdd.Clear();
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
            foreach (GameObject member in Subtree(gameObject)) _objects.Remove(member);
            return index;
        }

        /// <summary>Puts back an object taken out by <see cref="Detach"/>, at <paramref name="index"/> among the scene's objects, with everything under it.</summary>
        internal void Attach(GameObject gameObject, int index)
        {
            ApplyChanges();
            List<GameObject> members = Subtree(gameObject).Where(member => !_objects.Contains(member)).ToList();
            _objects.InsertRange(Math.Clamp(index, 0, _objects.Count), members);
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
            _objects.AddRange(_toAdd);
            _toAdd.Clear();

            // Objects created during this loop are picked up next frame.
            int count = _objects.Count;
            for (int i = 0; i < count; i++)
            {
                GameObject gameObject = _objects[i];
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
                GameObject gameObject = _objects[i];
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

        private void DestroyPending()
        {
            if (_toDestroy.Count == 0) return;

            foreach (GameObject gameObject in _toDestroy.ToArray())
            {
                gameObject.DestroyImmediate();
            }

            _objects.RemoveAll(o => o.IsDestroyed);
            _toAdd.RemoveAll(o => o.IsDestroyed);
            _toDestroy.Clear();
        }
    }
}
