// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Edict.Harness;

/// <summary>
/// A reference .edh file (ref/src/edict_hash.c, version 3): its spawn records, the frame-0 states after
/// ED_LoadFromFile, each with the map it belongs to, and its frame records by host_framecount.
/// </summary>
public sealed class EdhFile
{
    public readonly record struct Spawn(string Map, uint FrameCount, double Time, int NumEdicts, ulong Edicts,
                                        ulong GlobalsTyped, ulong GlobalsRaw, ulong Messages, ulong[] Entities);

    /// <summary>The four lanes of a frame record (the state after a host frame, the messages sent during it).</summary>
    public readonly record struct Frame(double Time, int NumEdicts, ulong Edicts, ulong GlobalsTyped, ulong GlobalsRaw,
                                        ulong Messages);

    public IReadOnlyList<Spawn> Spawns { get; }
    public int Frames { get; }

    /// <summary>The frame records by host_framecount (a respawned map keeps counting).</summary>
    public IReadOnlyDictionary<uint, Frame> FrameRecords { get; }

    public EdhFile(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 12 || !d.AsSpan(0, 4).SequenceEqual("EDH1"u8) || BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)) != 3)
            throw new InvalidDataException($"{path}: not an EDH1 version 3 file (run tools/captures.sh)");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8));
        var spawns = new List<Spawn>();
        var frameRecords = new Dictionary<uint, Frame>();
        string map = "";
        int p = 12, frames = 0;
        while (p < d.Length)
        {
            byte k = d[p++];
            switch (k)
            {
                case (byte)'M':
                    map = Encoding.Latin1.GetString(d, p, 64).TrimEnd('\0');
                    p += 64 + 12;
                    break;
                case (byte)'S':
                case (byte)'F':
                {
                    uint fc = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
                    double t = BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(p + 4));
                    int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 12));
                    ulong le = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 16));
                    ulong lt = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 24));
                    ulong lr = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 32));
                    ulong lm = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 40));
                    p += 48;
                    bool perEntity = k == (byte)'S' || (flags & 1) != 0;
                    var ents = new ulong[perEntity ? n : 0];
                    for (int i = 0; i < ents.Length; i++, p += 8)
                        ents[i] = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p));
                    if (k == (byte)'S')
                        spawns.Add(new Spawn(map, fc, t, n, le, lt, lr, lm, ents));
                    else
                    {
                        frames++;
                        frameRecords[fc] = new Frame(t, n, le, lt, lr, lm);
                    }
                    break;
                }
                default:
                    throw new InvalidDataException($"{path}: bad record '{(char)k}' at {p - 1}");
            }
        }
        Spawns = spawns;
        Frames = frames;
        FrameRecords = frameRecords;
    }
}
