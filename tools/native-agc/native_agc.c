// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Native fast paths for hot libSceAgc exports (SHARPEMU_NATIVE_AGC=1).
//
// These run directly on the guest thread with the guest's SysV ABI, so a call costs what a call into
// guest code costs. Each one ports the HLE export in src/SharpEmu.Libs/Agc for its common case and
// returns NATIVE_AGC_FALLBACK, before it writes anything, for every case it does not handle. The
// wrapper that DirectExecutionBackend builds around it then restores the argument registers and
// jumps to the export's HLE trampoline, so unusual inputs and every error keep the HLE behaviour.
//
// Built by build.sh into flat position-independent code: no calls, no data, no SSE.

#include <stdint.h>

#define NATIVE_AGC_FALLBACK 0xFEEDFACEDEADBEEFull

static inline uint32_t rd32(uint64_t a) { return *(volatile uint32_t *)a; }
static inline uint64_t rd64(uint64_t a) { return *(volatile uint64_t *)a; }
static inline uint16_t rd16(uint64_t a) { return *(volatile uint16_t *)a; }
static inline uint8_t rd8(uint64_t a) { return *(volatile uint8_t *)a; }
static inline void wr32(uint64_t a, uint32_t v) { *(volatile uint32_t *)a = v; }

// AgcExports.PacketPatching.cs: AddIndirectPatchRegisters / TryGetIndirectPatchLayout.
static inline uint64_t add_registers(uint64_t cmd, uint32_t count, uint32_t op, uint32_t legacyReg)
{
    if (cmd == 0)
        return NATIVE_AGC_FALLBACK;
    uint32_t header = rd32(cmd);
    uint32_t packetOp = (header >> 8) & 0xFFu;
    uint32_t length = ((header >> 16) & 0x3FFFu) + 2u;
    if (packetOp == op && length == 5) {
        uint32_t current = rd32(cmd + 16);
        uint32_t total = ((current & 0x3FFFu) + count) & 0x3FFFu;
        wr32(cmd + 16, (current & ~0x3FFFu) | total);
        return 0;
    }
    if (packetOp == 0x10 && ((header >> 2) & 0x3Fu) == legacyReg && length == 4) {
        wr32(cmd + 4, rd32(cmd + 4) + count);
        return 0;
    }
    return NATIVE_AGC_FALLBACK;
}

uint64_t agc_cx_add_registers(uint64_t cmd, uint64_t count) { return add_registers(cmd, (uint32_t)count, 0x9F, 0x12); }
uint64_t agc_sh_add_registers(uint64_t cmd, uint64_t count) { return add_registers(cmd, (uint32_t)count, 0x63, 0x11); }
uint64_t agc_uc_add_registers(uint64_t cmd, uint64_t count) { return add_registers(cmd, (uint32_t)count, 0x64, 0x13); }

// AgcPrimitiveHelpers.PrimitiveTypeToGsOut.
static inline uint32_t primitive_to_gs_out(uint32_t primitiveType)
{
    if (primitiveType == 1) return 0;
    if (primitiveType == 2 || primitiveType == 3 || primitiveType == 10 || primitiveType == 11 || primitiveType == 18) return 1;
    if (primitiveType == 7) return 3;
    if (primitiveType == 0x11) return 4;
    return 2;
}

// AgcExports.ShaderState.cs: CreatePrimState / TryCreatePrimCxState.
uint64_t agc_create_prim_state(uint64_t cx, uint64_t uc, uint64_t hull, uint64_t gs, uint64_t prim64)
{
    uint32_t prim = (uint32_t)prim64;
    if (cx == 0 && uc == 0)
        return 0;
    if (gs == 0)
        return NATIVE_AGC_FALLBACK;
    uint8_t shaderType = rd8(gs + 0x5A);
    if (shaderType != 2 && shaderType != 6)
        return NATIVE_AGC_FALLBACK;
    uint64_t specials = rd64(gs + 0x28);
    if (specials == 0)
        return NATIVE_AGC_FALLBACK;
    uint64_t hullSpecials = 0;
    if (hull != 0) {
        if (rd8(hull + 0x5A) != 3)
            return NATIVE_AGC_FALLBACK;
        hullSpecials = rd64(hull + 0x28);
        if (hullSpecials == 0)
            return NATIVE_AGC_FALLBACK;
    }

    if (cx != 0) {
        uint64_t stages = rd64(specials + 0x08);
        uint32_t stagesOffset = (uint32_t)stages;
        uint32_t stagesValue = (uint32_t)(stages >> 32);
        uint32_t outOffset, outValue;
        if (stagesValue & 0x20u) {
            uint64_t pair = rd64(specials + 0x20);
            outOffset = (uint32_t)pair;
            outValue = (uint32_t)(pair >> 32);
        } else {
            outOffset = 0x29B;
            outValue = primitive_to_gs_out(prim);
        }
        if (hullSpecials != 0) {
            stagesValue |= rd32(hullSpecials + 0x08 + 4);
            if ((stagesValue & 0x20u) == 0) {
                uint64_t pair = rd64(hullSpecials + 0x20);
                outOffset = (uint32_t)pair;
                outValue = (uint32_t)(pair >> 32);
            }
        }
        wr32(cx + 0, stagesOffset);
        wr32(cx + 4, stagesValue);
        wr32(cx + 8, outOffset);
        wr32(cx + 12, outValue);
    }

    if (uc != 0) {
        uint64_t geCntl = rd64(specials + 0x00);
        uint64_t userVgpr = rd64((hullSpecials != 0 ? hullSpecials : specials) + 0x28);
        wr32(uc + 0, (uint32_t)geCntl);
        wr32(uc + 4, (uint32_t)(geCntl >> 32));
        wr32(uc + 8, (uint32_t)userVgpr);
        wr32(uc + 12, (uint32_t)(userVgpr >> 32));
        wr32(uc + 16, 0x242);
        wr32(uc + 20, prim);
    }
    return 0;
}

// AgcExports.ShaderState.cs: CreateInterpolantMapping and its value helpers.
static inline uint32_t apply_default(uint32_t value, uint32_t ps)
{
    value &= ~0x00000300u;
    value |= ((ps >> 28) & 0x3u) << 8;
    return value;
}

static inline uint32_t apply_default_hi(uint32_t value, uint32_t ps)
{
    value &= ~0x00600000u;
    value |= ((ps >> 30) & 0x3u) << 21;
    return value;
}

uint64_t agc_create_interpolant_mapping(uint64_t regs, uint64_t gs, uint64_t ps)
{
    if (regs == 0)
        return NATIVE_AGC_FALLBACK;
    uint32_t inputCount = 0;
    uint64_t inputAddress = 0;
    if (ps != 0) {
        inputAddress = rd64(ps + 0x30);
        inputCount = rd32(ps + 0x50);
    }
    if (inputCount != 0 && inputAddress != 0 && gs == 0)
        return NATIVE_AGC_FALLBACK;

    uint64_t outputAddress = 0;
    uint32_t outputCount = 0;
    if (inputCount != 0 && inputAddress != 0) {
        outputAddress = rd64(gs + 0x38);
        outputCount = rd16(gs + 0x56);
        if (outputAddress == 0)
            outputCount = 0;
        if (inputCount > 32)
            inputCount = 32;
    } else {
        inputCount = 0;
    }

    for (uint32_t i = 0; i < 32; i++) {
        uint32_t value = i;
        if (i < inputCount) {
            uint32_t psWord = rd32(inputAddress + (uint64_t)i * 4);
            uint32_t gsWord = 0;
            int found = 0;
            for (uint32_t j = 0; j < outputCount; j++) {
                uint32_t candidate = rd32(outputAddress + (uint64_t)j * 4);
                if ((uint8_t)candidate == (uint8_t)psWord) {
                    gsWord = candidate;
                    found = 1;
                    break;
                }
            }

            if (psWord & 0x00300000u) {
                value = (psWord << 4) & 0x03000000u;
                if (!found) {
                    value |= 0x00180020u;
                } else {
                    uint32_t common = psWord & gsWord;
                    value &= 0xFFF7FFDFu;
                    value |= (common >> 15) & 0x20u;
                    value ^= 0x00080020u;
                    value &= ~0x00100000u;
                    value |= (~common >> 1) & 0x00100000u;
                }
                value = apply_default_hi(value, psWord);
            } else {
                value = ((psWord & 0x01000000u) != 0 || !found) ? 0x20u : 0u;
            }

            if (found) {
                uint32_t flat = ((psWord & 0x00400000u) != 0 || (psWord & 0x01000000u) != 0) ? 0x400u : 0u;
                value &= ~0x1Fu;
                value |= (gsWord >> 8) & 0x1Fu;
                value &= ~0x400u;
                value |= flat;
                value = apply_default(value, psWord);
            } else {
                value &= ~0x1Fu;
                value &= ~0x400u;
                value = apply_default(value, psWord);
            }
        }
        wr32(regs + (uint64_t)i * 8, 0x191u + i);
        wr32(regs + (uint64_t)i * 8 + 4, value);
    }
    return 0;
}

// ---- Command buffer packet builders -------------------------------------------------------------------------------
//
// AgcExports.cs: TryPrepareCommandDwords. The fast path only appends when the packets fit without the guest's
// buffer-full callback; otherwise it falls back before writing, and the HLE export runs the callback. It does not
// take the HLE's per-buffer lock or record the buffer's writer thread, so it is only correct while one guest thread
// appends to a buffer at a time, which is how libSceAgc command buffers are meant to be used.

static inline void wr64(uint64_t a, uint64_t v) { *(volatile uint64_t *)a = v; }

static inline uint32_t pm4(uint32_t lengthDwords, uint32_t op, uint32_t reg)
{
    return 0xC0000000u | ((((uint16_t)lengthDwords - 2u) & 0x3FFFu) << 16) | ((op & 0xFFu) << 8) | ((reg & 0x3Fu) << 2);
}

// The dwords left in the buffer, as GetRemainingCommandDwords computes them.
static inline uint32_t remaining_dwords(uint64_t buffer)
{
    uint64_t up = rd64(buffer + 0x10);
    uint64_t down = rd64(buffer + 0x18);
    uint32_t reserved = rd32(buffer + 0x30);
    uint32_t available = (uint32_t)((int64_t)(down - up) / 4);
    return (available > reserved ? available : reserved) - reserved;
}

static inline uint64_t take_dwords(uint64_t buffer, uint32_t dwords)
{
    uint64_t up = rd64(buffer + 0x10);
    wr64(buffer + 0x10, up + (uint64_t)dwords * 4);
    return up;
}

// AgcExports.DrawPackets.cs: DcbDrawIndex (INDEX_BASE + INDEX_BUFFER_SIZE, then DRAW_INDEX_2).
uint64_t agc_dcb_draw_index(uint64_t buffer, uint64_t count64, uint64_t indexAddress, uint64_t modifier)
{
    if (buffer == 0 || remaining_dwords(buffer) < 11)
        return NATIVE_AGC_FALLBACK;
    uint32_t count = (uint32_t)count64;
    uint32_t initiator = (modifier & (1ull << 32)) != 0 ? 0u : (((uint32_t)modifier >> 3) & 0x20u);
    uint64_t base = take_dwords(buffer, 5);
    wr32(base + 0, pm4(3, 0x26, 0));
    wr32(base + 4, (uint32_t)indexAddress);
    wr32(base + 8, (uint32_t)(indexAddress >> 32));
    wr32(base + 12, pm4(2, 0x13, 0));
    wr32(base + 16, count);
    uint64_t draw = take_dwords(buffer, 6);
    wr32(draw + 0, pm4(6, 0x27, 0));
    wr32(draw + 4, count);
    wr32(draw + 8, (uint32_t)indexAddress);
    wr32(draw + 12, (uint32_t)(indexAddress >> 32));
    wr32(draw + 16, count);
    wr32(draw + 20, initiator);
    return draw;
}

// AgcExports.DrawPackets.cs: DcbSetIndexSize.
uint64_t agc_dcb_set_index_size(uint64_t buffer, uint64_t size64, uint64_t cache64)
{
    if (buffer == 0 || remaining_dwords(buffer) < 3)
        return NATIVE_AGC_FALLBACK;
    uint32_t size = (uint32_t)(size64 & 0xFF);
    uint32_t cache = (uint32_t)(cache64 & 0xFF);
    uint64_t command = take_dwords(buffer, 3);
    wr32(command + 0, pm4(3, 0x7A, 0));
    wr32(command + 4, 0x20000000u | 0x243u);
    wr32(command + 8, 0x400u | (size & 0x3u) | ((cache & 0x3u) << 6));
    return command;
}

// AgcExports.RegisterPackets.cs: DcbSetRegistersIndirect.
static inline uint64_t set_registers_indirect(uint64_t buffer, uint64_t registers, uint64_t count, uint32_t op)
{
    if (buffer == 0 || remaining_dwords(buffer) < 5)
        return NATIVE_AGC_FALLBACK;
    uint64_t command = take_dwords(buffer, 5);
    wr32(command + 0, pm4(5, op, 0));
    wr32(command + 4, (uint32_t)(registers & 0xFFFFFFFCull));
    wr32(command + 8, (uint32_t)(registers >> 32));
    wr32(command + 12, 0x80000000u);
    wr32(command + 16, (uint32_t)count & 0x3FFFu);
    return command;
}

uint64_t agc_dcb_set_cx_registers_indirect(uint64_t b, uint64_t r, uint64_t c) { return set_registers_indirect(b, r, c, 0x9F); }
uint64_t agc_dcb_set_sh_registers_indirect(uint64_t b, uint64_t r, uint64_t c) { return set_registers_indirect(b, r, c, 0x63); }
uint64_t agc_dcb_set_uc_registers_indirect(uint64_t b, uint64_t r, uint64_t c) { return set_registers_indirect(b, r, c, 0x64); }

// AgcExports.RegisterPackets.cs: CbSetShRegisterRangeDirect (SharpEmu marker NOP, then SET_SH_REG and its values).
uint64_t agc_cb_set_sh_register_range_direct(uint64_t buffer, uint64_t offset64, uint64_t values, uint64_t count64)
{
    uint32_t offset = (uint32_t)offset64;
    uint32_t count = (uint32_t)count64;
    if (buffer == 0 || offset == 0 || offset > 0x3FF || count == 0 || count > 0x3FFF)
        return NATIVE_AGC_FALLBACK;
    uint32_t packetDwords = count + 2;
    if (values != 0 && values > ~0ull - (uint64_t)count * 4)
        return NATIVE_AGC_FALLBACK;
    if (remaining_dwords(buffer) < 2 + packetDwords)
        return NATIVE_AGC_FALLBACK;
    uint64_t marker = take_dwords(buffer, 2 + packetDwords);
    wr32(marker + 0, pm4(2, 0x10, 0));
    wr32(marker + 4, 0x6875000Du);
    wr32(marker + 8, pm4(packetDwords, 0x76, 0));
    wr32(marker + 12, offset);
    // A null source reserves the payload for the caller to fill later. Otherwise forward word copy, which is also
    // what the HLE's bulk copy produces when the ranges do not overlap.
    if (values != 0) {
        for (uint32_t i = 0; i < count; i++)
            wr32(marker + 16 + (uint64_t)i * 4, rd32(values + (uint64_t)i * 4));
    }
    return marker + 8;
}
