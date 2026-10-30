using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Gpu;

public enum BlendMode { None, Alpha, PremultipliedAlpha, Additive, Haze, AddWithAlpha }

/// <summary>Graphics pipeline description for dynamic rendering; viewport and scissor are dynamic.</summary>
public sealed class GraphicsPipelineDesc
{
    public ShaderModule Vertex, Fragment;
    public Format[] ColorFormats = Array.Empty<Format>();
    public BlendMode[]? Blend;           // per attachment; defaults to None
    /// <summary>Explicit blend state for attachment 0 (particles' per-system blend functions); overrides Blend[0].</summary>
    public PipelineColorBlendAttachmentState? Blend0;
    public ColorComponentFlags[]? WriteMasks;
    public Format DepthFormat = Format.Undefined;
    public bool DepthTest = true, DepthWrite = true;
    public CompareOp DepthCompare = CompareOp.GreaterOrEqual;   // reverse-Z
    public CullModeFlags Cull = CullModeFlags.BackBit;
    public FrontFace FrontFace = FrontFace.CounterClockwise;
    public bool DepthClamp;
    public bool DepthBias;
    public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;
    public bool VertexInput = true;     // MeshVertex layout at binding 0
    public PipelineLayout Layout;
    public SpecializationInfo? Specialization;
    /// <summary>Multiview: the views (array layers) each draw renders to at once, gl_ViewIndex telling them apart; 0 none.</summary>
    public uint ViewMask;

    public GraphicsPipelineDesc WithViewMask(uint mask)
    {
        var c = (GraphicsPipelineDesc)MemberwiseClone();
        c.ViewMask = mask;
        return c;
    }
}

public static unsafe class PipelineBuilder
{
    /// <summary>The one vertex layout all scene geometry shares: position, normal, tangent, uv (48 bytes).</summary>
    public const uint VertexStride = 48;

    public static Pipeline Create(VulkanContext vk, GraphicsPipelineDesc d)
    {
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = d.Vertex, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = d.Fragment, PName = entry };
            uint stageCount = d.Fragment.Handle == 0 ? 1u : 2u;

            var binding = new VertexInputBindingDescription(0, VertexStride, VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[4];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32B32A32Sfloat, 24);
            attrs[3] = new VertexInputAttributeDescription(3, 0, Format.R32G32Sfloat, 40);
            var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            if (d.VertexInput)
            {
                vi.VertexBindingDescriptionCount = 1;
                vi.PVertexBindingDescriptions = &binding;
                vi.VertexAttributeDescriptionCount = 4;
                vi.PVertexAttributeDescriptions = attrs;
            }
            var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = d.Topology };
            var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var rs = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = d.Cull,
                FrontFace = d.FrontFace,
                LineWidth = 1,
                DepthClampEnable = d.DepthClamp,
                DepthBiasEnable = d.DepthBias,
            };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var ds = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = d.DepthTest,
                DepthWriteEnable = d.DepthWrite,
                DepthCompareOp = d.DepthCompare,
            };
            int n = d.ColorFormats.Length;
            var blends = stackalloc PipelineColorBlendAttachmentState[Math.Max(1, n)];
            for (int i = 0; i < n; i++)
            {
                var mode = d.Blend != null && i < d.Blend.Length ? d.Blend[i] : BlendMode.None;
                var mask = d.WriteMasks != null && i < d.WriteMasks.Length ? d.WriteMasks[i]
                    : ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
                blends[i] = mode switch
                {
                    // LLDrawPoolAlpha: (SRC_ALPHA, ONE_MINUS_SRC_ALPHA, ZERO, ONE_MINUS_SRC_ALPHA), so translucent faces
                    // dim the glow mask behind them
                    BlendMode.Alpha => new PipelineColorBlendAttachmentState(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                        BlendFactor.Zero, BlendFactor.OneMinusSrcAlpha, BlendOp.Add, mask),
                    BlendMode.PremultipliedAlpha => new PipelineColorBlendAttachmentState(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                        BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add, mask),
                    BlendMode.Haze => new PipelineColorBlendAttachmentState(true, BlendFactor.One, BlendFactor.SrcAlpha, BlendOp.Add,
                        BlendFactor.Zero, BlendFactor.SrcAlpha, BlendOp.Add, mask),
                    BlendMode.Additive => new PipelineColorBlendAttachmentState(true, BlendFactor.One, BlendFactor.One, BlendOp.Add,
                        BlendFactor.One, BlendFactor.One, BlendOp.Add, mask),
                    // BT_ADD_WITH_ALPHA (SRC_ALPHA, ONE); destination alpha kept
                    BlendMode.AddWithAlpha => new PipelineColorBlendAttachmentState(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add,
                        BlendFactor.Zero, BlendFactor.One, BlendOp.Add, mask),
                    _ => new PipelineColorBlendAttachmentState(false, colorWriteMask: mask),
                };
                if (i == 0 && d.Blend0 is { } b0) blends[i] = b0;
            }
            var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = (uint)n, PAttachments = blends };
            var dyn = stackalloc DynamicState[3] { DynamicState.Viewport, DynamicState.Scissor, DynamicState.DepthBias };
            var dynInfo = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = d.DepthBias ? 3u : 2u, PDynamicStates = dyn };

            var formats = stackalloc Format[Math.Max(1, n)];
            for (int i = 0; i < n; i++) formats[i] = d.ColorFormats[i];
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = (uint)n,
                PColorAttachmentFormats = formats,
                DepthAttachmentFormat = d.DepthFormat,
                ViewMask = d.ViewMask,
            };
            if (d.Specialization is { } spec)
            {
                stages[0].PSpecializationInfo = &spec;
                stages[1].PSpecializationInfo = &spec;
            }
            var ci = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = stageCount,
                PStages = stages,
                PVertexInputState = &vi,
                PInputAssemblyState = &ia,
                PViewportState = &vp,
                PRasterizationState = &rs,
                PMultisampleState = &ms,
                PDepthStencilState = &ds,
                PColorBlendState = &cb,
                PDynamicState = &dynInfo,
                Layout = d.Layout,
            };
            VulkanContext.Check(vk.Api.CreateGraphicsPipelines(vk.Device, default, 1, in ci, null, out var pipeline), "vkCreateGraphicsPipelines");
            return pipeline;
        }
        finally { SilkMarshal.Free((nint)entry); }
    }

    public static Pipeline CreateCompute(VulkanContext vk, ShaderModule module, PipelineLayout layout)
    {
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var ci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = entry },
                Layout = layout,
            };
            VulkanContext.Check(vk.Api.CreateComputePipelines(vk.Device, default, 1, in ci, null, out var p), "vkCreateComputePipelines");
            return p;
        }
        finally { SilkMarshal.Free((nint)entry); }
    }

    public static PipelineLayout CreateLayout(VulkanContext vk, DescriptorSetLayout[] sets, uint pushConstantBytes)
    {
        fixed (DescriptorSetLayout* ps = sets)
        {
            var range = new PushConstantRange(ShaderStageFlags.All, 0, pushConstantBytes);
            var ci = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)sets.Length,
                PSetLayouts = ps,
                PushConstantRangeCount = pushConstantBytes > 0 ? 1u : 0u,
                PPushConstantRanges = &range,
            };
            VulkanContext.Check(vk.Api.CreatePipelineLayout(vk.Device, in ci, null, out var layout), "vkCreatePipelineLayout");
            return layout;
        }
    }
}
