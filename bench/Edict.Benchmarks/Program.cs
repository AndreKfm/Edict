// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using BenchmarkDotNet.Running;
using Edict.Benchmarks;

// "profile <tape> <runs>": the frame benchmark's loop alone, for a profiler (dotnet-trace) to sample
if (args.Length == 3 && args[0] == "profile")
{
    var b = new FrameBenchmarks { Tape = args[1] };
    b.Setup();
    for (int run = 0; run < int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture); run++)
    {
        b.Spawn();
        long t = Stopwatch.GetTimestamp();
        b.HostFrame();
        Console.WriteLine($"run {run}: {Stopwatch.GetElapsedTime(t).TotalMilliseconds:F1} ms");
    }
    b.Cleanup();
    return;
}
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
