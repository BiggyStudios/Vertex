using Vertex.Physics;

namespace Vertex
{
    /// <summary>
    /// Base class for anything that can be attached to a GameObject (renderers, cameras, lights, your own scripts).
    /// </summary>
    public abstract class Component
    {
        public GameObject GameObject { get; internal set; } = null!;
        public Transform Transform => GameObject.Transform;
        public bool Enabled { get; set; } = true;

        internal bool Started;

        /// <summary>Called once, right before the first Update this component receives.</summary>
        protected internal virtual void Start() {}

        /// <summary>Called every frame while the component is enabled and its GameObject is active.</summary>
        protected internal virtual void Update() {}

        /// <summary>
        /// Called every <see cref="Time.FixedDeltaTime"/> seconds, right before each physics step.
        /// Apply forces and set velocities here; read input in <see cref="Update"/>.
        /// </summary>
        protected internal virtual void FixedUpdate() {}

        /// <summary>Called when this object's collider starts touching another one.</summary>
        protected internal virtual void OnCollisionEnter(Collision collision) {}

        /// <summary>Called every physics step while this object's collider keeps touching another one.</summary>
        protected internal virtual void OnCollisionStay(Collision collision) {}

        /// <summary>Called when this object's collider stops touching another one.</summary>
        protected internal virtual void OnCollisionExit(Collision collision) {}

        /// <summary>Called when a collider enters a trigger, on both the trigger and the collider.</summary>
        protected internal virtual void OnTriggerEnter(Collider other) {}

        /// <summary>Called when a collider leaves a trigger, on both the trigger and the collider.</summary>
        protected internal virtual void OnTriggerExit(Collider other) {}

        /// <summary>Called when the component or its GameObject is destroyed.</summary>
        protected internal virtual void OnDestroy() {}

        public T AddComponent<T>() where T : Component, new() => GameObject.AddComponent<T>();
        public T? GetComponent<T>() where T : Component => GameObject.GetComponent<T>();
    }
}
