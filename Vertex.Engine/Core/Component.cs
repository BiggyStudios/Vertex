using Vertex.Engine.Components;

namespace Vertex.Engine.Core
{
    public abstract class Component
    {
        private Entity _entity;

        public Entity Entity
        {
            get => _entity;
            internal set
            {
                if (_entity == value) return;
                _entity = value;

            }
        }

        public Transform Transform => Entity.Transform;
        public bool IsEnabled { get; set; } = true;

        protected virtual void OnAttached() { }
        public virtual void Start() { }
        public virtual void Update(float deltaTime) { }
        public virtual void OnDestroy() { }
    }
}