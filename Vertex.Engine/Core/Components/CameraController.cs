using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Vertex.Engine.Core.Components
{
    public class CameraController : Component
    {
        private bool _firstMove = true;
        private Vector2 _lastPos;
        private Camera _camera;
        private const float _cameraSpeed = 5.5f;
        private const float _sensitivity = 0.2f;

        public override void Start()
        {
            _camera = GameObject.GetComponent<Camera>();
        }

        public override void Update(double delaTime)
        {
            var window = Application.Instance.Window;
            if (window == null) return;

            var input = window.KeyboardState;
            var mouse = window.MouseState;

            if (input.IsKeyDown(Keys.W))
                _camera.Transform.Position += _camera.Front * _cameraSpeed * (float)delaTime;
            if (input.IsKeyDown(Keys.S))
                _camera.Transform.Position -= _camera.Front * _cameraSpeed * (float)delaTime;
            if (input.IsKeyDown(Keys.D))
                _camera.Transform.Position += _camera.Transform.Right * _cameraSpeed * (float)delaTime;
            if (input.IsKeyDown(Keys.A))
                _camera.Transform.Position -= _camera.Transform.Right * _cameraSpeed * (float)delaTime;
            if (input.IsKeyDown(Keys.Space))
                _camera.Transform.Position += _camera.Transform.Up * _cameraSpeed * (float)delaTime;
            if (input.IsKeyDown(Keys.LeftShift))
                _camera.Transform.Position -= _camera.Transform.Up * _cameraSpeed * (float)delaTime;

            if (_firstMove)
            {
                _lastPos = new Vector2(mouse.X, mouse.Y);
                _firstMove = false;
            }

            else
            {
                var deltaX = mouse.X - _lastPos.X;
                var deltaY = mouse.Y - _lastPos.Y;
                _lastPos = new Vector2(mouse.X, mouse.Y);

                _camera.Transform.Rotation += new Vector3(-deltaY * _sensitivity, deltaX * _sensitivity, 0.0f);
            }
        }
    }
}