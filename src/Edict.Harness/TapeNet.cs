// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Edict.Engine;

namespace Edict.Harness;

/// <summary>
/// The tape network driver of the reference (ref/src/edict_tape.c) for the port: one client connection fed from an
/// input tape (.etp, tools/tapegen.py); a record tagged with frame f is delivered when the server reads during a
/// host frame with host_framecount ≥ f. Open-loop, as in C. What the server sends is kept in the stream the messages
/// lane hashes (ref/src/edict_hash.c: per message u8 kind, 1 reliable or 2 unreliable, u32 length, the bytes) until
/// the harness takes it at a state record.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class TapeNet : INetServer, IClientConnection, IFrameCounted
{
    private const int Connect = 0;

    private static readonly SysErrorException SecondConnect = new("Tape: second connect record");
    private static readonly SysErrorException BadRecord = new("Tape: bad record type");
    private static readonly SysErrorException Truncated = new("Tape: truncated record");
    private static readonly SysErrorException SentOverflow = new("Tape: message stream overflow");
    private const int SentSize = 1 << 20;            // SENT_SIZE

    private readonly byte[] _tape;
    private int _pos = 16;
    private bool _connected;
    private readonly byte[] _sent;
    private int _sentLength;

    /// <summary>What was sent since the last <see cref="TakeSent"/>, in the messages lane's format.</summary>
    public ReadOnlySpan<byte> Sent
    {
        get
        {
            fixed (byte* s = _sent)
                return new ReadOnlySpan<byte>(s, _sentLength);
        }
    }

    /// <summary>Ends the messages of one state record: <see cref="Sent"/> starts empty again.</summary>
    public void TakeSent() => _sentLength = 0;

    /// <summary>host_framecount while the server reads.</summary>
    public int FrameCount { get; set; }

    [NoAllocUnchecked("load path: reads the tape")]
    public TapeNet(string path)
    {
        _tape = GC.AllocateArray<byte>((int)new FileInfo(path).Length, pinned: true);
        _sent = GC.AllocateArray<byte>(SentSize, pinned: true);
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

    private int Record(int kind, SizeBuf data)
    {
        int n = data.CurSize;
        if (_sentLength + 5 + n > SentSize)
            throw SentOverflow;
        fixed (byte* s = _sent)
        {
            byte* m = s + _sentLength;
            m[0] = (byte)kind;
            for (int i = 0; i < 4; i++)
                m[1 + i] = (byte)(n >> (8 * i));
            new ReadOnlySpan<byte>(data.Data, n).CopyTo(new Span<byte>(m + 5, n));
        }
        _sentLength += 5 + n;
        return 1;
    }

    public int SendMessage(SizeBuf data) => Record(1, data);

    public int SendUnreliableMessage(SizeBuf data) => Record(2, data);

    public bool CanSendMessage() => true;
}
