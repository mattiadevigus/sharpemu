// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native;

// SHARPEMU_NATIVE_AGC=1: hot libSceAgc exports run as native code on the guest thread (tools/native-agc). Each import
// gets a wrapper that calls the native fast path and, when that path asks for it, restores the argument registers
// and jumps to the export's HLE trampoline, so every case the fast path does not handle keeps the HLE behaviour.
public sealed unsafe partial class DirectExecutionBackend
{
	private static readonly bool NativeAgcEnabled =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_AGC"), "1", StringComparison.Ordinal);

	private const ulong NativeAgcFallback = 0xFEEDFACEDEADBEEFUL;

	private nint _nativeAgcText;

	private static bool TryGetNativeAgcEntry(string nid, out int entry)
	{
		entry = nid switch
		{
			"d-6uF9sZDIU" => NativeAgcCode.CxAddRegisters,
			"z2duB-hHQSM" => NativeAgcCode.ShAddRegisters,
			"vRoArM9zaIk" => NativeAgcCode.UcAddRegisters,
			"D9sr1xGUriE" => NativeAgcCode.CreatePrimState,
			"HV4j+E0MBHE" => NativeAgcCode.CreateInterpolantMapping,
			"q88lQ+GP5Yk" => NativeAgcCode.DcbDrawIndex,
			"GIIW2J37e70" => NativeAgcCode.DcbSetIndexSize,
			"ZvwO9euwYzc" => NativeAgcCode.DcbSetCxRegistersIndirect,
			"-HOOCn0JY48" => NativeAgcCode.DcbSetShRegistersIndirect,
			"hvUfkUIQcOE" => NativeAgcCode.DcbSetUcRegistersIndirect,
			"n2fD4A+pb+g" => NativeAgcCode.CbSetShRegisterRangeDirect,
			_ => -1,
		};
		return entry >= 0;
	}

	private bool TryInstallNativeAgcImport(string nid, int importIndex, nint stubAddress)
	{
		if (!NativeAgcEnabled || !TryGetNativeAgcEntry(nid, out var entry))
		{
			return false;
		}

		if (_nativeAgcText == 0)
		{
			_nativeAgcText = AllocateExecutable(NativeAgcCode.Text);
			if (_nativeAgcText == 0)
			{
				return false;
			}
		}

		var trampoline = CreateImportHandlerTrampoline(importIndex);
		if (trampoline == 0)
		{
			return false;
		}

		ReadOnlySpan<byte> template =
		[
			0x57,                                     // push rdi
			0x56,                                     // push rsi
			0x52,                                     // push rdx
			0x51,                                     // push rcx
			0x41, 0x50,                               // push r8
			0x41, 0x51,                               // push r9
			0x48, 0x83, 0xEC, 0x08,                   // sub rsp, 8 (16-byte aligned call)
			0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,       // mov rax, fast path      (imm at 0x0E)
			0xFF, 0xD0,                               // call rax
			0x48, 0x83, 0xC4, 0x08,                   // add rsp, 8
			0x49, 0xBB, 0, 0, 0, 0, 0, 0, 0, 0,       // mov r11, fallback marker (imm at 0x1E)
			0x4C, 0x39, 0xD8,                         // cmp rax, r11
			0x74, 0x05,                               // je fallback
			0x48, 0x83, 0xC4, 0x30,                   // add rsp, 48
			0xC3,                                     // ret
			0x41, 0x59,                               // fallback: pop r9
			0x41, 0x58,                               // pop r8
			0x59,                                     // pop rcx
			0x5A,                                     // pop rdx
			0x5E,                                     // pop rsi
			0x5F,                                     // pop rdi
			0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,       // mov rax, HLE trampoline (imm at 0x3A)
			0xFF, 0xE0,                               // jmp rax
		];
		Span<byte> wrapper = stackalloc byte[template.Length];
		template.CopyTo(wrapper);
		System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(wrapper[0x0E..], (ulong)(_nativeAgcText + entry));
		System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(wrapper[0x1E..], NativeAgcFallback);
		System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(wrapper[0x3A..], (ulong)trampoline);
		var address = AllocateExecutable(wrapper);
		return address != 0 && PatchImportStub(stubAddress, address);
	}

	private nint AllocateExecutable(ReadOnlySpan<byte> code)
	{
		var size = (nuint)code.Length;
		void* memory = VirtualAlloc(null, size, 12288u, 4u);
		if (memory == null)
		{
			return 0;
		}

		code.CopyTo(new Span<byte>(memory, code.Length));
		uint oldProtect = 0;
		if (!VirtualProtect(memory, size, 32u, &oldProtect))
		{
			VirtualFree(memory, 0u, 32768u);
			return 0;
		}

		FlushInstructionCache(GetCurrentProcess(), memory, size);
		_importHandlerTrampolines.Add((nint)memory);
		return (nint)memory;
	}
}
