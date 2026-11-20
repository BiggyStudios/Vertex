using Silk.NET.Vulkan;

using Vertex.Rendering.Shaders;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// The images a view is drawn through before it's finished: the scene in linear light (half floats, so bright
    /// things stay bright), the part of it that's soft light from all around (for ambient occlusion to take from), what glows (for bloom), depth,
    /// the occlusion itself, bloom (the bright parts halved again and again), and the finished picture before anti-aliasing.
    /// One set for the screen and one for each render texture, as they're sized to match. Like the OpenGL backend's GLViewTargets.
    /// </summary>
    internal sealed class VkViewTargets
    {
        public const Format HdrFormat = Format.R16G16B16A16Sfloat;
        /// <summary>Ambient occlusion: how open each point is, and how far away (see ShaderLibrary.OcclusionFragment).</summary>
        public const Format OcclusionFormat = Format.R16G16Sfloat;

        /// <summary>The volumetric fog's cells: light (rgb) and density or what shows through (a).</summary>
        public const Format FogFormat = Format.R16G16B16A16Sfloat;

        /// <summary>The finished picture: render textures, and the one anti-aliasing reads.</summary>
        public const Format OutputFormat = Format.R8G8B8A8Unorm;

        // Each one is half the size of the one before, so the last is 1/64 of the view and the glow spreads wide.
        private const int _maxLevels = 6;

        private readonly VkContext _context;

        public VkImageResource? Color { get; private set; }
        public VkImageResource? Ambient { get; private set; }
        public VkImageResource? Glow { get; private set; }
        public VkImageResource? Depth { get; private set; }
        public VkImageResource? Occlusion { get; private set; }
        public VkImageResource? OcclusionBlur { get; private set; }
        public VkImageResource? Finished { get; private set; }
        public List<VkImageResource> BloomLevels { get; } = new();
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>
        /// The volumetric fog's images, made the first time the view has fog: its lit fog, twice (this frame's and last
        /// frame's, taking turns), and the fog added up from the camera. Each is a layer per slice, with a view of each
        /// layer to draw into.
        /// </summary>
        public VkImageResource?[] FogLight { get; } = new VkImageResource?[2];
        public VkImageResource? FogTotal { get; private set; }
        public List<VkImageResource>[] FogLightLayers { get; } = [new(), new()];
        public List<VkImageResource> FogTotalLayers { get; } = new();
        public Shaders.VolumetricFog Fog { get; } = new();

        /// <summary>
        /// Two more images like the finished picture, made the first time the view has post effects (see PostEffect): each
        /// effect reads one and draws into the other.
        /// </summary>
        public VkImageResource?[] Effects { get; } = new VkImageResource?[2];

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
            Color = _context.CreateImage(width, height, HdrFormat, attachment);
            Ambient = _context.CreateImage(width, height, _context.LightFormat, attachment);
            Glow = _context.CreateImage(width, height, _context.LightFormat, attachment);
            Depth = _context.CreateImage(width, height, _context.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit);
            // At half size: it's soft, and blurred anyway.
            int halfWidth = Math.Max((width + 1) / 2, 1), halfHeight = Math.Max((height + 1) / 2, 1);
            Occlusion = _context.CreateImage(halfWidth, halfHeight, OcclusionFormat, attachment);
            OcclusionBlur = _context.CreateImage(halfWidth, halfHeight, OcclusionFormat, attachment);
            Finished = _context.CreateImage(width, height, OutputFormat, attachment);

            int levelWidth = width, levelHeight = height;
            for (int i = 0; i < _maxLevels; i++)
            {
                levelWidth = Math.Max(levelWidth / 2, 1);
                levelHeight = Math.Max(levelHeight / 2, 1);
                BloomLevels.Add(_context.CreateImage(levelWidth, levelHeight, _context.LightFormat, attachment));

                if (levelWidth <= 2 || levelHeight <= 2) break;
            }
        }

        /// <summary>Makes <see cref="Effects"/>, the size of the view, if they aren't yet.</summary>
        public void EnsureEffects()
        {
            if (Effects[0] != null) return;

            const ImageUsageFlags attachment = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
            for (int i = 0; i < Effects.Length; i++) Effects[i] = _context.CreateImage(Width, Height, OutputFormat, attachment);
        }

        /// <summary>
        /// Makes the fog's images <paramref name="width"/> by <paramref name="height"/> cells, in no particular layout.
        /// True if they were just made.
        /// </summary>
        public bool EnsureFog(VkContext context, int width, int height, Action<Action> deleteLater)
        {
            if (FogTotal != null && FogTotal.Width == width && FogTotal.Height == height) return false;

            ReleaseFog(deleteLater);
            for (int i = 0; i < 2; i++) FogLight[i] = CreateFogImage(context, width, height, FogLightLayers[i]);
            FogTotal = CreateFogImage(context, width, height, FogTotalLayers);
            return true;
        }

        private static VkImageResource CreateFogImage(VkContext context, int width, int height, List<VkImageResource> layers)
        {
            VkImageResource image = context.CreateImage(width, height, FogFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
                layers: ShaderLibrary.FogSlices);
            for (uint i = 0; i < ShaderLibrary.FogSlices; i++) layers.Add(context.CreateLayerView(image, i));
            return image;
        }

        private void ReleaseFog(Action<Action> deleteLater)
        {
            var images = new List<VkImageResource>();
            foreach (List<VkImageResource> layers in new[] { FogLightLayers[0], FogLightLayers[1], FogTotalLayers })
            {
                images.AddRange(layers);
                layers.Clear();
            }

            foreach (VkImageResource? image in new[] { FogLight[0], FogLight[1], FogTotal })
            {
                if (image != null) images.Add(image);
            }

            FogLight[0] = FogLight[1] = FogTotal = null;
            if (images.Count > 0) deleteLater(() => images.ForEach(image => image.Destroy(_context)));
        }

        public void Release(Action<Action> deleteLater)
        {
            ReleaseFog(deleteLater);
            var images = new List<VkImageResource>(BloomLevels);
            foreach (VkImageResource? image in new[] { Color, Ambient, Glow, Depth, Occlusion, OcclusionBlur, Finished, Effects[0], Effects[1] })
            {
                if (image != null) images.Add(image);
            }

            BloomLevels.Clear();
            Color = Ambient = Glow = Depth = Occlusion = OcclusionBlur = Finished = Effects[0] = Effects[1] = null;
            Width = Height = 0;

            if (images.Count > 0) deleteLater(() => images.ForEach(image => image.Destroy(_context)));
        }
    }

    /// <summary>
    /// The sun's shadow maps: one depth image array, a layer per cascade (see <see cref="ShadowCascades"/>), read
    /// whole by the scene shaders and drawn into a layer at a time.
    /// </summary>
    internal sealed class VkShadowMaps
    {
        public VkImageResource Array { get; }
        public VkImageResource[] Layers { get; }

        /// <summary>How many texels across each layer is.</summary>
        public int Size { get; }

        public VkShadowMaps(VkContext context, int size, int layers = ShadowCascades.Count)
        {
            Size = size;
            Array = context.CreateImage(size, size, context.ShadowDepthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                layers: (uint)layers);

            Layers = new VkImageResource[layers];
            for (uint i = 0; i < Layers.Length; i++) Layers[i] = context.CreateLayerView(Array, i);
        }

        public void Destroy(VkContext context)
        {
            foreach (VkImageResource layer in Layers) layer.Destroy(context);
            Array.Destroy(context);
        }
    }
}
