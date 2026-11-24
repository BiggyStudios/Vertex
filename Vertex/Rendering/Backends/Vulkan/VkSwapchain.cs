using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// The window's images that finished frames are shown in, one at a time, in turn. Shown in step with the screen's
    /// refresh (like OpenGL with vsync), so frames never tear. Made again whenever the window changes size.
    /// </summary>
    internal sealed unsafe class VkSwapchain : IDisposable
    {
        private readonly VkContext _context;
        private SwapchainKHR _swapchain;

        public VkImageResource[] Images { get; private set; } = [];

        /// <summary>Signalled when each image is drawn and can be shown. One per image, as presenting may hold on to it.</summary>
        public VkSemaphore[] RenderFinished { get; private set; } = [];

        public Format Format { get; private set; }
        /// <summary>The window size the images were last made for; the images themselves may differ (<see cref="Width"/>).</summary>
        public int RequestedWidth { get; private set; }
        public int RequestedHeight { get; private set; }

        public int Width { get; private set; }
        public int Height { get; private set; }

        public VkSwapchain(VkContext context, int width, int height)
        {
            _context = context;
            Format = PickFormat().Format;
            Create(width, height);
        }

        /// <summary>Makes the images again at the window's new size. Waits for the GPU first, as they may still be in use.</summary>
        public void Recreate(int width, int height)
        {
            _context.Vk.DeviceWaitIdle(_context.Device);
            SwapchainKHR old = _swapchain;
            DestroyImages();
            Create(width, height, old);
            _context.KhrSwapchain.DestroySwapchain(_context.Device, old, null);
        }

        /// <summary>The next image to draw into, or null if the swapchain needs making again (the window changed).</summary>
        public uint? Acquire(VkSemaphore imageAvailable)
        {
            uint index = 0;
            Result result = _context.KhrSwapchain.AcquireNextImage(_context.Device, _swapchain, ulong.MaxValue, imageAvailable, default, &index);
            if (result == Result.ErrorOutOfDateKhr) return null;
            if (result != Result.Success && result != Result.SuboptimalKhr) VkContext.Check(result, "get the next image to draw into");
            return index;
        }

        /// <summary>For waiting on presents (see <see cref="VkContext.PresentWait"/>).</summary>
        public SwapchainKHR Handle => _swapchain;

        /// <summary>
        /// Shows image <paramref name="index"/> once it's drawn, tagged with <paramref name="presentId"/> (0 for none) to
        /// wait on later. False if the swapchain needs making again.
        /// </summary>
        public bool Present(uint index, ulong presentId = 0)
        {
            SwapchainKHR swapchain = _swapchain;
            VkSemaphore wait = RenderFinished[index];
            PresentIdKHR id = new() { SType = VkContext.PresentIdType, SwapchainCount = 1, PPresentIds = &presentId };
            PresentInfoKHR info = new()
            {
                PNext = presentId != 0 ? &id : null,
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &wait,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &index
            };

            Result result = _context.KhrSwapchain.QueuePresent(_context.Queue, &info);
            if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) return false;
            VkContext.Check(result, "show the frame");
            return true;
        }

        private SurfaceFormatKHR PickFormat()
        {
            uint count = 0;
            _context.KhrSurface.GetPhysicalDeviceSurfaceFormats(_context.PhysicalDevice, _context.Surface, &count, null);
            var formats = new SurfaceFormatKHR[count];
            fixed (SurfaceFormatKHR* pointer = formats) _context.KhrSurface.GetPhysicalDeviceSurfaceFormats(_context.PhysicalDevice, _context.Surface, &count, pointer);

            // Not an sRGB format: the shaders already write screen colours, as OpenGL's default framebuffer takes them.
            foreach (Format wanted in new[] { Format.B8G8R8A8Unorm, Format.R8G8B8A8Unorm })
            {
                foreach (SurfaceFormatKHR format in formats)
                {
                    if (format.Format == wanted && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr) return format;
                }
            }

            return formats[0];
        }

        /// <summary>Whether frames wait for the screen's refresh. Takes effect when the swapchain's next made.</summary>
        public bool VSync { get; set; } = true;

        /// <summary>How the swapchain presents now: FIFO with vsync, otherwise Mailbox or Immediate where the system has them.</summary>
        public PresentModeKHR PresentMode { get; private set; } = PresentModeKHR.FifoKhr;

        private PresentModeKHR PickPresentMode()
        {
            if (VSync) return PresentModeKHR.FifoKhr;

            uint count = 0;
            _context.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_context.PhysicalDevice, _context.Surface, &count, null);
            var modes = new PresentModeKHR[count];
            fixed (PresentModeKHR* pointer = modes) _context.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_context.PhysicalDevice, _context.Surface, &count, pointer);
            if (modes.Contains(PresentModeKHR.MailboxKhr)) return PresentModeKHR.MailboxKhr;
            if (modes.Contains(PresentModeKHR.ImmediateKhr)) return PresentModeKHR.ImmediateKhr;
            return PresentModeKHR.FifoKhr;
        }

        private void Create(int width, int height, SwapchainKHR old = default)
        {
            _context.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(_context.PhysicalDevice, _context.Surface, out SurfaceCapabilitiesKHR capabilities);

            Extent2D extent = capabilities.CurrentExtent.Width != uint.MaxValue
                ? capabilities.CurrentExtent
                : new Extent2D(
                    Math.Clamp((uint)width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                    Math.Clamp((uint)height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));

            // As few as the driver allows (two where it can): one on screen and one being drawn. Every extra image is
            // another finished frame that can queue up in front of the screen, a whole refresh of input lag each.
            // Without vsync, Mailbox replaces the waiting image with each newer one (no tearing, no waiting); it needs a third
            // image to draw into while one's shown and one waits. Immediate (which can tear) if there's no Mailbox.
            PresentModeKHR presentMode = PickPresentMode();
            uint imageCount = Math.Max(capabilities.MinImageCount, presentMode == PresentModeKHR.MailboxKhr ? 3u : 2u);
            if (capabilities.MaxImageCount > 0) imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

            CompositeAlphaFlagsKHR alpha = (capabilities.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0
                ? CompositeAlphaFlagsKHR.OpaqueBitKhr
                : CompositeAlphaFlagsKHR.InheritBitKhr;

            SwapchainCreateInfoKHR info = new()
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _context.Surface,
                MinImageCount = imageCount,
                ImageFormat = Format,
                ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = alpha,
                PresentMode = presentMode,
                Clipped = true,
                OldSwapchain = old
            };

            VkContext.Check(_context.KhrSwapchain.CreateSwapchain(_context.Device, &info, null, out _swapchain), "create the swapchain");
            PresentMode = presentMode;
            RequestedWidth = width;
            RequestedHeight = height;
            Width = (int)extent.Width;
            Height = (int)extent.Height;

            uint count = 0;
            _context.KhrSwapchain.GetSwapchainImages(_context.Device, _swapchain, &count, null);
            var images = new Image[count];
            fixed (Image* pointer = images) _context.KhrSwapchain.GetSwapchainImages(_context.Device, _swapchain, &count, pointer);

            Images = new VkImageResource[count];
            RenderFinished = new VkSemaphore[count];
            for (int i = 0; i < count; i++)
            {
                ImageViewCreateInfo viewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = images[i],
                    ViewType = ImageViewType.Type2D,
                    Format = Format,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
                };
                VkContext.Check(_context.Vk.CreateImageView(_context.Device, &viewInfo, null, out ImageView view), "create a swapchain image view");

                // The swapchain owns the images themselves; only the views are ours.
                Images[i] = new VkImageResource(images[i], default, view, Format, ImageAspectFlags.ColorBit, Width, Height, 1) { Owned = false };

                SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
                VkContext.Check(_context.Vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, out RenderFinished[i]), "create a semaphore");
            }
        }

        private void DestroyImages()
        {
            foreach (VkImageResource image in Images) image.Destroy(_context);
            foreach (VkSemaphore semaphore in RenderFinished) _context.Vk.DestroySemaphore(_context.Device, semaphore, null);
            Images = [];
            RenderFinished = [];
        }

        public void Dispose()
        {
            DestroyImages();
            _context.KhrSwapchain.DestroySwapchain(_context.Device, _swapchain, null);
        }
    }
}
