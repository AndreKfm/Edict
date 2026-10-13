// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using Edict.Exact;
using static Edict.Engine.Tests.VmOpcodeFuzz;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 for the builtins of cut 2, step 3a that read only the VM's memory and the rand stream: each runs as the
/// reference's <c>pr_builtins[num] ()</c> through the oracle and as the port's on identical memory, on edge values
/// and random inputs; then globals, edicts, the string temp and the rand seed are compared. The capture replay
/// covers the same builtins (and cvar, precache) on what the game passes them.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class BuiltinTests
{
    private static readonly nint Lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("EDICT_ORACLE")
        ?? throw new InvalidOperationException("EDICT_ORACLE is not set (docs/TESTING.md)"));
    private static readonly delegate* unmanaged<int, int, float*, int, byte*, int, int, void*, int, int, uint, uint> BuiltinFn =
        (delegate* unmanaged<int, int, float*, int, byte*, int, int, void*, int, int, uint, uint>)NativeLibrary.GetExport(Lib, "EdictOracle_Builtin");

    private static readonly delegate* unmanaged<byte*, float> QAtofFn =
        (delegate* unmanaged<byte*, float>)NativeLibrary.GetExport(Lib, "EdictOracle_QAtof");

    /// <summary>Engine strings at offsets the test chooses (cvar names for PF_cvar).</summary>
    private sealed class TestStrings : IEngineStrings
    {
        private readonly Dictionary<int, nint> _s = [];

        public void Set(int ofs, ReadOnlySpan<byte> text)
        {
            byte* p = (byte*)NativeMemory.AllocZeroed((nuint)text.Length + 1);
            text.CopyTo(new Span<byte>(p, text.Length));
            _s[ofs] = (nint)p;
        }

        public byte* Get(int ofs) => _s.TryGetValue(ofs, out nint p) ? (byte*)p : throw new InvalidOperationException($"no engine string at {ofs}");
    }

    /// <summary>
    /// Cvar values as text: integers, decimals, signs, hexadecimal, exponents (which Q_atof stops at and atof reads),
    /// leading zeros, an empty value.
    /// </summary>
    private static string CvarValue(ref Rng rng) => rng.Below(8) switch
    {
        0 => $"{rng.Below(100000) - 50000}",
        1 => $"{(rng.Below(2) == 0 ? "-" : "")}{rng.Below(1000)}.{rng.Below(1000000):D6}",
        2 => $"0.{rng.Below(10)}",
        3 => $"0x{rng.Bits() & 0xFFFFF:x}",
        4 => $"{rng.Below(100)}e{rng.Below(10)}",
        5 => $"{rng.Below(10)}.{rng.Below(10)}e-{rng.Below(5)}",
        6 => $"00{rng.Below(1000)}.5",
        _ => rng.Below(4) == 0 ? "" : $"{rng.Below(1 << 30)}.{rng.Below(100)}",
    };

    /// <summary>
    /// The first divergence over all cases, or null. <paramref name="call"/> runs a builtin on the port's side
    /// (default <see cref="Builtins.Call"/>; known-bad variants replace it).
    /// </summary>
    internal static string? FirstDivergence(Action<Server, int>? call = null, int random = 50_000)
    {
        call ??= Builtins.Call;
        using var f = new VmOpcodeFuzz();
        int numStrings = f.Progs.NumStrings;
        int tempOfs = numStrings;                   // the temp right after the strings, on both sides
        byte* refStrings = (byte*)NativeMemory.AllocZeroed((nuint)(numStrings + Server.StringTempSize));
        byte* portTemp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
        try
        {
            new ReadOnlySpan<byte>(f.Strings, numStrings).CopyTo(new Span<byte>(refStrings, numStrings));
            var vm = new QcVm(f.Progs, (int*)f.PortGlobals, f.PortEdicts, f.Header, VmReplay.NumBuiltins);
            var rng = new Rng(7);
            const int CvarCount = 2000, NameBase = 1 << 20;     // the names live at engine string offsets
            var strings = new TestStrings();
            var cvarList = new List<(byte[], byte[])>();
            for (int i = 0; i < CvarCount; i++)
            {
                byte[] name = Encoding.ASCII.GetBytes($"edict_fuzz_{i}");
                strings.Set(NameBase + i, name);
                cvarList.Add((name, Encoding.ASCII.GetBytes(CvarValue(ref rng))));
            }
            var sv = new Server(vm, f.Progs, (int*)f.PortGlobals, f.PortEdicts, f.Header, new Cvars(cvarList), strings,
                                portTemp, tempOfs);

            string? Run(int num, int argc, string what)
            {
                uint seed = rng.Bits();
                uint refSeed = BuiltinFn(num, argc, f.RefGlobals, NumGlobalsOf(f), refStrings, numStrings, tempOfs,
                                         f.RefEdicts, Fields, 0, seed);
                sv.Rand.Srand(seed);
                call(sv, num);
                string? d = f.Compare(nanClass: true);       // the builtins here only compute (docs/adr/D8)
                if (d is null && sv.Rand.Seed != refSeed)
                    d = $"rand seed afterwards: port 0x{sv.Rand.Seed:X8}, reference 0x{refSeed:X8}";
                if (d is null && ((int*)f.RefGlobals)[QcVm.OfsReturn] == tempOfs)
                {
                    var r = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(refStrings + tempOfs);
                    var p = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(portTemp);
                    if (!r.SequenceEqual(p))
                        d = $"string temp: port \"{Encoding.Latin1.GetString(p)}\", reference \"{Encoding.Latin1.GetString(r)}\"";
                }
                return d is null ? null : $"builtin #{num} {what}: {d}";
            }

            void Vector(int ofs, uint x, uint y, uint z)
            {
                f.Global(ofs, x);
                f.Global(ofs + 1, y);
                f.Global(ofs + 2, z);
            }

            uint Angle() => rng.Below(3) switch
            {
                0 => rng.Float(),
                1 => BitConverter.SingleToUInt32Bits(rng.Below(17) * 45f - 360),
                _ => BitConverter.SingleToUInt32Bits((float)((rng.Next() >> 11) * (1.0 / 9007199254740992.0) * 1440 - 720)),
            };

            // one-vector builtins: edges in every component combination of two values, then random vectors
            int[] vectorBuiltins = [Builtins.MakeVectors, Builtins.Normalize, Builtins.Vlen, Builtins.VectoYaw, Builtins.VectoAngles];
            uint[] e = Rng.Edges;
            foreach (int num in vectorBuiltins)
            {
                for (int i = 0; i < e.Length; i++)
                    for (int j = 0; j < e.Length; j++)
                    {
                        f.Scramble(ref rng);
                        Vector(OfsParm0, e[i], e[j], e[(i + j) % e.Length]);
                        if (Run(num, 1, $"({Hex(e[i])}, {Hex(e[j])}, {Hex(e[(i + j) % e.Length])})") is { } d)
                            return d;
                    }
                for (int n = 0; n < random; n++)
                {
                    f.Scramble(ref rng);
                    uint x = num == Builtins.MakeVectors ? Angle() : rng.Float();
                    uint y = num == Builtins.MakeVectors ? Angle() : rng.Float();
                    uint z = num == Builtins.MakeVectors ? Angle() : rng.Float();
                    if (rng.Below(8) == 0)
                        z = 0;
                    if (rng.Below(8) == 0)
                        (x, y) = (0, rng.Below(2) == 0 ? 0 : 0x80000000u);
                    Vector(OfsParm0, x, y, z);
                    if (Run(num, 1, $"({Hex(x)}, {Hex(y)}, {Hex(z)})") is { } d)
                        return d;
                }
            }

            // one-float builtins
            foreach (int num in new[] { Builtins.Fabs, Builtins.Floor, Builtins.Ceil, Builtins.Rint, Builtins.Ftos })
            {
                IEnumerable<uint> inputs = e.Concat(Enumerable.Range(0, random).Select(_ => rng.Below(4) == 0
                    ? BitConverter.SingleToUInt32Bits(rng.Below(20000) / 8f - 1250)        // halves, tenths, integers
                    : rng.Float()));
                foreach (uint x in inputs.ToArray())
                {
                    f.Scramble(ref rng);
                    f.Global(OfsParm0, x);
                    if (Run(num, 1, Hex(x)) is { } d)
                        return d;
                }
            }

            // vtos: components below 1e30 (from 1e37 on PF_vtos overflows its 128-byte temp in C)
            for (int n = 0; n < random; n++)
            {
                f.Scramble(ref rng);
                uint x = Small(rng.Float()), y = Small(rng.Float()), z = Small(rng.Float());
                Vector(OfsParm0, x, y, z);
                if (Run(Builtins.Vtos, 1, $"({Hex(x)}, {Hex(y)}, {Hex(z)})") is { } d)
                    return d;
            }

            // random: any seed
            for (int n = 0; n < random; n++)
            {
                f.Scramble(ref rng);
                if (Run(Builtins.Random, 0, $"#{n}") is { } d)
                    return d;
            }

            // cvar: C's Cvar_VariableValue is Q_atof (var->string); the oracle has the reference's Q_atof
            for (int i = 0; i < CvarCount; i++)
            {
                f.Scramble(ref rng);
                f.Global(OfsParm0, (uint)(NameBase + i));
                call(sv, Builtins.Cvar);
                byte[] value = cvarList[i].Item2;
                float reference;
                fixed (byte* v = value.Length == 0 ? new byte[1] : [.. value, 0])
                    reference = QAtofFn(v);
                uint port = ((uint*)f.PortGlobals)[QcVm.OfsReturn];
                if (port != BitConverter.SingleToUInt32Bits(reference))
                    return $"builtin #{Builtins.Cvar} \"{Encoding.ASCII.GetString(value)}\": port {Hex(port)}, reference Q_atof {Hex(BitConverter.SingleToUInt32Bits(reference))}";
            }

            // changeyaw: self's angles[1], ideal_yaw and yaw_speed
            for (int n = 0; n < random * 2; n++)
            {
                f.Scramble(ref rng);
                int ent = 1 + rng.Below(Edicts - 1);
                f.Global(GlobalVars.Self, (uint)f.EdictOffset(ent));
                uint current = rng.Below(2) == 0 ? Angle() : rng.Float();
                uint ideal = rng.Below(3) == 0 ? current : rng.Below(2) == 0 ? Angle() : rng.Float();
                uint speed = rng.Below(2) == 0 ? BitConverter.SingleToUInt32Bits(rng.Below(41) * 5f) : rng.Float();
                f.Field(ent, EntVars.Angles + 1, current);
                f.Field(ent, EntVars.IdealYaw, ideal);
                f.Field(ent, EntVars.YawSpeed, speed);
                if (Run(Builtins.ChangeYaw, 0, $"yaw {Hex(current)}, ideal {Hex(ideal)}, speed {Hex(speed)}") is { } d)
                    return d;
            }
            return null;
        }
        finally
        {
            NativeMemory.Free(refStrings);
            NativeMemory.Free(portTemp);
        }
    }

    private const int OfsParm0 = QcVm.OfsParm0;

    private static int NumGlobalsOf(VmOpcodeFuzz f) => f.Progs.NumGlobals;

    private static uint Small(uint bits) => MathF.Abs(BitConverter.UInt32BitsToSingle(bits)) < 1e30f ? bits : bits & 0x8FFFFFFFu;

    [Fact]
    public void Pure_builtins_match_the_reference_on_edge_and_random_inputs()
    {
        string? d = FirstDivergence();
        Assert.True(d is null, "first divergence: " + d);
    }

    // docs/TESTING.md R7 for the builtins: one deliberate mistake at a time, run through this fuzz and the replay.
    public enum Mutation
    {
        RandomBy32768,          // (rand () & 0x7fff) / 32768.0f
        VectoyawInFloat,        // atan2 and the scaling in float
        RintFloatSum,           // (int)(f + 0.5f)
        NormalizeInDouble,      // 1/len and the products in double
        MakevectorsRuntimeSin,  // Math.Sin/Math.Cos instead of the pinned libm
        AnglemodInFloat,        // anglemod with a float product
        ChangeyawStrictBound,   // move > 180 instead of move >= 180
        CvarWithAtof,           // Cvar_VariableValue with atof instead of Q_atof
        PrecacheNoReturn,       // precache_* without the return value
        FtosAlwaysFixed,        // ftos always "%5.1f"
    }

    private static float* G(Server sv, int ofs) => sv.G(ofs);

    internal static Action<Server, int> Mutant(Mutation m) => (sv, num) =>
    {
        switch (m)
        {
            case Mutation.RandomBy32768 when num == Builtins.Random:
                *G(sv, QcVm.OfsReturn) = (sv.Rand.Next() & 0x7fff) / 32768f;
                return;
            case Mutation.VectoyawInFloat when num == Builtins.VectoYaw:
            {
                float y = G(sv, OfsParm0)[1], x = G(sv, OfsParm0)[0];
                float yaw = y == 0 && x == 0 ? 0 : CCast.I32(MathF.Atan2(y, x) * 180 / MathF.PI);
                if (yaw < 0)
                    yaw += 360;
                *G(sv, QcVm.OfsReturn) = yaw;
                return;
            }
            case Mutation.RintFloatSum when num == Builtins.Rint:
            {
                float v = *G(sv, OfsParm0);
                *G(sv, QcVm.OfsReturn) = v > 0 ? CCast.I32(v + 0.5f) : CCast.I32(v - 0.5f);
                return;
            }
            case Mutation.NormalizeInDouble when num == Builtins.Normalize:
            {
                float* v = G(sv, OfsParm0);
                float n = (float)Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
                double inv = n == 0 ? 0 : 1.0 / n;
                float x = (float)(v[0] * inv), y = (float)(v[1] * inv), z = (float)(v[2] * inv);
                G(sv, QcVm.OfsReturn)[0] = x;
                G(sv, QcVm.OfsReturn)[1] = y;
                G(sv, QcVm.OfsReturn)[2] = z;
                return;
            }
            case Mutation.MakevectorsRuntimeSin when num == Builtins.MakeVectors:
            {
                float* a = G(sv, OfsParm0);
                float ay = (float)(a[1] * (Math.PI * 2 / 360)), ap = (float)(a[0] * (Math.PI * 2 / 360)), ar = (float)(a[2] * (Math.PI * 2 / 360));
                float sy = (float)Math.Sin(ay), cy = (float)Math.Cos(ay), sp = (float)Math.Sin(ap), cp = (float)Math.Cos(ap);
                float sr = (float)Math.Sin(ar), cr = (float)Math.Cos(ar);
                *(Vec3*)G(sv, GlobalVars.VForward) = new Vec3(cp * cy, cp * sy, -sp);
                *(Vec3*)G(sv, GlobalVars.VRight) = new Vec3(-1 * sr * sp * cy + -1 * cr * -sy, -1 * sr * sp * sy + -1 * cr * cy, -1 * sr * cp);
                *(Vec3*)G(sv, GlobalVars.VUp) = new Vec3(cr * sp * cy + -sr * -sy, cr * sp * sy + -sr * cy, cr * cp);
                return;
            }
            case Mutation.AnglemodInFloat or Mutation.ChangeyawStrictBound when num == Builtins.ChangeYaw:
            {
                static float AnglemodF(float a) => 360f / 65536 * (CCast.I32(a * (65536f / 360)) & 65535);
                Func<float, float> mod = m == Mutation.AnglemodInFloat ? AnglemodF : Mathlib.Anglemod;
                float* ent = (float*)(sv.Edicts + sv.Globals[GlobalVars.Self] + sv.HeaderBytes);
                float current = mod(ent[EntVars.Angles + 1]), ideal = ent[EntVars.IdealYaw], speed = ent[EntVars.YawSpeed];
                if (current == ideal)
                    return;
                float move = ideal - current;
                if (ideal > current)
                {
                    if (m == Mutation.ChangeyawStrictBound ? move > 180 : move >= 180)
                        move -= 360;
                }
                else if (move <= -180)
                    move += 360;
                if (move > 0)
                {
                    if (move > speed)
                        move = speed;
                }
                else if (move < -speed)
                    move = -speed;
                ent[EntVars.Angles + 1] = mod(current + move);
                return;
            }
            case Mutation.CvarWithAtof when num == Builtins.Cvar:
            {
                byte* s = sv.Cvars.VariableString(sv.String(sv.Globals[OfsParm0]));
                *G(sv, QcVm.OfsReturn) = (float)CAtof.Atof(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(s));
                return;
            }
            case Mutation.PrecacheNoReturn when num is Builtins.PrecacheSound or Builtins.PrecacheModel or Builtins.PrecacheSound2 or Builtins.PrecacheModel2:
            {
                int saved = sv.Globals[QcVm.OfsReturn];
                Builtins.Call(sv, num);
                sv.Globals[QcVm.OfsReturn] = saved;
                return;
            }
            case Mutation.FtosAlwaysFixed when num == Builtins.Ftos:
            {
                Span<byte> buf = stackalloc byte[CFormat.MaxFtosLength];
                int n = CFormat.Fixed5_1(*G(sv, OfsParm0), buf);
                Span<byte> temp = sv.StringTemp;
                buf[..n].CopyTo(temp);
                temp[n] = 0;
                sv.Globals[QcVm.OfsReturn] = sv.StringTempOffset;
                return;
            }
        }
        Builtins.Call(sv, num);
    };
}
