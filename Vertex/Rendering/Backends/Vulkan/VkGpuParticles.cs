using System.Numerics;
using System.Runtime.InteropServices;

using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// The GPU particle pool: one storage buffer holding every <see cref="GpuParticleSystem"/>'s particles, moved on by
    /// one compute dispatch a frame, with one more per burst to make new ones, then drawn in a single draw call per view
    /// with the vertex shader building each particle's quad straight from the buffer. The CPU only ever sends each
    /// system's settings and where to emit.
    /// </summary>
    internal sealed unsafe class VkGpuParticles : IDisposable
    {
        // Matches the shaders' Particle: three vec4s.
        private const int _particleSize = 48;
        private const uint _groupSize = 64;

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemGpu
        {
            public Vector4 StartColor;
            public Vector4 EndColor;
            public Vector4 Shape;   // end size, glow, stretch, gravity
            public Vector4 Flags;   // drag, additive, visible
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EmissionGpu
        {
            public Matrix4x4 World;
            public Vector4 AxisSpread;
            public Vector4 Area;
            public Vector4 SpeedLifetime;
            public Vector4 Size;
            public uint RangeOffset, RangeSize, First, Count;
            public uint System, Seed, Unused0, Unused1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StepConstants
        {
            public float DeltaTime;
            public uint Count;
            public uint Emission;
        }

        private readonly VkContext _context;
        private readonly DescriptorSetLayout _computeSetLayout;
        private readonly PipelineLayout _computeLayout;
        private readonly Pipeline _update;
        private readonly Pipeline _spawn;
        private readonly ShaderModule _updateShader;
        private readonly ShaderModule _spawnShader;

        private VkBufferResource? _pool;
        private int _capacity;
        private SystemGpu[] _systems = [];
        private EmissionGpu[] _emissions = [];

        /// <summary>The layout of <see cref="DrawSet"/>: the pool and the systems, for the vertex shader.</summary>
        public DescriptorSetLayout DrawSetLayout { get; }

        /// <summary>What to bind to draw this frame's particles, once <see cref="Update"/> has run. Default if there are none.</summary>
        public DescriptorSet DrawSet { get; private set; }

        /// <summary>How many particles to draw quads for this frame (live or not; dead ones vanish in the vertex shader).</summary>
        public int DrawCount { get; private set; }

        public VkGpuParticles(VkContext context, VkShaderCompiler compiler)
        {
            _context = context;

            _computeSetLayout = VkDescriptors.CreateLayout(context,
                (DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit),
                (DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit),
                (DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit));
            DrawSetLayout = VkDescriptors.CreateLayout(context,
                (DescriptorType.StorageBuffer, ShaderStageFlags.VertexBit),
                (DescriptorType.StorageBuffer, ShaderStageFlags.VertexBit));

            _computeLayout = VkDescriptors.CreatePipelineLayout(context, [_computeSetLayout], (uint)sizeof(StepConstants), ShaderStageFlags.ComputeBit);
            _updateShader = compiler.Create("particle update", ShaderKind.ComputeShader, VkShaderSources.ParticleUpdateCompute);
            _spawnShader = compiler.Create("particle spawn", ShaderKind.ComputeShader, VkShaderSources.ParticleSpawnCompute);
            _update = VkPipelines.CreateCompute(context, _updateShader, _computeLayout);
            _spawn = VkPipelines.CreateCompute(context, _spawnShader, _computeLayout);
        }

        /// <summary>Records this frame's simulation into <paramref name="commands"/>: clears, moving everything on, then the new ones.</summary>
        public void Update(CommandBuffer commands, VkFrame frame, GpuParticleUpdate update)
        {
            DrawSet = default;
            DrawCount = 0;
            if (update.PoolSize == 0) return;

            // Last frame's drawing (and simulating) must be finished with the pool before it's written again.
            _context.MemoryBarrier(commands,
                new Stage(PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit),
                new Stage(PipelineStageFlags2.AllTransferBit | PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));

            EnsureCapacity(commands, frame, update.PoolSize);

            foreach ((int offset, int count) in update.Clears)
            {
                _context.Vk.CmdFillBuffer(commands, _pool!.Buffer, (ulong)offset * _particleSize, (ulong)count * _particleSize, 0);
            }

            _context.MemoryBarrier(commands, Stage.TransferWrite, Stage.ComputeWrite);

            // The systems' settings and the bursts, in the layouts the shaders read.
            IReadOnlyList<GpuParticleSystemData> systems = update.Systems;
            if (_systems.Length < systems.Count) _systems = new SystemGpu[Math.Max(systems.Count, _systems.Length * 2)];
            for (int i = 0; i < systems.Count; i++)
            {
                GpuParticleSystemData system = systems[i];
                _systems[i] = new SystemGpu
                {
                    StartColor = ToVector(system.StartColor),
                    EndColor = ToVector(system.EndColor),
                    Shape = new Vector4(system.EndSize, system.Glow, system.Stretch, system.Gravity),
                    Flags = new Vector4(system.Drag, system.Additive ? 1f : 0f, system.Visible ? 1f : 0f, 0f)
                };
            }

            IReadOnlyList<GpuParticleEmission> emissions = update.Emissions;
            if (_emissions.Length < emissions.Count) _emissions = new EmissionGpu[Math.Max(emissions.Count, _emissions.Length * 2)];
            for (int i = 0; i < emissions.Count; i++)
            {
                GpuParticleEmission emission = emissions[i];
                _emissions[i] = new EmissionGpu
                {
                    World = emission.World,
                    AxisSpread = new Vector4(emission.Axis, emission.Spread),
                    Area = new Vector4(emission.Area, 0f),
                    SpeedLifetime = new Vector4(emission.Speed.Min, emission.Speed.Max, emission.Lifetime.Min, emission.Lifetime.Max),
                    Size = new Vector4(emission.Size.Min, emission.Size.Max, 0f, 0f),
                    RangeOffset = (uint)emission.RangeOffset,
                    RangeSize = (uint)emission.RangeSize,
                    First = (uint)emission.First,
                    Count = (uint)emission.Count,
                    System = (uint)emission.System,
                    Seed = emission.Seed
                };
            }

            ulong systemsSize = (ulong)(Math.Max(systems.Count, 1) * sizeof(SystemGpu));
            (VkBuffer systemsBuffer, ulong systemsOffset) = frame.Uploads.Write<SystemGpu>(_systems.AsSpan(0, Math.Max(systems.Count, 1)));
            (VkBuffer emissionsBuffer, ulong emissionsOffset) = emissions.Count > 0
                ? frame.Uploads.Write<EmissionGpu>(_emissions.AsSpan(0, emissions.Count))
                : (systemsBuffer, systemsOffset);
            ulong emissionsSize = emissions.Count > 0 ? (ulong)(emissions.Count * sizeof(EmissionGpu)) : systemsSize;

            DescriptorSet computeSet = frame.Descriptors.Allocate(_computeSetLayout);
            VkDescriptors.WriteBuffer(_context, computeSet, 0, DescriptorType.StorageBuffer, _pool!.Buffer, 0, Vk.WholeSize);
            VkDescriptors.WriteBuffer(_context, computeSet, 1, DescriptorType.StorageBuffer, systemsBuffer, systemsOffset, systemsSize);
            VkDescriptors.WriteBuffer(_context, computeSet, 2, DescriptorType.StorageBuffer, emissionsBuffer, emissionsOffset, emissionsSize);

            _context.Vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Compute, _computeLayout, 0, 1, &computeSet, 0, null);

            // Everything there moves on...
            _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Compute, _update);
            var step = new StepConstants { DeltaTime = update.DeltaTime, Count = (uint)update.PoolSize };
            _context.Vk.CmdPushConstants(commands, _computeLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(StepConstants), &step);
            _context.Vk.CmdDispatch(commands, Groups(update.PoolSize), 1, 1);

            // ...then each burst makes its new ones, in order, as a later burst may wrap round over an earlier one's.
            _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Compute, _spawn);
            for (int i = 0; i < emissions.Count; i++)
            {
                _context.MemoryBarrier(commands, Stage.ComputeWrite, Stage.ComputeWrite);
                step = new StepConstants { Emission = (uint)i };
                _context.Vk.CmdPushConstants(commands, _computeLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(StepConstants), &step);
                _context.Vk.CmdDispatch(commands, Groups(emissions[i].Count), 1, 1);
            }

            _context.MemoryBarrier(commands, Stage.ComputeWrite, Stage.VertexStorageRead);

            DescriptorSet drawSet = frame.Descriptors.Allocate(DrawSetLayout);
            VkDescriptors.WriteBuffer(_context, drawSet, 0, DescriptorType.StorageBuffer, _pool.Buffer, 0, Vk.WholeSize);
            VkDescriptors.WriteBuffer(_context, drawSet, 1, DescriptorType.StorageBuffer, systemsBuffer, systemsOffset, systemsSize);
            DrawSet = drawSet;
            DrawCount = update.PoolSize;
        }

        // Grows the pool, keeping the particles already in it. New space starts empty (lifetime 0).
        private void EnsureCapacity(CommandBuffer commands, VkFrame frame, int needed)
        {
            if (needed <= _capacity && _pool != null) return;

            int capacity = Math.Max(needed, Math.Max(_capacity * 2, 1024));
            VkBufferResource pool = _context.CreateBuffer((ulong)capacity * _particleSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.DeviceLocalBit);

            ulong kept = 0;
            if (_pool != null)
            {
                kept = (ulong)_capacity * _particleSize;
                BufferCopy copy = new(0, 0, kept);
                _context.Vk.CmdCopyBuffer(commands, _pool.Buffer, pool.Buffer, 1, &copy);

                VkBufferResource old = _pool;
                frame.DeleteLater(() => old.Destroy(_context));
            }

            _context.Vk.CmdFillBuffer(commands, pool.Buffer, kept, (ulong)capacity * _particleSize - kept, 0);
            _pool = pool;
            _capacity = capacity;

            // Clearing a system's range comes next and may overlap what was just copied.
            _context.MemoryBarrier(commands, Stage.TransferWrite, Stage.Transfer);
        }

        private static uint Groups(int count) => (uint)((count + _groupSize - 1) / _groupSize);

        private static Vector4 ToVector(Color color) => new(color.R, color.G, color.B, color.A);

        public void Dispose()
        {
            _pool?.Destroy(_context);
            _context.Vk.DestroyPipeline(_context.Device, _update, null);
            _context.Vk.DestroyPipeline(_context.Device, _spawn, null);
            _context.Vk.DestroyShaderModule(_context.Device, _updateShader, null);
            _context.Vk.DestroyShaderModule(_context.Device, _spawnShader, null);
            _context.Vk.DestroyPipelineLayout(_context.Device, _computeLayout, null);
            _context.Vk.DestroyDescriptorSetLayout(_context.Device, _computeSetLayout, null);
            _context.Vk.DestroyDescriptorSetLayout(_context.Device, DrawSetLayout, null);
        }
    }
}
