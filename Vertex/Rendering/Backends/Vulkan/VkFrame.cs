using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// What one frame in flight needs of its own: while the GPU draws one frame, the CPU records the next, so each has
    /// its own command buffer, upload space and descriptor sets, and things freed are destroyed once their frame is done.
    /// </summary>
    internal sealed unsafe class VkFrame : IDisposable
    {
        private readonly VkContext _context;
        private readonly List<Action> _deletions = new();

        public readonly CommandBuffer Commands;
        public readonly Fence Done;
        public readonly VkSemaphore ImageAvailable;
        public readonly VkUploadRing Uploads;
        public readonly VkDescriptorAllocator Descriptors;

        public VkFrame(VkContext context)
        {
            _context = context;

            CommandBufferAllocateInfo allocate = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = context.CommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            VkContext.Check(context.Vk.AllocateCommandBuffers(context.Device, &allocate, out Commands), "allocate a command buffer");

            // Starts signalled: there's no earlier frame to wait for the first time round.
            FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            VkContext.Check(context.Vk.CreateFence(context.Device, &fenceInfo, null, out Done), "create a fence");

            SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
            VkContext.Check(context.Vk.CreateSemaphore(context.Device, &semaphoreInfo, null, out ImageAvailable), "create a semaphore");

            Uploads = new VkUploadRing(context);
            Descriptors = new VkDescriptorAllocator(context);
        }

        /// <summary>Waits until the GPU has finished this frame's last use, then frees what was waiting on it and starts afresh.</summary>
        public void WaitAndReset()
        {
            Fence done = Done;
            _context.Vk.WaitForFences(_context.Device, 1, &done, true, ulong.MaxValue);

            foreach (Action delete in _deletions) delete();
            _deletions.Clear();

            Uploads.Reset();
            Descriptors.Reset();
        }

        /// <summary>Destroys something once the GPU is done with everything recorded so far for this frame.</summary>
        public void DeleteLater(Action delete) => _deletions.Add(delete);

        public void Dispose()
        {
            foreach (Action delete in _deletions) delete();
            _deletions.Clear();

            Uploads.Dispose();
            Descriptors.Dispose();
            _context.Vk.DestroySemaphore(_context.Device, ImageAvailable, null);
            _context.Vk.DestroyFence(_context.Device, Done, null);
        }
    }

    /// <summary>
    /// Space the CPU writes into for the GPU to read this frame: uniforms, the canvas's and particles' vertices,
    /// particle emissions. Used from the start each frame; grows by adding more buffers when a frame needs more.
    /// </summary>
    internal sealed unsafe class VkUploadRing : IDisposable
    {
        private const ulong _chunkSize = 4 * 1024 * 1024;

        private readonly VkContext _context;
        private readonly List<VkBufferResource> _chunks = new();
        private readonly ulong _alignment;
        private int _current;
        private ulong _offset;

        public VkUploadRing(VkContext context)
        {
            _context = context;
            PhysicalDeviceLimits limits = context.Properties.Limits;
            _alignment = Math.Max(Math.Max(limits.MinUniformBufferOffsetAlignment, limits.MinStorageBufferOffsetAlignment), 16);
        }

        /// <summary>Room for <paramref name="size"/> bytes: the buffer, where in it, and where to write them.</summary>
        public (VkBuffer Buffer, ulong Offset, nint Pointer) Allocate(ulong size)
        {
            size = Math.Max(size, 4);

            while (true)
            {
                if (_current == _chunks.Count)
                {
                    _chunks.Add(_context.CreateBuffer(Math.Max(_chunkSize, size),
                        BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit |
                        BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferSrcBit,
                        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
                }

                VkBufferResource chunk = _chunks[_current];
                ulong offset = (_offset + _alignment - 1) / _alignment * _alignment;
                if (offset + size <= chunk.Size)
                {
                    _offset = offset + size;
                    return (chunk.Buffer, offset, (nint)((byte*)chunk.Mapped + offset));
                }

                _current++;
                _offset = 0;
            }
        }

        /// <summary>Copies <paramref name="data"/> in and says where it went.</summary>
        public (VkBuffer Buffer, ulong Offset) Write<T>(ReadOnlySpan<T> data) where T : unmanaged
        {
            ulong size = (ulong)(data.Length * sizeof(T));
            var (buffer, offset, pointer) = Allocate(size);
            data.CopyTo(new Span<T>((void*)pointer, data.Length));
            return (buffer, offset);
        }

        public (VkBuffer Buffer, ulong Offset) Write<T>(in T value) where T : unmanaged
        {
            var (buffer, offset, pointer) = Allocate((ulong)sizeof(T));
            *(T*)pointer = value;
            return (buffer, offset);
        }

        public void Reset()
        {
            _current = 0;
            _offset = 0;
        }

        public void Dispose()
        {
            foreach (VkBufferResource chunk in _chunks) chunk.Destroy(_context);
            _chunks.Clear();
        }
    }

    /// <summary>Descriptor sets for one frame, from pools emptied all at once when the frame starts again.</summary>
    internal sealed unsafe class VkDescriptorAllocator : IDisposable
    {
        private readonly VkContext _context;
        private readonly List<DescriptorPool> _pools = new();
        private int _current;

        public VkDescriptorAllocator(VkContext context)
        {
            _context = context;
        }

        public DescriptorSet Allocate(DescriptorSetLayout layout)
        {
            while (true)
            {
                if (_current == _pools.Count) _pools.Add(CreatePool());

                DescriptorPool pool = _pools[_current];
                DescriptorSetAllocateInfo info = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = pool,
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout
                };

                Result result = _context.Vk.AllocateDescriptorSets(_context.Device, &info, out DescriptorSet set);
                if (result == Result.Success) return set;
                if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)) VkContext.Check(result, "allocate a descriptor set");

                // This pool's full: on to the next.
                _current++;
            }
        }

        private DescriptorPool CreatePool()
        {
            DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[3]
            {
                new(DescriptorType.UniformBuffer, 2048),
                new(DescriptorType.CombinedImageSampler, 4096),
                new(DescriptorType.StorageBuffer, 512)
            };

            DescriptorPoolCreateInfo info = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 2048,
                PoolSizeCount = 3,
                PPoolSizes = sizes
            };

            VkContext.Check(_context.Vk.CreateDescriptorPool(_context.Device, &info, null, out DescriptorPool pool), "create a descriptor pool");
            return pool;
        }

        public void Reset()
        {
            foreach (DescriptorPool pool in _pools) _context.Vk.ResetDescriptorPool(_context.Device, pool, 0);
            _current = 0;
        }

        public void Dispose()
        {
            foreach (DescriptorPool pool in _pools) _context.Vk.DestroyDescriptorPool(_context.Device, pool, null);
            _pools.Clear();
        }
    }

    /// <summary>Filling in descriptor sets.</summary>
    internal static unsafe class VkDescriptors
    {
        public static void WriteBuffer(VkContext context, DescriptorSet set, uint binding, DescriptorType type, VkBuffer buffer, ulong offset, ulong range)
        {
            DescriptorBufferInfo info = new(buffer, offset, range);
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = binding,
                DescriptorCount = 1,
                DescriptorType = type,
                PBufferInfo = &info
            };
            context.Vk.UpdateDescriptorSets(context.Device, 1, &write, 0, null);
        }

        public static void WriteImage(VkContext context, DescriptorSet set, uint binding, ImageView view, Sampler sampler,
            ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal)
        {
            DescriptorImageInfo info = new(sampler, view, layout);
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = binding,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &info
            };
            context.Vk.UpdateDescriptorSets(context.Device, 1, &write, 0, null);
        }

        public static DescriptorSetLayout CreateLayout(VkContext context, params (DescriptorType Type, ShaderStageFlags Stages)[] bindings)
        {
            var layoutBindings = new DescriptorSetLayoutBinding[bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
            {
                layoutBindings[i] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)i,
                    DescriptorType = bindings[i].Type,
                    DescriptorCount = 1,
                    StageFlags = bindings[i].Stages
                };
            }

            fixed (DescriptorSetLayoutBinding* pointer = layoutBindings)
            {
                DescriptorSetLayoutCreateInfo info = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)layoutBindings.Length,
                    PBindings = pointer
                };
                VkContext.Check(context.Vk.CreateDescriptorSetLayout(context.Device, &info, null, out DescriptorSetLayout layout), "create a descriptor set layout");
                return layout;
            }
        }

        public static PipelineLayout CreatePipelineLayout(VkContext context, DescriptorSetLayout[] sets, uint pushConstantSize, ShaderStageFlags pushStages)
        {
            PushConstantRange range = new(pushStages, 0, pushConstantSize);
            fixed (DescriptorSetLayout* setPointer = sets)
            {
                PipelineLayoutCreateInfo info = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)sets.Length,
                    PSetLayouts = setPointer,
                    PushConstantRangeCount = pushConstantSize > 0 ? 1u : 0u,
                    PPushConstantRanges = &range
                };
                VkContext.Check(context.Vk.CreatePipelineLayout(context.Device, &info, null, out PipelineLayout layout), "create a pipeline layout");
                return layout;
            }
        }
    }
}
