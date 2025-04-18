using Vertex.Engine.Core.Components;

namespace Vertex.Engine.Core
{
    /// <summary>
    /// Manages and contains gameobjects
    /// </summary>
    public class Scene
    {
        private readonly List<GameObject> _gameObjects = new();
        private readonly List<GameObject> _gameObjectsToAdd = new();
        private readonly List<GameObject> _gameObjectsToRemove = new();

        /// <summary>
        /// Games the name of the scene.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets or sets whether the scene is active or not.
        /// </summary>
        public bool IsActive { get; set; } = true;


        /// <summary>
        /// Initializes a new instance of the Scene class.
        /// </summary>
        /// <param name="name">The name of the scene</param>
        public Scene(string name)
        {
            Name = name;
        }


        /// <summary>
        /// Creates a new GameObject and adds it to the scene.
        /// </summary>
        /// <param name="name">Optional name for the GameObject. Defaults to "GameObject".</param>
        /// <returns>The new created GameObject instance.</returns>
        public GameObject CreateGameObject(string name = "GameObject")
        {
            var gameObject = new GameObject(name);
            gameObject.Scene = this;
            _gameObjectsToAdd.Add(gameObject);

            return gameObject;
        }

        /// <summary>
        /// Gets the first active Camera component in the scene.
        /// </summary>
        /// <returns>The main camera if found null otherwise.</returns>
        public Camera? GetMainCamera()
        {
            return _gameObjects
                .Where(go => go.IsActive)
                .Select(go => go.GetComponent<Camera>())
                .FirstOrDefault(camera => camera != null);
        }

        /// <summary>
        /// Removes a GameObject from the scene.
        /// </summary>
        /// <param name="gameObject">The GameObject to remove.</param>
        public void RemoveGameObject(GameObject gameObject)
        {
            if (_gameObjects.Contains(gameObject))
            {
                _gameObjectsToRemove.Add(gameObject);
            }
        }

        //Update loop for the scene
        internal void Update(double deltaTime)
        {
            if (!IsActive) return;

            foreach (var gameObject in _gameObjectsToAdd)
            {
                _gameObjects.Add(gameObject);
            }
            _gameObjectsToAdd.Clear();

            foreach (var gameObject in _gameObjectsToRemove)
            {
                _gameObjects.Remove(gameObject);
            }
            _gameObjectsToRemove.Clear();

            foreach (var gameObject in _gameObjects)
            {
                if (gameObject.IsActive)
                {
                    gameObject.Update(deltaTime);
                }
            }
        }

        //Gets all IRenderable components and renders them
        internal void Render()
        {
            if (!IsActive) return;

            foreach (var gameObject in _gameObjects)
            {
                if (gameObject.IsActive)
                {
                    foreach (var component in gameObject.GetRenderableComponents())
                    {
                        component.Render();
                    }
                }
            }
        }
    }
}