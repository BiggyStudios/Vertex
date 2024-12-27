using Vertex.Engine.Rendering;

namespace Vertex.Engine.Core.Components
{
    /// <summary>
    /// Component responsible for rendering a mesh with a material.
    /// </summary>
    public class MeshRenderer : Component, IRenderable
    {
        private Mesh? _mesh;
        private Material? _material;

        /// <summary>
        /// Gets or sets the mesh.
        /// </summary>
        public Mesh? Mesh
        {
            get => _mesh;
            set => _mesh = value;
        }

        /// <summary>
        /// Gets or sets the material.
        /// </summary>
        public Material? Material
        {
            get => _material;
            set => _material = value;
        }
        
        /// <summary>
        /// Renders the mesh using the assigned material and current transform.
        /// Updates shader uniforms with model, view, and projection matrices.
        /// </summary>
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