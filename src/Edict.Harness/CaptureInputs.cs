// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Edict.Harness;

/// <summary>What a map's first spawn starts from, as the capture recorded it.</summary>
public sealed record SpawnInputs(string Map, int HeaderBytes, int MaxClients, List<(byte[] Name, byte[] Value)> Cvars,
                                 uint RandSeed);

/// <summary>
/// The inputs the port's host takes from a reference capture (.ecp, ref/src/edict_capture.c): the corpus is in
/// EDICT_CAPTURES (default data/captures next to the trace corpus).
/// </summary>
public static class CaptureInputs
{
    public static string Directory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_CAPTURES");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(TraceCorpus.Directory)!, "captures") : dir;
        }
    }

    /// <summary>
    /// The first map record of <paramref name="file"/> (header size, maxclients, cvars) and the rand seed at its first
    /// engine entry (the world's spawn function: nothing draws from rand before it in SV_SpawnServer).
    /// </summary>
    public static SpawnInputs FirstSpawn(string file)
    {
        byte[] d = File.ReadAllBytes(file);
        int p = 8;
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at, 4));
        byte[] Z()
        {
            int e = Array.IndexOf(d, (byte)0, p);
            byte[] s = d[p..e];
            p = e + 1;
            return s;
        }
        if (d[p++] != (byte)'M')
            throw new InvalidDataException($"{file}: no map record first");
        string map = Encoding.Latin1.GetString(d, p, 64).TrimEnd('\0');
        p += 64;
        int header = I(p + 12), maxClients = I(p + 28);
        p += 32 + 4 * maxClients;           // then the client name offsets (version 9)
        int numCvars = I(p);
        p += 4;
        var cvars = new List<(byte[], byte[])>();
        for (int i = 0; i < numCvars; i++)
            cvars.Add((Z(), Z()));
        while (d[p] == (byte)'F')
        {
            p += 5;
            Z();
        }
        if (d[p] != (byte)'E')
            throw new InvalidDataException($"{file}: record '{(char)d[p]}' before the first engine entry");
        uint seed = (uint)I(p + 1 + 17);   // fnum, framecount, state, self, other, then rand_seed
        return new SpawnInputs(map, header, maxClients, cvars, seed);
    }
}
