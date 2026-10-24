using Vertex.Physics;

namespace Vertex
{
    /// <summary>
    /// Holds every GameObject and drives their Start/Update lifecycle.
    /// </summary>
    public class Scene
    {
        /// <summary>The scene that is currently updated and rendered. New GameObjects are added to it.</summary>
        public static Scene Active { get; set; } = new Scene();

        private readonly List<GameObject> _objects = new();
        private readonly List<GameObject> _toAdd = new();
        private readonly List<GameObject> _toDestroy = new();
        private PhysicsWorld? _physics;

        public Color AmbientLight { get; set; } = new Color(0.15f, 0.15f, 0.18f);

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
            foreach (GameObject gameObject in _objects)
            {
                if (!gameObject.ActiveInHierarchy) continue;

                foreach (Component component in gameObject.Components)
                {
                    if (component is T match && component.Enabled) yield return match;
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
        internal void InterpolatePhysics(float alpha) => _physics?.Interpolate(alpha);

        /// <summary>Frees the physics simulation. Called when the window closes.</summary>
        internal void Shutdown()
        {
            _physics?.Dispose();
            _physics = null;
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

            if (_toDestroy.Count == 0) return;

            foreach (GameObject gameObject in _toDestroy)
            {
                gameObject.DestroyImmediate();
            }

            _objects.RemoveAll(o => o.IsDestroyed);
            _toAdd.RemoveAll(o => o.IsDestroyed);
            _toDestroy.Clear();
        }
    }
}
