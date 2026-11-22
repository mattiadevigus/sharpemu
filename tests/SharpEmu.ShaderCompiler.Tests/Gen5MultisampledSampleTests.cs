// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// The GT7 demo samples an MSAA surface with IMAGE_SAMPLE_LZ in a compute program. Vulkan forbids sampling a
// multisampled image, and the AMD driver fails the whole pipeline, so the translator reads sample 0 instead.
public sealed class Gen5MultisampledSampleTests
{
    private const ulong ShaderAddress = 0x1_0000_C000;

    [Theory]
    [InlineData("ImageSampleLz")]
    [InlineData("ImageSample")]
    public void SamplingAnMsaaImageFetchesSampleZero(string opcode)
    {
        var control = new Gen5ImageControl(0xF, 0, [0, 1], 4, 8, 16, 6, false, false, false, false, false);
        var program = new Gen5ShaderProgram(ShaderAddress,
        [
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mimg, opcode, [], [], [], control),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
        ]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, ResourceTestProgram.Hash, 0, 20);
        var userData = new uint[20];
        userData[8] = 0x20;
        userData[9] = 71u << 20;
        userData[11] = (14u << 28) | (2u << 16); // 2D MSAA, four samples
        userData[13] = (1u << 25) | (2u << 4);
        userData[14] = 7;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(userData), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 20), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        };

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var opcodes = new List<SpirvOp>();
        for (var index = 20; index + 4 <= shader.Spirv.Length;)
        {
            var word = BitConverter.ToUInt32(shader.Spirv, index);
            opcodes.Add((SpirvOp)(word & 0xFFFF));
            index += (int)(word >> 16) * sizeof(uint);
        }

        Assert.Contains(SpirvOp.ImageFetch, opcodes);
        Assert.DoesNotContain(SpirvOp.ImageSampleExplicitLod, opcodes);
        Assert.DoesNotContain(SpirvOp.ImageSampleImplicitLod, opcodes);
    }
}
