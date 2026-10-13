// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Edict.Engine;

namespace Edict.Harness;

/// <summary>
/// The tape network driver of the reference (ref/src/edict_tape.c) for the port: one client connection fed from an
/// input tape (.etp, tools/tapegen.py); a record tagged with frame f is delivered when the server reads during a
/// host frame with host_framecount ≥ f. What the server sends is discarded. Open-loop, as in C.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class TapeNet : INetServer, IClientConnection, IFrameCounted
{
    private const int Connect = 0;

    private static readonly SysErrorException SecondConnect = new("Tape: second connect record");
    private static readonly SysErrorException BadRecord = new("Tape: bad record type");
    private static readonly SysErrorException Truncated = new("Tape: truncated record");

    private readonly byte[] _tape;
    private int _pos = 16;
    private bool _connected;

    /// <summary>host_framecount while the server reads.</summary>
    public int FrameCount { get; set; }

    [NoAllocUnchecked("load path: reads the tape")]
    public TapeNet(string path)
    {
        _tape = GC.AllocateArray<byte>((int)new FileInfo(path).Length, pinned: true);
        using (FileStream f = File.OpenRead(path))
            f.ReadExactly(_tape);
        if (_tape.Length < 16 || !_tape.AsSpan(0, 4).SequenceEqual("ETP1"u8) || BinaryPrimitives.ReadUInt32LittleEndian(_tape.AsSpan(4)) != 1)
            throw new InvalidDataException($"{path}: not a version 1 tape");
    }

    private bool Next(out int type, out int length)
    {
        type = length = 0;
        if (_pos + 7 > _tape.Length)
            return false;
        uint frame = (uint)(_tape[_pos] | (_tape[_pos + 1] << 8) | (_tape[_pos + 2] << 16) | (_tape[_pos + 3] << 24));
        if (frame > (uint)FrameCount)
            return false;
        type = _tape[_pos + 4];
        length = _tape[_pos + 5] | (_tape[_pos + 6] << 8);
        if (_pos + 7 + length > _tape.Length)
            throw Truncated;
        return true;
    }

    public IClientConnection? CheckNewConnections()
    {
        if (!Next(out int type, out int length) || type != Connect)
            return null;
        if (_connected)
            throw SecondConnect;
        _pos += 7 + length;
        _connected = true;
        return this;
    }

    public int GetMessage(SizeBuf netMessage)
    {
        if (!Next(out int type, out int length) || type == Connect)
            return 0;
        if (type != 1 && type != 2)
            throw BadRecord;
        fixed (byte* d = _tape)
        {
            netMessage.Clear();
            netMessage.Write(d + _pos + 7, length);
        }
        _pos += 7 + length;
        return type;
    }
}
