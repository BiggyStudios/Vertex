using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Vertex.Rendering.Vulkan
{
    internal enum VkBlend
    {
        None,

        /// <summary>Colours already multiplied by their alpha: the canvas and particles.</summary>
        Premultiplied,

        /// <summary>Adds onto what's there: the bloom building back up.</summary>
        Additive
    }

    /// <summary>Everything about a graphics pipeline that differs between the backend's passes.</summary>
    internal sealed class VkPipelineDescription
    {
        public required PipelineLayout Layout;
        public required ShaderModule Vertex;
        public ShaderModule Fragment;

        /// <summary>Bytes per vertex, 0 for none (the shader makes its own).</summary>
        public uint VertexStride;
        public (uint Location, Format Format, uint Offset)[] Attributes = [];

        /// <summary>Bytes per instance in a second vertex buffer (binding 1), 0 for none: what each copy of an instanced draw gets.</summary>
        public uint InstanceStride;
        public (uint Location, Format Format, uint Offset)[] InstanceAttributes = [];

        public CullModeFlags Cull = CullModeFlags.None;
        public bool DepthTest;
        public bool DepthWrite;
        public VkBlend Blend = VkBlend.None;

        public Format[] ColorFormats = [];
        public Format DepthFormat = Format.Undefined;
    }

    internal static unsafe class VkPipelines
    {
        /// <summary>
        /// A pipeline for dynamic rendering into <see cref="VkPipelineDescription.ColorFormats"/>. The viewport and scissor
        /// are set when drawing. Front faces are counter-clockwise, as in OpenGL: 3D passes use a flipped viewport, which
        /// keeps OpenGL's winding.
        /// </summary>
        public static Pipeline CreateGraphics(VkContext context, VkPipelineDescription description)
        {
            nint entry = SilkMarshal.StringToPtr("main");
            try
            {
                PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = description.Vertex,
                    PName = (byte*)entry
                };
                uint stageCount = 1;
                if (description.Fragment.Handle != 0)
                {
                    stages[1] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.FragmentBit,
                        Module = description.Fragment,
                        PName = (byte*)entry
                    };
                    stageCount = 2;
                }

                VertexInputBindingDescription* bindings = stackalloc VertexInputBindingDescription[2];
                bindings[0] = new VertexInputBindingDescription(0, description.VertexStride, VertexInputRate.Vertex);
                bindings[1] = new VertexInputBindingDescription(1, description.InstanceStride, VertexInputRate.Instance);
                uint bindingCount = description.VertexStride > 0 ? (description.InstanceStride > 0 ? 2u : 1u) : 0u;
                var attributes = new VertexInputAttributeDescription[description.Attributes.Length + description.InstanceAttributes.Length];
                for (int i = 0; i < description.Attributes.Length; i++)
                {
                    (uint location, Format format, uint offset) = description.Attributes[i];
                    attributes[i] = new VertexInputAttributeDescription(location, 0, format, offset);
                }

                for (int i = 0; i < description.InstanceAttributes.Length; i++)
                {
                    (uint location, Format format, uint offset) = description.InstanceAttributes[i];
                    attributes[description.Attributes.Length + i] = new VertexInputAttributeDescription(location, 1, format, offset);
                }

                fixed (VertexInputAttributeDescription* attributePointer = attributes)
                fixed (Format* colorFormats = description.ColorFormats)
                {
                    PipelineVertexInputStateCreateInfo vertexInput = new()
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = bindingCount,
                        PVertexBindingDescriptions = bindings,
                        VertexAttributeDescriptionCount = (uint)attributes.Length,
                        PVertexAttributeDescriptions = attributePointer
                    };

                    PipelineInputAssemblyStateCreateInfo inputAssembly = new()
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = PrimitiveTopology.TriangleList
                    };

                    PipelineViewportStateCreateInfo viewport = new()
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        ViewportCount = 1,
                        ScissorCount = 1
                    };

                    PipelineRasterizationStateCreateInfo rasterization = new()
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        PolygonMode = PolygonMode.Fill,
                        CullMode = description.Cull,
                        FrontFace = FrontFace.CounterClockwise,
                        LineWidth = 1f
                    };

                    PipelineMultisampleStateCreateInfo multisample = new()
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        RasterizationSamples = SampleCountFlags.Count1Bit
                    };

                    PipelineDepthStencilStateCreateInfo depthStencil = new()
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthTestEnable = description.DepthTest,
                        DepthWriteEnable = description.DepthWrite,
                        DepthCompareOp = CompareOp.LessOrEqual
                    };

                    int colorCount = description.ColorFormats.Length;
                    PipelineColorBlendAttachmentState* blends = stackalloc PipelineColorBlendAttachmentState[Math.Max(colorCount, 1)];
                    for (int i = 0; i < colorCount; i++) blends[i] = Blend(description.Blend);

                    PipelineColorBlendStateCreateInfo colorBlend = new()
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        AttachmentCount = (uint)colorCount,
                        PAttachments = blends
                    };

                    DynamicState* dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                    PipelineDynamicStateCreateInfo dynamic = new()
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = 2,
                        PDynamicStates = dynamicStates
                    };

                    PipelineRenderingCreateInfo rendering = new()
                    {
                        SType = StructureType.PipelineRenderingCreateInfo,
                        ColorAttachmentCount = (uint)colorCount,
                        PColorAttachmentFormats = colorFormats,
                        DepthAttachmentFormat = description.DepthFormat
                    };

                    GraphicsPipelineCreateInfo info = new()
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        PNext = &rendering,
                        StageCount = stageCount,
                        PStages = stages,
                        PVertexInputState = &vertexInput,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewport,
                        PRasterizationState = &rasterization,
                        PMultisampleState = &multisample,
                        PDepthStencilState = &depthStencil,
                        PColorBlendState = &colorBlend,
                        PDynamicState = &dynamic,
                        Layout = description.Layout
                    };

                    VkContext.Check(context.Vk.CreateGraphicsPipelines(context.Device, default, 1, &info, null, out Pipeline pipeline), "create a pipeline");
                    return pipeline;
                }
            }
            finally
            {
                SilkMarshal.Free(entry);
            }
        }

        public static Pipeline CreateCompute(VkContext context, ShaderModule module, PipelineLayout layout)
        {
            nint entry = SilkMarshal.StringToPtr("main");
            try
            {
                ComputePipelineCreateInfo info = new()
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = module,
                        PName = (byte*)entry
                    },
                    Layout = layout
                };

                VkContext.Check(context.Vk.CreateComputePipelines(context.Device, default, 1, &info, null, out Pipeline pipeline), "create a compute pipeline");
                return pipeline;
            }
            finally
            {
                SilkMarshal.Free(entry);
            }
        }

        private static PipelineColorBlendAttachmentState Blend(VkBlend blend)
        {
            const ColorComponentFlags all = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
            return blend switch
            {
                VkBlend.Premultiplied => new PipelineColorBlendAttachmentState
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = BlendFactor.One,
                    DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One,
                    DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = all
                },
                VkBlend.Additive => new PipelineColorBlendAttachmentState
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = BlendFactor.One,
                    DstColorBlendFactor = BlendFactor.One,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One,
                    DstAlphaBlendFactor = BlendFactor.One,
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = all
                },
                _ => new PipelineColorBlendAttachmentState { BlendEnable = false, ColorWriteMask = all }
            };
        }

        /// <summary>
        /// Sets a viewport covering <paramref name="width"/> by <paramref name="height"/>. Flipped, for 3D and the canvas,
        /// puts clip space +Y at the top as in OpenGL; otherwise the first row of the image is clip space -Y.
        /// </summary>
        public static void SetViewport(VkContext context, CommandBuffer commands, int width, int height, bool flipped)
        {
            Viewport viewport = flipped
                ? new Viewport(0, height, width, -height, 0f, 1f)
                : new Viewport(0, 0, width, height, 0f, 1f);
            Rect2D scissor = new(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
            context.Vk.CmdSetViewport(commands, 0, 1, &viewport);
            context.Vk.CmdSetScissor(commands, 0, 1, &scissor);
        }

        /// <summary>Starts drawing into <paramref name="colors"/> (and <paramref name="depth"/>), clearing them or keeping what's there.</summary>
        public static void BeginRendering(VkContext context, CommandBuffer commands, int width, int height,
            ReadOnlySpan<(VkImageResource Image, ClearColorValue? Clear)> colors, VkImageResource? depth = null, bool clearDepth = true)
        {
            RenderingAttachmentInfo* colorInfos = stackalloc RenderingAttachmentInfo[Math.Max(colors.Length, 1)];
            for (int i = 0; i < colors.Length; i++)
            {
                (VkImageResource image, ClearColorValue? clear) = colors[i];
                colorInfos[i] = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = image.View,
                    ImageLayout = ImageLayout.ColorAttachmentOptimal,
                    LoadOp = clear.HasValue ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue(clear ?? default)
                };
            }

            RenderingAttachmentInfo depthInfo = default;
            if (depth != null)
            {
                depthInfo = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = depth.View,
                    ImageLayout = ImageLayout.DepthAttachmentOptimal,
                    LoadOp = clearDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0))
                };
            }

            RenderingInfo info = new()
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
                LayerCount = 1,
                ColorAttachmentCount = (uint)colors.Length,
                PColorAttachments = colorInfos,
                PDepthAttachment = depth != null ? &depthInfo : null
            };

            context.Vk.CmdBeginRendering(commands, &info);
        }
    }
}
