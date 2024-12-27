using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Vertex.Engine.Core.Components
{
    /// <summary>
    /// Component that handles camera movement and rotation based on keyboard and mouse input.
    /// </summary>
    public class CameraController : Component
    {
        private bool _firstMove = true;
        private Vector2 _lastPos;
        private Camera? _camera;
        private const float _cameraSpeed = 5.5f;
        private const float _sensitivity = 8f;

        private float _pitch;
        private float _yaw = -MathHelper.PiOver2;

        public override void Start()
        {
            _camera = GameObject.GetComponent<Camera>();
            UpdateVectors();
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
                _camera.Transform.Position += _camera.Right * _cameraSpeed * (float)delaTime;
            if (input.IsKeyDown(Keys.A))
                _camera.Transform.Position -= _camera.Right * _cameraSpeed * (float)delaTime;
            if (input.IsKeyDown(Keys.Space))
                _camera.Transform.Position += _camera.Up * _cameraSpeed * (float)delaTime;
            if (input.IsKeyDown(Keys.LeftShift))
                _camera.Transform.Position -= _camera.Up * _cameraSpeed * (float)delaTime;

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

                _yaw += deltaX * _sensitivity * (float)delaTime;
                _pitch -= deltaY * _sensitivity * (float)delaTime;
                _pitch = Math.Clamp(_pitch, -89f, 89f);

                UpdateVectors();
            }
        }

        private void UpdateVectors()
        {
            var front = new Vector3
            {
                X = MathF.Cos(MathHelper.DegreesToRadians(_pitch)) * MathF.Cos(MathHelper.DegreesToRadians(_yaw)),
                Y = MathF.Sin(MathHelper.DegreesToRadians(_pitch)),
                Z = MathF.Cos(MathHelper.DegreesToRadians(_pitch)) * MathF.Sin(MathHelper.DegreesToRadians(_yaw))
            };

            _camera.UpdateVectors(front);
        }
    }
}