using Vertex.Engine.Rendering;

namespace Vertex.Engine.Core.Components
{
    public class MeshRenderer : Component, IRenderable
    {
        private Mesh? _mesh;
        private Material? _material;

        public Mesh? Mesh
        {
            get => _mesh;
            set => _mesh = value;
        }

        public Material? Material
        {
            get => _material;
            set => _material = value;
        }

        public void Render()
        {
            if (_mesh == null || _material == null) return;

            _material.Use();
            _material.SetMatrix4("model", Transform.WorldMatrix);

            var camera = GameObject.Scene.GetMainCamera();
            if (camera != null)
            {
                _material.SetMatrix4("view", camera.ViewMatrix);
                _material.SetMatrix4("projection", camera.ProjectionMatrix);
            }

            _mesh.Draw();
        }
    }
}