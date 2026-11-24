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

        /// <summary>Found by its scene's searches (<see cref="Scene.FindComponents"/>): on an object in the scene, and not taken off.</summary>
        internal bool Tracked;

        /// <summary>In its scene's list for its type, perhaps no longer <see cref="Tracked"/> (it's taken out of the list later, in a batch).</summary>
        internal bool Listed;

        /// <summary>Called once, right before the first Update this component receives.</summary>
        protected internal virtual void Start() {}

        /// <summary>Called every frame while the component is enabled and its GameObject is active.</summary>
        protected internal virtual void Update() {}

        /// <summary>
        /// Called every frame after every Update and after animation has posed the objects: to change what an animation
        /// put somewhere (turn a character's head towards what it's looking at), or follow something that has moved.
        /// </summary>
        protected internal virtual void LateUpdate() {}

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

        /// <summary>Called when one of this object's joints breaks, pulled harder than its <see cref="Joint.BreakForce"/>.</summary>
        protected internal virtual void OnJointBreak(Joint joint) {}

        /// <summary>Called when the component or its GameObject is destroyed.</summary>
        protected internal virtual void OnDestroy() {}

        /// <summary>
        /// Called when a network event arrives about this component's object (or about no object in particular: then every
        /// component hears it). Sent with <see cref="Networking.NetworkScene.SendEvent"/>; the C# side of a visual script's
        /// On Network Event.
        /// </summary>
        protected internal virtual void OnNetworkEvent(Networking.NetworkEvent networkEvent) {}

        /// <summary>Whether this machine is in charge of this object in a network game (see <see cref="GameObject.IsMine"/>).</summary>
        [HideInInspector]
        public bool IsMine => GameObject.IsMine;

        public T AddComponent<T>() where T : Component, new() => GameObject.AddComponent<T>();
        public T? GetComponent<T>() where T : Component => GameObject.GetComponent<T>();
    }
}
