// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// The P5 gate (docs/PLAN.md): the port's client plays each demo of tools/demos.sh (what a corpus or long run's
/// server sent) by itself, from the reference's inputs (the 'I' record: the rand state before frame 0, the cvars
/// after it), and after every host frame its state must hash as the reference's client record: the rand state first,
/// then the client lane, each entity, the entities lane and the effects lane. The run ends with the demo, as the
/// reference's does.
/// </summary>
public sealed class ClientGateTests
{
    /// <summary>The client corpus (data/client, or EDICT_CLIENT): one .edh per demo.</summary>
    public static string Directory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_CLIENT");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, "client") : dir;
        }
    }

    public static IEnumerable<TheoryDataRow<string>> Runs => CorpusRuns.OrSkip(
        (System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.edh") : [])
        .Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).Select(r => new TheoryDataRow<string>(r)),
        Directory, "tools/demos.sh");

    /// <summary>The first difference, or null; <paramref name="frames"/> gets the number of client frames that hashed alike.</summary>
    internal static string? FirstDivergence(string run, out int frames)
    {
        var edh = new EdhFile(Path.Combine(Directory, run + ".edh"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using var host = new ClientHost(files, new Cvars(edh.ClientCvars), edh.ClientRandState);
        var hash = new ClientHash();
        host.AddText(Encoding.Latin1.GetBytes($"playdemo edict/{run}\n"));
        float time = host.HostFramerate();
        frames = 0;
        int next = 0;
        for (int frame = 0; frame < 100_000; frame++)
        {
            host.Frame(time);
            if (host.Ended)
                break;
            if (!host.Cl.DemoPlayback)
                continue;
            if (next >= edh.ClientFrames.Count)
                return $"frame {frame}: the reference wrote no more client records ({edh.ClientFrames.Count})";
            EdhFile.ClientFrame r = edh.ClientFrames[next++];
            if (r.FrameCount != (uint)host.FrameCount)
                return $"frame {frame}: host_framecount {host.FrameCount}, the reference's record has {r.FrameCount}";
            ClientState cl = host.Cl;
            if (cl.Rand.Seed != r.RandState)
                return $"frame {frame}: rand state 0x{cl.Rand.Seed:X8}, the reference's 0x{r.RandState:X8}";
            if (BitConverter.DoubleToInt64Bits(cl.Time) != BitConverter.DoubleToInt64Bits(r.Time))
                return $"frame {frame}: cl.time {cl.Time:R}, the reference's {r.Time:R}";
            if (cl.NumEntities != r.NumEntities)
                return $"frame {frame}: cl.num_entities {cl.NumEntities}, the reference's {r.NumEntities}";
            ClientHash.Lanes l = hash.Of(cl);
            if (l.Client != r.Client)
                return $"frame {frame}: client lane differs";
            for (int e = 0; e < r.PerEntity.Length; e++)
                if (l.PerEntity[e] != r.PerEntity[e])
                    return $"frame {frame}: cl_entities[{e}] differs";
            if (l.Entities != r.Entities)
                return $"frame {frame}: entities lane differs (statics, temp entities or the gun model)";
            if (l.Effects != r.Effects)
                return $"frame {frame}: effects lane differs";
            frames++;
        }
        if (next != edh.ClientFrames.Count)
            return $"the demo ended after {next} client records, the reference wrote {edh.ClientFrames.Count}";
        return null;
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public void Client_hashes_as_the_reference_every_frame(string run)
    {
        string? d = FirstDivergence(run, out int frames);
        Assert.True(d is null, $"{run}, after {frames} client frames hashed alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {frames} client frames hashed as the reference");
    }
}
