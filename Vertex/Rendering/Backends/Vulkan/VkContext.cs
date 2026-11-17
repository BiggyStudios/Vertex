using System.Runtime.InteropServices;

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// The Vulkan instance, the chosen GPU and its device and queue, plus helpers for making buffers and images.
    /// Needs Vulkan 1.3 (dynamic rendering and synchronization2), which every desktop GPU from the last several years has.
    /// </summary>
    internal sealed unsafe class VkContext : IDisposable
    {
        private const string _validationLayer = "VK_LAYER_KHRONOS_validation";

        public readonly Vk Vk;
        public readonly Instance Instance;
        public readonly PhysicalDevice PhysicalDevice;
        public readonly Device Device;
        public readonly Queue Queue;
        public readonly uint QueueFamily;
        public readonly CommandPool CommandPool;
        public readonly KhrSurface KhrSurface;
        public readonly KhrSwapchain KhrSwapchain;
        public readonly SurfaceKHR Surface;
        public readonly PhysicalDeviceProperties Properties;
        public readonly string DeviceName;

        /// <summary>
        /// VK_KHR_present_wait, if the driver has it (with VK_KHR_present_id): waits until a given present has reached the
        /// screen. Null where it isn't supported.
        /// </summary>
        public readonly KhrPresentWait? PresentWait;

        private const string PresentIdExtension = "VK_KHR_present_id";
        private const string PresentWaitExtension = "VK_KHR_present_wait";

        // Silk.NET's StructureType doesn't list VK_KHR_present_id's two; these are their values from the Vulkan spec.
        public const StructureType PresentIdType = (StructureType)1000294000;
        private const StructureType PresentIdFeaturesType = (StructureType)1000294001;

        private bool SupportsPresentWait(PhysicalDevice device)
        {
            if (!HasDeviceExtension(device, PresentIdExtension) || !HasDeviceExtension(device, PresentWaitExtension)) return false;

            PhysicalDevicePresentWaitFeaturesKHR presentWait = new() { SType = StructureType.PhysicalDevicePresentWaitFeaturesKhr };
            PhysicalDevicePresentIdFeaturesKHR presentId = new() { SType = PresentIdFeaturesType, PNext = &presentWait };
            PhysicalDeviceFeatures2 features = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &presentId };
            Vk.GetPhysicalDeviceFeatures2(device, &features);
            return presentId.PresentId && presentWait.PresentWait;
        }

        /// <summary>The depth format for the shadow map and scene: 32-bit float if it can be filtered for soft shadows.</summary>
        public readonly Format DepthFormat;

        /// <summary>How sharply textures can be filtered on surfaces seen at a slant (anisotropic filtering); 1 if the GPU can't.</summary>
        public readonly float MaxAnisotropy = 1f;

        private readonly PhysicalDeviceMemoryProperties _memory;
        private readonly ExtDebugUtils? _debugUtils;
        private readonly DebugUtilsMessengerEXT _messenger;

        // Kept alive for as long as Vulkan might call it.
        private readonly DebugUtilsMessengerCallbackFunctionEXT? _debugCallback;

        /// <summary>
        /// Whether this computer can run the Vulkan backend: a Vulkan 1.3 GPU and driver. Doesn't need a window, so it can
        /// be asked before choosing which backend to make one for.
        /// </summary>
        public static bool IsSupported(out string reason)
        {
            try
            {
                Vk vk = Vk.GetApi();
                ApplicationInfo app = new() { SType = StructureType.ApplicationInfo, ApiVersion = Vk.MakeVersion(1, 3) };
                InstanceCreateInfo info = new() { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
                if (vk.CreateInstance(&info, null, out Instance instance) != Result.Success)
                {
                    reason = "no Vulkan 1.3 driver";
                    return false;
                }

                try
                {
                    foreach (PhysicalDevice device in vk.GetPhysicalDevices(instance))
                    {
                        if (MissingFeature(vk, device) == null)
                        {
                            reason = "";
                            return true;
                        }
                    }

                    reason = "no GPU with Vulkan 1.3, dynamic rendering and synchronization2";
                    return false;
                }
                finally
                {
                    vk.DestroyInstance(instance, null);
                }
            }
            catch (Exception exception)
            {
                reason = exception.Message;
                return false;
            }
        }

        public VkContext(IWindow window)
        {
            Vk = Vk.GetApi();
            if (window.VkSurface == null) throw new InvalidOperationException("The window wasn't made for Vulkan.");

            // Validation checks every call for mistakes. It's only there if the Vulkan SDK is installed, and it's slow,
            // so debug builds use it when they can, and release builds only when VERTEX_VULKAN_VALIDATION=1.
#if DEBUG
            bool wantValidation = Environment.GetEnvironmentVariable("VERTEX_VULKAN_VALIDATION") != "0";
#else
            bool wantValidation = Environment.GetEnvironmentVariable("VERTEX_VULKAN_VALIDATION") == "1";
#endif
            bool validation = wantValidation && HasLayer(_validationLayer);

            byte** windowExtensions = window.VkSurface.GetRequiredExtensions(out uint windowExtensionCount);
            var extensions = new List<string>();
            for (int i = 0; i < windowExtensionCount; i++) extensions.Add(SilkMarshal.PtrToString((nint)windowExtensions[i])!);
            if (validation) extensions.Add(ExtDebugUtils.ExtensionName);

            nint appName = SilkMarshal.StringToPtr("Vertex");
            nint extensionNames = SilkMarshal.StringArrayToPtr(extensions);
            nint layerNames = SilkMarshal.StringArrayToPtr(validation ? [_validationLayer] : []);
            try
            {
                ApplicationInfo app = new()
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = (byte*)appName,
                    PEngineName = (byte*)appName,
                    ApiVersion = Vk.MakeVersion(1, 3)
                };

                InstanceCreateInfo info = new()
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &app,
                    EnabledExtensionCount = (uint)extensions.Count,
                    PpEnabledExtensionNames = (byte**)extensionNames,
                    EnabledLayerCount = validation ? 1u : 0u,
                    PpEnabledLayerNames = (byte**)layerNames
                };

                Check(Vk.CreateInstance(&info, null, out Instance), "create the Vulkan instance");
            }
            finally
            {
                SilkMarshal.Free(appName);
                SilkMarshal.Free(extensionNames);
                SilkMarshal.Free(layerNames);
            }

            if (validation && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils))
            {
                _debugUtils = debugUtils;
                _debugCallback = OnDebugMessage;
                DebugUtilsMessengerCreateInfoEXT messengerInfo = new()
                {
                    SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                    MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                    MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                                  DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                    PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(_debugCallback)
                };
                debugUtils.CreateDebugUtilsMessenger(Instance, &messengerInfo, null, out _messenger);
                Console.WriteLine("[Vulkan] Validation is on.");
            }

            if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface))
                throw new InvalidOperationException("Vulkan can't draw to windows here (no VK_KHR_surface).");

            Surface = window.VkSurface.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();

            (PhysicalDevice, QueueFamily) = PickDevice();
            PhysicalDeviceProperties properties = Vk.GetPhysicalDeviceProperties(PhysicalDevice);
            Properties = properties;
            DeviceName = SilkMarshal.PtrToString((nint)properties.DeviceName)!;
            _memory = Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice);
            DepthFormat = PickDepthFormat();

            float priority = 1f;
            DeviceQueueCreateInfo queueInfo = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = QueueFamily,
                QueueCount = 1,
                PQueuePriorities = &priority
            };

            PhysicalDeviceVulkan13Features features13 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan13Features,
                DynamicRendering = true,
                Synchronization2 = true
            };

            // Present wait: being told when a frame has actually reached the screen, which lets the backend keep frames
            // from queueing up in front of it (see VulkanRenderBackend.WaitForNextFrame). Used wherever it's there.
            bool presentTiming = SupportsPresentWait(PhysicalDevice);
            PhysicalDevicePresentWaitFeaturesKHR presentWait = new() { SType = StructureType.PhysicalDevicePresentWaitFeaturesKhr, PresentWait = true };
            PhysicalDevicePresentIdFeaturesKHR presentId = new() { SType = PresentIdFeaturesType, PresentId = true, PNext = &presentWait };
            if (presentTiming) features13.PNext = &presentId;

            nint deviceExtensions = SilkMarshal.StringArrayToPtr(presentTiming
                ? [KhrSwapchain.ExtensionName, PresentIdExtension, PresentWaitExtension]
                : [KhrSwapchain.ExtensionName]);
            // Sharper textures on surfaces seen at a slant, wherever the GPU has it (every desktop one does).
            PhysicalDeviceFeatures supported = Vk.GetPhysicalDeviceFeatures(PhysicalDevice);
            PhysicalDeviceFeatures enabled = new() { SamplerAnisotropy = supported.SamplerAnisotropy };
            if (supported.SamplerAnisotropy) MaxAnisotropy = MathF.Min(8f, Properties.Limits.MaxSamplerAnisotropy);

            try
            {
                DeviceCreateInfo deviceInfo = new()
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &features13,
                    PEnabledFeatures = &enabled,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = presentTiming ? 3u : 1u,
                    PpEnabledExtensionNames = (byte**)deviceExtensions
                };

                Check(Vk.CreateDevice(PhysicalDevice, &deviceInfo, null, out Device), "create the Vulkan device");
            }
            finally
            {
                SilkMarshal.Free(deviceExtensions);
            }

            if (presentTiming && Vk.TryGetDeviceExtension(Instance, Device, out KhrPresentWait wait)) PresentWait = wait;

            Vk.GetDeviceQueue(Device, QueueFamily, 0, out Queue);
            if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain))
                throw new InvalidOperationException("Vulkan can't present to the window here (no VK_KHR_swapchain).");

            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = QueueFamily
            };
            Check(Vk.CreateCommandPool(Device, &poolInfo, null, out CommandPool), "create a command pool");
        }

        /// <summary>The first thing <paramref name="device"/> lacks that the backend needs, or null if it has everything.</summary>
        private static string? MissingFeature(Vk vk, PhysicalDevice device)
        {
            PhysicalDeviceProperties properties = vk.GetPhysicalDeviceProperties(device);
            if (properties.ApiVersion < Vk.MakeVersion(1, 3)) return "Vulkan 1.3";

            PhysicalDeviceVulkan13Features features13 = new() { SType = StructureType.PhysicalDeviceVulkan13Features };
            PhysicalDeviceFeatures2 features = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features13 };
            vk.GetPhysicalDeviceFeatures2(device, &features);
            if (!features13.DynamicRendering) return "dynamic rendering";
            if (!features13.Synchronization2) return "synchronization2";

            return null;
        }

        private (PhysicalDevice Device, uint QueueFamily) PickDevice()
        {
            PhysicalDevice best = default;
            uint bestFamily = 0;
            int bestScore = -1;

            foreach (PhysicalDevice device in Vk.GetPhysicalDevices(Instance))
            {
                if (MissingFeature(Vk, device) != null || !HasDeviceExtension(device, KhrSwapchain.ExtensionName)) continue;

                // One queue that draws, computes and presents to this window: every desktop GPU has one.
                uint familyCount = 0;
                Vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
                var families = new QueueFamilyProperties[familyCount];
                fixed (QueueFamilyProperties* familiesPointer = families) Vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, familiesPointer);

                for (uint i = 0; i < familyCount; i++)
                {
                    QueueFlags needed = QueueFlags.GraphicsBit | QueueFlags.ComputeBit;
                    if ((families[i].QueueFlags & needed) != needed) continue;

                    KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out Bool32 present);
                    if (!present) continue;

                    // A graphics card of its own beats one built into the processor.
                    PhysicalDeviceProperties properties = Vk.GetPhysicalDeviceProperties(device);
                    int score = properties.DeviceType switch
                    {
                        PhysicalDeviceType.DiscreteGpu => 3,
                        PhysicalDeviceType.IntegratedGpu => 2,
                        PhysicalDeviceType.VirtualGpu => 1,
                        _ => 0
                    };

                    if (score > bestScore)
                    {
                        best = device;
                        bestFamily = i;
                        bestScore = score;
                    }

                    break;
                }
            }

            if (bestScore < 0) throw new InvalidOperationException("No GPU here can run Vertex's Vulkan renderer (it needs Vulkan 1.3).");
            return (best, bestFamily);
        }

        private Format PickDepthFormat()
        {
            foreach (Format format in new[] { Format.D32Sfloat, Format.X8D24UnormPack32, Format.D16Unorm })
            {
                Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out FormatProperties properties);
                FormatFeatureFlags needed = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit |
                                            FormatFeatureFlags.SampledImageFilterLinearBit;
                if ((properties.OptimalTilingFeatures & needed) == needed) return format;
            }

            return Format.D32Sfloat;
        }

        private bool HasLayer(string name)
        {
            uint count = 0;
            Vk.EnumerateInstanceLayerProperties(&count, null);
            var layers = new LayerProperties[count];
            fixed (LayerProperties* pointer = layers) Vk.EnumerateInstanceLayerProperties(&count, pointer);

            foreach (LayerProperties layer in layers)
            {
                if (SilkMarshal.PtrToString((nint)layer.LayerName) == name) return true;
            }

            return false;
        }

        private bool HasDeviceExtension(PhysicalDevice device, string name)
        {
            uint count = 0;
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
            var extensions = new ExtensionProperties[count];
            fixed (ExtensionProperties* pointer = extensions) Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, pointer);

            foreach (ExtensionProperties extension in extensions)
            {
                if (SilkMarshal.PtrToString((nint)extension.ExtensionName) == name) return true;
            }

            return false;
        }

        private static uint OnDebugMessage(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* data, void* userData)
        {
            string level = severity >= DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt ? "error" : "warning";
            Console.WriteLine($"[Vulkan {level}] {SilkMarshal.PtrToString((nint)data->PMessage)}");
            return Vk.False;
        }

        public uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
        {
            for (int i = 0; i < _memory.MemoryTypeCount; i++)
            {
                if ((typeBits & (1u << i)) != 0 && (_memory.MemoryTypes[i].PropertyFlags & properties) == properties) return (uint)i;
            }

            throw new InvalidOperationException($"The GPU has no memory that's {properties}.");
        }

        /// <summary>A buffer with its own memory. Host-visible ones are mapped for as long as they live.</summary>
        public VkBufferResource CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
        {
            BufferCreateInfo info = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = Math.Max(size, 4),
                Usage = usage,
                SharingMode = SharingMode.Exclusive
            };
            Check(Vk.CreateBuffer(Device, &info, null, out VkBuffer buffer), "create a buffer");

            Vk.GetBufferMemoryRequirements(Device, buffer, out MemoryRequirements requirements);
            MemoryAllocateInfo allocation = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, properties)
            };
            Check(Vk.AllocateMemory(Device, &allocation, null, out DeviceMemory memory), "allocate buffer memory");
            Check(Vk.BindBufferMemory(Device, buffer, memory, 0), "bind buffer memory");

            void* mapped = null;
            if ((properties & MemoryPropertyFlags.HostVisibleBit) != 0) Check(Vk.MapMemory(Device, memory, 0, Vk.WholeSize, 0, &mapped), "map memory");

            return new VkBufferResource(buffer, memory, info.Size, mapped);
        }

        /// <summary>
        /// A 2D image with its own memory and a view of every mip level, in <see cref="ImageLayout.Undefined"/>. With more
        /// than one <paramref name="layers"/> it's an array, viewed whole; see <see cref="CreateLayerView"/> to draw into one.
        /// </summary>
        public VkImageResource CreateImage(int width, int height, Format format, ImageUsageFlags usage, uint mipLevels = 1, uint layers = 1)
        {
            ImageCreateInfo info = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D((uint)Math.Max(width, 1), (uint)Math.Max(height, 1), 1),
                MipLevels = mipLevels,
                ArrayLayers = layers,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
            Check(Vk.CreateImage(Device, &info, null, out Image image), "create an image");

            Vk.GetImageMemoryRequirements(Device, image, out MemoryRequirements requirements);
            MemoryAllocateInfo allocation = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
            };
            Check(Vk.AllocateMemory(Device, &allocation, null, out DeviceMemory memory), "allocate image memory");
            Check(Vk.BindImageMemory(Device, image, memory, 0), "bind image memory");

            ImageAspectFlags aspect = IsDepth(format) ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, mipLevels, 0, layers)
            };
            Check(Vk.CreateImageView(Device, &viewInfo, null, out ImageView view), "create an image view");

            return new VkImageResource(image, memory, view, format, aspect, width, height, mipLevels) { Layers = layers };
        }

        /// <summary>
        /// A view of one layer of an image array, to draw into. It shares the array's image: destroying it only frees the
        /// view, and the array's own <see cref="VkImageResource.Layout"/> is the one that counts.
        /// </summary>
        public VkImageResource CreateLayerView(VkImageResource array, uint layer)
        {
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = array.Image,
                ViewType = ImageViewType.Type2D,
                Format = array.Format,
                SubresourceRange = new ImageSubresourceRange(array.Aspect, 0, 1, layer, 1)
            };
            Check(Vk.CreateImageView(Device, &viewInfo, null, out ImageView view), "create an image layer view");
            return new VkImageResource(array.Image, default, view, array.Format, array.Aspect, array.Width, array.Height, 1) { Owned = false };
        }

        public static bool IsDepth(Format format) => format is Format.D32Sfloat or Format.X8D24UnormPack32 or Format.D16Unorm;

        /// <param name="anisotropic">Filter sharply on surfaces seen at a slant, where the GPU can.</param>
        public Sampler CreateSampler(Filter filter, SamplerAddressMode address, bool mipmaps, bool compare = false, bool anisotropic = false)
        {
            SamplerCreateInfo info = new()
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = filter,
                MinFilter = filter,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = address,
                AddressModeV = address,
                AddressModeW = address,
                MaxLod = mipmaps ? Vk.LodClampNone : 0f,
                AnisotropyEnable = anisotropic && MaxAnisotropy > 1f,
                MaxAnisotropy = anisotropic ? MaxAnisotropy : 1f,
                CompareEnable = compare,
                CompareOp = CompareOp.LessOrEqual,
                BorderColor = BorderColor.FloatOpaqueWhite
            };
            Check(Vk.CreateSampler(Device, &info, null, out Sampler sampler), "create a sampler");
            return sampler;
        }

        /// <summary>
        /// Records commands with <paramref name="record"/> and runs them straight away, waiting until they're done. Only
        /// for setting up: during a frame, record into the frame's own command buffer.
        /// </summary>
        public void RunNow(Action<CommandBuffer> record)
        {
            CommandBufferAllocateInfo allocate = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = CommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            Check(Vk.AllocateCommandBuffers(Device, &allocate, out CommandBuffer commands), "allocate a command buffer");

            CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Vk.BeginCommandBuffer(commands, &begin);
            record(commands);
            Vk.EndCommandBuffer(commands);

            SubmitInfo submit = new() { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commands };
            Check(Vk.QueueSubmit(Queue, 1, &submit, default), "submit setup commands");
            Vk.QueueWaitIdle(Queue);
            Vk.FreeCommandBuffers(Device, CommandPool, 1, &commands);
        }

        /// <summary>Moves (part of) an image to <paramref name="layout"/>, after <paramref name="after"/> and before <paramref name="before"/>.</summary>
        public void Transition(CommandBuffer commands, VkImageResource image, ImageLayout layout, Stage after, Stage before,
            bool discard = false, uint baseMip = 0, uint mipCount = Vk.RemainingMipLevels)
        {
            ImageMemoryBarrier2 barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = after.Stages,
                SrcAccessMask = after.Access,
                DstStageMask = before.Stages,
                DstAccessMask = before.Access,
                OldLayout = discard ? ImageLayout.Undefined : image.Layout,
                NewLayout = layout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image.Image,
                SubresourceRange = new ImageSubresourceRange(image.Aspect, baseMip, mipCount, 0, image.Layers)
            };

            DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
            Vk.CmdPipelineBarrier2(commands, &dependency);
            if (mipCount == Vk.RemainingMipLevels && baseMip == 0) image.Layout = layout;
        }

        /// <summary>Moves some mip levels from <paramref name="from"/> to <paramref name="to"/>, for when they're in different layouts.</summary>
        public void TransitionMips(CommandBuffer commands, VkImageResource image, uint baseMip, uint mipCount, ImageLayout from, ImageLayout to,
            Stage after, Stage before)
        {
            ImageMemoryBarrier2 barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = after.Stages,
                SrcAccessMask = after.Access,
                DstStageMask = before.Stages,
                DstAccessMask = before.Access,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image.Image,
                SubresourceRange = new ImageSubresourceRange(image.Aspect, baseMip, mipCount, 0, image.Layers)
            };

            DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
            Vk.CmdPipelineBarrier2(commands, &dependency);
        }

        /// <summary>Makes everything <paramref name="after"/> wrote visible to <paramref name="before"/>, for buffers.</summary>
        public void MemoryBarrier(CommandBuffer commands, Stage after, Stage before)
        {
            MemoryBarrier2 barrier = new()
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = after.Stages,
                SrcAccessMask = after.Access,
                DstStageMask = before.Stages,
                DstAccessMask = before.Access
            };

            DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
            Vk.CmdPipelineBarrier2(commands, &dependency);
        }

        public static void Check(Result result, string what)
        {
            if (result != Result.Success) throw new InvalidOperationException($"Vulkan couldn't {what}: {result}.");
        }

        public void Dispose()
        {
            Vk.DestroyCommandPool(Device, CommandPool, null);
            Vk.DestroyDevice(Device, null);
            KhrSurface.DestroySurface(Instance, Surface, null);
            if (_debugUtils != null) _debugUtils.DestroyDebugUtilsMessenger(Instance, _messenger, null);
            Vk.DestroyInstance(Instance, null);
        }
    }

    /// <summary>A pipeline stage and the kind of memory access done there: one side of a barrier.</summary>
    internal readonly record struct Stage(PipelineStageFlags2 Stages, AccessFlags2 Access)
    {
        public static readonly Stage None = new(PipelineStageFlags2.None, AccessFlags2.None);
        public static readonly Stage Transfer = new(PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit | AccessFlags2.TransferReadBit);
        public static readonly Stage TransferWrite = new(PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit);
        public static readonly Stage TransferRead = new(PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);
        public static readonly Stage ColorOutput = new(PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit);
        public static readonly Stage DepthOutput = new(PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit);
        public static readonly Stage FragmentRead = new(PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
        public static readonly Stage VertexInput = new(PipelineStageFlags2.VertexInputBit, AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit);
        public static readonly Stage ComputeWrite = new(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit);
        public static readonly Stage VertexStorageRead = new(PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderStorageReadBit);
        public static readonly Stage Present = new(PipelineStageFlags2.None, AccessFlags2.None);
    }

    internal sealed unsafe class VkBufferResource(VkBuffer buffer, DeviceMemory memory, ulong size, void* mapped)
    {
        public readonly VkBuffer Buffer = buffer;
        public readonly DeviceMemory Memory = memory;
        public readonly ulong Size = size;
        public readonly void* Mapped = mapped;

        public void Destroy(VkContext context)
        {
            context.Vk.DestroyBuffer(context.Device, Buffer, null);
            context.Vk.FreeMemory(context.Device, Memory, null);
        }
    }

    internal sealed unsafe class VkImageResource(Image image, DeviceMemory memory, ImageView view, Format format, ImageAspectFlags aspect,
        int width, int height, uint mipLevels)
    {
        public readonly Image Image = image;
        public readonly DeviceMemory Memory = memory;
        public readonly ImageView View = view;
        public readonly Format Format = format;
        public readonly ImageAspectFlags Aspect = aspect;
        public readonly int Width = width;
        public readonly int Height = height;
        public readonly uint MipLevels = mipLevels;

        /// <summary>How many layers: more than 1 for an image array, like the shadow cascades.</summary>
        public uint Layers { get; init; } = 1;

        /// <summary>The layout every mip level is in, as far as the commands recorded so far go.</summary>
        public ImageLayout Layout = ImageLayout.Undefined;

        /// <summary>False for a swapchain's images, which belong to the swapchain: only the view is freed.</summary>
        public bool Owned { get; init; } = true;

        public void Destroy(VkContext context)
        {
            context.Vk.DestroyImageView(context.Device, View, null);
            if (!Owned) return;

            context.Vk.DestroyImage(context.Device, Image, null);
            context.Vk.FreeMemory(context.Device, Memory, null);
        }
    }
}
