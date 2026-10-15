// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// net_loop.c: the loopback driver of a listen server, two sockets that write into each other's receive buffer
/// (NET_MAXMESSAGE bytes; per message a type byte, the length in two bytes, an alignment byte, the data, padded to
/// 4 bytes). A reliable message keeps its sender from sending another until the receiver has read it; an unreliable
/// one that does not fit is dropped. On driver 0 NET_GetMessage has no timeout. The server's sends can be logged in the
/// messages lane's format (per message u8 kind, u32 length, the bytes), for the harness.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class Loopback : INetServer
{
    private const int NetMaxMessage = 8192;

    private static readonly SysErrorException SendOverflow = new("Loop_SendMessage: overflow");
    private static readonly SysErrorException UnreliableOverflow =
        new("Loop_SendUnreliableMessage: its check leaves 3 bytes for the 4-byte header, and this message would write one past the buffer");
    private static readonly SysErrorException LogOverflow = new("Loopback: the send log is full");

    /// <summary>One end of the loop (a qsocket_t of driver 0).</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    public sealed class Socket : IClientConnection
    {
        internal readonly byte[] Receive;
        internal int ReceiveLength;
        internal bool CanSend;
        internal Socket? Peer;                       // driverdata
        internal readonly Loopback Owner;
        internal readonly bool IsServer;

        [NoAllocUnchecked("load path: the socket's buffer")]
        internal Socket(Loopback owner, bool isServer)
        {
            Owner = owner;
            IsServer = isServer;
            Receive = GC.AllocateArray<byte>(NetMaxMessage, pinned: true);
        }

        /// <summary>Loop_GetMessage: the next message's type (1, 2) into net_message, 0 if none is waiting.</summary>
        public int GetMessage(SizeBuf netMessage)
        {
            if (ReceiveLength == 0)
                return 0;
            int ret = Receive[0];
            int length = Receive[1] + (Receive[2] << 8);
            // alignment byte skipped here
            netMessage.Clear();
            fixed (byte* r = Receive)
                netMessage.Write(r + 4, length);
            length = IntAlign(length + 4);
            ReceiveLength -= length;
            if (ReceiveLength != 0)
                new ReadOnlySpan<byte>(Receive, length, ReceiveLength).CopyTo(new Span<byte>(Receive, 0, ReceiveLength));
            if (Peer != null && ret == 1)
                Peer.CanSend = true;
            return ret;
        }

        /// <summary>Loop_SendMessage; the harness logs the server's message before (edict_tape.c), a failing one too.</summary>
        public int SendMessage(SizeBuf data)
        {
            if (IsServer)
                Owner.Log(1, data);
            if (Peer is not { } peer)
                return -1;
            if (peer.ReceiveLength + data.CurSize + 4 > NetMaxMessage)
                throw SendOverflow;
            Write(peer, 1, data);
            CanSend = false;
            return 1;
        }

        /// <summary>Loop_SendUnreliableMessage: a message that does not fit is dropped (0).</summary>
        public int SendUnreliableMessage(SizeBuf data)
        {
            if (IsServer)
                Owner.Log(2, data);
            if (Peer is not { } peer)
                return -1;
            if (peer.ReceiveLength + data.CurSize + 1 + 2 > NetMaxMessage)    // sizeof(byte) + sizeof(short)
                return 0;
            if (peer.ReceiveLength + data.CurSize + 4 > NetMaxMessage)
                throw UnreliableOverflow;
            Write(peer, 2, data);
            return 1;
        }

        /// <summary>Loop_CanSendMessage.</summary>
        public bool CanSendMessage() => Peer != null && CanSend;

        /// <summary>The address Loop_Connect gives: "localhost" the client's end, "LOCAL" the server's.</summary>
        public ReadOnlySpan<byte> Address => IsServer ? "LOCAL"u8 : "localhost"u8;

        /// <summary>Loop_Close: the peer unlinked, both buffers emptied.</summary>
        public void Close()
        {
            if (Peer is { } peer)
                peer.Peer = null;
            Peer = null;
            ReceiveLength = 0;
            CanSend = true;
        }

        private static void Write(Socket peer, int type, SizeBuf data)
        {
            int at = peer.ReceiveLength;
            byte[] b = peer.Receive;
            b[at] = (byte)type;
            b[at + 1] = (byte)(data.CurSize & 0xff);
            b[at + 2] = (byte)(data.CurSize >> 8);
            // align: b[at + 3] is skipped, as in C
            new ReadOnlySpan<byte>(data.Data, data.CurSize).CopyTo(new Span<byte>(b, at + 4, data.CurSize));
            peer.ReceiveLength = IntAlign(at + data.CurSize + 4);
        }
    }

    private Socket? _client, _server;
    private bool _localConnectPending;
    private readonly byte[]? _log;
    private int _logLength;

    /// <param name="logSize">Bytes for the log of the server's sends, or 0 for none.</param>
    [NoAllocUnchecked("load path: the send log")]
    public Loopback(int logSize = 0)
    {
        if (logSize > 0)
            _log = GC.AllocateArray<byte>(logSize, pinned: true);
    }

    private static int IntAlign(int value) => (value + (sizeof(int) - 1)) & ~(sizeof(int) - 1);

    /// <summary>What the server sent since the last <see cref="TakeSent"/>, in the messages lane's format.</summary>
    public ReadOnlySpan<byte> Sent => _log is null ? default : new ReadOnlySpan<byte>(_log, 0, _logLength);

    /// <summary>Ends the messages of one state record.</summary>
    public void TakeSent() => _logLength = 0;

    private void Log(int kind, SizeBuf data)
    {
        if (_log is null)
            return;
        int n = data.CurSize;
        if (_logLength + 5 + n > _log.Length)
            throw LogOverflow;
        _log[_logLength] = (byte)kind;
        for (int i = 0; i < 4; i++)
            _log[_logLength + 1 + i] = (byte)(n >> (8 * i));
        new ReadOnlySpan<byte>(data.Data, n).CopyTo(new Span<byte>(_log, _logLength + 5, n));
        _logLength += 5 + n;
    }

    /// <summary>Loop_Connect ("local"): both sockets (made once), emptied and linked; the server finds the client next.</summary>
    [NoAllocUnchecked("load path: the sockets, once")]
    public Socket Connect()
    {
        _localConnectPending = true;
        _client ??= new Socket(this, isServer: false);
        _client.ReceiveLength = 0;
        _client.CanSend = true;
        _server ??= new Socket(this, isServer: true);
        _server.ReceiveLength = 0;
        _server.CanSend = true;
        _client.Peer = _server;
        _server.Peer = _client;
        return _client;
    }

    /// <summary>Loop_CheckNewConnections: the server's socket once a connect is pending.</summary>
    public IClientConnection? CheckNewConnections()
    {
        if (!_localConnectPending)
            return null;
        _localConnectPending = false;
        Socket s = _server!, c = _client!;
        s.ReceiveLength = 0;
        s.CanSend = true;
        c.ReceiveLength = 0;
        c.CanSend = true;
        return s;
    }
}
