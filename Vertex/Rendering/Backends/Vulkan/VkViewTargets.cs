using System.Numerics;

using Silk.NET.Vulkan;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// The images a view is drawn into before it's finished: the scene's colour, what glows, and depth, then the glow
    /// halved several times for bloom. One set for the screen and one for each render texture, as they're sized to match.
    /// Like the OpenGL backend's GLBloom.
    /// </summary>
    internal sealed class VkViewTargets
    {
        public const Format ColorFormat = Format.R8G8B8A8Unorm;
        public const Format GlowFormat = Format.R16G16B16A16Sfloat;

        // Each one is half the size of the one before, so the last is 1/64 of the view and the glow spreads wide.
        private const int _maxLevels = 6;

        private readonly VkContext _context;

        public VkImageResource? Color { get; private set; }
        public VkImageResource? Glow { get; private set; }
        public VkImageResource? Depth { get; private set; }
        public List<VkImageResource> BloomLevels { get; } = new();
        public int Width { get; private set; }
        public int Height { get; private set; }

        public VkViewTargets(VkContext context)
        {
            _context = context;
        }

        /// <summary>Makes the images match the view. The old ones are handed to <paramref name="deleteLater"/>, as the GPU may still be using them.</summary>
        public void Ensure(int width, int height, Action<Action> deleteLater)
        {
            if (width == Width && height == Height && Color != null) return;

            Release(deleteLater);
            Width = width;
            Height = height;

            const ImageUsageFlags attachment = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
            Color = _context.CreateImage(width, height, ColorFormat, attachment);
            Glow = _context.CreateImage(width, height, GlowFormat, attachment);
            Depth = _context.CreateImage(width, height, _context.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit);

            int levelWidth = width, levelHeight = height;
            for (int i = 0; i < _maxLevels; i++)
            {
                levelWidth = Math.Max(levelWidth / 2, 1);
                levelHeight = Math.Max(levelHeight / 2, 1);
                BloomLevels.Add(_context.CreateImage(levelWidth, levelHeight, GlowFormat, attachment));

                if (levelWidth <= 2 || levelHeight <= 2) break;
            }
        }

        public void Release(Action<Action> deleteLater)
        {
            var images = new List<VkImageResource>(BloomLevels);
            if (Color != null) images.Add(Color);
            if (Glow != null) images.Add(Glow);
            if (Depth != null) images.Add(Depth);
            BloomLevels.Clear();
            Color = Glow = Depth = null;
            Width = Height = 0;

            if (images.Count > 0) deleteLater(() => images.ForEach(image => image.Destroy(_context)));
        }
    }

    /// <summary>
    /// Shadows from the sun: the scene's depth drawn from the sun's point of view, covering an area that follows the
    /// camera. The same placement as the OpenGL backend's GLShadowMap.
    /// </summary>
    internal sealed class VkShadowMap
    {
        public const int Size = 2048;

        // How far the sun "sees", centred on the shadowed area. Tall enough for the surf map and anything above it.
        private const float _depthRange = 300f;

        public VkImageResource Image { get; }

        /// <summary>World to shadow-map space, from the last <see cref="Place"/>.</summary>
        public Matrix4x4 LightSpace { get; private set; }

        /// <summary>The size of one shadow-map pixel in world units, from the last <see cref="Place"/>.</summary>
        public float TexelWorldSize { get; private set; }

        public VkShadowMap(VkContext context)
        {
            Image = context.CreateImage(Size, Size, context.DepthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        }

        /// <summary>Works out where the shadowed area is this view: around the camera, a little ahead of it.</summary>
        public void Place(Vector3 cameraPosition, Vector3 cameraForward, Vector3 sunDirection, float distance)
        {
            float halfSize = MathF.Max(distance, 1f);
            TexelWorldSize = halfSize * 2f / Size;

            Vector3 centre = cameraPosition + (cameraForward with { Y = 0f }) * (halfSize * 0.4f);
            Vector3 up = MathF.Abs(sunDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

            // Snap the centre to whole shadow-map pixels, so shadow edges don't shimmer as the camera moves.
            Matrix4x4 rotation = Matrix4x4.CreateLookAt(Vector3.Zero, sunDirection, up);
            Vector3 local = Vector3.Transform(centre, rotation);
            local.X = MathF.Round(local.X / TexelWorldSize) * TexelWorldSize;
            local.Y = MathF.Round(local.Y / TexelWorldSize) * TexelWorldSize;
            Matrix4x4.Invert(rotation, out Matrix4x4 inverseRotation);
            centre = Vector3.Transform(local, inverseRotation);

            Matrix4x4 view = Matrix4x4.CreateLookAt(centre - sunDirection * (_depthRange * 0.5f), centre, up);
            Matrix4x4 projection = Matrix4x4.CreateOrthographic(halfSize * 2f, halfSize * 2f, 0f, _depthRange);
            LightSpace = view * projection;
        }
    }
}
