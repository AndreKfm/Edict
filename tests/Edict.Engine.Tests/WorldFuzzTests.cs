// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using Edict.Exact;
using Edict.Harness;
using static Edict.Engine.Tests.VmOpcodeFuzz;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 on a world: the oracle loads the map with Mod_LoadBrushModel and SV_ClearWorld (EdictOracle_LoadWorld),
/// the port with BrushLoader and ClearWorld, and both get the same edicts. Then, per map: the PVS of every leaf
/// (Mod_LeafPVS), Mod_PointInLeaf on random points and points on node planes, and rounds of random entities, linked
/// with SV_LinkEdict on both sides, against which SV_Move, PF_checkclient (with its state at the edges of the 0.1 s
/// interval), PF_aim and SV_NewChaseDir run. The capture replay covers the same code on what the game does; this
/// covers what it rarely does (notarget clients, teamplay, missiles against monsters, owners, ties).
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class WorldFuzzTests
{
    private static readonly nint Lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("EDICT_ORACLE")
        ?? throw new InvalidOperationException("EDICT_ORACLE is not set (docs/TESTING.md)"));

    private static readonly delegate* unmanaged<byte*, byte*, int> LoadWorldFn =
        (delegate* unmanaged<byte*, byte*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_LoadWorld");
    private static readonly delegate* unmanaged<void*, int, int, int, int, void> WorldFn =
        (delegate* unmanaged<void*, int, int, int, int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_World");
    private static readonly delegate* unmanaged<int, short*, int> LinkFn =
        (delegate* unmanaged<int, short*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_LinkEdict");
    private static readonly delegate* unmanaged<int, void> UnlinkFn =
        (delegate* unmanaged<int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_UnlinkEdict");
    private static readonly delegate* unmanaged<float*, float*, float*, float*, int, int, float*, int> MoveFn =
        (delegate* unmanaged<float*, float*, float*, float*, int, int, float*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_Move");
    private static readonly delegate* unmanaged<float*, int> PointInLeafFn =
        (delegate* unmanaged<float*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_PointInLeaf");
    private static readonly delegate* unmanaged<int, byte*, int> LeafPvsFn =
        (delegate* unmanaged<int, byte*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_LeafPvs");
    private static readonly delegate* unmanaged<int, int*, double*, byte*, void> CheckStateFn =
        (delegate* unmanaged<int, int*, double*, byte*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_CheckState");
    private static readonly delegate* unmanaged<float*, int, int, float, uint, uint> NewChaseDirFn =
        (delegate* unmanaged<float*, int, int, float, uint, uint>)NativeLibrary.GetExport(Lib, "EdictOracle_NewChaseDir");
    private static readonly delegate* unmanaged<float, float, void> AimCvarsFn =
        (delegate* unmanaged<float, float, void>)NativeLibrary.GetExport(Lib, "EdictOracle_AimCvars");
    private static readonly delegate* unmanaged<byte*, float> QAtofFn =
        (delegate* unmanaged<byte*, float>)NativeLibrary.GetExport(Lib, "EdictOracle_QAtof");
    private static readonly delegate* unmanaged<int, int, float*, int, byte*, int, int, void*, int, int, uint, int*, int, int, double, uint> BuiltinFn =
        (delegate* unmanaged<int, int, float*, int, byte*, int, int, void*, int, int, uint, int*, int, int, double, uint>)NativeLibrary.GetExport(Lib, "EdictOracle_Builtin");
    private static readonly delegate* unmanaged<void*, int, int, int, int*, float*, void> HeaderFn =
        (delegate* unmanaged<void*, int, int, int, int*, float*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_EdictHeader");

    private const int FuzzEdicts = 24;
    private const int SolidNot = 0, SolidTrigger = 1, SolidBbox = 2, SolidSlidebox = 3, SolidBsp = 4;
    private const int MovetypePush = 7;
    private const int CheckPvsBytes = Server.MaxMapLeafs / 8;

    public static IEnumerable<TheoryDataRow<string>> MapNames => VmReplay.Maps().Select(m => new TheoryDataRow<string>(m));

    private sealed class NoHooks : IServerHooks
    {
        public void ExecuteProgram(int fnum) => throw new InvalidOperationException("no QuakeC from engine code here");
        public void Link(int e, bool touchTriggers) { }
        public void Linked(int e) { }
        public void Unlink(int e) { }
    }

    private sealed class NoStrings : IEngineStrings
    {
        public byte* Get(int ofs) => throw new InvalidOperationException("no engine strings here");
    }

    // box sizes as the game uses them, and some others (points, asymmetric, thin)
    private static readonly (Vec3 Mins, Vec3 Maxs)[] Boxes =
    [
        (default, default),
        (new(-16, -16, -24), new(16, 16, 32)),
        (new(-32, -32, -24), new(32, 32, 64)),
        (new(-16, -16, 0), new(16, 16, 56)),
        (new(-8, -8, -8), new(8, 8, 8)),
        (new(-3, -1, 0), new(1, 3, 2)),
        (new(-1, -1, -1), new(1, 1, 1)),
    ];

    [Theory]
    [MemberData(nameof(MapNames))]
    public void World_matches_the_reference(string map)
    {
        var stats = new Stats();
        string? d = FirstDivergence(map, stats: stats);
        Assert.True(d is null, d);
        TestContext.Current.TestOutputHelper?.WriteLine($"{map}: {stats}");
    }

    /// <summary>How often the fuzz reached the interesting outcomes (so that a green run means something).</summary>
    internal sealed class Stats
    {
        public int Moves, MovesHitEntity, MovesStartSolid, CheckClients, CheckClientsFound, NewChecks, Aims, AimsTarget,
            Chases, ChasesMoved;

        public override string ToString() =>
            $"{Moves} moves ({MovesHitEntity} hit an entity, {MovesStartSolid} start solid), {CheckClients} checkclient " +
            $"({CheckClientsFound} found a client, {NewChecks} picked a new one), {Aims} aim ({AimsTarget} turned to a " +
            $"target), {Chases} newchasedir ({ChasesMoved} moved)";
    }

    /// <summary>The first divergence on <paramref name="map"/>, or null. <paramref name="call"/> runs a builtin on the
    /// port's side (default <see cref="Builtins.Call"/>).</summary>
    internal static string? FirstDivergence(string map, Action<Server, int>? call = null, int rounds = 40, Stats? stats = null)
    {
        stats ??= new Stats();
        call ??= Builtins.Call;
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        byte[] wn = Encoding.Latin1.GetBytes($"maps/{map}.bsp");
        QFile file = files.Find(wn) ?? throw new FileNotFoundException(map);
        using BrushModelSet world = BrushLoader.Load(file, wn);
        byte[] name = [.. wn, 0];
        int refLeafs;
        byte* bsp = (byte*)NativeMemory.Alloc((nuint)file.Length);
        try
        {
            file.Span.CopyTo(new Span<byte>(bsp, file.Length));
            fixed (byte* n = name)
                refLeafs = LoadWorldFn(bsp, n);
        }
        finally
        {
            NativeMemory.Free(bsp);
        }
        Model* w = world.World;
        if (refLeafs != w->NumLeafs)
            return $"numleafs: port {w->NumLeafs}, reference {refLeafs}";

        using var f = new VmOpcodeFuzz(FuzzEdicts);
        var vm = new QcVm(f.Progs, (int*)f.PortGlobals, f.PortEdicts, f.Header, VmReplay.NumBuiltins);
        byte* portTemp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
        byte* empty = (byte*)NativeMemory.AllocZeroed(1);
        var names = new nint[world.Count];
        var models = new nint[world.Count];
        for (int i = 0; i < world.Count; i++)
        {
            names[i] = (nint)empty;         // the names are not read here
            models[i] = (nint)(world.Models + i);
        }
        var cvars = new List<(byte[], byte[])> { ("teamplay"u8.ToArray(), "0"u8.ToArray()), ("sv_aim"u8.ToArray(), "0.93"u8.ToArray()) };
        var sv = new Server(vm, f.Progs, (int*)f.PortGlobals, f.PortEdicts, f.Header, new Cvars(cvars), new NoStrings(),
                            portTemp, 0) { MaxEdicts = FuzzEdicts, NumEdicts = FuzzEdicts, Hooks = new NoHooks() };
        sv.InitPrecache(empty, names, models);
        sv.ClearWorld(w);
        try
        {
            return Pvs(sv, w) ?? Points(sv, w, map) ?? Rounds(f, sv, w, world.Count, call, rounds, stats);
        }
        finally
        {
            NativeMemory.Free(portTemp);
            NativeMemory.Free(empty);
        }
    }

    // Mod_LeafPVS for every world leaf, leaf 0 (mod_novis) included
    private static string? Pvs(Server sv, Model* w)
    {
        byte* r = stackalloc byte[CheckPvsBytes];
        for (int leaf = 0; leaf <= w->NumLeafs; leaf++)
        {
            int row = LeafPvsFn(leaf, r);
            sv.LeafPvs(leaf);
            for (int b = 0; b < row; b++)
                if (sv.Decompressed[b] != r[b])
                    return $"Mod_LeafPVS leaf {leaf} byte {b}: port 0x{sv.Decompressed[b]:X2}, reference 0x{r[b]:X2}";
        }
        return null;
    }

    private static float U(ref Rng rng, float lo, float hi) => lo + (hi - lo) * (float)((rng.Next() >> 40) * (1.0 / (1 << 24)));

    // a point in the world's bounds; often a leaf's centre or corner (on node planes), sometimes snapped to 1/8
    private static Vec3 Point(ref Rng rng, Model* w)
    {
        switch (rng.Below(4))
        {
            case 0:
                return new Vec3(U(ref rng, w->Mins.X, w->Maxs.X), U(ref rng, w->Mins.Y, w->Maxs.Y), U(ref rng, w->Mins.Z, w->Maxs.Z));
            case 1:
            {
                MLeaf* l = w->Leafs + 1 + rng.Below(w->NumLeafs);
                return new Vec3(l->MinMaxs[rng.Below(2) * 3], l->MinMaxs[1 + rng.Below(2) * 3], l->MinMaxs[2 + rng.Below(2) * 3]);
            }
            default:
            {
                MLeaf* l = w->Leafs + 1 + rng.Below(w->NumLeafs);
                var p = new Vec3(U(ref rng, l->MinMaxs[0], l->MinMaxs[3]), U(ref rng, l->MinMaxs[1], l->MinMaxs[4]),
                                 U(ref rng, l->MinMaxs[2], l->MinMaxs[5]));
                if (rng.Below(2) == 0)
                    p = new Vec3(MathF.Round(p.X * 8) / 8, MathF.Round(p.Y * 8) / 8, MathF.Round(p.Z * 8) / 8);
                return p;
            }
        }
    }

    // Mod_PointInLeaf: random points, and points exactly on node planes (d == 0 takes the back side). On the same
    // points SV_PointContents through its cache against the walk without it (which the trace corpus gates against the
    // reference's hull 0 contents): each point, an earlier one again, and an earlier one's x and y with this z.
    private static string? Points(Server sv, Model* w, string map)
    {
        var rng = new Rng(VmOpcodeFuzz.MapSeed(map));
        var earlier = new Vec3[64];
        for (int n = 0; n < 40_000; n++)
        {
            Vec3 p = Point(ref rng, w);
            if (n % 2 == 1)
            {
                MPlane* plane = w->Planes + w->Nodes[rng.Below(w->NumNodes)].PlaneNum;
                if (plane->Type < 3)
                    Component(ref p, plane->Type) = plane->Dist;
                else
                    p = new Vec3(plane->Normal.X * plane->Dist, plane->Normal.Y * plane->Dist, plane->Normal.Z * plane->Dist);
            }
            int r = PointInLeafFn((float*)&p);
            int q = sv.PointInLeaf(p);
            if (q != r)
                return $"Mod_PointInLeaf ({Hex(p.X)}, {Hex(p.Y)}, {Hex(p.Z)}): port leaf {q}, reference leaf {r}";

            Vec3 again = earlier[rng.Below(earlier.Length)];
            Vec3 column = earlier[rng.Below(earlier.Length)];
            column.Z = p.Z;
            earlier[n % earlier.Length] = p;
            foreach (Vec3 c in (ReadOnlySpan<Vec3>)[p, again, column, p])
            {
                int cached = sv.PointContents(c), walked = sv.WorldContents(c);
                if (cached != walked)
                    return $"SV_PointContents ({Hex(c.X)}, {Hex(c.Y)}, {Hex(c.Z)}): {cached} through the cache, {walked} without it";
            }
        }
        return null;
    }

    private static ref float Component(ref Vec3 v, int i) => ref System.Runtime.CompilerServices.Unsafe.Add(ref v.X, i);

    // a point in open space (the port's SV_PointContents, gated by the trace corpus), else any point
    private static Vec3 OpenPoint(ref Rng rng, Server sv, Model* w)
    {
        for (int i = 0; i < 16; i++)
        {
            Vec3 p = Point(ref rng, w);
            if (sv.PointContents(p) != Bsp.ContentsSolid)
                return p;
        }
        return Point(ref rng, w);
    }

    private static string Hex(float f) => VmOpcodeFuzz.Hex(BitConverter.SingleToUInt32Bits(f));

    private static string? Rounds(VmOpcodeFuzz f, Server sv, Model* w, int numModels, Action<Server, int> call, int rounds,
                                  Stats stats)
    {
        var rng = new Rng(17);
        int numGlobals = f.Progs.NumGlobals;
        short* leafs = stackalloc short[16];
        float* trace = stackalloc float[12];
        byte* refPvs = (byte*)NativeMemory.AllocZeroed(CheckPvsBytes);
        try
        {
            void F(int e, int field, float v) => f.Field(e, field, BitConverter.SingleToUInt32Bits(v));
            void I(int e, int field, int v) => f.Field(e, field, (uint)v);
            void V(int e, int field, in Vec3 v)
            {
                F(e, field, v.X);
                F(e, field + 1, v.Y);
                F(e, field + 2, v.Z);
            }
            void G(int ofs, float v) => f.Global(ofs, BitConverter.SingleToUInt32Bits(v));
            float* Port(int e) => sv.Fields(e);
            int PortWord(int ofs) => ((int*)f.PortGlobals)[ofs];
            Vec3 Origin(int e) => *(Vec3*)(Port(e) + EntVars.Origin);

            (int, uint) RefHeader(int e)
            {
                int free = 0;
                float t = 0;
                HeaderFn(f.RefEdicts, Fields, e, 0, &free, &t);
                return (free, BitConverter.SingleToUInt32Bits(t));
            }

            void SetFree(int e, int free)
            {
                float t = 0;
                HeaderFn(f.RefEdicts, Fields, e, 1, &free, &t);
                var h = sv.EdictNum(e);
                *h = default;       // as the oracle's memset: unlinked, no leafs
                h->Free = free;
            }

            void RefWorld(int maxclients) => WorldFn(f.RefEdicts, Fields, FuzzEdicts, FuzzEdicts, maxclients);

            // the world edict as SV_SpawnServer sets it up
            for (int fl = 0; fl < Fields; fl++)
                f.Field(0, fl, 0);
            F(0, EntVars.Solid, SolidBsp);
            F(0, EntVars.Movetype, MovetypePush);
            F(0, EntVars.Modelindex, 1);

            for (int round = 0; round < rounds; round++)
            {
                RefWorld(1);
                for (int e = 1; e < FuzzEdicts; e++)
                {
                    UnlinkFn(e);
                    sv.UnlinkEdict(e);
                }
                for (int e = 1; e < FuzzEdicts; e++)
                {
                    for (int fl = 0; fl < Fields; fl++)
                        f.Field(e, fl, 0);
                    SetFree(e, rng.Below(16) == 0 ? 1 : 0);
                    int solid = rng.Below(5);
                    (Vec3 mins, Vec3 maxs) = Boxes[rng.Below(Boxes.Length)];
                    F(e, EntVars.Solid, solid);
                    if (solid == SolidBsp)
                    {
                        if (numModels < 2)
                            F(e, EntVars.Solid, solid = SolidBbox);
                        else
                        {
                            F(e, EntVars.Movetype, MovetypePush);
                            F(e, EntVars.Modelindex, 2 + rng.Below(numModels - 1));
                            // the submodel's own bounds, as setmodel sets them
                            mins = (sv.ModelAt(CCast.I32(Port(e)[EntVars.Modelindex])))->Mins;
                            maxs = (sv.ModelAt(CCast.I32(Port(e)[EntVars.Modelindex])))->Maxs;
                        }
                    }
                    else if (rng.Below(2) == 0)
                        F(e, EntVars.Modelindex, 1 + rng.Below(Math.Max(1, numModels)));
                    Vec3 o = rng.Below(8) == 0 ? Point(ref rng, w) : OpenPoint(ref rng, sv, w);
                    if (solid == SolidBsp && rng.Below(2) == 0)
                        o = default;
                    V(e, EntVars.Origin, o);
                    V(e, EntVars.Mins, mins);
                    V(e, EntVars.Maxs, maxs);
                    V(e, EntVars.Size, Vec3.Subtract(maxs, mins));
                    int flags = 0;
                    if (rng.Below(3) == 0)
                        flags |= Server.FlMonster;
                    if (rng.Below(6) == 0)
                        flags |= Server.FlItem;
                    F(e, EntVars.Flags, flags);
                    if (rng.Below(4) == 0)
                        I(e, EntVars.Owner, f.EdictOffset(rng.Below(FuzzEdicts)));
                    F(e, EntVars.Takedamage, rng.Below(3) == 0 ? 0 : rng.Below(4) == 0 ? 1 : 2);
                    F(e, EntVars.Team, rng.Below(4) - 1);
                    F(e, EntVars.Health, rng.Below(8) switch { 0 => 0, 1 => -0f, 2 => -5, 3 => 1e-45f, _ => 100 });
                    V(e, EntVars.ViewOfs, rng.Below(2) == 0 ? new Vec3(0, 0, 22) : new Vec3(0, 0, U(ref rng, -40, 40)));

                    int nr = LinkFn(e, leafs);
                    sv.LinkEdict(e, false);
                    EdictHeader* h = sv.EdictNum(e);
                    if (h->NumLeafs != nr)
                        return $"round {round}, SV_LinkEdict {e}: port {h->NumLeafs} leafs, reference {nr}";
                    for (int i = 0; i < nr && i < 16; i++)
                        if (h->LeafNums[i] != leafs[i])
                            return $"round {round}, SV_LinkEdict {e}: leaf {i} port {h->LeafNums[i]}, reference {leafs[i]}";
                }
                if (f.Compare(nanClass: true, RefHeader) is { } linked)
                    return $"round {round}, after linking: {linked}";

                // SV_Move
                for (int n = 0; n < 1500; n++)
                {
                    Vec3 start = rng.Below(3) switch
                    {
                        0 => Point(ref rng, w),
                        1 => OpenPoint(ref rng, sv, w),
                        _ => Vec3.Add(Origin(1 + rng.Below(FuzzEdicts - 1)),
                                      new Vec3(U(ref rng, -64, 64), U(ref rng, -64, 64), U(ref rng, -64, 64))),
                    };
                    Vec3 end = rng.Below(4) switch
                    {
                        0 => OpenPoint(ref rng, sv, w),
                        1 or 2 => Origin(1 + rng.Below(FuzzEdicts - 1)),
                        _ => Vec3.Add(start, new Vec3(U(ref rng, -256, 256), U(ref rng, -256, 256), U(ref rng, -256, 256))),
                    };
                    if (rng.Below(3) == 0)
                    {   // from close by, through an entity
                        Vec3 o = Origin(1 + rng.Below(FuzzEdicts - 1));
                        start = Vec3.Add(o, new Vec3(U(ref rng, -160, 160), U(ref rng, -160, 160), U(ref rng, -64, 64)));
                        float k = U(ref rng, 0.5f, 2);
                        end = new Vec3(start.X + (o.X - start.X) * k, start.Y + (o.Y - start.Y) * k, start.Z + (o.Z - start.Z) * k);
                    }
                    (Vec3 mins, Vec3 maxs) = Boxes[rng.Below(Boxes.Length)];
                    int type = rng.Below(3);
                    int pass = rng.Below(4) == 0 ? -1 : rng.Below(FuzzEdicts);
                    int re = MoveFn((float*)&start, (float*)&mins, (float*)&maxs, (float*)&end, type, pass, trace);
                    Trace t = sv.Move(start, mins, maxs, end, type, pass);
                    stats.Moves++;
                    stats.MovesHitEntity += t.Ent > 0 ? 1 : 0;
                    stats.MovesStartSolid += t.StartSolid ? 1 : 0;
                    string? d = null;
                    uint* r = (uint*)trace;
                    if (re != t.Ent)
                        d = $"ent: port {t.Ent}, reference {re}";
                    else if ((r[0] != 0) != t.AllSolid || (r[1] != 0) != t.StartSolid || (r[2] != 0) != t.InOpen || (r[3] != 0) != t.InWater)
                        d = $"flags: port {t.AllSolid} {t.StartSolid} {t.InOpen} {t.InWater}, reference {r[0]} {r[1]} {r[2]} {r[3]}";
                    else
                    {
                        uint[] port =
                        [
                            BitConverter.SingleToUInt32Bits(t.Fraction), BitConverter.SingleToUInt32Bits(t.EndPos.X),
                            BitConverter.SingleToUInt32Bits(t.EndPos.Y), BitConverter.SingleToUInt32Bits(t.EndPos.Z),
                            BitConverter.SingleToUInt32Bits(t.Plane.Normal.X), BitConverter.SingleToUInt32Bits(t.Plane.Normal.Y),
                            BitConverter.SingleToUInt32Bits(t.Plane.Normal.Z), BitConverter.SingleToUInt32Bits(t.Plane.Dist),
                        ];
                        for (int i = 0; i < 8 && d is null; i++)
                            if (port[i] != r[4 + i])
                                d = $"word {i} (fraction, endpos, normal, dist): port {VmOpcodeFuzz.Hex(port[i])}, reference {VmOpcodeFuzz.Hex(r[4 + i])}";
                    }
                    if (d is not null)
                        return $"round {round}, SV_Move ({Hex(start.X)}, {Hex(start.Y)}, {Hex(start.Z)}) -> " +
                               $"({Hex(end.X)}, {Hex(end.Y)}, {Hex(end.Z)}), box {mins.X},{mins.Y},{mins.Z} {maxs.X},{maxs.Y},{maxs.Z}, " +
                               $"type {type}, pass {pass}: {d}";
                }

                // PF_checkclient: clients 1..maxclients, the state at the edges of the 0.1 s interval
                for (int n = 0; n < 150; n++)
                {
                    int maxclients = 1 + rng.Below(6);
                    sv.MaxClients = maxclients;
                    for (int c = 1; c <= maxclients; c++)
                    {
                        F(c, EntVars.Health, rng.Below(6) switch { 0 => 0, 1 => -0f, 2 => -1, 3 => 1e-45f, _ => 100 });
                        F(c, EntVars.Flags, rng.Below(4) == 0 ? Server.FlNoTarget : 0);
                    }
                    int self = rng.Below(FuzzEdicts);
                    f.Global(GlobalVars.Self, (uint)f.EdictOffset(self));
                    double time = 1 + rng.Below(100000) / 72.0;
                    double last = rng.Below(6) switch
                    {
                        0 => time - 0.1,
                        1 => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(time - 0.1) + 1),
                        2 => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(time - 0.1) - 1),
                        3 => time - rng.Below(20) / 72.0,
                        4 => (float)(time - 0.1),
                        _ => 0,
                    };
                    int lastcheck = rng.Below(maxclients + 1);     // as the game keeps it: 0 (never), or a client
                    for (int b = 0; b < CheckPvsBytes; b++)
                        refPvs[b] = sv.CheckPvs[b] = (byte)rng.Bits();
                    CheckStateFn(1, &lastcheck, &last, refPvs);
                    sv.LastCheck = lastcheck;
                    sv.LastCheckTime = last;
                    sv.Time = time;
                    int numEdicts = FuzzEdicts;
                    uint seed = rng.Bits();
                    BuiltinFn(Builtins.CheckClient, 0, f.RefGlobals, numGlobals, f.Strings, f.Progs.NumStrings, 0,
                              f.RefEdicts, Fields, 1, seed, &numEdicts, FuzzEdicts, maxclients, time);
                    call(sv, Builtins.CheckClient);
                    stats.CheckClients++;
                    stats.CheckClientsFound += sv.Globals[QcVm.OfsReturn] != 0 ? 1 : 0;
                    stats.NewChecks += time - last >= 0.1 ? 1 : 0;
                    CheckStateFn(0, &lastcheck, &last, refPvs);
                    string? d = f.Compare(nanClass: true, RefHeader);
                    if (d is null && sv.LastCheck != lastcheck)
                        d = $"sv.lastcheck: port {sv.LastCheck}, reference {lastcheck}";
                    if (d is null && BitConverter.DoubleToInt64Bits(sv.LastCheckTime) != BitConverter.DoubleToInt64Bits(last))
                        d = $"sv.lastchecktime: port {sv.LastCheckTime:R}, reference {last:R}";
                    for (int b = 0; b < CheckPvsBytes && d is null; b++)
                        if (sv.CheckPvs[b] != refPvs[b])
                            d = $"checkpvs byte {b}: port 0x{sv.CheckPvs[b]:X2}, reference 0x{refPvs[b]:X2}";
                    if (d is not null)
                        return $"round {round}, PF_checkclient (self {self}, maxclients {maxclients}, time {time:R}): {d}";
                }
                sv.MaxClients = 1;

                // PF_aim: from an entity, along a direction at or near another
                for (int n = 0; n < 60; n++)
                {
                    string tp = rng.Below(3) == 0 ? "1" : "0", aim = rng.Below(4) switch { 0 => "0.5", 1 => "1", 2 => "-1", _ => "0.93" };
                    fixed (byte* tn = "teamplay\0"u8, an = "sv_aim\0"u8)
                    fixed (byte* tv = Encoding.ASCII.GetBytes(tp + "\0"), av = Encoding.ASCII.GetBytes(aim + "\0"))
                    {
                        sv.Cvars.Set(tn, tv);
                        sv.Cvars.Set(an, av);
                        AimCvarsFn(QAtofFn(av), QAtofFn(tv));
                    }
                    int shooter = 1 + rng.Below(FuzzEdicts - 1);
                    Vec3 dir;
                    int t1 = 1 + rng.Below(FuzzEdicts - 1), t2 = 1 + rng.Below(FuzzEdicts - 1);
                    if (rng.Below(3) == 0 && t1 != shooter && t2 != shooter && t1 != t2
                        && sv.EdictNum(t1)->Free == 0 && sv.EdictNum(t2)->Free == 0 && sv.EdictNum(shooter)->Free == 0)
                    {   // two targets at the same angle to v_forward, one beside it and one above it: equal cosines, so
                        // the comparison's tie rule decides, and the results differ (aim keeps the target's height)
                        Vec3 o = OpenPoint(ref rng, sv, w);
                        o = new Vec3(MathF.Round(o.X), MathF.Round(o.Y), MathF.Round(o.Z));
                        int axis = rng.Below(2);
                        float sign = rng.Below(2) == 0 ? 1 : -1, a = MathF.Round(U(ref rng, 64, 256)),
                            b = MathF.Round(U(ref rng, 4, 0.35f * a));
                        dir = axis == 0 ? new Vec3(sign, 0, 0) : new Vec3(0, sign, 0);
                        Vec3 side = axis == 0 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
                        V(shooter, EntVars.Origin, o);
                        for (int t = 0; t < 2; t++)
                        {
                            int e = t == 0 ? t1 : t2;
                            Vec3 c = t == 0
                                ? new Vec3(o.X + dir.X * a + side.X * b, o.Y + dir.Y * a + side.Y * b, o.Z + 20)
                                : new Vec3(o.X + dir.X * a, o.Y + dir.Y * a, o.Z + 20 + b);
                            V(e, EntVars.Origin, new Vec3(c.X, c.Y, c.Z - 4));      // the box's centre is 4 above
                            V(e, EntVars.Mins, new Vec3(-16, -16, -24));
                            V(e, EntVars.Maxs, new Vec3(16, 16, 32));
                            V(e, EntVars.Size, new Vec3(32, 32, 56));
                            F(e, EntVars.Solid, SolidSlidebox);
                            F(e, EntVars.Takedamage, 2);
                        }
                        foreach (int e in new[] { shooter, t1, t2 })
                        {
                            LinkFn(e, leafs);
                            sv.LinkEdict(e, false);
                        }
                    }
                    else if (rng.Below(3) == 0)
                        dir = new Vec3(U(ref rng, -1, 1), U(ref rng, -1, 1), U(ref rng, -1, 1));
                    else
                    {
                        int target = 1 + rng.Below(FuzzEdicts - 1);
                        dir = Vec3.Subtract(Origin(target), Origin(shooter));
                        dir.Z += U(ref rng, -40, 40);
                    }
                    Mathlib.VectorNormalize(ref dir);
                    G(GlobalVars.VForward, dir.X);
                    G(GlobalVars.VForward + 1, dir.Y);
                    G(GlobalVars.VForward + 2, dir.Z);
                    f.Global(QcVm.OfsParm0, (uint)f.EdictOffset(shooter));
                    G(QcVm.OfsParm0 + 3, 1000);
                    int numEdicts = FuzzEdicts;
                    BuiltinFn(Builtins.Aim, 2, f.RefGlobals, numGlobals, f.Strings, f.Progs.NumStrings, 0,
                              f.RefEdicts, Fields, 1, rng.Bits(), &numEdicts, FuzzEdicts, 1, 1);
                    call(sv, Builtins.Aim);
                    stats.Aims++;
                    bool straight = sv.Globals[QcVm.OfsReturn] == PortWord(GlobalVars.VForward)
                        && sv.Globals[QcVm.OfsReturn + 1] == PortWord(GlobalVars.VForward + 1)
                        && sv.Globals[QcVm.OfsReturn + 2] == PortWord(GlobalVars.VForward + 2);
                    stats.AimsTarget += straight ? 0 : 1;
                    if (f.Compare(nanClass: true, RefHeader) is { } d)
                        return $"round {round}, PF_aim (shooter {shooter}, teamplay {tp}, sv_aim {aim}): {d}";
                }

                // SV_NewChaseDir: an actor on the ground, flying or swimming; enemy deltas that truncate alike
                for (int n = 0; n < 40; n++)
                {
                    int actor = 1 + rng.Below(FuzzEdicts - 1), enemy = 1 + rng.Below(FuzzEdicts - 1);
                    if (sv.EdictNum(actor)->Free != 0 || enemy == actor)
                        continue;
                    if (Port(actor)[EntVars.Solid] == SolidBsp)
                        continue;   // a pusher does not walk
                    F(actor, EntVars.Flags, rng.Below(4) switch { 0 => Server.FlOnGround, 1 => Server.FlFly, 2 => Server.FlSwim,
                        _ => Server.FlOnGround | Server.FlPartialGround });
                    I(actor, EntVars.Enemy, f.EdictOffset(enemy));
                    F(actor, EntVars.IdealYaw, rng.Below(2) == 0 ? rng.Below(8) * 45 : U(ref rng, -720, 720));
                    F(actor, EntVars.YawSpeed, rng.Below(3) == 0 ? 360 : U(ref rng, 0, 90));
                    F(actor, EntVars.Angles + 1, U(ref rng, 0, 360));
                    Vec3 a = Origin(actor);
                    float k = rng.Below(64), dx = k + U(ref rng, 0, 0.99f), dy = k + U(ref rng, 0, 0.99f);
                    if (rng.Below(2) == 0)
                        dx = -dx;
                    if (rng.Below(2) == 0)
                        dy = -dy;
                    V(enemy, EntVars.Origin, new Vec3(a.X + dx, a.Y + dy, a.Z + U(ref rng, -32, 32)));
                    f.Global(GlobalVars.Self, (uint)f.EdictOffset(actor));
                    float dist = rng.Below(3) switch { 0 => 8, 1 => 10, _ => 20 };
                    uint seed = rng.Bits();
                    RefWorld(1);
                    uint refSeed = NewChaseDirFn(f.RefGlobals, actor, enemy, dist, seed);
                    sv.Rand.Srand(seed);
                    Vec3 before = Origin(actor);
                    sv.NewChaseDir(actor, enemy, dist);
                    stats.Chases++;
                    Vec3 after = Origin(actor);
                    stats.ChasesMoved += before.X != after.X || before.Y != after.Y || before.Z != after.Z ? 1 : 0;
                    string? d = f.Compare(nanClass: true, RefHeader);
                    if (d is null && sv.Rand.Seed != refSeed)
                        d = $"rand seed afterwards: port 0x{sv.Rand.Seed:X8}, reference 0x{refSeed:X8}";
                    if (d is not null)
                        return $"round {round}, SV_NewChaseDir (actor {actor}, enemy {enemy}, dist {dist}, deltas {Hex(dx)} {Hex(dy)}): {d}";
                }
            }
            return null;
        }
        finally
        {
            NativeMemory.Free(refPvs);
        }
    }
}
