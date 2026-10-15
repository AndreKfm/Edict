// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// The listen-server gate of P5 (docs/PLAN.md): the port's host runs "map" with a local client connected through
/// its loopback (net_loop.c), server and client in one frame and on one rand stream, from the reference's inputs (the
/// 'I' record: the rand state before frame 0, the cvars after it; maxclients 1). After every frame the server's state
/// and messages must hash as the reference's frame record and the client's as its client record; at the spawn, the
/// spawn record. The edict header size comes from the corpus capture of the same map.
/// </summary>
public sealed class ListenGateTests
{
    [ThreadStatic] private static string? _lastTrace;

    /// <summary>The listen corpus (tools/listen.sh: data/listen, or EDICT_LISTEN).</summary>
    public static string Directory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_LISTEN");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, "listen") : dir;
        }
    }

    public static IEnumerable<TheoryDataRow<string>> Maps =>
        (System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.edh") : [])
        .Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).Select(m => new TheoryDataRow<string>(m));

    internal static string? FirstDivergence(string map, out int frames, out int clientFrames)
    {
        string edhPath = Path.Combine(Directory, map + ".edh");
        var edh = new EdhFile(edhPath);
        string emgPath = Path.ChangeExtension(edhPath, ".emg");
        EmgFile? emg = File.Exists(emgPath) ? new EmgFile(emgPath) : null;
        // the run's console commands (tools/listen.sh), the first of them a map command
        string cmdPath = Path.ChangeExtension(edhPath, ".cmd");
        string commands = File.Exists(cmdPath) ? File.ReadAllText(cmdPath, Encoding.Latin1) : $"map {map}\n";
        string first = commands.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        SpawnInputs inputs = VmReplay.FirstSpawn(Path.Combine(VmReplay.Directory, first + ".ecp"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        var loop = new Loopback(1 << 20);
        using var host = new Host(files, new Cvars(edh.ClientCvars), 1, edh.ClientRandState, loop, inputs.HeaderBytes,
                                  VmReplay.NumBuiltins);
        int spawn = 0, hashed = 0, clientHashed = 0, nextClient = 0;
        bool played = false;
        string? failure = null;
        StateHash? hash = null;
        var clientHash = new ClientHash();
        host.Spawned = sv =>
        {
            if (failure is not null)
                return;
            if (spawn >= edh.Spawns.Count)
            {
                failure = $"spawn {spawn}: the reference has no spawn record for it";
                return;
            }
            EdhFile.Spawn r = edh.Spawns[spawn++];
            hash ??= new StateHash(sv.Progs);
            StateHash.Lanes l = hash.Of(sv);
            if (sv.NumEdicts != r.NumEdicts)
                failure = $"spawn of {r.Map}: num_edicts {sv.NumEdicts}, the reference's {r.NumEdicts}";
            else if (l.Edicts != r.Edicts || l.GlobalsTyped != r.GlobalsTyped)
                failure = $"spawn of {r.Map}: the edicts or typed globals lane differs";
            else if (Xxh64.Hash(loop.Sent) != r.Messages)
                failure = $"spawn of {r.Map}: messages lane differs" + (emg is null ? "" : ": " + EmgFile.Difference(loop.Sent, emg.Spawns[spawn - 1]));
            loop.TakeSent();
        };
        // the reference's arguments (<run>.args): quake.rc in frame 0, as Host_Init inserted it, whose stuffcmds makes
        // each "+" one a command, up to the next '+' or '-' with the space before it, and the rest of quake.rc
        // (startdemos, a wait) comes after them in the buffer
        string argsPath = Path.ChangeExtension(edhPath, ".args");
        if (File.Exists(argsPath))
        {
            host.Cmd.CommandLine = File.ReadAllLines(argsPath);
            commands = "exec quake.rc\n";
        }
        host.AddText(Encoding.Latin1.GetBytes(commands));
        float time = host.HostFramerate();
        // the run's host frames: its last record's framecount (frames without a server write no frame record)
        int total = (int)Math.Max(edh.FrameRecords.Keys.DefaultIfEmpty(0u).Max(),
                                  edh.ClientFrames.Select(c => c.FrameCount).DefaultIfEmpty(0u).Max());
        for (int frame = 0; frame < total && failure is null; frame++)
        {
            host.Frame(time);
            if (failure is not null)
                break;
            if (Environment.GetEnvironmentVariable("EDICT_LISTENTRACE") is { Length: > 0 })
            {
                string st = $"sv {(host.Sv is { Active: true } ? 1 : 0)} cl {host.Cl!.State} signon {host.Cl.Signon}";
                if (st != _lastTrace)
                    TestContext.Current.TestOutputHelper?.WriteLine($"frame {frame} fc {host.FrameCount}: {st}");
                _lastTrace = st;
            }

            // the reference's run ends with a demo the client played: after that frame's client record, no frame record
            bool demoEnded = played && !host.Cl!.DemoPlayback;
            played |= host.Cl!.DemoPlayback;
            Server? sv = demoEnded ? null : host.Sv;
            if (!demoEnded && sv is not { Active: true } && edh.FrameRecords.ContainsKey((uint)host.FrameCount))
            {
                failure = $"frame {frame}: the reference's server ran frame {host.FrameCount}, the port's is not active";
                break;
            }
            if (sv is { Active: true })
            {
            if (!edh.FrameRecords.TryGetValue((uint)host.FrameCount, out EdhFile.Frame r))
            {
                failure = $"frame {frame}: the reference wrote no frame record {host.FrameCount}";
                break;
            }
            hash ??= new StateHash(sv.Progs);
            StateHash.Lanes l = hash.Of(sv);
            if (BitConverter.DoubleToInt64Bits(sv.Time) != BitConverter.DoubleToInt64Bits(r.Time))
                failure = $"frame {frame}: sv.time {sv.Time:R}, the reference's {r.Time:R}";
            else if (sv.NumEdicts != r.NumEdicts)
                failure = $"frame {frame}: num_edicts {sv.NumEdicts}, the reference's {r.NumEdicts}";
            else if (l.GlobalsTyped != r.GlobalsTyped)
                failure = $"frame {frame}: typed globals lane differs";
            else if (l.Edicts != r.Edicts)
                failure = $"frame {frame}: edicts lane differs";
            else if (Xxh64.Hash(loop.Sent) != r.Messages)
                failure = $"frame {frame}: messages lane differs"
                          + (emg is null ? "" : ": " + EmgFile.Difference(loop.Sent, emg.Frames[(uint)host.FrameCount]));
            else
                hashed++;
            loop.TakeSent();
            if (failure is not null)
                break;
            }
            ClientState cl = host.Cl!;
            if (cl.State == ClientState.CaConnected)
            {
                if (nextClient >= edh.ClientFrames.Count)
                {
                    failure = $"frame {frame}: the reference wrote no more client records";
                    break;
                }
                EdhFile.ClientFrame c = edh.ClientFrames[nextClient++];
                if (c.FrameCount != (uint)host.FrameCount)
                    failure = $"frame {frame}: client record for framecount {c.FrameCount}, the port is at {host.FrameCount}";
                else if (cl.Rand.Seed != c.RandState)
                    failure = $"frame {frame}: client rand state 0x{cl.Rand.Seed:X8}, the reference's 0x{c.RandState:X8}";
                else if (BitConverter.DoubleToInt64Bits(cl.Time) != BitConverter.DoubleToInt64Bits(c.Time))
                    failure = $"frame {frame}: cl.time {cl.Time:R}, the reference's {c.Time:R}";
                else if (cl.NumEntities != c.NumEntities)
                    failure = $"frame {frame}: cl.num_entities {cl.NumEntities}, the reference's {c.NumEntities}";
                else
                {
                    ClientHash.Lanes cll = clientHash.Of(cl);
                    if (cll.Client != c.Client)
                        failure = $"frame {frame}: client lane differs" + DumpDifference(clientHash.Streams(cl).Client, host.FrameCount, 'c');
                    else
                    {
                        for (int e = 0; e < c.PerEntity.Length && failure is null; e++)
                            if (cll.PerEntity[e] != c.PerEntity[e])
                                failure = $"frame {frame}: cl_entities[{e}] differs";
                        if (failure is null && cll.Entities != c.Entities)
                            failure = $"frame {frame}: client entities lane differs";
                        if (failure is null && cll.Effects != c.Effects)
                            failure = $"frame {frame}: client effects lane differs";
                    }
                }
                if (failure is not null)
                    break;
                clientHashed++;
            }
            if (demoEnded)
                break;
        }
        if (failure is null && nextClient != edh.ClientFrames.Count)
            failure = $"the port's client wrote {nextClient} records, the reference {edh.ClientFrames.Count}";
        frames = hashed;
        clientFrames = clientHashed;
        return failure;
    }

    /// <summary>
    /// With EDICT_CLIENTDUMP naming the reference's -edictclientdump of the run: where the port's stream of this kind
    /// differs from the reference's for this framecount (offset and the bytes there), for localizing a difference.
    /// </summary>
    internal static string DumpDifference(byte[] port, int frameCount, char kind)
    {
        string? path = Environment.GetEnvironmentVariable("EDICT_CLIENTDUMP");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return "";
        byte[] d = File.ReadAllBytes(path);
        for (int p = 0; p + 13 <= d.Length;)
        {
            int fc = BitConverter.ToInt32(d, p), len = BitConverter.ToInt32(d, p + 9);
            if (fc == frameCount && d[p + 4] == kind)
            {
                var r = new ReadOnlySpan<byte>(d, p + 13, len);
                int i = 0;
                while (i < r.Length && i < port.Length && r[i] == port[i])
                    i++;
                string Hex(ReadOnlySpan<byte> s) => Convert.ToHexString(s[i..Math.Min(s.Length, i + 12)]);
                return $"; first difference at byte {i} of {len} (port {port.Length}): port {Hex(port)}, reference {Hex(r)}";
            }
            p += 13 + len;
        }
        return $"; no '{kind}' stream for framecount {frameCount} in {path}";
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Listen_server_hashes_as_the_reference_every_frame(string map)
    {
        string? d = FirstDivergence(map, out int frames, out int clientFrames);
        Assert.True(d is null, $"{map}, after {frames} server and {clientFrames} client frames hashed alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{map}: {frames} server and {clientFrames} client frames hashed as the reference");
    }
}
