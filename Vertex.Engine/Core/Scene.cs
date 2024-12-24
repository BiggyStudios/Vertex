using Vertex.Engine.Core.Components;

namespace Vertex.Engine.Core
{
    public class Scene
    {
        private readonly List<GameObject> _gameObjects = new();
        private readonly List<GameObject> _gameObjectsToAdd = new();
        private readonly List<GameObject> _gameObjectsToRemove = new();

        public string Name { get; }
        public bool IsActive { get; set; } = true;

        public Scene(string name)
        {
            Name = name;
        }

        public GameObject CreateGameObject(string name = "GameObject")
        {
            var gameObject = new GameObject(name);
            gameObject.Scene = this;
            _gameObjectsToAdd.Add(gameObject);

            return gameObject;
        }

        public Camera? GetMainCamera()
        {
            return _gameObjects
                .Where(go => go.IsActive)
                .Select(go => go.GetComponent<Camera>())
                .FirstOrDefault(camera => camera != null);
        }

        public void RemoveGameObject(GameObject gameObject)
        {
            if (_gameObjects.Contains(gameObject))
            {
                _gameObjectsToRemove.Add(gameObject);
            }
        }

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