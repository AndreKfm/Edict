// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Edict.Harness;

/// <summary>
/// A reference .emg file (ref/src/edict_hash.c, -edictmessages): the messages the messages lane of each .edh record
/// hashes, kept in the lane's own format (per message u8 kind, u32 length, the bytes), so that a difference in the
/// lane can be shown down to the message and the byte.
/// </summary>
public sealed class EmgFile
{
    /// <summary>The messages of each spawn record, in order.</summary>
    public IReadOnlyList<byte[]> Spawns { get; }

    /// <summary>The messages of each frame record, by host_framecount.</summary>
    public IReadOnlyDictionary<uint, byte[]> Frames { get; }

    public EmgFile(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 8 || !d.AsSpan(0, 4).SequenceEqual("EMG1"u8) || BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)) != 1)
            throw new InvalidDataException($"{path}: not an EMG1 version 1 file");
        var spawns = new List<byte[]>();
        var frames = new Dictionary<uint, byte[]>();
        var current = new MemoryStream();
        int p = 8;
        while (p < d.Length)
        {
            byte kind = d[p];
            uint fc = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 1));
            int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 5));
            switch (kind)
            {
                case 1:
                case 2:
                    current.WriteByte(kind);            // kind and length, as the lane has them
                    current.Write(d, p + 5, 4 + n);
                    break;
                case (byte)'S':
                    spawns.Add(current.ToArray());
                    current.SetLength(0);
                    break;
                case (byte)'F':
                    frames[fc] = current.ToArray();
                    current.SetLength(0);
                    break;
                default:
                    throw new InvalidDataException($"{path}: bad record {kind} at {p}");
            }
            p += 9 + n;
        }
        Spawns = spawns;
        Frames = frames;
    }

    /// <summary>The messages of a lane stream: kind and bytes.</summary>
    public static List<(int Kind, byte[] Data)> Split(ReadOnlySpan<byte> stream)
    {
        var list = new List<(int, byte[])>();
        int p = 0;
        while (p + 5 <= stream.Length)
        {
            int n = BinaryPrimitives.ReadInt32LittleEndian(stream[(p + 1)..]);
            list.Add((stream[p], stream.Slice(p + 5, n).ToArray()));
            p += 5 + n;
        }
        return list;
    }

    /// <summary>Where two lane streams differ first: the message, its kind and length on both sides, the byte.</summary>
    public static string Difference(ReadOnlySpan<byte> port, ReadOnlySpan<byte> reference)
    {
        List<(int Kind, byte[] Data)> a = Split(port), b = Split(reference);
        for (int m = 0; m < Math.Max(a.Count, b.Count); m++)
        {
            if (m >= a.Count)
                return $"the reference sent {b.Count} messages, the port {a.Count}; the next is {Describe(b[m])}";
            if (m >= b.Count)
                return $"the port sent {a.Count} messages, the reference {b.Count}; the next is {Describe(a[m])}";
            if (a[m].Kind != b[m].Kind)
                return $"message {m}: the port's is {Describe(a[m])}, the reference's {Describe(b[m])}";
            int i = 0;
            while (i < a[m].Data.Length && i < b[m].Data.Length && a[m].Data[i] == b[m].Data[i])
                i++;
            if (i < a[m].Data.Length || i < b[m].Data.Length)
                return $"message {m} ({Describe(a[m])}; the reference's {b[m].Data.Length} bytes), first difference at byte {i}:"
                       + $" port {Hex(a[m].Data, i)}, reference {Hex(b[m].Data, i)}; before it {Hex(a[m].Data, Math.Max(0, i - 16), i)}";
        }
        return "the messages are the same";
    }

    private static string Describe((int Kind, byte[] Data) m) =>
        $"{(m.Kind == 1 ? "reliable" : "unreliable")}, {m.Data.Length} bytes";

    private static string Hex(byte[] d, int from, int to = -1)
    {
        if (to < 0)
            to = Math.Min(d.Length, from + 16);
        var s = new StringBuilder();
        for (int i = from; i < to; i++)
            s.Append(d[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture)).Append(i + 1 < to ? " " : "");
        return from >= d.Length ? "(end)" : s.ToString();
    }
}
