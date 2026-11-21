// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

// Diagnostic: SHARPEMU_PROFILE_IMPORT_TIME=1 times every HLE import and the guest memory
// reads/writes made while it runs, and prints the most expensive imports every 5 seconds.
internal static class ImportTimeProfile
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_IMPORT_TIME") == "1";

    [ThreadStatic] internal static long ReadTicks;
    [ThreadStatic] internal static long ReadOps;
    [ThreadStatic] internal static long WriteTicks;
    [ThreadStatic] internal static long WriteOps;

    private const int Slots = 8192;
    private static readonly long[] Calls = new long[Slots];
    private static readonly long[] Ticks = new long[Slots];
    private static readonly long[] RTicks = new long[Slots];
    private static readonly long[] ROps = new long[Slots];
    private static readonly long[] WTicks = new long[Slots];
    private static readonly long[] WOps = new long[Slots];
    private static readonly string?[] Names = new string?[Slots];
    private static int _started;

    internal readonly struct Scope
    {
        private readonly int _index;
        private readonly long _start, _r, _ro, _w, _wo;
        internal Scope(int index, string name)
        {
            _index = index;
            if ((uint)index < Slots) Names[index] ??= name;
            if (Interlocked.Exchange(ref _started, 1) == 0) StartReporter();
            _r = ReadTicks; _ro = ReadOps; _w = WriteTicks; _wo = WriteOps;
            _start = Stopwatch.GetTimestamp();
        }

        internal void End()
        {
            var elapsed = Stopwatch.GetTimestamp() - _start;
            var i = _index;
            if ((uint)i >= Slots) return;
            Interlocked.Increment(ref Calls[i]);
            Interlocked.Add(ref Ticks[i], elapsed);
            Interlocked.Add(ref RTicks[i], ReadTicks - _r);
            Interlocked.Add(ref ROps[i], ReadOps - _ro);
            Interlocked.Add(ref WTicks[i], WriteTicks - _w);
            Interlocked.Add(ref WOps[i], WriteOps - _wo);
        }
    }

    private static void StartReporter()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(5000);
                Report();
            }
        }) { IsBackground = true, Name = "SharpEmu import time profile" };
        thread.Start();
    }

    private static void Report()
    {
        double us(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
        var rows = Enumerable.Range(0, Slots)
            .Select(i => (i, calls: Interlocked.Exchange(ref Calls[i], 0), ticks: Interlocked.Exchange(ref Ticks[i], 0),
                r: Interlocked.Exchange(ref RTicks[i], 0), ro: Interlocked.Exchange(ref ROps[i], 0),
                w: Interlocked.Exchange(ref WTicks[i], 0), wo: Interlocked.Exchange(ref WOps[i], 0)))
            .Where(row => row.calls > 0)
            .OrderByDescending(row => row.ticks)
            .Take(20)
            .ToArray();
        var total = rows.Sum(row => row.ticks);
        Console.Error.WriteLine($"[PERF][IMPORT_TIME] window=5s top20_total_ms={us(total) / 1000:F1}");
        foreach (var row in rows)
        {
            Console.Error.WriteLine(
                $"[PERF][IMPORT_TIME] {Names[row.i]} calls={row.calls} total_ms={us(row.ticks) / 1000:F1} " +
                $"avg_us={us(row.ticks) / row.calls:F2} reads={row.ro} read_ms={us(row.r) / 1000:F1} " +
                $"writes={row.wo} write_ms={us(row.w) / 1000:F1}");
        }
    }
}
