// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Runs the native Agc fast paths (SHARPEMU_NATIVE_AGC) against their HLE exports on the same random
// guest memory. A native call must either leave memory untouched and ask for the HLE fallback, or
// return 0 with exactly the bytes the HLE export writes. The native code uses the SysV ABI, so this
// runs on x64 Linux only.
public sealed unsafe class NativeAgcCodeTests
{
    private const ulong RegionAddress = 0x2000_0000_0000;
    private const int RegionSize = 0x4000;
    private const int Iterations = 20000;
    private const ulong Fallback = 0xFEEDFACEDEADBEEF;

    [Fact]
    public void AddRegisters_MatchHle()
    {
        RunDifferential(seed: 1, (random, region) =>
        {
            var space = random.Next(3);
            var command = PickPointer(random, allowZero: random.Next(16) == 0);
            if (command != 0)
            {
                uint[] ops = [0x9F, 0x63, 0x64, 0x10, 0x61];
                // Half the packets are the valid new or legacy form for this register space.
                var legacy = random.Next(2) == 0;
                var valid = random.Next(2) == 0;
                var op = valid ? (legacy ? 0x10u : ops[space]) : ops[random.Next(ops.Length)];
                var length = valid ? (legacy ? 4u : 5u) : (uint)random.Next(2, 8);
                var register = valid && legacy ? 0x12u - (space == 1 ? 1u : 0u) + (space == 2 ? 1u : 0u) : (uint)random.Next(0x10, 0x15);
                var header = 0xC0000000u | ((length - 2) << 16) | (op << 8) | (register << 2);
                Write32(region, command, random.Next(4) == 0 ? (uint)random.Next() : header);
            }

            ulong[] arguments = [command, (ulong)random.NextInt64()];
            Func<CpuContext, int> hle = space switch
            {
                0 => AgcExports.SetCxRegIndirectPatchAddRegisters,
                1 => AgcExports.SetShRegIndirectPatchAddRegisters,
                _ => AgcExports.SetUcRegIndirectPatchAddRegisters,
            };
            var entry = space switch
            {
                0 => NativeAgcCode.CxAddRegisters,
                1 => NativeAgcCode.ShAddRegisters,
                _ => NativeAgcCode.UcAddRegisters,
            };
            return (arguments, hle, entry);
        });
    }

    [Fact]
    public void CreatePrimState_MatchesHle()
    {
        RunDifferential(seed: 2, (random, region) =>
        {
            byte[] types = [2, 6, 3, 1, 0];
            var geometry = PickPointer(random, allowZero: random.Next(8) == 0);
            var hull = random.Next(2) == 0 ? 0 : PickPointer(random, allowZero: false);
            if (geometry != 0)
            {
                region[(int)(geometry - RegionAddress) + 0x5A] = random.Next(4) == 0 ? types[random.Next(types.Length)] : (byte)2;
                Write64(region, geometry + 0x28, PickPointer(random, allowZero: random.Next(10) == 0));
            }

            if (hull != 0)
            {
                region[(int)(hull - RegionAddress) + 0x5A] = random.Next(4) == 0 ? types[random.Next(types.Length)] : (byte)3;
                Write64(region, hull + 0x28, PickPointer(random, allowZero: random.Next(10) == 0));
            }

            ulong[] arguments =
            [
                random.Next(4) == 0 ? 0 : PickPointer(random, allowZero: false),
                random.Next(4) == 0 ? 0 : PickPointer(random, allowZero: false),
                hull,
                geometry,
                (ulong)random.Next(0, 24),
            ];
            return (arguments, AgcExports.CreatePrimState, NativeAgcCode.CreatePrimState);
        });
    }

    [Fact]
    public void CreateInterpolantMapping_MatchesHle()
    {
        RunDifferential(seed: 3, (random, region) =>
        {
            var registers = PickPointer(random, allowZero: random.Next(16) == 0);
            var geometry = PickPointer(random, allowZero: random.Next(8) == 0);
            var pixel = PickPointer(random, allowZero: random.Next(8) == 0);
            if (pixel != 0)
            {
                var inputs = PickPointer(random, allowZero: random.Next(8) == 0);
                Write64(region, pixel + 0x30, inputs);
                Write32(region, pixel + 0x50, (uint)random.Next(0, 40));
                FillSemantics(random, region, inputs);
            }

            if (geometry != 0)
            {
                var outputs = PickPointer(random, allowZero: random.Next(8) == 0);
                Write64(region, geometry + 0x38, outputs);
                BinaryPrimitives.WriteUInt16LittleEndian(region.AsSpan((int)(geometry - RegionAddress) + 0x56), (ushort)random.Next(0, 40));
                FillSemantics(random, region, outputs);
            }

            ulong[] arguments = [registers, geometry, pixel];
            return (arguments, AgcExports.CreateInterpolantMapping, NativeAgcCode.CreateInterpolantMapping);
        });
    }

    [Fact]
    public void DcbPacketBuilders_MatchHle()
    {
        RunDifferential(seed: 4, (random, region) =>
        {
            var buffer = PickPointer(random, allowZero: random.Next(32) == 0);
            if (buffer != 0)
            {
                // Room for 0 to 96 dwords, so some packets fit and some need the buffer-full callback.
                var cursor = PickPointer(random, allowZero: false);
                Write64(region, buffer + 0x10, cursor);
                Write64(region, buffer + 0x18, cursor + (ulong)(random.Next(0, 97) * 4));
                Write32(region, buffer + 0x30, (uint)random.Next(0, 4) == 0 ? (uint)random.Next(0, 8) : 0);
            }

            var builder = random.Next(7);
            ulong values = random.Next(4) == 0 ? 0 : PickPointer(random, allowZero: false);
            ulong[] arguments = builder switch
            {
                0 => [buffer, (ulong)random.NextInt64(), (ulong)random.NextInt64(), (ulong)random.NextInt64()],
                1 => [buffer, (ulong)random.NextInt64(), (ulong)random.NextInt64()],
                2 or 3 or 4 => [buffer, (ulong)random.NextInt64(), (ulong)random.NextInt64()],
                _ => [buffer, (ulong)random.Next(0, 0x420), values, (ulong)(random.Next(8) == 0 ? random.Next(0, 0x5000) : random.Next(0, 0x40))],
            };
            Func<CpuContext, int> hle = builder switch
            {
                0 => AgcExports.DcbDrawIndex,
                1 => AgcExports.DcbSetIndexSize,
                2 => AgcExports.DcbSetCxRegistersIndirect,
                3 => AgcExports.DcbSetShRegistersIndirect,
                4 => AgcExports.DcbSetUcRegistersIndirect,
                _ => AgcExports.CbSetShRegisterRangeDirect,
            };
            var entry = builder switch
            {
                0 => NativeAgcCode.DcbDrawIndex,
                1 => NativeAgcCode.DcbSetIndexSize,
                2 => NativeAgcCode.DcbSetCxRegistersIndirect,
                3 => NativeAgcCode.DcbSetShRegistersIndirect,
                4 => NativeAgcCode.DcbSetUcRegistersIndirect,
                _ => NativeAgcCode.CbSetShRegisterRangeDirect,
            };
            return (arguments, hle, entry);
        });
    }

    private static void RunDifferential(
        int seed,
        Func<System.Random, byte[], (ulong[] Arguments, Func<CpuContext, int> Hle, int Entry)> setup)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var region = (byte*)Mmap((nint)RegionAddress, RegionSize, ProtRead | ProtWrite, MapPrivate | MapAnonymous | MapFixedNoReplace);
        Assert.Equal((nint)RegionAddress, (nint)region);
        var code = NativeAgcCode.Text;
        var text = (byte*)Mmap(0, (nuint)code.Length, ProtRead | ProtWrite | ProtExec, MapPrivate | MapAnonymous, 0);
        Assert.NotEqual(-1, (nint)text);
        try
        {
            code.CopyTo(new Span<byte>(text, code.Length));
            var random = new System.Random(seed);
            var native = 0;
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var image = new byte[RegionSize];
                random.NextBytes(image);
                ResetSlots(random);
                var (arguments, hle, entry) = setup(random, image);

                var memory = new FakeCpuMemory(RegionAddress, RegionSize);
                Assert.True(memory.TryWrite(RegionAddress, image));
                var ctx = new CpuContext(memory, Generation.Gen5);
                CpuRegister[] registers = [CpuRegister.Rdi, CpuRegister.Rsi, CpuRegister.Rdx, CpuRegister.Rcx, CpuRegister.R8];
                for (var i = 0; i < arguments.Length; i++)
                {
                    ctx[registers[i]] = arguments[i];
                }

                var hleResult = hle(ctx);
                var hleImage = new byte[RegionSize];
                Assert.True(memory.TryRead(RegionAddress, hleImage));

                image.CopyTo(new Span<byte>(region, RegionSize));
                var function = (delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong>)(text + entry);
                var nativeResult = function(
                    arguments[0],
                    arguments[1],
                    arguments.Length > 2 ? arguments[2] : 0,
                    arguments.Length > 3 ? arguments[3] : 0,
                    arguments.Length > 4 ? arguments[4] : 0);
                var nativeImage = new ReadOnlySpan<byte>(region, RegionSize);
                if (nativeResult == Fallback)
                {
                    Assert.True(nativeImage.SequenceEqual(image), $"iteration {iteration}: fallback after a write");
                    continue;
                }

                native++;
                Assert.True(ctx[CpuRegister.Rax] == nativeResult,
                    $"iteration {iteration}: HLE returned 0x{ctx[CpuRegister.Rax]:X16} (status 0x{hleResult:X8}), native 0x{nativeResult:X16}");
                Assert.True(nativeImage.SequenceEqual(hleImage), $"iteration {iteration}: memory differs");
            }

            // Most generated cases must take the native path, or the comparison proves little.
            Assert.True(native > Iterations / 4, $"only {native} of {Iterations} cases ran natively");
        }
        finally
        {
            Munmap((nint)region, RegionSize);
            Munmap((nint)text, (nuint)code.Length);
        }
    }

    // Each pointer gets its own 0x200-byte slot (at a random offset inside it), so planted pointer fields never
    // overlap and every field and array the exports read stays inside the region.
    [ThreadStatic] private static Queue<int>? _freeSlots;

    private static void ResetSlots(System.Random random)
    {
        var slots = Enumerable.Range(0, RegionSize / 0x200).OrderBy(_ => random.Next()).ToArray();
        _freeSlots = new Queue<int>(slots);
    }

    private static ulong PickPointer(System.Random random, bool allowZero) =>
        allowZero ? 0 : RegionAddress + (ulong)(_freeSlots!.Dequeue() * 0x200) + (ulong)(random.Next(0, 0x20) * 4);

    private static void FillSemantics(System.Random random, byte[] region, ulong address)
    {
        if (address == 0)
        {
            return;
        }

        for (var i = 0; i < 40; i++)
        {
            // Few distinct semantic ids, so inputs and outputs match often.
            var word = (uint)random.Next() << 1 ^ (uint)random.Next(2);
            word = (word & ~0xFFu) | (uint)random.Next(0, 12);
            Write32(region, address + (ulong)(i * 4), word);
        }
    }

    private static void Write32(byte[] region, ulong address, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan((int)(address - RegionAddress)), value);

    private static void Write64(byte[] region, ulong address, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(region.AsSpan((int)(address - RegionAddress)), value);

    private const int ProtRead = 1;
    private const int ProtWrite = 2;
    private const int ProtExec = 4;
    private const int MapPrivate = 0x02;
    private const int MapAnonymous = 0x20;
    private const int MapFixedNoReplace = 0x100000;

    private static nint Mmap(nint address, nuint length, int protection, int flags, int unused = 0) =>
        mmap(address, length, protection, flags, -1, 0);

    [DllImport("libc", SetLastError = true)]
    private static extern nint mmap(nint address, nuint length, int protection, int flags, int fd, nint offset);

    [DllImport("libc", EntryPoint = "munmap")]
    private static extern int Munmap(nint address, nuint length);
}
