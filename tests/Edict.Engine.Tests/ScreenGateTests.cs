// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// The gate of P6, step 3 (docs/PLAN.md): the port's client starts as the reference does (Host_Init's console lines,
/// quake.rc in the first frame, the "+" commands of the command line), plays each demo of data/screen2d with the
/// screen, the 3D view's rectangle cleared (-edict2d), and every present of the frame buffer must hash as the
/// reference's screen record — frame buffer and palette, in order — and every client record as the reference's. The
/// cvars after the first frame must be the reference's ('I' record), quake.rc's work included.
/// </summary>
public sealed class ScreenGateTests
{
    /// <summary>The 2D corpus (tools/screens.sh with EXTRA=-edict2d: data/screen2d, or EDICT_SCREEN2D).</summary>
    public static string Directory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_SCREEN2D");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, "screen2d") : dir;
        }
    }

    /// <summary>The runs, or only the one EDICT_SCREENRUN names (for localizing a difference).</summary>
    public static IEnumerable<TheoryDataRow<string>> Runs =>
        (System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.edh") : [])
        .Select(f => Path.GetFileNameWithoutExtension(f))
        .Where(r => Environment.GetEnvironmentVariable("EDICT_SCREENRUN") is not { Length: > 0 } only || r == only)
        .Order(StringComparer.Ordinal).Select(r => new TheoryDataRow<string>(r));

    /// <summary>Compares every present with the reference's screen records, in order; keeps the first difference.</summary>
    private sealed class Sink(IReadOnlyList<EdhFile.ScreenPresent> reference, ClientHost host) : IScreenSink
    {
        public int Next;
        public string? Failure;
        public byte[]? FailedFrame;

        public void Present(ReadOnlySpan<byte> frameBuffer, ReadOnlySpan<byte> palette)
        {
            if (Failure is not null)
                return;
            if (Next >= reference.Count)
            {
                Failure = $"present {Next} (framecount {host.FrameCount}): the reference presented {reference.Count} times";
                return;
            }
            EdhFile.ScreenPresent r = reference[Next];
            if (r.FrameCount != (uint)host.FrameCount)
                Failure = $"present {Next}: framecount {host.FrameCount}, the reference's {r.FrameCount}";
            else if (Xxh64.Hash(frameBuffer) != r.FrameBuffer)
            {
                Failure = $"present {Next} (framecount {r.FrameCount}): frame buffer differs";
                FailedFrame = frameBuffer.ToArray();
            }
            else if (Xxh64.Hash(palette) != r.Palette)
                Failure = $"present {Next} (framecount {r.FrameCount}): palette differs";
            Next++;
        }
    }

    internal static string? FirstDivergence(string run, out int presents, out int clientFrames)
    {
        presents = clientFrames = 0;
        var edh = new EdhFile(Path.Combine(Directory, run + ".edh"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        string cmdline = Encoding.Latin1.GetString(edh.ClientCvars.First(c => Encoding.Latin1.GetString(c.Name) == "cmdline").Value);
        // the reference's arguments are its cmdline (com_cmdline: argv joined by spaces, cut at 255 characters)
        if (cmdline.Length >= 255)
            return $"cmdline \"{cmdline}\" may be cut: its arguments are not known";
        string[] args = cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
        using var host = new ClientHost(files, edh.ClientRandState, args, cmdline, withScreen: true);
        host.Screen!.Only2D = true;
        var sink = new Sink(edh.Screens, host);
        host.Screen.Sink = sink;
        var hash = new ClientHash();
        float time = float.Parse(ClientHost.HarnessFrameRate, System.Globalization.CultureInfo.InvariantCulture);
        presents = clientFrames = 0;
        int next = 0;
        string? failure = null;
        for (int frame = 0; frame < 100_000 && failure is null; frame++)
        {
            try
            {
                host.Frame(time);
            }
            catch (Exception e) when (sink.Failure is null)
            {
                failure = $"frame {frame} (present {sink.Next}): {e.GetType().Name}: {e.Message} at "
                          + string.Join(" < ", (e.StackTrace ?? "").Split('\n').Take(4).Select(l => l.Trim().Split(" in ")[0]));
                break;
            }
            if (sink.Failure is not null)
                break;
            if (Environment.GetEnvironmentVariable("EDICT_SCREENTRACE") is { Length: > 0 } && frame < 8)
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"frame {frame}: time {host.Cl.Time:R} old {host.Cl.OldTime:R} vel {host.Cl.Velocity.X:R},{host.Cl.Velocity.Y:R} onground {host.Cl.OnGround} gun {host.Cl.ViewEnt.Origin.X:R},{host.Cl.ViewEnt.Origin.Y:R},{host.Cl.ViewEnt.Origin.Z:R} ent {host.Cl.Entities[host.Cl.ViewEntity].Origin.Z:R}");
            if (frame == 0)
                failure = CvarDifference(host.Cvars, edh.ClientCvars);
            if (host.Ended)
                break;
            if (!host.Cl.DemoPlayback || failure is not null)
                continue;
            if (next >= edh.ClientFrames.Count)
            {
                failure = $"frame {frame}: the reference wrote no more client records ({edh.ClientFrames.Count})";
                break;
            }
            EdhFile.ClientFrame r = edh.ClientFrames[next++];
            ClientState cl = host.Cl;
            ClientHash.Lanes l = hash.Of(cl);
            if (r.FrameCount != (uint)host.FrameCount)
                failure = $"frame {frame}: host_framecount {host.FrameCount}, the reference's record has {r.FrameCount}";
            else if (cl.Rand.Seed != r.RandState)
                failure = $"frame {frame}: rand state 0x{cl.Rand.Seed:X8}, the reference's 0x{r.RandState:X8}";
            else if (BitConverter.DoubleToInt64Bits(cl.Time) != BitConverter.DoubleToInt64Bits(r.Time))
                failure = $"frame {frame}: cl.time {cl.Time:R}, the reference's {r.Time:R}";
            else if (cl.NumEntities != r.NumEntities)
                failure = $"frame {frame}: cl.num_entities {cl.NumEntities}, the reference's {r.NumEntities}";
            else if (l.Client != r.Client)
                failure = $"frame {frame}: client lane differs" + ListenGateTests.DumpDifference(hash.Streams(cl).Client, host.FrameCount, 'c');
            else if (l.Entities != r.Entities)
            {
                byte[] stream = hash.Streams(cl).EntitiesLane;
                failure = $"frame {frame}: entities lane differs" + ListenGateTests.DumpDifference(stream, host.FrameCount, 'E');
                if (Environment.GetEnvironmentVariable("EDICT_PORTSTREAM") is { Length: > 0 } ps)
                    File.WriteAllBytes(ps, stream);
            }
            else if (l.Effects != r.Effects)
                failure = $"frame {frame}: effects lane differs" + ListenGateTests.DumpDifference(hash.Streams(cl).Effects, host.FrameCount, 'f');
            else
                clientFrames++;
        }
        presents = sink.Next - (sink.Failure is null ? 0 : 1);
        failure ??= sink.Failure;
        if (sink.FailedFrame is { } f && Environment.GetEnvironmentVariable("EDICT_PORTDUMP") is { Length: > 0 } dump)
            WriteDump(dump, (uint)host.FrameCount, f, host.Screen.Palette);
        if (failure is null && sink.Next != edh.Screens.Count)
            failure = $"the port presented {sink.Next} times, the reference {edh.Screens.Count}";
        if (failure is null && next != edh.ClientFrames.Count)
            failure = $"the demo ended after {next} client records, the reference wrote {edh.ClientFrames.Count}";
        return failure;
    }

    /// <summary>The port's cvars against the reference's after the first frame: the first that differs, or null.</summary>
    private static unsafe string? CvarDifference(Cvars cvars, IReadOnlyList<(byte[] Name, byte[] Value)> reference)
    {
        foreach ((byte[] name, byte[] value) in reference)
        {
            byte[] z = Cmd.Z(name);
            fixed (byte* n = z)
            {
                if (!cvars.Exists(n))
                    return $"after frame 0: the port has no cvar {Encoding.Latin1.GetString(name)}";
                byte* s = cvars.VariableString(n);
                int len = 0;
                while (s[len] != 0)
                    len++;
                if (!new ReadOnlySpan<byte>(s, len).SequenceEqual(value))
                    return $"after frame 0: cvar {Encoding.Latin1.GetString(name)} is \"{Encoding.Latin1.GetString(new ReadOnlySpan<byte>(s, len))}\", the reference's \"{Encoding.Latin1.GetString(value)}\"";
            }
        }
        return null;
    }

    /// <summary>One present in -edictscreendump's format (tools/screen.py diff compares it with the reference's).</summary>
    private static void WriteDump(string path, uint frameCount, byte[] frame, ReadOnlySpan<byte> palette)
    {
        using var f = File.Create(path);
        void Record(byte kind, ReadOnlySpan<byte> data)
        {
            Span<byte> h = stackalloc byte[9];
            BitConverter.TryWriteBytes(h, frameCount);
            h[4] = kind;
            BitConverter.TryWriteBytes(h[5..], data.Length);
            f.Write(h);
            f.Write(data);
        }
        Record((byte)'V', frame);
        Record((byte)'P', palette);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public void Screen_2d_presents_as_the_reference(string run)
    {
        string? d = FirstDivergence(run, out int presents, out int clientFrames);
        Assert.True(d is null, $"{run}, after {presents} presents and {clientFrames} client frames alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {presents} presents and {clientFrames} client frames as the reference");
    }
}
