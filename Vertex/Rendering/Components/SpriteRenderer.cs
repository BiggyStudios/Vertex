using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Draws a picture flat in the world at its object: a 2D game's characters, tiles and pickups, or a flat cut-out in a
    /// 3D one. Its size comes from the picture's pixels and <see cref="PixelsPerUnit"/>, times the object's scale. A
    /// picture with several frames on it in a grid (a sprite sheet) shows one, chosen by <see cref="Frame"/>; a
    /// <see cref="SpriteAnimator"/> plays through them. With no texture, it's a square of its colour a unit across.
    /// </summary>
    public sealed class SpriteRenderer : FlatRenderer
    {
        [Tooltip("The picture. Its see-through parts show what's behind")]
        public Texture? Texture { get; set; }

        [Tooltip("Multiplied with the picture: white leaves it as it is. Its alpha fades it out")]
        public Color Color { get; set; } = Color.White;

        [Tooltip("How many of the picture's pixels make one unit (a metre) across in the world")]
        public float PixelsPerUnit { get; set; } = 100f;

        [Tooltip("The point of the picture at the object's position: (0.5, 0.5) its middle, (0.5, 0) the middle of its bottom edge")]
        public Vector2 Pivot { get; set; } = new(0.5f);

        [Tooltip("Shows it mirrored left to right: a character turning round")]
        public bool FlipX { get; set; }

        [Tooltip("Shows it upside down")]
        public bool FlipY { get; set; }

        [Tooltip("Draws it with sharp, square pixels rather than smoothing between them: for pixel art")]
        public bool Pixelated { get; set; }

        [Header("Sprite Sheet")]
        [Tooltip("How many frames across the picture is cut into")]
        public int Columns { get; set; } = 1;

        [Tooltip("How many frames down the picture is cut into")]
        public int Rows { get; set; } = 1;

        [Tooltip("Which frame it shows, counting along each row from the top-left, from 0")]
        public int Frame { get; set; }

        /// <summary>How many frames the sheet has.</summary>
        public int FrameCount => Math.Max(Columns, 1) * Math.Max(Rows, 1);

        /// <summary>The size of one frame in the world, in units, before the object's scale.</summary>
        public Vector2 Size
        {
            get
            {
                if (Texture is not { Width: > 0, Height: > 0 } texture) return Vector2.One;
                Vector2 pixels = new Vector2(texture.Width, texture.Height) / new Vector2(Math.Max(Columns, 1), Math.Max(Rows, 1));
                return pixels / MathF.Max(PixelsPerUnit, 0.001f);
            }
        }

        internal override void Collect(List<WorldQuad> into, in ViewAxes view)
        {
            int columns = Math.Max(Columns, 1), rows = Math.Max(Rows, 1);
            int frame = ((Frame % FrameCount) + FrameCount) % FrameCount;
            int column = frame % columns, row = frame / columns;
            var uvMin = new Vector2(column / (float)columns, row / (float)rows);
            var uvMax = new Vector2((column + 1) / (float)columns, (row + 1) / (float)rows);
            if (FlipX) (uvMin.X, uvMax.X) = (uvMax.X, uvMin.X);
            if (FlipY) (uvMin.Y, uvMax.Y) = (uvMax.Y, uvMin.Y);

            Vector2 size = Size;
            float left = -Pivot.X * size.X, right = left + size.X;
            float bottom = -Pivot.Y * size.Y, top = bottom + size.Y;

            Matrix4x4 world = DrawMatrix(view);
            into.Add(new WorldQuad(Texture,
                Vector3.Transform(new Vector3(left, top, 0f), world),
                Vector3.Transform(new Vector3(right, top, 0f), world),
                Vector3.Transform(new Vector3(right, bottom, 0f), world),
                Vector3.Transform(new Vector3(left, bottom, 0f), world),
                uvMin, uvMax, Color) { Pixelated = Pixelated });
        }
    }

    /// <summary>
    /// Plays through the frames of its object's <see cref="SpriteRenderer"/>'s sprite sheet: a character walking, a coin
    /// spinning, a fire flickering. It plays from <see cref="FirstFrame"/> for <see cref="FrameCount"/> frames.
    /// </summary>
    public sealed class SpriteAnimator : Component
    {
        [Tooltip("The frame it starts on, counting from 0")]
        public int FirstFrame { get; set; }

        [Tooltip("How many frames it plays. 0 plays to the end of the sheet")]
        public int FrameCount { get; set; }

        [Tooltip("How many frames a second")]
        [Range(0f, 60f)]
        public float FramesPerSecond { get; set; } = 12f;

        [Tooltip("Starts again from the first frame when it reaches the last")]
        public bool Loop { get; set; } = true;

        [Tooltip("Starts playing as soon as the game does")]
        public bool PlayOnStart { get; set; } = true;

        /// <summary>Whether it's playing.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>Raised when it plays its last frame, if it doesn't loop.</summary>
        public event Action? Finished;

        private float _time;

        /// <summary>Plays from the first frame; or, with a first frame and count, that stretch of the sheet ("walk" might be 4 to 7).</summary>
        public void Play(int? firstFrame = null, int? frameCount = null)
        {
            if (firstFrame is int first) FirstFrame = first;
            if (frameCount is int count) FrameCount = count;
            _time = 0f;
            IsPlaying = true;
            Show();
        }

        /// <summary>Stops on the frame it's showing.</summary>
        public void Stop() => IsPlaying = false;

        protected internal override void Start()
        {
            if (PlayOnStart) Play();
        }

        protected internal override void Update()
        {
            if (!IsPlaying) return;

            _time += Time.DeltaTime;
            int frames = Frames();
            if (!Loop && _time * FramesPerSecond >= frames)
            {
                _time = frames / MathF.Max(FramesPerSecond, 0.001f);
                IsPlaying = false;
                Show();
                Finished?.Invoke();
                return;
            }

            Show();
        }

        private int Frames()
        {
            int sheet = GetComponent<SpriteRenderer>()?.FrameCount ?? 1;
            return FrameCount > 0 ? FrameCount : Math.Max(sheet - FirstFrame, 1);
        }

        private void Show()
        {
            if (GetComponent<SpriteRenderer>() is not { } sprite) return;

            int frames = Frames();
            int step = (int)MathF.Floor(_time * FramesPerSecond);
            step = Loop ? step % frames : Math.Min(step, frames - 1);
            sprite.Frame = FirstFrame + step;
        }
    }
}
