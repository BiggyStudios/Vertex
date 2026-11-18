namespace Vertex.Networking
{
    /// <summary>
    /// Marks an object as the same object on everyone's machine in a network game, with one player in charge of it (its
    /// owner): only the owner moves it and decides what happens to it, and everyone else sees it. Objects made with
    /// <see cref="NetworkScene.Spawn"/> (or Network Spawn in a visual script) get one, owned by whoever made them; one
    /// placed in the scene is owned by the host.
    /// <para>
    /// Every machine runs the object's scripts, so a script that moves it or reads keys should check Is Mine first. Other
    /// players' cameras are switched off, so each player sees through their own.
    /// </para>
    /// </summary>
    public sealed class NetworkObject : Component
    {
        /// <summary>The number everyone knows it by.</summary>
        public uint NetworkId { get; internal set; }

        /// <summary>The player in charge of it; nobody (the default) means the host.</summary>
        public PeerId Owner { get; internal set; }

        /// <summary>Whether it's a player's own object, made from <see cref="NetworkManager.PlayerPrefab"/>.</summary>
        public bool IsPlayer { get; internal set; }

        /// <summary>Whether this machine is in charge of it. Always, playing alone.</summary>
        public new bool IsMine => Owner == default ? Network.IsHost : Owner == Network.LocalPeer;

        /// <summary>The name of the player in charge of it.</summary>
        public string OwnerName => IsMine ? NetworkScene.LocalName : Network.GetName(Owner == default ? Network.HostPeer : Owner);

        // The prefab it was made from, over the network (so others can make it too); null for one placed in the scene.
        internal string? SpawnedFrom { get; set; }

        // Taken away because another machine said so, so it isn't announced back to them.
        internal bool Despawned { get; set; }

        protected internal override void Start()
        {
            if (NetworkId == 0) NetworkId = SceneId(GameObject);
            NetworkScene.Register(this);

            if (!IsMine)
            {
                foreach (Rendering.Camera camera in CamerasUnder(GameObject)) camera.Enabled = false;
            }
        }

        protected internal override void OnDestroy()
        {
            NetworkScene.Unregister(this);
            if (!Despawned && SpawnedFrom != null && IsMine) NetworkScene.SendDespawn(this);
        }

        private static IEnumerable<Rendering.Camera> CamerasUnder(GameObject gameObject)
        {
            foreach (Rendering.Camera camera in gameObject.GetComponents<Rendering.Camera>()) yield return camera;
            foreach (Transform child in gameObject.Transform.Children)
            {
                foreach (Rendering.Camera camera in CamerasUnder(child.GameObject)) yield return camera;
            }
        }

        /// <summary>
        /// A number for an object placed in the scene, the same on every machine: worked out from where it is among the
        /// scene's objects, by name. The top bit's set, so it never clashes with a spawned object's.
        /// </summary>
        private static uint SceneId(GameObject gameObject)
        {
            uint hash = 2166136261;
            for (Transform? at = gameObject.Transform; at != null; at = at.Parent)
            {
                foreach (char c in at.GameObject.Name + "/" + at.SiblingIndex) hash = (hash ^ c) * 16777619;
            }

            return hash | 0x80000000;
        }
    }
}
