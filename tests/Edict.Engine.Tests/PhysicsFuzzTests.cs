// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 for sv_phys.c: SV_Physics itself in the oracle (EdictOracle_Physics) and the port's, on a map loaded on
/// both sides, the game's progs.dat, and the same random entities: every movetype (the client's walk, fly, noclip and
/// toss too), speeds beyond sv_maxvelocity, gravity fields, flying and swimming monsters, pushers with riders,
/// players stuck in the world, think and frame times on the frame boundary. Every function SV_Physics calls points at
/// SUB_Null, so QuakeC changes nothing but its return value. After every frame globals, fields, free flags, sv.time
/// and num_edicts are compared. The captures cover what the game does; this covers what it rarely does.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class PhysicsFuzzTests
{
    private static readonly nint Lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("EDICT_ORACLE")
        ?? throw new InvalidOperationException("EDICT_ORACLE is not set (docs/TESTING.md)"));
    private static readonly delegate* unmanaged<byte*, int, int> ProgsFn =
        (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_Progs");
    private static readonly delegate* unmanaged<byte*, byte*, int> LoadWorldFn =
        (delegate* unmanaged<byte*, byte*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_LoadWorld");
    private static readonly delegate* unmanaged<void*, int, int, int, int, void> WorldFn =
        (delegate* unmanaged<void*, int, int, int, int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_World");
    private static readonly delegate* unmanaged<int, short*, int> LinkFn =
        (delegate* unmanaged<int, short*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_LinkEdict");
    private static readonly delegate* unmanaged<int, void> UnlinkFn =
        (delegate* unmanaged<int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_UnlinkEdict");
    private static readonly delegate* unmanaged<int, int*, float, float, float, void> SetupFn =
        (delegate* unmanaged<int, int*, float, float, float, void>)NativeLibrary.GetExport(Lib, "EdictOracle_PhysicsSetup");
    private static readonly delegate* unmanaged<float*, void*, int*, int, double*, double, void> PhysicsFn =
        (delegate* unmanaged<float*, void*, int*, int, double*, double, void>)NativeLibrary.GetExport(Lib, "EdictOracle_Physics");
    private static readonly delegate* unmanaged<void*, int, int, int, int*, float*, void> HeaderFn =
        (delegate* unmanaged<void*, int, int, int, int*, float*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_EdictHeader");

    private const int Edicts = 24;
    private const int SolidNot = 0, SolidTrigger = 1, SolidBbox = 2, SolidSlidebox = 3, SolidBsp = 4;

    public static IEnumerable<TheoryDataRow<string>> MapNames =>
        VmReplay.Maps().Where(m => m is "start" or "lq_e0m1" or "lq_e0m3" or "lq_e0m6").Select(m => new TheoryDataRow<string>(m));

    private sealed class NoStrings : IEngineStrings
    {
        public byte* Get(int ofs) => throw new InvalidOperationException("no engine strings here");
    }

    /// <summary>What the fuzz reached (so that a green run can be judged).</summary>
    internal sealed class Stats
    {
        public int Frames, Thinks, Blocked, Stuck, Bounded, InWater, OnGround;

        public override string ToString() =>
            $"{Frames} frames, {Thinks} thinks run, {Blocked} pushers blocked, {Stuck} stuck players, {Bounded} velocities " +
            $"bounded, {InWater} in water, {OnGround} landed";
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Physics_matches_the_reference(string map)
    {
        var stats = new Stats();
        string? d = FirstDivergence(map, stats);
        Assert.True(d is null, d);
        TestContext.Current.TestOutputHelper?.WriteLine($"{map}: {stats}");
    }

    internal static string? FirstDivergence(string map, Stats stats, int rounds = 150, int frames = 10)
    {
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        QFile progsFile = files.Find("progs.dat") ?? throw new FileNotFoundException("progs.dat");
        using var progs = new Progs(progsFile);
        fixed (byte* image = progsFile.Span)
            if (ProgsFn(image, progsFile.Length) != 0)
                return "EdictOracle_Progs failed";

        byte[] wn = Encoding.Latin1.GetBytes($"maps/{map}.bsp");
        QFile bspFile = files.Find(wn) ?? throw new FileNotFoundException(map);
        using BrushModelSet world = BrushLoader.Load(bspFile, wn);
        byte* bsp = (byte*)NativeMemory.Alloc((nuint)bspFile.Length);
        try
        {
            bspFile.Span.CopyTo(new Span<byte>(bsp, bspFile.Length));
            fixed (byte* n = (byte[])[.. wn, 0])
                if (LoadWorldFn(bsp, n) < 0)
                    return "EdictOracle_LoadWorld failed";
        }
        finally
        {
            NativeMemory.Free(bsp);
        }

        int header = VmOpcodeFuzz.HeaderBytes, fields = progs.EntityFields, stride = header + 4 * fields, ng = progs.NumGlobals;
        float* refGlobals = (float*)NativeMemory.AllocZeroed((nuint)ng, 4);
        float* portGlobals = (float*)NativeMemory.AllocZeroed((nuint)ng, 4);
        byte* refEdicts = (byte*)NativeMemory.AllocZeroed(Edicts, (nuint)stride);
        byte* portEdicts = (byte*)NativeMemory.AllocZeroed(Edicts, (nuint)stride);
        byte* temp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
        byte* empty = (byte*)NativeMemory.AllocZeroed(1);
        try
        {
            int subNull = -1;
            for (int i = 1; i < progs.NumFunctions; i++)
                if (progs.String(progs.Functions[i].SName).SequenceEqual("SUB_Null"u8))
                    subNull = i;
            if (subNull < 0)
                return "progs.dat has no SUB_Null";
            int gravityField = -1;
            for (int i = 0; i < progs.Header->NumFieldDefs; i++)
                if (progs.String(progs.FieldDefs[i].SName).SequenceEqual("gravity"u8))
                    gravityField = progs.FieldDefs[i].Ofs;

            var vm = new QcVm(progs, (int*)portGlobals, portEdicts, header, VmReplay.NumBuiltins);
            var cvars = new List<(byte[], byte[])>
            {
                ("sv_gravity"u8.ToArray(), "800"u8.ToArray()), ("sv_maxvelocity"u8.ToArray(), "2000"u8.ToArray()),
                ("sv_nostep"u8.ToArray(), "0"u8.ToArray()),
            };
            var sv = new Server(vm, progs, (int*)portGlobals, portEdicts, header, new Cvars(cvars), new NoStrings(), temp,
                                progs.NumStrings) { MaxEdicts = Edicts, MaxClients = 1 };
            var names = new nint[world.Count];
            var models = new nint[world.Count];
            for (int i = 0; i < world.Count; i++)
            {
                names[i] = (nint)empty;
                models[i] = (nint)(world.Models + i);
            }
            sv.InitPrecache(empty, names, models);
            sv.ClearWorld(world.World);
            sv.Clients = [new Client { Active = true }];
            sv.Hooks = new GameHooks(sv);
            sv.SvPlayer = 1;
            int active = 1;
            SetupFn(1, &active, 800, 2000, 0);

            var rng = new VmOpcodeFuzz.Rng((ulong)map.GetHashCode() & 0xFFFF);
            short* leafs = stackalloc short[16];
            Model* w = world.World;

            void F(int e, int f, float v)
            {
                *(float*)(refEdicts + e * stride + header + 4 * f) = v;
                *(float*)(portEdicts + e * stride + header + 4 * f) = v;
            }
            void I(int e, int f, int v)
            {
                *(int*)(refEdicts + e * stride + header + 4 * f) = v;
                *(int*)(portEdicts + e * stride + header + 4 * f) = v;
            }
            void V(int e, int f, in Vec3 v)
            {
                F(e, f, v.X);
                F(e, f + 1, v.Y);
                F(e, f + 2, v.Z);
            }
            void G(int ofs, int bits)
            {
                ((int*)refGlobals)[ofs] = bits;
                ((int*)portGlobals)[ofs] = bits;
            }
            float U(float lo, float hi) => lo + (hi - lo) * (float)((rng.Next() >> 40) * (1.0 / (1 << 24)));
            Vec3 Open()
            {
                for (int k = 0; k < 16; k++)
                {
                    MLeaf* l = w->Leafs + 1 + rng.Below(w->NumLeafs);
                    var p = new Vec3(U(l->MinMaxs[0], l->MinMaxs[3]), U(l->MinMaxs[1], l->MinMaxs[4]), U(l->MinMaxs[2], l->MinMaxs[5]));
                    if (sv.PointContents(p) != Bsp.ContentsSolid)
                        return p;
                }
                return new Vec3(U(w->Mins.X, w->Maxs.X), U(w->Mins.Y, w->Maxs.Y), U(w->Mins.Z, w->Maxs.Z));
            }

            for (int round = 0; round < rounds; round++)
            {
                // fresh globals (the progs' own, with SUB_Null for the frame functions) and entities
                new ReadOnlySpan<int>(progs.InitialGlobals, ng).CopyTo(new Span<int>(refGlobals, ng));
                new ReadOnlySpan<int>(progs.InitialGlobals, ng).CopyTo(new Span<int>(portGlobals, ng));
                G(GlobalVars.StartFrame, subNull);
                G(GlobalVars.PlayerPreThink, subNull);
                G(GlobalVars.PlayerPostThink, subNull);
                *(float*)&((int*)refGlobals)[GlobalVars.ForceRetouch] = *(float*)&((int*)portGlobals)[GlobalVars.ForceRetouch] = rng.Below(4) == 0 ? 2 : 0;

                WorldFn(refEdicts, fields, Edicts, Edicts, 1);
                for (int e = 1; e < Edicts; e++)
                {
                    UnlinkFn(e);
                    sv.UnlinkEdict(e);
                }
                new Span<byte>(refEdicts + header, fields * 4).Clear();
                new Span<byte>(portEdicts + header, fields * 4).Clear();
                F(0, EntVars.Solid, SolidBsp);
                F(0, EntVars.Movetype, Server.MovetypePush);
                F(0, EntVars.Modelindex, 1);

                // a frame time that is exact in binary, or the game's, and a time where think times can meet it
                double frametime = rng.Below(3) == 0 ? 1.0 / 64 : rng.Below(2) == 0 ? (float)(1.0 / 72) : 0.1;
                double time = rng.Below(2) == 0 ? 2 + rng.Below(64) / 16.0 : 1 + rng.Below(100000) / 72.0;

                for (int e = 1; e < Edicts; e++)
                {
                    for (int fl = 0; fl < fields; fl++)
                        I(e, fl, 0);
                    int free = rng.Below(16) == 0 ? 1 : 0;
                    float ft = 0;
                    HeaderFn(refEdicts, fields, e, 1, &free, &ft);
                    *sv.EdictNum(e) = default;
                    sv.EdictNum(e)->Free = free;

                    bool client = e == 1;
                    int movetype = client
                        ? rng.Below(6) switch { 0 => Server.MovetypeFly, 1 => Server.MovetypeNoclip, 2 => Server.MovetypeToss, 3 => Server.MovetypeNone, _ => Server.MovetypeWalk }
                        : rng.Below(9) switch
                        {
                            0 => Server.MovetypeNone, 1 or 2 => Server.MovetypeStep, 3 => Server.MovetypeToss, 4 => Server.MovetypeBounce,
                            5 => Server.MovetypeFly, 6 => Server.MovetypeFlyMissile, 7 => Server.MovetypeNoclip, _ => Server.MovetypePush,
                        };
                    if (movetype == Server.MovetypePush && world.Count < 2)
                        movetype = Server.MovetypeToss;
                    F(e, EntVars.Movetype, movetype);
                    int solid = movetype == Server.MovetypePush ? SolidBsp : client ? SolidSlidebox : rng.Below(5) switch
                    { 0 => SolidNot, 1 => SolidTrigger, 2 => SolidBbox, _ => SolidSlidebox };
                    F(e, EntVars.Solid, solid);
                    Vec3 mins, maxs;
                    if (solid == SolidBsp)
                    {
                        int mi = 2 + rng.Below(world.Count - 1);
                        F(e, EntVars.Modelindex, mi);
                        mins = sv.ModelAt(mi)->Mins;
                        maxs = sv.ModelAt(mi)->Maxs;
                    }
                    else
                    {
                        (mins, maxs) = rng.Below(3) switch
                        {
                            0 => (new Vec3(-16, -16, -24), new Vec3(16, 16, 32)),
                            1 => (new Vec3(-32, -32, -24), new Vec3(32, 32, 64)),
                            _ => (default, default),
                        };
                        F(e, EntVars.Modelindex, rng.Below(2));
                    }
                    Vec3 o = solid == SolidBsp ? default : Open();
                    if (client && rng.Below(4) == 0)
                        o = new Vec3(U(w->Mins.X, w->Maxs.X), U(w->Mins.Y, w->Maxs.Y), U(w->Mins.Z, w->Maxs.Z));   // maybe stuck
                    V(e, EntVars.Origin, o);
                    V(e, EntVars.Oldorigin, Vec3.Add(o, new Vec3(U(-8, 8), U(-8, 8), U(-20, 4))));
                    V(e, EntVars.Mins, mins);
                    V(e, EntVars.Maxs, maxs);
                    V(e, EntVars.Size, Vec3.Subtract(maxs, mins));
                    float speed = rng.Below(5) == 0 ? 3000 : rng.Below(2) == 0 ? 300 : 40;
                    V(e, EntVars.Velocity, new Vec3(U(-speed, speed), U(-speed, speed), U(-speed, speed)));
                    if (rng.Below(3) == 0)
                        V(e, EntVars.Avelocity, new Vec3(U(-200, 200), U(-200, 200), U(-200, 200)));
                    V(e, EntVars.Angles, new Vec3(U(-90, 90), U(0, 360), 0));
                    V(e, EntVars.VAngle, new Vec3(U(-60, 60), U(0, 360), 0));
                    V(e, EntVars.ViewOfs, new Vec3(0, 0, client ? 22 : U(-8, 24)));
                    int flags = rng.Below(3) == 0 ? Server.FlOnGround : 0;
                    if (rng.Below(6) == 0)
                        flags |= rng.Below(2) == 0 ? Server.FlFly : Server.FlSwim;
                    if (client && rng.Below(6) == 0)
                        flags |= Server.FlWaterJump;
                    F(e, EntVars.Flags, flags);
                    F(e, EntVars.Waterlevel, rng.Below(4));
                    F(e, EntVars.Watertype, rng.Below(3) == 0 ? 0 : rng.Below(2) == 0 ? -1 : -3);
                    F(e, EntVars.TeleportTime, (float)(time + rng.Below(3) - 1));
                    F(e, EntVars.Health, 100);
                    if (gravityField >= 0 && rng.Below(3) == 0)
                        F(e, gravityField, rng.Below(2) == 0 ? 0.5f : 2);
                    // think: none, due in this frame (on its boundary at times), later; always the empty function
                    float next = rng.Below(5) switch
                    {
                        0 => 0,
                        1 => (float)(time + frametime),
                        2 => (float)(time + frametime * U(0, 1)),
                        3 => (float)(time - 1),
                        _ => (float)(time + 5),
                    };
                    F(e, EntVars.Nextthink, next);
                    F(e, EntVars.Ltime, (float)(rng.Below(2) == 0 ? time : time - frametime * U(0, 2)));
                    I(e, EntVars.Think, subNull);
                    I(e, EntVars.Touch, rng.Below(2) == 0 ? subNull : 0);
                    I(e, EntVars.Blocked, rng.Below(2) == 0 ? subNull : 0);
                    I(e, EntVars.Groundentity, rng.Below(3) == 0 ? 0 : (1 + rng.Below(Edicts - 1)) * stride);
                    I(e, EntVars.Owner, rng.Below(4) == 0 ? (1 + rng.Below(Edicts - 1)) * stride : 0);
                }

                WorldFn(refEdicts, fields, Edicts, Edicts, 1);
                for (int e = 1; e < Edicts; e++)
                {
                    LinkFn(e, leafs);
                    sv.LinkEdict(e, false);
                }
                sv.NumEdicts = Edicts;
                if (Compare(sv, refGlobals, portGlobals, refEdicts, portEdicts, header, fields, stride, ng) is { } linked)
                    return $"round {round}, after linking: {linked}";

                for (int frame = 0; frame < frames; frame++)
                {
                    int n = Edicts;
                    double t = time;
                    var before = new float[Edicts * 4];
                    for (int e = 0; e < Edicts; e++)
                    {
                        float* pv = sv.Fields(e);
                        before[4 * e] = pv[EntVars.Nextthink];
                        before[4 * e + 1] = Math.Abs(pv[EntVars.Velocity]);
                        before[4 * e + 2] = pv[EntVars.Flags];
                        before[4 * e + 3] = pv[EntVars.Ltime];
                    }
                    PhysicsFn(refGlobals, refEdicts, &n, Edicts, &t, frametime);
                    sv.Time = time;
                    sv.FrameTime = frametime;
                    sv.Physics();
                    stats.Frames++;
                    for (int e = 1; e < Edicts; e++)
                    {
                        float* pv = sv.Fields(e);
                        if (before[4 * e] != 0 && pv[EntVars.Nextthink] == 0)
                            stats.Thinks++;
                        if (before[4 * e + 1] > 2000 && Math.Abs(pv[EntVars.Velocity]) <= 2000)
                            stats.Bounded++;
                        if (pv[EntVars.Waterlevel] > 1)
                            stats.InWater++;
                        if (((int)before[4 * e + 2] & Server.FlOnGround) == 0 && ((int)pv[EntVars.Flags] & Server.FlOnGround) != 0)
                            stats.OnGround++;
                        if (pv[EntVars.Movetype] == Server.MovetypePush && pv[EntVars.Ltime] == before[4 * e + 3])
                            stats.Blocked++;
                    }
                    if (sv.TestEntityPosition(1))
                        stats.Stuck++;
                    if (n != sv.NumEdicts)
                        return $"round {round}, frame {frame}: num_edicts port {sv.NumEdicts}, reference {n}";
                    if (BitConverter.DoubleToInt64Bits(t) != BitConverter.DoubleToInt64Bits(sv.Time))
                        return $"round {round}, frame {frame}: sv.time port {sv.Time:R}, reference {t:R}";
                    if (Compare(sv, refGlobals, portGlobals, refEdicts, portEdicts, header, fields, stride, ng) is { } d)
                        return $"round {round}, frame {frame} (frametime {frametime:R}, time {time:R}): {d}";
                    time = t;
                }
            }
            return null;
        }
        finally
        {
            NativeMemory.Free(refGlobals);
            NativeMemory.Free(portGlobals);
            NativeMemory.Free(refEdicts);
            NativeMemory.Free(portEdicts);
            NativeMemory.Free(temp);
            NativeMemory.Free(empty);
        }
    }

    private static string? Compare(Server sv, float* rg, float* pg, byte* re, byte* pe, int header, int fields, int stride, int ng)
    {
        for (int i = 0; i < ng; i++)
        {
            uint a = ((uint*)rg)[i], b = ((uint*)pg)[i];
            if (!VmOpcodeFuzz.Same(a, b, nanClass: true))
                return $"global {i}: port {VmOpcodeFuzz.Hex(b)}, reference {VmOpcodeFuzz.Hex(a)}";
        }
        for (int e = 0; e < Edicts; e++)
        {
            int free = 0;
            float ft = 0;
            HeaderFn(re, fields, e, 0, &free, &ft);
            EdictHeader* h = sv.EdictNum(e);
            if (h->Free != free || BitConverter.SingleToUInt32Bits(h->FreeTime) != BitConverter.SingleToUInt32Bits(ft))
                return $"edict {e} header: port free={h->Free} freetime={h->FreeTime:R}, reference free={free} freetime={ft:R}";
            for (int f = 0; f < fields; f++)
            {
                uint a = *(uint*)(re + e * stride + header + 4 * f), b = *(uint*)(pe + e * stride + header + 4 * f);
                if (!VmOpcodeFuzz.Same(a, b, nanClass: true))
                    return $"edict {e} (movetype {*(float*)(pe + e * stride + header + 4 * EntVars.Movetype)}) field {f}: port {VmOpcodeFuzz.Hex(b)}, reference {VmOpcodeFuzz.Hex(a)}";
            }
        }
        return null;
    }
}
