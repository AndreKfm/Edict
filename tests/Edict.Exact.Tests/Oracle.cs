// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Exact.Tests;

/// <summary>
/// The C reference as an in-process oracle (docs/TESTING.md, level 1). Path from EDICT_ORACLE; built by ref/build.sh
/// (libedict_oracle.so on Linux, edict_oracle.dll on Windows). Blittable arguments only.
/// </summary>
internal static unsafe class Oracle
{
    private static readonly nint Handle = Load();

    private static nint Load()
    {
        string? path = Environment.GetEnvironmentVariable("EDICT_ORACLE");
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException(
                "EDICT_ORACLE is not set: point it to the oracle library built by ref/build.sh (docs/TESTING.md).");
        nint handle = NativeLibrary.Load(path);
        var version = (delegate* unmanaged<int>)NativeLibrary.GetExport(handle, "EdictOracle_Version");
        if (version() != 1)
            throw new InvalidOperationException($"Oracle version {version()} at {path}, expected 1.");
        return handle;
    }

    private static nint Get(string name) => NativeLibrary.GetExport(Handle, name);

    private static readonly delegate* unmanaged<double, double> SinFn = (delegate* unmanaged<double, double>)Get("EdictOracle_Sin");
    private static readonly delegate* unmanaged<double, double> CosFn = (delegate* unmanaged<double, double>)Get("EdictOracle_Cos");
    private static readonly delegate* unmanaged<double, double> AtanFn = (delegate* unmanaged<double, double>)Get("EdictOracle_Atan");
    private static readonly delegate* unmanaged<double, double, double> Atan2Fn = (delegate* unmanaged<double, double, double>)Get("EdictOracle_Atan2");
    private static readonly delegate* unmanaged<uint, void> SrandFn = (delegate* unmanaged<uint, void>)Get("EdictOracle_Srand");
    private static readonly delegate* unmanaged<int> RandFn = (delegate* unmanaged<int>)Get("EdictOracle_Rand");
    private static readonly delegate* unmanaged<byte*, byte*, int> StrcmpFn = (delegate* unmanaged<byte*, byte*, int>)Get("EdictOracle_Strcmp");
    private static readonly delegate* unmanaged<float, int> FloatToIntFn = (delegate* unmanaged<float, int>)Get("EdictOracle_FloatToInt");
    private static readonly delegate* unmanaged<double, int> DoubleToIntFn = (delegate* unmanaged<double, int>)Get("EdictOracle_DoubleToInt");

    public static double Sin(double x) => SinFn(x);
    public static double Cos(double x) => CosFn(x);
    public static double Atan(double x) => AtanFn(x);
    public static double Atan2(double y, double x) => Atan2Fn(y, x);
    public static void Srand(uint seed) => SrandFn(seed);
    public static int Rand() => RandFn();
    public static int FloatToInt(float f) => FloatToIntFn(f);
    public static int DoubleToInt(double d) => DoubleToIntFn(d);

    /// <summary>strcmp on two NUL-terminated copies of the spans.</summary>
    public static int Strcmp(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Span<byte> ca = stackalloc byte[a.Length + 1];
        Span<byte> cb = stackalloc byte[b.Length + 1];
        a.CopyTo(ca);
        b.CopyTo(cb);
        ca[a.Length] = 0;
        cb[b.Length] = 0;
        fixed (byte* pa = ca)
        fixed (byte* pb = cb)
            return StrcmpFn(pa, pb);
    }
}
