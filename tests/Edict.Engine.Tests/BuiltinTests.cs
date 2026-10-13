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
    private static readonly delegate* unmanaged<int, int, float*, int, byte*, int, int, void*, int, int, uint, int*, int, int, double, uint> BuiltinFn =
        (delegate* unmanaged<int, int, float*, int, byte*, int, int, void*, int, int, uint, int*, int, int, double, uint>)NativeLibrary.GetExport(Lib, "EdictOracle_Builtin");
    private static readonly delegate* unmanaged<void*, int, int, int, int*, float*, void> HeaderFn =
        (delegate* unmanaged<void*, int, int, int, int*, float*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_EdictHeader");

    private const int FuzzEdicts = 32;

    private static readonly delegate* unmanaged<byte*, float> QAtofFn =
        (delegate* unmanaged<byte*, float>)NativeLibrary.GetExport(Lib, "EdictOracle_QAtof");

    /// <summary>Engine strings at offsets the test chooses (cvar names for PF_cvar).</summary>
    /// <summary>No QuakeC from engine code and no capture points: the builtins fuzzed here link nothing.</summary>
    private sealed class NoHooks : IServerHooks
    {
        public void ExecuteProgram(int fnum) => throw new InvalidOperationException("no QuakeC from engine code here");
        public void Link(int e, bool touchTriggers) { }
        public void Linked(int e) { }
        public void Unlink(int e) { }
    }

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
    /// <param name="only">The builtins to fuzz (default all); a known-bad variant needs only its own.</param>
    internal static string? FirstDivergence(Action<Server, int>? call = null, int random = 50_000, Func<int, bool>? only = null)
    {
        Func<int, bool> want = only ?? (_ => true);
        int N(int num, int count) => want(num) ? count : 0;
        call ??= Builtins.Call;
        using var f = new VmOpcodeFuzz(FuzzEdicts);
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
                                portTemp, tempOfs) { MaxEdicts = FuzzEdicts, Hooks = new NoHooks() };
            Model emptyWorld = default;                 // remove unlinks; nothing is linked in this fuzz
            sv.ClearWorld(&emptyWorld);
            int numEdicts = 2;              // the reference's sv.num_edicts; the port's is set alongside
            sv.NumEdicts = numEdicts;

            // the edict headers on both sides (reference layout through the oracle, port layout directly)
            void Header(int e, int free, uint freetime)
            {
                float t = BitConverter.UInt32BitsToSingle(freetime);
                HeaderFn(f.RefEdicts, Fields, e, 1, &free, &t);
                var h = (EdictHeader*)(f.PortEdicts + e * f.Stride);
                h->Free = free;
                h->FreeTime = BitConverter.UInt32BitsToSingle(freetime);
            }

            (int, uint) RefHeader(int e)
            {
                int free = 0;
                float t = 0;
                HeaderFn(f.RefEdicts, Fields, e, 0, &free, &t);
                return (free, BitConverter.SingleToUInt32Bits(t));
            }

            string? Run(int num, int argc, string what)
            {
                uint seed = rng.Bits();
                int n = numEdicts;
                uint refSeed = BuiltinFn(num, argc, f.RefGlobals, NumGlobalsOf(f), refStrings, numStrings, tempOfs,
                                         f.RefEdicts, Fields, 0, seed, &n, FuzzEdicts, sv.MaxClients, sv.Time);
                sv.Rand.Srand(seed);
                sv.NumEdicts = numEdicts;
                call(sv, num);
                string? d = sv.NumEdicts != n ? $"num_edicts: port {sv.NumEdicts}, reference {n}" : null;
                numEdicts = n;
                d ??= f.Compare(nanClass: true, RefHeader);  // computed results: NaN as a class (docs/adr/D8)
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
            uint[] edgeBits = Rng.Edges;
            foreach (int num in vectorBuiltins.Where(want))
            {
                for (int i = 0; i < edgeBits.Length; i++)
                    for (int j = 0; j < edgeBits.Length; j++)
                    {
                        f.Scramble(ref rng);
                        Vector(OfsParm0, edgeBits[i], edgeBits[j], edgeBits[(i + j) % edgeBits.Length]);
                        if (Run(num, 1, $"({Hex(edgeBits[i])}, {Hex(edgeBits[j])}, {Hex(edgeBits[(i + j) % edgeBits.Length])})") is { } d)
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
            foreach (int num in new[] { Builtins.Fabs, Builtins.Floor, Builtins.Ceil, Builtins.Rint, Builtins.Ftos }.Where(want))
            {
                IEnumerable<uint> inputs = edgeBits.Concat(Enumerable.Range(0, random).Select(_ => rng.Below(4) == 0
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
            for (int n = 0; n < N(Builtins.Vtos, random); n++)
            {
                f.Scramble(ref rng);
                uint x = Small(rng.Float()), y = Small(rng.Float()), z = Small(rng.Float());
                Vector(OfsParm0, x, y, z);
                if (Run(Builtins.Vtos, 1, $"({Hex(x)}, {Hex(y)}, {Hex(z)})") is { } d)
                    return d;
            }

            // random: any seed
            for (int n = 0; n < N(Builtins.Random, random); n++)
            {
                f.Scramble(ref rng);
                if (Run(Builtins.Random, 0, $"#{n}") is { } d)
                    return d;
            }

            // cvar: C's Cvar_VariableValue is Q_atof (var->string); the oracle has the reference's Q_atof
            for (int i = 0; i < N(Builtins.Cvar, CvarCount); i++)
            {
                f.Scramble(ref rng);
                f.Global(OfsParm0, (uint)(NameBase + i));
                if (i % 4 == 3)
                {   // the name in the string temp, which ftos rewrites: other names at the same address
                    byte[] name = cvarList[i].Item1;
                    name.CopyTo(new Span<byte>(portTemp, name.Length));
                    portTemp[name.Length] = 0;
                    f.Global(OfsParm0, (uint)tempOfs);
                }
                call(sv, Builtins.Cvar);
                byte[] value = cvarList[i].Item2;
                float reference;
                fixed (byte* v = value.Length == 0 ? new byte[1] : [.. value, 0])
                    reference = QAtofFn(v);
                uint port = ((uint*)f.PortGlobals)[QcVm.OfsReturn];
                if (port != BitConverter.SingleToUInt32Bits(reference))
                    return $"builtin #{Builtins.Cvar} \"{Encoding.ASCII.GetString(value)}\": port {Hex(port)}, reference Q_atof {Hex(BitConverter.SingleToUInt32Bits(reference))}";
            }

            // edicts: headers, num_edicts, maxclients and sv.time chosen so that ED_Alloc's tests sit on their edges
            uint FreeTime(double time) => rng.Below(10) switch
            {
                0 => 0,
                1 => BitConverter.SingleToUInt32Bits(1.99999988f),
                2 => BitConverter.SingleToUInt32Bits(2f),
                3 => BitConverter.SingleToUInt32Bits(2.00000024f),
                4 => BitConverter.SingleToUInt32Bits((float)(time - 0.5)),
                5 => BitConverter.SingleToUInt32Bits(MathF.BitDecrement((float)(time - 0.5))),
                6 => BitConverter.SingleToUInt32Bits(MathF.BitIncrement((float)(time - 0.5))),
                7 => rng.Float(),
                _ => BitConverter.SingleToUInt32Bits((float)(time * ((rng.Next() >> 11) * (1.0 / 9007199254740992.0)))),
            };

            void SetUpEdicts(double time)
            {
                f.Scramble(ref rng);
                sv.Time = time;
                sv.MaxClients = 1 + rng.Below(3);
                numEdicts = sv.MaxClients + 1 + rng.Below(FuzzEdicts - sv.MaxClients - 2);   // ED_Alloc can append
                for (int e = 0; e < FuzzEdicts; e++)
                    Header(e, e > 0 && rng.Below(3) == 0 ? 1 : 0, FreeTime(time));
            }

            double Time() => rng.Below(4) switch
            {
                0 => rng.Below(40) * 0.25,
                1 => rng.Below(4000) * (double)(float)0.013889 + 1,
                2 => (rng.Next() >> 11) * (1.0 / 9007199254740992.0) * 1e6,
                _ => (rng.Next() >> 11) * (1.0 / 9007199254740992.0) * 5,
            };

            for (int n = 0; n < N(Builtins.Spawn, random); n++)
            {
                SetUpEdicts(Time());
                if (Run(Builtins.Spawn, 0, $"spawn #{n}, time {sv.Time:R}") is { } d)
                    return d;
            }
            // remove (ED_Free); makestatic frees the same way but writes the signon buffer first, which the oracle has not
            foreach (int num in new[] { Builtins.Remove }.Where(want))
                for (int n = 0; n < random / 4; n++)
                {
                    SetUpEdicts(Time());
                    int e = rng.Below(numEdicts);
                    f.Global(OfsParm0, (uint)f.EdictOffset(e));
                    if (Run(num, 1, $"edict {e}, time {sv.Time:R}") is { } d)
                        return d;
                }
            for (int n = 0; n < N(Builtins.NextEnt, random / 4); n++)
            {
                SetUpEdicts(Time());
                int e = rng.Below(numEdicts);
                f.Global(OfsParm0, (uint)f.EdictOffset(e));
                if (Run(Builtins.NextEnt, 1, $"after edict {e}") is { } d)
                    return d;
            }

            // find: string fields with values from the strings block, empty and equal strings at other offsets
            int[] stringFields = [EntVars.Classname, EntVars.Model, EntVars.Netname, 3, Fields - 1];
            for (int n = 0; n < N(Builtins.Find, random); n++)
            {
                SetUpEdicts(Time());
                int field = stringFields[rng.Below(stringFields.Length)];
                for (int e = 0; e < FuzzEdicts; e++)
                    f.Field(e, field, (uint)f.StringOffset(rng.Below(f.StringCount)));
                int start = rng.Below(numEdicts);
                f.Global(OfsParm0, (uint)f.EdictOffset(start));
                f.Global(OfsParm0 + 3, (uint)field);
                f.Global(OfsParm0 + 6, (uint)f.StringOffset(rng.Below(f.StringCount)));
                if (Run(Builtins.Find, 3, $"from edict {start}, field {field}") is { } d)
                    return d;
            }

            // findradius: centres, solids, free edicts and radii from edges and game-like values
            for (int n = 0; n < N(Builtins.FindRadius, random); n++)
            {
                SetUpEdicts(Time());
                for (int e = 0; e < FuzzEdicts; e++)
                {
                    uint solid = rng.Below(4) switch { 0 => 0, 1 => 0x80000000u, 2 => BitConverter.SingleToUInt32Bits(Solid.BBox), _ => rng.Float() };
                    f.Field(e, EntVars.Solid, solid);
                    for (int k = 0; k < 3; k++)
                    {
                        f.Field(e, EntVars.Origin + k, rng.Float());
                        f.Field(e, EntVars.Mins + k, rng.Below(2) == 0 ? BitConverter.SingleToUInt32Bits(-16f) : rng.Float());
                        f.Field(e, EntVars.Maxs + k, rng.Below(2) == 0 ? BitConverter.SingleToUInt32Bits(16f) : rng.Float());
                    }
                }
                Vector(OfsParm0, rng.Float(), rng.Float(), rng.Float());
                uint rad = rng.Below(3) switch { 0 => BitConverter.SingleToUInt32Bits(rng.Below(1000)), 1 => Rng.Edges[rng.Below(Rng.Edges.Length)], _ => rng.Float() };
                f.Global(OfsParm0 + 3, rad);
                if (Run(Builtins.FindRadius, 2, $"radius {Hex(rad)}") is { } d)
                    return d;
            }

            // changeyaw: self's angles[1], ideal_yaw and yaw_speed
            for (int n = 0; n < N(Builtins.ChangeYaw, random * 2); n++)
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
        AllocNoRelax,           // ED_Alloc without "freetime < 2"
        AllocFloatTime,         // ED_Alloc's sv.time - freetime > 0.5 in float
        AllocFromOne,           // ED_Alloc scans from edict 1, not maxclients+1
        FreeKeepsOrigin,        // ED_Free leaves origin as it was
        FindTakesFree,          // find does not skip free edicts
        FindradiusFloatCentre,  // findradius's centre (mins + maxs)*0.5f in float
        FindradiusTakesNotSolid,// findradius includes SOLID_NOT
        NextentTakesFree,       // nextent returns free edicts too
    }

    /// <summary>The builtins a known-bad variant changes: the fuzz runs only those for it.</summary>
    internal static int[] Target(Mutation m) => m switch
    {
        Mutation.RandomBy32768 => [Builtins.Random],
        Mutation.VectoyawInFloat => [Builtins.VectoYaw],
        Mutation.RintFloatSum => [Builtins.Rint],
        Mutation.NormalizeInDouble => [Builtins.Normalize],
        Mutation.MakevectorsRuntimeSin => [Builtins.MakeVectors],
        Mutation.AnglemodInFloat or Mutation.ChangeyawStrictBound => [Builtins.ChangeYaw],
        Mutation.CvarWithAtof => [Builtins.Cvar],
        Mutation.PrecacheNoReturn => [Builtins.PrecacheSound, Builtins.PrecacheModel, Builtins.PrecacheSound2, Builtins.PrecacheModel2],
        Mutation.FtosAlwaysFixed => [Builtins.Ftos],
        Mutation.AllocNoRelax or Mutation.AllocFloatTime or Mutation.AllocFromOne => [Builtins.Spawn],
        Mutation.FreeKeepsOrigin => [Builtins.Remove],
        Mutation.FindTakesFree => [Builtins.Find],
        Mutation.FindradiusFloatCentre or Mutation.FindradiusTakesNotSolid => [Builtins.FindRadius],
        Mutation.NextentTakesFree => [Builtins.NextEnt],
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    // ED_Alloc with the mistakes above (the port's Server.Alloc otherwise)
    private static int MutantAlloc(Server sv, Mutation m)
    {
        int i;
        for (i = m == Mutation.AllocFromOne ? 1 : sv.MaxClients + 1; i < sv.NumEdicts; i++)
        {
            EdictHeader* e = sv.EdictNum(i);
            bool old = m == Mutation.AllocFloatTime ? (float)sv.Time - e->FreeTime > 0.5f : sv.Time - e->FreeTime > 0.5;
            if (e->Free != 0 && ((m != Mutation.AllocNoRelax && e->FreeTime < 2) || old))
                break;
        }
        if (i == sv.NumEdicts)
            sv.NumEdicts++;
        new Span<byte>(sv.Fields(i), sv.Progs.EntityFields * 4).Clear();
        sv.EdictNum(i)->Free = 0;
        return i;
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
            case Mutation.AllocNoRelax or Mutation.AllocFloatTime or Mutation.AllocFromOne when num == Builtins.Spawn:
                sv.Globals[QcVm.OfsReturn] = MutantAlloc(sv, m) * sv.EdictSize;
                return;
            case Mutation.FreeKeepsOrigin when num == Builtins.Remove:
            {
                int e = sv.Globals[OfsParm0] / sv.EdictSize;
                float* v = sv.Fields(e);
                float x = v[EntVars.Origin], y = v[EntVars.Origin + 1], z = v[EntVars.Origin + 2];
                sv.Free(e);
                v[EntVars.Origin] = x;
                v[EntVars.Origin + 1] = y;
                v[EntVars.Origin + 2] = z;
                return;
            }
            case Mutation.FindTakesFree when num == Builtins.Find:
            {
                int e = sv.NumForEdict(sv.Globals[OfsParm0]), f = sv.Globals[OfsParm0 + 3];
                byte* s = sv.String(sv.Globals[OfsParm0 + 6]);
                for (e++; e < sv.NumEdicts; e++)
                    if (CString.Strcmp(sv.String(((int*)sv.Fields(e))[f]), s) == 0)
                    {
                        sv.Globals[QcVm.OfsReturn] = e * sv.EdictSize;
                        return;
                    }
                sv.Globals[QcVm.OfsReturn] = 0;
                return;
            }
            case Mutation.FindradiusFloatCentre or Mutation.FindradiusTakesNotSolid when num == Builtins.FindRadius:
            {
                int chain = 0;
                Vec3 org = *(Vec3*)G(sv, OfsParm0);
                float rad = *G(sv, OfsParm0 + 3);
                for (int i = 1; i < sv.NumEdicts; i++)
                {
                    if (sv.EdictNum(i)->Free != 0)
                        continue;
                    float* v = sv.Fields(i);
                    if (m != Mutation.FindradiusTakesNotSolid && v[EntVars.Solid] == Solid.Not)
                        continue;
                    Vec3 d;
                    if (m == Mutation.FindradiusFloatCentre)
                    {
                        d.X = org.X - (v[EntVars.Origin] + (v[EntVars.Mins] + v[EntVars.Maxs]) * 0.5f);
                        d.Y = org.Y - (v[EntVars.Origin + 1] + (v[EntVars.Mins + 1] + v[EntVars.Maxs + 1]) * 0.5f);
                        d.Z = org.Z - (v[EntVars.Origin + 2] + (v[EntVars.Mins + 2] + v[EntVars.Maxs + 2]) * 0.5f);
                    }
                    else
                    {
                        d.X = (float)(org.X - (v[EntVars.Origin] + (v[EntVars.Mins] + v[EntVars.Maxs]) * 0.5));
                        d.Y = (float)(org.Y - (v[EntVars.Origin + 1] + (v[EntVars.Mins + 1] + v[EntVars.Maxs + 1]) * 0.5));
                        d.Z = (float)(org.Z - (v[EntVars.Origin + 2] + (v[EntVars.Mins + 2] + v[EntVars.Maxs + 2]) * 0.5));
                    }
                    if (Mathlib.Length(d) > rad)
                        continue;
                    ((int*)v)[EntVars.Chain] = chain * sv.EdictSize;
                    chain = i;
                }
                sv.Globals[QcVm.OfsReturn] = chain * sv.EdictSize;
                return;
            }
            case Mutation.NextentTakesFree when num == Builtins.NextEnt:
            {
                int i = sv.NumForEdict(sv.Globals[OfsParm0]) + 1;
                sv.Globals[QcVm.OfsReturn] = i == sv.NumEdicts ? 0 : i * sv.EdictSize;
                return;
            }
        }
        Builtins.Call(sv, num);
    };
}
