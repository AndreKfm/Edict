// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Edict.Engine;

namespace Edict.Harness;

/// <summary>
/// The tape network driver of the reference (ref/src/edict_tape.c) for the port: the client connections of an input
/// tape (.etp, tools/tapegen.py; version 1 one client, version 2 up to 16), each fed the tape's records of its client;
/// a record tagged with frame f is delivered when the server reads that client during a host frame with
/// host_framecount ≥ f. Version 2 also has read errors, failing sends and connections that cannot send (busy).
/// Open-loop, as in C. What the server sends is kept in the stream the messages lane hashes (ref/src/edict_hash.c:
/// per message u8 kind, the type plus 16 times the client, u32 length, the bytes) until the harness takes it at a
/// state record.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class TapeNet : INetServer, IFrameCounted
{
    private const int TypeConnect = 0, TypeError = 3, TypeSendFail = 4, TypeBusy = 5, TypeReady = 6, Clients = 16;

    private static readonly SysErrorException ConnectWhileConnected = new("Tape: a client connects again while connected");
    private static readonly SysErrorException BadRecord = new("Tape: bad record type");
    private static readonly SysErrorException Truncated = new("Tape: truncated record");
    private static readonly SysErrorException BadClient = new("Tape: a client number of 16 or more");
    private static readonly SysErrorException SentOverflow = new("Tape: message stream overflow");
    private const int SentSize = 1 << 20;            // SENT_SIZE

    /// <summary>A client's connection (a qsocket_t of the tape driver).</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    private sealed class Connection(TapeNet tape, int client) : IClientConnection
    {
        public readonly int Client = client;
        public int Pos;                          // the client's next record, or past it
        public bool Open, SendFail, Busy;

        public int GetMessage(SizeBuf netMessage) => tape.GetMessage(this, netMessage);

        public int SendMessage(SizeBuf data) => tape.Send(this, 1, data);

        public int SendUnreliableMessage(SizeBuf data) => tape.Send(this, 2, data);

        public bool CanSendMessage() => !Busy;

        public void Close() => Open = false;

        public ReadOnlySpan<byte> Address => "tape"u8;
    }

    private readonly byte[] _tape;
    private readonly int _version, _head;
    private int _connectPos = 16;
    private readonly Connection[] _clients;
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
        if (_tape.Length < 16 || !_tape.AsSpan(0, 4).SequenceEqual("ETP1"u8))
            throw new InvalidDataException($"{path}: not a tape");
        _version = (int)BinaryPrimitives.ReadUInt32LittleEndian(_tape.AsSpan(4));
        if (_version is not (1 or 2))
            throw new InvalidDataException($"{path}: not a version 1 or 2 tape");
        _head = _version == 1 ? 7 : 8;
        _clients = new Connection[Clients];
        for (int i = 0; i < Clients; i++)
            _clients[i] = new Connection(this, i);
    }

    /// <summary>The record at <paramref name="pos"/>; false past the end.</summary>
    private bool RecordAt(int pos, out uint frame, out int type, out int client, out int length)
    {
        frame = 0;
        type = client = length = 0;
        if (pos + _head > _tape.Length)
            return false;
        frame = (uint)(_tape[pos] | (_tape[pos + 1] << 8) | (_tape[pos + 2] << 16) | (_tape[pos + 3] << 24));
        type = _tape[pos + 4];
        client = _version == 1 ? 0 : _tape[pos + 5];
        length = _tape[pos + _head - 2] | (_tape[pos + _head - 1] << 8);
        if (pos + _head + length > _tape.Length)
            throw Truncated;
        if (client >= Clients)
            throw BadClient;
        return true;
    }

    /// <summary>The next due connect record, in file order: its client gets the connection, its records start after it.</summary>
    public IClientConnection? CheckNewConnections()
    {
        while (RecordAt(_connectPos, out uint frame, out int type, out int client, out int length))
        {
            if (frame > (uint)FrameCount)
                return null;
            _connectPos += _head + length;
            if (type != TypeConnect)
                continue;
            Connection c = _clients[client];
            if (c.Open)
                throw ConnectWhileConnected;
            c.Open = true;
            c.Pos = _connectPos;
            c.SendFail = c.Busy = false;
            return c;
        }
        return null;
    }

    private int GetMessage(Connection c, SizeBuf netMessage)
    {
        while (RecordAt(c.Pos, out uint frame, out int type, out int client, out int length))
        {
            if (client != c.Client)
            {
                c.Pos += _head + length;    // another client's
                continue;
            }
            if (type == TypeConnect || frame > (uint)FrameCount)
                return 0;                   // the next connection's, or not yet
            int data = c.Pos + _head;
            c.Pos += _head + length;
            switch (type)
            {
                case 1:
                case 2:
                    fixed (byte* d = _tape)
                    {
                        netMessage.Clear();
                        netMessage.Write(d + data, length);
                    }
                    return type;
                case TypeError:
                    return -1;
                case TypeSendFail:
                    c.SendFail = true;
                    break;
                case TypeBusy:
                    c.Busy = true;
                    break;
                case TypeReady:
                    c.Busy = false;
                    break;
                default:
                    throw BadRecord;
            }
        }
        return 0;
    }

    private int Send(Connection c, int type, SizeBuf data)
    {
        if (c.SendFail)
            return -1;
        int n = data.CurSize;
        if (_sentLength + 5 + n > SentSize)
            throw SentOverflow;
        fixed (byte* s = _sent)
        {
            byte* m = s + _sentLength;
            m[0] = (byte)(type + 16 * c.Client);
            for (int i = 0; i < 4; i++)
                m[1 + i] = (byte)(n >> (8 * i));
            new ReadOnlySpan<byte>(data.Data, n).CopyTo(new Span<byte>(m + 5, n));
        }
        _sentLength += 5 + n;
        return 1;
    }
}
