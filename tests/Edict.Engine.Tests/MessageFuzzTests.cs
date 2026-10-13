// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 for a client's datagram (sv_main.c): SV_WriteClientdataToMessage and SV_WriteEntitiesToClient in the oracle
/// (EdictOracle_ClientDatagram) and the port's, on a map loaded on both sides, the game's progs.dat and the same
/// linked entities, 300 of them so that entity numbers reach U_LONGENTITY. What the game rarely does: origins a tenth
/// of a unit off their baseline and one float either side, fractional and out-of-byte frames, skins, colormaps and
/// model indexes, the client 8 units from a node plane (SV_AddToFatPVS's edge), a message that starts near its end
/// (the 16-byte check, and overflow, which this buffer allows), damage, fixangle, punch and velocity edges. The bytes
/// written, the overflow flag and afterwards every global and field are compared.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class MessageFuzzTests
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
    private static readonly delegate* unmanaged<void*, int, int, EntityState*, void> BaselineFn =
        (delegate* unmanaged<void*, int, int, EntityState*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_SetBaseline");
    private static readonly delegate* unmanaged<float*, void*, int, int, int, byte*, int, int, int> DatagramFn =
        (delegate* unmanaged<float*, void*, int, int, int, byte*, int, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_ClientDatagram");
    private static readonly delegate* unmanaged<float*, void*, int, int, double, byte*, int, byte*, int> SendFn =
        (delegate* unmanaged<float*, void*, int, int, double, byte*, int, byte*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_SendClientDatagram");

    private const int Edicts = 300, MaxDatagram = 1024;
    private const int SolidNot = 0, SolidTrigger = 1, SolidBbox = 2, SolidSlidebox = 3, SolidBsp = 4;

    public static IEnumerable<TheoryDataRow<string>> MapNames =>
        VmReplay.Maps().Where(m => m is "start" or "lq_e0m1" or "lq_e0m3" or "lq_e0m6").Select(m => new TheoryDataRow<string>(m));

    private sealed class NoStrings : IEngineStrings
    {
        public byte* Get(int ofs) => throw new InvalidOperationException("no engine strings here");
    }

    /// <summary>A connection that keeps the last unreliable message.</summary>
    private sealed class Recorder : IClientConnection
    {
        public byte[]? Last;

        public int GetMessage(SizeBuf netMessage) => 0;

        public int SendMessage(SizeBuf data) => 1;

        public int SendUnreliableMessage(SizeBuf data)
        {
            Last = new ReadOnlySpan<byte>(data.Data, data.CurSize).ToArray();
            return 1;
        }

        public bool CanSendMessage() => true;
    }

    /// <summary>What the fuzz reached (so that a green run can be judged).</summary>
    internal sealed class Stats
    {
        public int Calls, Updates, LongEntities, Full, Overflows, PlaneEdges, Sent, AtTheLimit;
        public long Bytes;

        public override string ToString() =>
            $"{Calls} datagrams, {Bytes} bytes, {Updates} entity updates ({LongEntities} with a long number), {Full} stopped " +
            $"by the 16-byte check, {Overflows} overflowed, {PlaneEdges} clients 8 units from a node plane; {Sent} datagrams " +
            $"sent whole, {AtTheLimit} with the server datagram one byte short of fitting or just fitting";
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Client_datagram_matches_the_reference(string map)
    {
        var stats = new Stats();
        string? d = FirstDivergence(map, stats);
        Assert.True(d is null, d);
        TestContext.Current.TestOutputHelper?.WriteLine($"{map}: {stats}");
    }

    internal static string? FirstDivergence(string map, Stats stats, int rounds = 40, int calls = 25)
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
        byte* refOut = (byte*)NativeMemory.AllocZeroed(MaxDatagram);
        try
        {
            // a non-empty string of the progs for model (pr_strings[ent->v.model] only tells empty from not)
            int modelName = -1;
            for (int i = 1; i < progs.NumStrings - 1 && modelName < 0; i++)
                if (progs.String(i).SequenceEqual("progs/player.mdl"u8))
                    modelName = i;
            if (modelName < 0)
                return "progs.dat has no string progs/player.mdl";

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
            var recorder = new Recorder();
            var client = new Client { Active = true, Spawned = true, NetConnection = recorder };
            sv.Clients = [client];
            sv.Hooks = new GameHooks(sv);
            int active = 1;
            SetupFn(1, &active, 800, 2000, 0);
            var portMsg = new SizeBuf(MaxDatagram, allowOverflow: true);

            var rng = new VmOpcodeFuzz.Rng(VmOpcodeFuzz.MapSeed(map) ^ 0x6d657373);
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
            float Get(int e, int f) => *(float*)(portEdicts + e * stride + header + 4 * f);
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
            // a byte-sized value, fractional, negative or out of a byte at times
            float Small(int range) => rng.Below(8) switch
            {
                0 => rng.Below(range) + 0.5f,
                1 => -rng.Below(4),
                2 => 256 + rng.Below(300),
                3 => 0,
                _ => rng.Below(range),
            };
            // a baseline coordinate's distance: none, the 0.1 threshold and a float either side, small, large
            float Miss() => rng.Below(10) switch
            {
                0 => 0.1f,
                1 => -0.1f,
                2 => BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(0.1f) - 1),
                3 => -BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(0.1f) + 1),
                4 => U(-0.2f, 0.2f),
                5 => U(-64, 64),
                _ => 0,
            };

            // SV_SendClientDatagram whole, with a server datagram that fits, just fits or is one byte too long: the
            // port's message without it is measured first (writing the client data again changes nothing more)
            byte* dg = stackalloc byte[MaxDatagram];
            string? SendDatagram(int clent)
            {
                F(clent, EntVars.DmgTake, 0);
                F(clent, EntVars.DmgSave, 0);
                F(clent, EntVars.Fixangle, 0);
                double time = 1 + rng.Below(100000) / 72.0;
                sv.Time = time;
                sv.SvPlayer = clent;
                client.Edict = clent;
                sv.Datagram.Clear();
                recorder.Last = null;
                sv.SendClientDatagram(client);
                int m0 = recorder.Last!.Length;
                if (m0 >= MaxDatagram)
                    return null;
                int length = rng.Below(4) switch { 0 => MaxDatagram - m0, 1 => MaxDatagram - m0 - 1, 2 => 0, _ => rng.Below(MaxDatagram - m0) };
                if (length < 0)
                    return null;
                for (int i = 0; i < length; i++)
                    dg[i] = (byte)rng.Next();
                int r = SendFn(refGlobals, refEdicts, Edicts, clent, time, dg, length, refOut);
                sv.Datagram.Clear();
                sv.Datagram.Write(dg, length);
                recorder.Last = null;
                sv.SendClientDatagram(client);
                sv.Datagram.Clear();
                byte[] p = recorder.Last!;
                stats.Sent++;
                if (length >= MaxDatagram - m0 - 1)
                    stats.AtTheLimit++;
                if (p.Length != r)
                    return $"server datagram of {length} bytes after {m0}: length port {p.Length}, reference {r}";
                for (int i = 0; i < r; i++)
                    if (p[i] != refOut[i])
                        return $"server datagram of {length} bytes after {m0}: byte {i} of {r}: port {p[i]:x2}, reference {refOut[i]:x2}";
                return Compare(refGlobals, portGlobals, refEdicts, portEdicts, header, fields, stride, ng);
            }

            for (int round = 0; round < rounds; round++)
            {
                new ReadOnlySpan<int>(progs.InitialGlobals, ng).CopyTo(new Span<int>(refGlobals, ng));
                new ReadOnlySpan<int>(progs.InitialGlobals, ng).CopyTo(new Span<int>(portGlobals, ng));
                refGlobals[GlobalVars.Serverflags] = portGlobals[GlobalVars.Serverflags] = rng.Below(16);

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
                I(0, EntVars.Model, modelName);

                // entities near the client at times, so that many are in its PVS; every fourth round few are shown,
                // so that SV_SendClientDatagram can be called without its buffer overflowing
                bool sparse = round % 4 == 3;
                Vec3 centre = Open();
                for (int e = 1; e < Edicts; e++)
                {
                    for (int fl = 0; fl < fields; fl++)
                        I(e, fl, 0);
                    *sv.EdictNum(e) = default;
                    bool shown = sparse ? rng.Below(25) == 0 : rng.Below(5) != 0;
                    int solid = rng.Below(6) switch { 0 => SolidNot, 1 => SolidTrigger, 2 => SolidBbox, 3 => SolidSlidebox, 4 => SolidBsp, _ => SolidBbox };
                    if (solid == SolidBsp && world.Count < 2)
                        solid = SolidBbox;
                    float modelindex = shown ? rng.Below(6) switch
                    {
                        0 => 1 + rng.Below(world.Count),
                        1 => 2.5f,
                        2 => 300,
                        _ => 2 + rng.Below(40),
                    } : rng.Below(2) == 0 ? 0 : 3;
                    if (solid == SolidBsp && world.Count > 1)
                        modelindex = 2 + rng.Below(world.Count - 1);
                    F(e, EntVars.Solid, solid);
                    F(e, EntVars.Modelindex, modelindex);
                    I(e, EntVars.Model, shown || rng.Below(2) == 0 ? modelName : 0);
                    F(e, EntVars.Movetype, solid == SolidBsp ? Server.MovetypePush : rng.Below(4) == 0 ? Server.MovetypeStep : rng.Below(7));
                    Vec3 o = rng.Below(2) == 0 ? Vec3.Add(centre, new Vec3(U(-300, 300), U(-300, 300), U(-100, 100))) : Open();
                    Vec3 size = rng.Below(6) switch
                    {
                        0 => new Vec3(U(200, 2000), U(200, 2000), U(100, 600)),     // many leafs (MAX_ENT_LEAFS)
                        1 => default,
                        _ => new Vec3(32, 32, 56),
                    };
                    Vec3 mins = new(-size.X / 2, -size.Y / 2, -24), maxs = new(size.X / 2, size.Y / 2, size.Z - 24);
                    V(e, EntVars.Origin, o);
                    V(e, EntVars.Mins, mins);
                    V(e, EntVars.Maxs, maxs);
                    V(e, EntVars.Size, Vec3.Subtract(maxs, mins));
                    V(e, EntVars.Angles, rng.Below(3) == 0 ? default : new Vec3(U(-90, 90), U(-720, 720), U(-30, 30)));
                    F(e, EntVars.Frame, Small(64));
                    F(e, EntVars.Skin, Small(4));
                    F(e, EntVars.Colormap, Small(17));
                    F(e, EntVars.Effects, Small(16));

                    // the baseline: the entity as it is, or nearly
                    EntityState b = default;
                    b.Origin = new Vec3(o.X - Miss(), o.Y - Miss(), o.Z - Miss());
                    // each angle the entity's or another (one alone gives bits == U_ANGLE1 or U_ANGLE2 or U_ANGLE3)
                    Vec3 a = *(Vec3*)(sv.Fields(e) + EntVars.Angles);
                    b.Angles = new Vec3(rng.Below(3) == 0 ? U(-90, 90) : a.X, rng.Below(3) == 0 ? U(0, 360) : a.Y, rng.Below(3) == 0 ? U(-30, 30) : a.Z);
                    b.ModelIndex = rng.Below(3) == 0 ? rng.Below(50) : I32(modelindex);
                    b.Frame = rng.Below(3) == 0 ? rng.Below(64) : I32(Get(e, EntVars.Frame));
                    b.Colormap = rng.Below(3) == 0 ? rng.Below(17) : I32(Get(e, EntVars.Colormap));
                    b.Skin = rng.Below(3) == 0 ? rng.Below(4) : I32(Get(e, EntVars.Skin));
                    b.Effects = rng.Below(3) == 0 ? rng.Below(16) : I32(Get(e, EntVars.Effects));
                    BaselineFn(refEdicts, fields, e, &b);
                    sv.EdictNum(e)->Baseline = b;
                }

                WorldFn(refEdicts, fields, Edicts, Edicts, 1);
                for (int e = 1; e < Edicts; e++)
                {
                    LinkFn(e, leafs);
                    sv.LinkEdict(e, false);
                }
                sv.NumEdicts = Edicts;
                if (Compare(refGlobals, portGlobals, refEdicts, portEdicts, header, fields, stride, ng) is { } linked)
                    return $"round {round}, after linking: {linked}";

                for (int call = 0; call < calls; call++)
                {
                    int clent = rng.Below(8) == 0 ? 2 + rng.Below(Edicts - 2) : 1;
                    // where the client looks from: in the open, or exactly 8 units (or a float less) from an axial
                    // node plane, where SV_AddToFatPVS goes down one side or both
                    Vec3 org = Open();
                    float viewHeight = rng.Below(3) == 0 ? 22 : rng.Below(2) == 0 ? 0 : U(-10, 30);
                    if (rng.Below(3) == 0)
                    {
                        MNode* node = w->Nodes + rng.Below(w->NumNodes);
                        MPlane* plane = w->Planes + node->PlaneNum;
                        if (plane->Type < 3)
                        {
                            float dist = rng.Below(3) switch { 0 => 8, 1 => -8, _ => rng.Below(2) == 0 ? 7.9999995f : -7.9999995f };
                            (&org.X)[plane->Type] = plane->Dist + dist;
                            viewHeight = 0;
                            stats.PlaneEdges++;
                        }
                    }
                    V(clent, EntVars.Origin, org);
                    V(clent, EntVars.ViewOfs, new Vec3(0, 0, viewHeight));
                    if (rng.Below(2) == 0)
                    {   // re-link it where it now is
                        WorldFn(refEdicts, fields, Edicts, Edicts, 1);
                        LinkFn(clent, leafs);
                        sv.LinkEdict(clent, false);
                    }

                    // the client's own data
                    bool damage = rng.Below(4) == 0;
                    F(clent, EntVars.DmgTake, damage ? Small(200) : 0);
                    F(clent, EntVars.DmgSave, damage && rng.Below(2) == 0 ? Small(200) : 0);
                    I(clent, EntVars.DmgInflictor, rng.Below(Edicts) * stride);
                    F(clent, EntVars.Fixangle, rng.Below(4) == 0 ? 1 : 0);
                    F(clent, EntVars.Flags, rng.Below(2) == 0 ? Server.FlOnGround : 0);
                    F(clent, EntVars.Waterlevel, rng.Below(4));
                    V(clent, EntVars.Punchangle, rng.Below(3) == 0 ? new Vec3(U(-10, 10), 0, rng.Below(2) == 0 ? -0.0f : 0.5f) : default);
                    V(clent, EntVars.Velocity, rng.Below(3) == 0 ? default
                        : new Vec3(U(-2100, 2100), rng.Below(2) == 0 ? 15.9f : -16, U(-400, 400)));
                    F(clent, EntVars.Idealpitch, rng.Below(3) == 0 ? U(-20, 20) : 0);
                    F(clent, EntVars.Items, rng.Below(1 << 22));
                    F(clent, EntVars.Weaponframe, Small(9));
                    F(clent, EntVars.Armorvalue, Small(250));
                    F(clent, EntVars.Health, rng.Below(4) == 0 ? U(-50, 40000) : rng.Below(101));
                    F(clent, EntVars.Currentammo, Small(200));
                    F(clent, EntVars.AmmoShells, Small(100));
                    F(clent, EntVars.AmmoNails, Small(200));
                    F(clent, EntVars.AmmoRockets, Small(100));
                    F(clent, EntVars.AmmoCells, Small(100));
                    F(clent, EntVars.Weapon, rng.Below(1 << 8));

                    // the message: empty, or near its end
                    int start = rng.Below(3) == 0 ? MaxDatagram - 80 + rng.Below(80) : rng.Below(2) == 0 ? 0 : rng.Below(600);
                    bool clientdata = rng.Below(5) != 0;
                    new Span<byte>(refOut, MaxDatagram).Clear();
                    int r = DatagramFn(refGlobals, refEdicts, Edicts, clent, clientdata ? 1 : 0, refOut, start, MaxDatagram);

                    portMsg.Clear();
                    portMsg.Overflowed = false;
                    new Span<byte>(portMsg.Data, MaxDatagram).Clear();
                    portMsg.CurSize = start;
                    sv.SvPlayer = clent;
                    if (clientdata)
                        sv.WriteClientdataToMessage(clent, portMsg);
                    sv.WriteEntitiesToClient(clent, portMsg);
                    int p = portMsg.Overflowed ? -1 - portMsg.CurSize : portMsg.CurSize;

                    stats.Calls++;
                    if (p != r)
                        return $"round {round}, call {call} (clent {clent}, start {start}, clientdata {clientdata}): cursize port {p}, reference {r}";
                    int end = r < 0 ? -1 - r : r;
                    for (int i = 0; i < end; i++)
                        if (refOut[i] != portMsg.Data[i])
                            return $"round {round}, call {call} (clent {clent}, start {start}, clientdata {clientdata}): byte {i} of {end}: "
                                   + $"port {portMsg.Data[i]:x2}, reference {refOut[i]:x2}";
                    stats.Bytes += end - (r < 0 ? 0 : start);
                    if (r < 0)
                        stats.Overflows++;
                    else if (end > MaxDatagram - 16)
                        stats.Full++;
                    if (Compare(refGlobals, portGlobals, refEdicts, portEdicts, header, fields, stride, ng) is { } d)
                        return $"round {round}, call {call} (clent {clent}): {d}";
                    CountUpdates(portMsg.Data, r < 0 ? 0 : start, end, clientdata, stats);

                    if (sparse && SendDatagram(clent) is { } sd)
                        return $"round {round}, call {call} (clent {clent}), SV_SendClientDatagram: {sd}";
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
            NativeMemory.Free(refOut);
        }
    }

    private static int I32(float f) => Exact.CCast.I32(f);

    /// <summary>Entity updates in a message without client data (only then the bytes are updates alone).</summary>
    private static void CountUpdates(byte* d, int from, int end, bool clientdata, Stats stats)
    {
        if (clientdata)
            return;
        int p = from;
        while (p < end && (d[p] & 0x80) != 0)
        {
            int bits = d[p++];
            if ((bits & 1) != 0)
                bits |= d[p++] << 8;
            stats.Updates++;
            if ((bits & (1 << 14)) != 0)
            {
                stats.LongEntities++;
                p += 2;
            }
            else
                p++;
            for (int b = 10; b <= 13; b++)              // model, colormap, skin, effects
                if ((bits & (1 << b)) != 0)
                    p++;
            if ((bits & (1 << 6)) != 0)                 // frame
                p++;
            for (int i = 0; i < 3; i++)
            {
                if ((bits & (2 << i)) != 0)             // origin
                    p += 2;
                if ((bits & (i == 0 ? 1 << 8 : i == 1 ? 1 << 4 : 1 << 9)) != 0)
                    p++;
            }
        }
    }

    private static string? Compare(float* rg, float* pg, byte* re, byte* pe, int header, int fields, int stride, int ng)
    {
        for (int i = 0; i < ng; i++)
        {
            uint a = ((uint*)rg)[i], b = ((uint*)pg)[i];
            if (!VmOpcodeFuzz.Same(a, b, nanClass: true))
                return $"global {i}: port {VmOpcodeFuzz.Hex(b)}, reference {VmOpcodeFuzz.Hex(a)}";
        }
        for (int e = 0; e < Edicts; e++)
            for (int f = 0; f < fields; f++)
            {
                uint a = *(uint*)(re + e * stride + header + 4 * f), b = *(uint*)(pe + e * stride + header + 4 * f);
                if (!VmOpcodeFuzz.Same(a, b, nanClass: true))
                    return $"edict {e} field {f}: port {VmOpcodeFuzz.Hex(b)}, reference {VmOpcodeFuzz.Hex(a)}";
            }
        return null;
    }
}
