// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Harness;
using static Edict.Engine.Tests.VmOpcodeFuzz;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 for the builtins whose effect is a message (pr_cmds.c): the Write* family to every destination, sound,
/// particle, sprint, centerprint, bprint, stuffcmd, lightstyle, ambientsound and makestatic, as
/// <c>pr_builtins[num] ()</c> in the oracle (EdictOracle_Builtin after EdictOracle_MessageSetup) and the port's, on
/// the same memory, clients and precache lists. After each call globals and edicts are compared, then every buffer a
/// builtin can write: sv.datagram, sv.reliable_datagram, sv.signon and each client's message. What the game does not
/// do: clients active but not spawned (and the reverse) when a light style changes, statics with skins and
/// colormaps, edge values in every argument.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class MessageBuiltinTests
{
    private static readonly nint Lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("EDICT_ORACLE")
        ?? throw new InvalidOperationException("EDICT_ORACLE is not set (docs/TESTING.md)"));
    private static readonly delegate* unmanaged<int, int, float*, int, byte*, int, int, void*, int, int, uint, int*, int, int, double, uint> BuiltinFn =
        (delegate* unmanaged<int, int, float*, int, byte*, int, int, void*, int, int, uint, int*, int, int, double, uint>)NativeLibrary.GetExport(Lib, "EdictOracle_Builtin");
    private static readonly delegate* unmanaged<void*, int, int, int, int*, float*, void> HeaderFn =
        (delegate* unmanaged<void*, int, int, int, int*, float*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_EdictHeader");
    private static readonly delegate* unmanaged<int, int*, int*, byte**, int, byte**, int, void> SetupFn =
        (delegate* unmanaged<int, int*, int*, byte**, int, byte**, int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_MessageSetup");
    private static readonly delegate* unmanaged<int, byte*, int, int> TakeFn =
        (delegate* unmanaged<int, byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_TakeMessage");

    private const int FuzzEdicts = 16, MaxClients = 4, OfsParm0 = QcVm.OfsParm0;

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

    private static readonly int[] Fuzzed =
    [
        Builtins.WriteByte, Builtins.WriteChar, Builtins.WriteShort, Builtins.WriteLong, Builtins.WriteCoord,
        Builtins.WriteAngle, Builtins.WriteString, Builtins.WriteEntity, Builtins.Sound, Builtins.Particle,
        Builtins.Sprint, Builtins.CenterPrint, Builtins.Bprint, Builtins.StuffCmd, Builtins.Lightstyle,
        Builtins.AmbientSound, Builtins.MakeStatic,
    ];

    [Fact]
    public void Message_builtins_match_the_reference()
    {
        var calls = new Dictionary<int, (int Calls, long Bytes)>();
        string? d = FirstDivergence(calls);
        Assert.True(d is null, d);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(", ",
            calls.OrderBy(c => c.Key).Select(c => $"#{c.Key}: {c.Value.Calls} calls, {c.Value.Bytes} bytes")));
    }

    internal static string? FirstDivergence(Dictionary<int, (int Calls, long Bytes)> calls, int rounds = 40_000, Func<int, bool>? only = null)
    {
        using var f = new VmOpcodeFuzz(FuzzEdicts);
        int numStrings = f.Progs.NumStrings;
        byte* refStrings = (byte*)NativeMemory.AllocZeroed((nuint)(numStrings + Server.StringTempSize));
        byte* portTemp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
        byte* empty = (byte*)NativeMemory.AllocZeroed(1);
        byte* refOut = (byte*)NativeMemory.AllocZeroed(Client.MaxMsgLen + 16);
        try
        {
            new ReadOnlySpan<byte>(f.Strings, numStrings).CopyTo(new Span<byte>(refStrings, numStrings));
            var vm = new QcVm(f.Progs, (int*)f.PortGlobals, f.PortEdicts, f.Header, VmReplay.NumBuiltins);
            var sv = new Server(vm, f.Progs, (int*)f.PortGlobals, f.PortEdicts, f.Header, new Cvars([]), new NoStrings(),
                                portTemp, numStrings) { MaxEdicts = FuzzEdicts, Hooks = new NoHooks() };
            Model emptyWorld = default;
            sv.ClearWorld(&emptyWorld);
            var clients = new Client[MaxClients];
            for (int i = 0; i < MaxClients; i++)
                clients[i] = new Client();
            sv.Clients = clients;

            // precached: models "a", "ab", "player"; sounds "b", "ba", "monster_dog" (the string table's indexes)
            int[] modelStrings = [2, 5, 11], soundStrings = [4, 6, 10];
            byte** refModels = stackalloc byte*[3], refSounds = stackalloc byte*[3];
            var portModels = new nint[3];
            for (int i = 0; i < 3; i++)
            {
                refModels[i] = refStrings + f.StringOffset(modelStrings[i]);
                refSounds[i] = refStrings + f.StringOffset(soundStrings[i]);
                portModels[i] = (nint)(f.Strings + f.StringOffset(modelStrings[i]));
            }

            var rng = new Rng(0x6d736762);
            int* active = stackalloc int[MaxClients], spawned = stackalloc int[MaxClients];
            uint Str() => (uint)f.StringOffset(rng.Below(f.StringCount));
            uint Unit() => rng.Below(4) switch      // volumes and the like: [0, 1] and its edges, or anything
            {
                0 => BitConverter.SingleToUInt32Bits(rng.Below(256) / 255f),
                1 => BitConverter.SingleToUInt32Bits(rng.Below(2)),
                2 => BitConverter.SingleToUInt32Bits(rng.Below(1 << 16) / 65536f),
                _ => rng.Float(),
            };
            uint Small(int range) => rng.Below(3) == 0 ? rng.Float() : BitConverter.SingleToUInt32Bits(rng.Below(range) + (rng.Below(4) == 0 ? 0.5f : 0));

            for (int round = 0; round < rounds; round++)
            {
                int num = Fuzzed[rng.Below(Fuzzed.Length)];
                if (only is not null && !only(num))
                    continue;

                // clients, precache lists and empty buffers on both sides
                int maxclients = 1 + rng.Below(MaxClients);
                for (int i = 0; i < maxclients; i++)
                {
                    active[i] = rng.Below(4) != 0 ? 1 : 0;
                    spawned[i] = rng.Below(3) != 0 ? 1 : 0;
                }
                SetupFn(maxclients, active, spawned, refModels, 3, refSounds, 3);
                sv.MaxClients = maxclients;
                for (int i = 0; i < MaxClients; i++)
                {
                    clients[i].Clear();
                    clients[i].Message.AllowOverflow = true;
                    clients[i].Active = i < maxclients && active[i] != 0;
                    clients[i].Spawned = i < maxclients && spawned[i] != 0;
                }
                sv.Active = false;
                sv.InitPrecache(empty, portModels, new nint[3]);
                for (int i = 0; i < 3; i++)
                    sv.PrecacheSound(f.Strings + f.StringOffset(soundStrings[i]));
                sv.Datagram.Clear();
                sv.ReliableDatagram.Clear();
                sv.Signon.Clear();
                bool isActive = rng.Below(3) != 0;
                sv.Active = isActive;
                sv.Time = rng.Below(1000) * 0.1 + 1;

                int numEdicts = maxclients + 1 + rng.Below(FuzzEdicts - maxclients - 1);
                f.Scramble(ref rng);
                for (int e = 0; e < FuzzEdicts; e++)
                {
                    int free = 0;
                    float ft = 0;
                    HeaderFn(f.RefEdicts, Fields, e, 1, &free, &ft);
                    *(EdictHeader*)(f.PortEdicts + e * f.Stride) = default;
                }
                int AClient() => 1 + rng.Below(maxclients);
                uint EdictOfs(int e) => (uint)f.EdictOffset(e);
                void Vector(int ofs)
                {
                    for (int k = 0; k < 3; k++)
                        f.Global(ofs + k, rng.Float());
                }

                // the arguments
                int argc = 1;
                switch (num)
                {
                    case Builtins.WriteByte or Builtins.WriteChar or Builtins.WriteShort or Builtins.WriteLong
                        or Builtins.WriteCoord or Builtins.WriteAngle or Builtins.WriteString or Builtins.WriteEntity:
                        argc = 2;
                        f.Global(OfsParm0, rng.Below(6) == 0 ? BitConverter.SingleToUInt32Bits(1.5f) : BitConverter.SingleToUInt32Bits(rng.Below(4)));
                        f.Global(GlobalVars.MsgEntity, EdictOfs(AClient()));
                        f.Global(OfsParm0 + 3, num switch
                        {
                            Builtins.WriteString => Str(),
                            Builtins.WriteEntity => EdictOfs(rng.Below(numEdicts)),
                            _ => rng.Float(),
                        });
                        break;
                    case Builtins.Sound:
                        argc = 5;
                        int se = rng.Below(numEdicts);
                        f.Global(OfsParm0, EdictOfs(se));
                        f.Global(OfsParm0 + 3, BitConverter.SingleToUInt32Bits(rng.Below(8) + (rng.Below(4) == 0 ? 0.9f : 0)));
                        f.Global(OfsParm0 + 6, Str());
                        f.Global(OfsParm0 + 9, BitConverter.SingleToUInt32Bits(rng.Below(3) == 0 ? 1 : rng.Below(256) / 255f));
                        f.Global(OfsParm0 + 12, BitConverter.SingleToUInt32Bits(rng.Below(3) == 0 ? 1 : rng.Below(17) * 0.25f));
                        break;
                    case Builtins.Particle:
                        argc = 4;
                        Vector(OfsParm0);
                        Vector(OfsParm0 + 3);
                        if (rng.Below(2) == 0)
                            for (int k = 0; k < 3; k++)     // dir*16 around the clamp at -128 and 127
                                f.Global(OfsParm0 + 3 + k, BitConverter.SingleToUInt32Bits((rng.Below(64) - 32) * 0.5f));
                        f.Global(OfsParm0 + 6, Small(256));
                        f.Global(OfsParm0 + 9, Small(300));
                        break;
                    case Builtins.Sprint or Builtins.CenterPrint:
                        argc = 2 + rng.Below(3);
                        f.Global(OfsParm0, EdictOfs(rng.Below(numEdicts)));
                        for (int k = 1; k < argc; k++)
                            f.Global(OfsParm0 + 3 * k, Str());
                        break;
                    case Builtins.Bprint:
                        argc = 1 + rng.Below(3);
                        for (int k = 0; k < argc; k++)
                            f.Global(OfsParm0 + 3 * k, Str());
                        break;
                    case Builtins.StuffCmd:
                        argc = 2;
                        f.Global(OfsParm0, EdictOfs(AClient()));
                        f.Global(OfsParm0 + 3, Str());
                        break;
                    case Builtins.Lightstyle:
                        argc = 2;
                        f.Global(OfsParm0, BitConverter.SingleToUInt32Bits(rng.Below(64) + (rng.Below(4) == 0 ? 0.5f : 0)));
                        f.Global(OfsParm0 + 3, Str());
                        break;
                    case Builtins.AmbientSound:
                        argc = 4;
                        Vector(OfsParm0);
                        f.Global(OfsParm0 + 3, rng.Below(4) == 0 ? Str() : (uint)f.StringOffset(soundStrings[rng.Below(3)]));
                        f.Global(OfsParm0 + 6, Unit());
                        f.Global(OfsParm0 + 9, rng.Below(2) == 0 ? BitConverter.SingleToUInt32Bits(rng.Below(17) * 0.25f) : rng.Float());
                        break;
                    case Builtins.MakeStatic:
                        int me = 1 + rng.Below(numEdicts - 1);
                        f.Global(OfsParm0, EdictOfs(me));
                        f.Field(me, EntVars.Model, rng.Below(4) == 0 ? 0 : (uint)f.StringOffset(modelStrings[rng.Below(3)]));
                        f.Field(me, EntVars.Frame, Small(64));
                        f.Field(me, EntVars.Colormap, Small(17));
                        f.Field(me, EntVars.Skin, Small(4));
                        break;
                }

                // both sides
                int n = numEdicts;
                uint seed = rng.Bits();
                uint refSeed = BuiltinFn(num, argc, f.RefGlobals, f.Progs.NumGlobals, refStrings, numStrings, numStrings,
                                         f.RefEdicts, Fields, isActive ? 1 : 0, seed, &n, FuzzEdicts, maxclients, sv.Time);
                sv.Rand.Srand(seed);
                sv.NumEdicts = numEdicts;
                sv.Vm.Argc = argc;
                Builtins.Call(sv, num);
                string what = $"round {round}, builtin #{num} (argc {argc}, maxclients {maxclients}, active {isActive})";
                if (sv.NumEdicts != n)
                    return $"{what}: num_edicts port {sv.NumEdicts}, reference {n}";
                if (f.Compare(nanClass: true, e =>
                    {
                        int free = 0;
                        float ft = 0;
                        HeaderFn(f.RefEdicts, Fields, e, 0, &free, &ft);
                        return (free, BitConverter.SingleToUInt32Bits(ft));
                    }) is { } d)
                    return $"{what}: {d}";
                if (sv.Rand.Seed != refSeed)
                    return $"{what}: rand seed port 0x{sv.Rand.Seed:X8}, reference 0x{refSeed:X8}";

                // every buffer
                long bytes = 0;
                for (int which = 0; which < 3 + maxclients; which++)
                {
                    SizeBuf b = which switch { 0 => sv.Datagram, 1 => sv.ReliableDatagram, 2 => sv.Signon, _ => clients[which - 3].Message };
                    int r = TakeFn(which, refOut, Client.MaxMsgLen + 16);
                    int p = b.Overflowed ? -1 - b.CurSize : b.CurSize;
                    string name = which switch { 0 => "sv.datagram", 1 => "sv.reliable_datagram", 2 => "sv.signon", _ => $"client {which - 3}'s message" };
                    if (p != r)
                        return $"{what}: {name} cursize port {p}, reference {r}";
                    int len = r < 0 ? -1 - r : r;
                    for (int i = 0; i < len; i++)
                        if (b.Data[i] != refOut[i])
                            return $"{what}: {name} byte {i} of {len}: port {b.Data[i]:x2}, reference {refOut[i]:x2}";
                    bytes += len;
                    b.Clear();
                    b.Overflowed = false;
                }
                calls[num] = calls.TryGetValue(num, out var c) ? (c.Calls + 1, c.Bytes + bytes) : (1, bytes);
            }
            return null;
        }
        finally
        {
            NativeMemory.Free(refStrings);
            NativeMemory.Free(portTemp);
            NativeMemory.Free(empty);
            NativeMemory.Free(refOut);
        }
    }
}
