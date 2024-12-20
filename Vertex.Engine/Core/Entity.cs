using Vertex.Engine.Components;

namespace Vertex.Engine.Core
{
    public class Entity
    {
        public string Name { get; set; }
        public Transform Transform { get; }

        private readonly List<Component> _components = new();
        private bool _isActive = true;

        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;
                foreach (var component in _components)
                    component.IsEnabled = value;
            }
        }

        public Entity(string name = "Entity")
        {
            Name = name;
            Transform = AddComponent<Transform>();
        }

        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T();
            component.Entity = this;
            _components.Add(component);
            return component;
        }

        public T GetComponent<T>() where T : Component
        {
            return _components.OfType<T>().FirstOrDefault();
        }

        public bool TryGetComponent<T>(out T component) where T : Component
        {
            component = GetComponent<T>();
            return component != null;
        }

        public void RemoveComponent<T>() where T : Component
        {
            var component = GetComponent<T>();
            if (component != null && component != Transform)
            {
                component.OnDestroy();
                _components.Remove(component);
            }
        }
    }
}