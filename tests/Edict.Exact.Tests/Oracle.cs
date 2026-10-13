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
        if (version() != 7)
            throw new InvalidOperationException($"Oracle version {version()} at {path}, expected 7.");
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
    private static readonly delegate* unmanaged<float, byte*, int, int> FtosFn = (delegate* unmanaged<float, byte*, int, int>)Get("EdictOracle_Ftos");
    private static readonly delegate* unmanaged<float, float, float, byte*, int, int> VtosFn = (delegate* unmanaged<float, float, float, byte*, int, int>)Get("EdictOracle_Vtos");
    private static readonly delegate* unmanaged<byte*, float> EpairFloatFn = (delegate* unmanaged<byte*, float>)Get("EdictOracle_EpairFloat");
    private static readonly delegate* unmanaged<byte*, float> QAtofFn = (delegate* unmanaged<byte*, float>)Get("EdictOracle_QAtof");
    private static readonly object FormatLock = new();     // PF_ftos/PF_vtos use the reference's globals

    public static double Sin(double x) => SinFn(x);
    public static double Cos(double x) => CosFn(x);
    public static double Atan(double x) => AtanFn(x);
    public static double Atan2(double y, double x) => Atan2Fn(y, x);
    public static void Srand(uint seed) => SrandFn(seed);
    public static int Rand() => RandFn();
    public static int FloatToInt(float f) => FloatToIntFn(f);
    public static int DoubleToInt(double d) => DoubleToIntFn(d);

    /// <summary>PF_ftos itself; the string is copied to <paramref name="dest"/>, the length returned.</summary>
    public static int Ftos(float v, Span<byte> dest)
    {
        lock (FormatLock)
            fixed (byte* p = dest)
                return FtosFn(v, p, dest.Length);
    }

    /// <summary>PF_vtos itself.</summary>
    public static int Vtos(float x, float y, float z, Span<byte> dest)
    {
        lock (FormatLock)
            fixed (byte* p = dest)
                return VtosFn(x, y, z, p, dest.Length);
    }

    /// <summary>ED_ParseEpair's float conversion: (float)atof (s).</summary>
    public static float EpairFloat(ReadOnlySpan<byte> s) => WithCString(s, p => EpairFloatFn(p));

    /// <summary>Q_atof itself.</summary>
    public static float QAtof(ReadOnlySpan<byte> s) => WithCString(s, p => QAtofFn(p));

    private delegate float CStringFunc(byte* p);

    private static float WithCString(ReadOnlySpan<byte> s, CStringFunc f)
    {
        Span<byte> c = s.Length < 1024 ? stackalloc byte[s.Length + 1] : new byte[s.Length + 1];
        s.CopyTo(c);
        c[s.Length] = 0;
        fixed (byte* p = c)
            return f(p);
    }

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
