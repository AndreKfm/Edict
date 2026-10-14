// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Edict.Harness;

/// <summary>
/// A reference .edh file (ref/src/edict_hash.c, version 3 to 6): its spawn records, the frame-0 states after
/// ED_LoadFromFile, each with the map it belongs to, its frame records by host_framecount, (version 4) the client
/// records of a demo playback or a listen server, (version 5) the screen's presents and (version 6) the sound build's
/// DMA buffer after each host frame.
/// </summary>
public sealed class EdhFile
{
    public readonly record struct Spawn(string Map, uint FrameCount, double Time, int NumEdicts, ulong Edicts,
                                        ulong GlobalsTyped, ulong GlobalsRaw, ulong Messages, ulong[] Entities);

    /// <summary>The four lanes of a frame record (the state after a host frame, the messages sent during it).</summary>
    public readonly record struct Frame(double Time, int NumEdicts, ulong Edicts, ulong GlobalsTyped, ulong GlobalsRaw,
                                        ulong Messages);

    /// <summary>A client record: the client's state after a host frame of a demo playback (P5).</summary>
    public readonly record struct ClientFrame(uint FrameCount, double Time, int NumEntities, uint RandState, ulong Client,
                                              ulong Entities, ulong Effects, ulong[] PerEntity);

    /// <summary>A screen record: the frame buffer and palette at a VID_Update (P6); a frame can present more than once.</summary>
    public readonly record struct ScreenPresent(uint FrameCount, int Width, int Height, ulong FrameBuffer, ulong Palette);

    /// <summary>The screen records, in order.</summary>
    public IReadOnlyList<ScreenPresent> Screens { get; }

    /// <summary>A sound record: the DMA buffer of the sound build after a host frame, with paintedtime and soundtime (P6).</summary>
    public readonly record struct SoundFrame(uint FrameCount, uint PaintedTime, uint SoundTime, ulong Buffer);

    /// <summary>The sound records, in order.</summary>
    public IReadOnlyList<SoundFrame> Sounds { get; }

    /// <summary>The client's inputs ('I'): the rand state before the first host frame, and the cvars.</summary>
    public uint ClientRandState { get; private set; }

    public IReadOnlyList<(byte[] Name, byte[] Value)> ClientCvars { get; private set; } = [];

    public IReadOnlyList<Spawn> Spawns { get; }

    /// <summary>The client records, in order.</summary>
    public IReadOnlyList<ClientFrame> ClientFrames { get; }
    public int Frames { get; }

    /// <summary>The frame records by host_framecount (a respawned map keeps counting).</summary>
    public IReadOnlyDictionary<uint, Frame> FrameRecords { get; }

    public EdhFile(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 12 || !d.AsSpan(0, 4).SequenceEqual("EDH1"u8) || BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)) is not (3 or 4 or 5 or 6))
            throw new InvalidDataException($"{path}: not an EDH1 version 3 to 6 file (run tools/captures.sh)");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8));
        var spawns = new List<Spawn>();
        var frameRecords = new Dictionary<uint, Frame>();
        var clients = new List<ClientFrame>();
        var screens = new List<ScreenPresent>();
        var sounds = new List<SoundFrame>();
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
                case (byte)'I':
                {
                    ClientRandState = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
                    int nv = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
                    p += 8;
                    var cvars = new List<(byte[], byte[])>(nv);
                    for (int i = 0; i < nv; i++)
                    {
                        int e = Array.IndexOf(d, (byte)0, p);
                        byte[] name = d[p..e];
                        p = e + 1;
                        e = Array.IndexOf(d, (byte)0, p);
                        cvars.Add((name, d[p..e]));
                        p = e + 1;
                    }
                    ClientCvars = cvars;
                    break;
                }
                case (byte)'C':
                {
                    uint fc = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
                    double t = BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(p + 4));
                    int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 12));
                    uint rs = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 16));
                    ulong lc = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 20));
                    ulong le = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 28));
                    ulong lf = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 36));
                    p += 44;
                    var ents = new ulong[(flags & 1) != 0 ? n : 0];
                    for (int i = 0; i < ents.Length; i++, p += 8)
                        ents[i] = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p));
                    clients.Add(new ClientFrame(fc, t, n, rs, lc, le, lf, ents));
                    break;
                }
                case (byte)'V':
                    screens.Add(new ScreenPresent(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)),
                        BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 8)),
                        BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 12)), BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 20))));
                    p += 28;
                    break;
                case (byte)'A':
                    sounds.Add(new SoundFrame(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)),
                        BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 8)),
                        BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 12))));
                    p += 20;
                    break;
                default:
                    throw new InvalidDataException($"{path}: bad record '{(char)k}' at {p - 1}");
            }
        }
        Spawns = spawns;
        ClientFrames = clients;
        Screens = screens;
        Sounds = sounds;
        Frames = frames;
        FrameRecords = frameRecords;
    }
}
