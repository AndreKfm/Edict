// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>struct qsockaddr: a family and 14 bytes, as the LAN driver fills them (net_udp.c: port, then the IPv4 address).</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public unsafe struct QSockAddr
{
    public short Family;
    public fixed byte Data[14];
}

/// <summary>
/// net_landriver_t: what net_dgrm.c asks of a LAN driver (net_udp.c, net_wins.c; the tests' MemNet). What a server's
/// frame calls promises no allocation; the start, the listen switch and the name lookups of a connect may allocate.
/// </summary>
public unsafe interface INetLan
{
    /// <summary>The control socket, or -1 when the driver cannot start.</summary>
    int Init();

    void Shutdown();

    void Listen(bool state);

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int OpenSocket(int port);

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int CloseSocket(int socket);

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int Connect(int socket, ref QSockAddr addr);

    /// <summary>The accept socket when a datagram waits on it, else -1.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int CheckNewConnections();

    /// <summary>A datagram into <paramref name="buf"/> (cut to <paramref name="len"/>), 0 for none, -1 for an error.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int Read(int socket, byte* buf, int len, out QSockAddr addr);

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int Write(int socket, byte* buf, int len, ref QSockAddr addr);

    int Broadcast(int socket, byte* buf, int len);

    /// <summary>The address as text (a static buffer in C), zero-terminated into <paramref name="text"/>.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void AddrToString(ref QSockAddr addr, Span<byte> text);

    int StringToAddr(byte* text, out QSockAddr addr);

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int GetSocketAddr(int socket, out QSockAddr addr);

    int GetNameFromAddr(ref QSockAddr addr, Span<byte> name);

    int GetAddrFromName(byte* name, out QSockAddr addr);

    /// <summary>-1 another family or host, 1 another port, 0 the same.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int AddrCompare(ref QSockAddr a, ref QSockAddr b);

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int GetSocketPort(ref QSockAddr addr);

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int SetSocketPort(ref QSockAddr addr, int port);
}

/// <summary>What the datagram driver reads of the host: Sys_FloatTime (SetNetTime), the server's state its replies tell.</summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public unsafe interface INetHost
{
    /// <summary>Sys_FloatTime: the host's clock, which SetNetTime copies into net_time.</summary>
    double FloatTime();

    /// <summary>svs.maxclients, NET_NewQSocket's limit.</summary>
    int MaxClients { get; }

    /// <summary>net_activeconnections.</summary>
    int ActiveConnections { get; }

    /// <summary>hostname's text and sv.name, for CCREP_SERVER_INFO.</summary>
    ReadOnlySpan<byte> Hostname { get; }

    ReadOnlySpan<byte> MapName { get; }

    /// <summary>Con_Printf.</summary>
    void Print(ReadOnlySpan<byte> text);

    /// <summary>SCR_UpdateScreen, while _Datagram_Connect waits.</summary>
    void UpdateScreen();
}

/// <summary>qsocket_t: a connection of the datagram driver (or of the loopback), its sequences and message buffers.</summary>
public sealed unsafe class QSocket
{
    public const int NetMaxMessage = 8192, NetNameLen = 64;

    public QSocket? Next;
    public double ConnectTime, LastMessageTime, LastSendTime;
    public bool Disconnected, CanSend, SendNext;
    public int Driver, LanDriver, Socket;
    public uint AckSequence, SendSequence, UnreliableSendSequence;
    public int SendMessageLength;
    public readonly byte[] SendMessage = GC.AllocateArray<byte>(NetMaxMessage, pinned: true);
    public uint ReceiveSequence, UnreliableReceiveSequence;
    public int ReceiveMessageLength;
    public readonly byte[] ReceiveMessage = GC.AllocateArray<byte>(NetMaxMessage, pinned: true);
    public QSockAddr Addr;
    public readonly byte[] Address = GC.AllocateArray<byte>(NetNameLen, pinned: true);

    /// <summary>The address text, without its zero.</summary>
    public ReadOnlySpan<byte> AddressText
    {
        [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
        get
        {
            int n = 0;
            while (n < Address.Length && Address[n] != 0)
                n++;
            return new ReadOnlySpan<byte>(Address, 0, n);
        }
    }
}

/// <summary>
/// net_dgrm.c with net_main.c's qsocket pool (NET_NewQSocket, NET_FreeQSocket, NET_Close for its sockets) and
/// net_message: the datagram protocol over a LAN driver (P6 step 9). Reliable messages go in packets of at most
/// MAX_DATAGRAM bytes, each acknowledged before the next (resent after a second without the ack); unreliable ones
/// with a sequence, stale ones dropped; control packets (NETFLAG_CTL) on the accept socket: connect, server info,
/// player info, rule info. Kept as in C: one static packet buffer (a header claiming more than was read keeps what
/// an earlier packet left), a stale unreliable datagram ends the read with 0, the ban check on AF_INET addresses.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class Datagram
{
    public const int MaxDatagram = 1024, NetHeaderSize = 8, NetDatagramSize = MaxDatagram + NetHeaderSize;
    public const uint NetFlagLengthMask = 0x0000ffff, NetFlagData = 0x00010000, NetFlagAck = 0x00020000,
        NetFlagNak = 0x00040000, NetFlagEom = 0x00080000, NetFlagUnreliable = 0x00100000, NetFlagCtl = 0x80000000;
    public const int NetProtocolVersion = 3, DefaultHostPort = 26000, AfInet = 2;
    public const int CcreqConnect = 0x01, CcreqServerInfo = 0x02, CcreqPlayerInfo = 0x03, CcreqRuleInfo = 0x04,
        CcrepAccept = 0x81, CcrepReject = 0x82, CcrepServerInfo = 0x83, CcrepPlayerInfo = 0x84, CcrepRuleInfo = 0x85;

    private static readonly SysErrorException ClaimPastBuffer = new("Datagram_GetMessage: a header claims more than the packet buffer (C reads past it)");
    private static readonly SysErrorException FragmentsPastMessage = new("Datagram_GetMessage: the fragments pass NET_MAXMESSAGE (C writes past receiveMessage)");
    private static readonly SysErrorException NotActive = new("NET_FreeQSocket: not active");

    private readonly INetLan[] _lans;
    private readonly bool[] _lanInitialized;
    private readonly int[] _controlSock;
    private readonly INetHost _host;
    private int _lanLevel;                              // net_landriverlevel
    private readonly int _driverLevel;                  // the datagram driver's index in net_drivers (myDriverLevel)

    private readonly byte[] _packet;                    // packetBuffer: length, sequence (big-endian), data[MAX_DATAGRAM]
    private readonly byte[] _addrText;                  // what AddrToString returns (its static buffer)

    // the statistic counters
    public int PacketsSent, PacketsReSent, PacketsReceived, ReceivedDuplicateCount, ShortPacketCount, DroppedDatagrams;

    // BAN_TEST
    public uint BanAddr, BanMask = 0xffffffff;

    /// <summary>net_message: what a read leaves, and the control replies' buffer.</summary>
    public SizeBuf NetMessage { get; }
    private readonly MsgReader _reader;

    /// <summary>net_time: the host's clock as the last SetNetTime saw it.</summary>
    public double NetTime { get; set; }

    // the qsocket pool (net_main.c)
    private QSocket? _activeSockets, _freeSockets;
    private readonly QSocket[] _sockets;

    [NoAllocUnchecked("load path: the sockets and buffers")]
    public Datagram(INetHost host, INetLan[] lans, int numSockets, int driverLevel)
    {
        _host = host;
        _lans = lans;
        _lanInitialized = new bool[lans.Length];
        _controlSock = new int[lans.Length];
        _driverLevel = driverLevel;
        _packet = GC.AllocateArray<byte>(NetDatagramSize, pinned: true);
        _addrText = GC.AllocateArray<byte>(64, pinned: true);
        NetMessage = new SizeBuf(QSocket.NetMaxMessage);
        _reader = new MsgReader(NetMessage);
        _sockets = new QSocket[numSockets];
        for (int i = 0; i < numSockets; i++)
        {
            var s = new QSocket { Next = _freeSockets, Disconnected = true };
            _freeSockets = s;
            _sockets[i] = s;
        }
    }

    /// <summary>The pool's sockets, in the order they were made (the tests compare them by their place).</summary>
    public ReadOnlySpan<QSocket> Sockets => _sockets;

    /// <summary>The i-th LAN driver.</summary>
    public INetLan Lan(int i) => _lans[i];

    /// <summary>SetNetTime: net_time from Sys_FloatTime.</summary>
    public double SetNetTime()
    {
        NetTime = _host.FloatTime();
        return NetTime;
    }

    // ---- net_main.c's pool

    /// <summary>NET_NewQSocket: a free socket into the active list (in front), or null (none left, or the server is full).</summary>
    public QSocket? NewQSocket()
    {
        if (_freeSockets is not { } sock)
            return null;
        if (_host.ActiveConnections >= _host.MaxClients)
            return null;
        // get one from free list
        _freeSockets = sock.Next;
        // add it to active list
        sock.Next = _activeSockets;
        _activeSockets = sock;
        sock.Disconnected = false;
        sock.ConnectTime = NetTime;
        "UNSET ADDRESS\0"u8.CopyTo(sock.Address);
        sock.Driver = _driverLevel;
        sock.Socket = 0;
        sock.CanSend = true;
        sock.SendNext = false;
        sock.LastMessageTime = NetTime;
        sock.AckSequence = 0;
        sock.SendSequence = 0;
        sock.UnreliableSendSequence = 0;
        sock.SendMessageLength = 0;
        sock.ReceiveSequence = 0;
        sock.UnreliableReceiveSequence = 0;
        sock.ReceiveMessageLength = 0;
        return sock;
    }

    /// <summary>NET_FreeQSocket: out of the active list, into the free one, disconnected.</summary>
    public void FreeQSocket(QSocket sock)
    {
        // remove it from active list
        if (sock == _activeSockets)
            _activeSockets = _activeSockets.Next;
        else
        {
            QSocket? s;
            for (s = _activeSockets; s != null; s = s.Next)
                if (s.Next == sock)
                {
                    s.Next = sock.Next;
                    break;
                }
            if (s == null)
                throw NotActive;
        }
        // add it to free list
        sock.Next = _freeSockets;
        _freeSockets = sock;
        sock.Disconnected = true;
    }

    /// <summary>NET_Close on a datagram socket: Datagram_Close, then the qsocket freed.</summary>
    public void Close(QSocket? sock)
    {
        if (sock == null || sock.Disconnected)
            return;
        SetNetTime();
        // call the driver_Close function
        _lans[sock.LanDriver].CloseSocket(sock.Socket);
        FreeQSocket(sock);
    }

    // ---- the packets

    private static void PutBig(byte* p, uint v)
    {
        p[0] = (byte)(v >> 24);
        p[1] = (byte)(v >> 16);
        p[2] = (byte)(v >> 8);
        p[3] = (byte)v;
    }

    private static uint GetBig(byte* p) => (uint)(p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3]);

    /// <summary>One packet of the reliable message: the first MAX_DATAGRAM bytes, EOM on the last, the given sequence.</summary>
    private int SendReliablePacket(QSocket sock, uint sequence)
    {
        int dataLen;
        uint eom;
        if (sock.SendMessageLength <= MaxDatagram)
        {
            dataLen = sock.SendMessageLength;
            eom = NetFlagEom;
        }
        else
        {
            dataLen = MaxDatagram;
            eom = 0;
        }
        int packetLen = NetHeaderSize + dataLen;
        fixed (byte* p = _packet)
        fixed (byte* m = sock.SendMessage)
        {
            PutBig(p, (uint)packetLen | NetFlagData | eom);
            PutBig(p + 4, sequence);
            for (int i = 0; i < dataLen; i++)
                p[NetHeaderSize + i] = m[i];
            return _lans[sock.LanDriver].Write(sock.Socket, p, packetLen, ref sock.Addr);
        }
    }

    /// <summary>Datagram_SendMessage: the message kept for the acks, its first packet sent; no other until the last is acked.</summary>
    public int SendMessage(QSocket sock, SizeBuf data)
    {
        new ReadOnlySpan<byte>(data.Data, data.CurSize).CopyTo(sock.SendMessage);
        sock.SendMessageLength = data.CurSize;
        uint sequence = sock.SendSequence++;
        sock.CanSend = false;
        if (SendReliablePacket(sock, sequence) == -1)
            return -1;
        sock.LastSendTime = NetTime;
        PacketsSent++;
        return 1;
    }

    /// <summary>SendMessageNext: the message's next packet, after the ack of the one before.</summary>
    private int SendMessageNext(QSocket sock)
    {
        uint sequence = sock.SendSequence++;
        sock.SendNext = false;
        if (SendReliablePacket(sock, sequence) == -1)
            return -1;
        sock.LastSendTime = NetTime;
        PacketsSent++;
        return 1;
    }

    /// <summary>ReSendMessage: the unacknowledged packet again, with its sequence.</summary>
    private int ReSendMessage(QSocket sock)
    {
        sock.SendNext = false;
        if (SendReliablePacket(sock, sock.SendSequence - 1) == -1)
            return -1;
        sock.LastSendTime = NetTime;
        PacketsReSent++;
        return 1;
    }

    /// <summary>Datagram_CanSendMessage: the next packet first if its ack came; whether a new message may go.</summary>
    public bool CanSendMessage(QSocket sock)
    {
        if (sock.SendNext)
            SendMessageNext(sock);
        return sock.CanSend;
    }

    /// <summary>Datagram_SendUnreliableMessage: one packet with the unreliable sequence.</summary>
    public int SendUnreliableMessage(QSocket sock, SizeBuf data)
    {
        int packetLen = NetHeaderSize + data.CurSize;
        fixed (byte* p = _packet)
        {
            PutBig(p, (uint)packetLen | NetFlagUnreliable);
            PutBig(p + 4, sock.UnreliableSendSequence++);
            new ReadOnlySpan<byte>(data.Data, data.CurSize).CopyTo(new Span<byte>(p + NetHeaderSize, MaxDatagram));
            if (_lans[sock.LanDriver].Write(sock.Socket, p, packetLen, ref sock.Addr) == -1)
                return -1;
        }
        PacketsSent++;
        return 1;
    }

    /// <summary>
    /// Datagram_GetMessage: the unacked packet resent after a second; then the socket's packets: another sender's,
    /// short and control packets skipped; an unreliable one returned (2; a stale one ends the read with 0); an ack
    /// moves the reliable message on; a data packet is acked, and its message returned when complete (1).
    /// </summary>
    public int GetMessage(QSocket sock)
    {
        int ret = 0;
        if (!sock.CanSend)
            if (NetTime - sock.LastSendTime > 1.0)
                ReSendMessage(sock);
        INetLan lan = _lans[sock.LanDriver];
        fixed (byte* p = _packet)
        {
            while (true)
            {
                uint length = (uint)lan.Read(sock.Socket, p, NetDatagramSize, out QSockAddr readaddr);
                if (length == 0)
                    break;
                if (length == unchecked((uint)-1))
                {
                    _host.Print("Read error\n"u8);
                    return -1;
                }
                if (lan.AddrCompare(ref readaddr, ref sock.Addr) != 0)
                    continue;   // Con_DPrintf ("Forged packet received\n")
                if (length < NetHeaderSize)
                {
                    ShortPacketCount++;
                    continue;
                }
                length = GetBig(p);
                uint flags = length & ~NetFlagLengthMask;
                length &= NetFlagLengthMask;
                if ((flags & NetFlagCtl) != 0)
                    continue;
                uint sequence = GetBig(p + 4);
                PacketsReceived++;

                if ((flags & NetFlagUnreliable) != 0)
                {
                    if (sequence < sock.UnreliableReceiveSequence)
                    {
                        ret = 0;    // Con_DPrintf ("Got a stale datagram\n")
                        break;
                    }
                    if (sequence != sock.UnreliableReceiveSequence)
                        DroppedDatagrams += (int)(sequence - sock.UnreliableReceiveSequence);
                    sock.UnreliableReceiveSequence = sequence + 1;
                    length -= NetHeaderSize;
                    if (length > MaxDatagram)
                        throw ClaimPastBuffer;
                    NetMessage.Clear();
                    NetMessage.Write(p + NetHeaderSize, (int)length);
                    ret = 2;
                    break;
                }

                if ((flags & NetFlagAck) != 0)
                {
                    if (sequence != sock.SendSequence - 1)
                        continue;   // Con_DPrintf ("Stale ACK received\n")
                    if (sequence == sock.AckSequence)
                        sock.AckSequence++;     // Con_DPrintf ("ack sequencing error\n") if it is not sendSequence now
                    else
                        continue;   // Con_DPrintf ("Duplicate ACK received\n")
                    sock.SendMessageLength -= MaxDatagram;
                    if (sock.SendMessageLength > 0)
                    {
                        new ReadOnlySpan<byte>(sock.SendMessage, MaxDatagram, sock.SendMessageLength).CopyTo(sock.SendMessage);
                        sock.SendNext = true;
                    }
                    else
                    {
                        sock.SendMessageLength = 0;
                        sock.CanSend = true;
                    }
                    continue;
                }

                if ((flags & NetFlagData) != 0)
                {
                    PutBig(p, NetHeaderSize | NetFlagAck);
                    PutBig(p + 4, sequence);
                    lan.Write(sock.Socket, p, NetHeaderSize, ref readaddr);
                    if (sequence != sock.ReceiveSequence)
                    {
                        ReceivedDuplicateCount++;
                        continue;
                    }
                    sock.ReceiveSequence++;
                    length -= NetHeaderSize;
                    if (length > MaxDatagram)
                        throw ClaimPastBuffer;
                    if ((flags & NetFlagEom) != 0)
                    {
                        NetMessage.Clear();
                        fixed (byte* r = sock.ReceiveMessage)
                            NetMessage.Write(r, sock.ReceiveMessageLength);
                        NetMessage.Write(p + NetHeaderSize, (int)length);
                        sock.ReceiveMessageLength = 0;
                        ret = 1;
                        break;
                    }
                    if (sock.ReceiveMessageLength + length > QSocket.NetMaxMessage)
                        throw FragmentsPastMessage;
                    new ReadOnlySpan<byte>(p + NetHeaderSize, (int)length).CopyTo(
                        new Span<byte>(sock.ReceiveMessage, sock.ReceiveMessageLength, QSocket.NetMaxMessage - sock.ReceiveMessageLength));
                    sock.ReceiveMessageLength += (int)length;
                    continue;
                }
            }
        }
        if (sock.SendNext)
            SendMessageNext(sock);
        return ret;
    }

    // ---- init, listen, connections

    /// <summary>Datagram_Init: each LAN driver's control socket (net_stats, ban, test and test2 are the host's commands).</summary>
    [NoAllocUnchecked("load path: the drivers start")]
    public int Init(bool noLan)
    {
        if (noLan)
            return -1;
        for (int i = 0; i < _lans.Length; i++)
        {
            int csock = _lans[i].Init();
            if (csock == -1)
                continue;
            _lanInitialized[i] = true;
            _controlSock[i] = csock;
        }
        return 0;
    }

    /// <summary>Datagram_Shutdown.</summary>
    [NoAllocUnchecked("the drivers stop")]
    public void Shutdown()
    {
        for (int i = 0; i < _lans.Length; i++)
            if (_lanInitialized[i])
            {
                _lans[i].Shutdown();
                _lanInitialized[i] = false;
            }
    }

    /// <summary>Datagram_Listen.</summary>
    [NoAllocUnchecked("console command: listen")]
    public void Listen(bool state)
    {
        for (int i = 0; i < _lans.Length; i++)
            if (_lanInitialized[i])
                _lans[i].Listen(state);
    }

    /// <summary>A control reply: the header (CTL and the length, big-endian) over the long saved for it, then the write.</summary>
    private void SendControl(INetLan lan, int socket, ref QSockAddr addr)
    {
        SizeBuf m = NetMessage;
        PutBig(m.Data, NetFlagCtl | ((uint)m.CurSize & NetFlagLengthMask));
        lan.Write(socket, m.Data, m.CurSize, ref addr);
        m.Clear();
    }

    private void BeginControl(int command)
    {
        NetMessage.Clear();
        // save space for the header, filled in later
        NetMessage.WriteLong(0);
        NetMessage.WriteByte(command);
    }

    private static bool StrEquals(byte* s, ReadOnlySpan<byte> word)
    {
        int i = 0;
        for (; i < word.Length; i++)
            if (s[i] != word[i])
                return false;
        return s[i] == 0;
    }

    /// <summary>
    /// _Datagram_CheckNewConnections on the current LAN driver: a control packet on the accept socket; the server
    /// info, player info and rule info requests answered; a connect refused (another version, a ban, a full
    /// server), answered again (a request repeated within 2 s), a returning client's old socket closed, or a new
    /// socket made and accepted with its port.
    /// </summary>
    private QSocket? CheckNewConnectionsOn(INetLan lan, IServerRules? rules)
    {
        int acceptsock = lan.CheckNewConnections();
        if (acceptsock == -1)
            return null;
        SizeBuf m = NetMessage;
        m.Clear();
        int len = lan.Read(acceptsock, m.Data, m.MaxSize, out QSockAddr clientaddr);
        if ((uint)len < sizeof(int))
            return null;
        m.CurSize = len;
        MsgReader r = _reader;
        r.BeginReading();
        int control = (int)GetBig(m.Data);
        r.ReadLong();
        if (control == -1)
            return null;
        if (((uint)control & ~NetFlagLengthMask) != NetFlagCtl)
            return null;
        if ((control & NetFlagLengthMask) != len)
            return null;

        int command = r.ReadByte();
        if (command == CcreqServerInfo)
        {
            if (!StrEquals(r.ReadString(), "QUAKE"u8))
                return null;
            BeginControl(CcrepServerInfo);
            lan.GetSocketAddr(acceptsock, out QSockAddr newaddr);
            lan.AddrToString(ref newaddr, _addrText);
            m.WriteString(_addrText);
            m.WriteString(_host.Hostname);
            m.WriteString(_host.MapName);
            m.WriteByte(_host.ActiveConnections);
            m.WriteByte(_host.MaxClients);
            m.WriteByte(NetProtocolVersion);
            SendControl(lan, acceptsock, ref clientaddr);
            return null;
        }

        if (command == CcreqPlayerInfo)
        {
            int playerNumber = r.ReadByte();
            if (rules is null || !rules.Player(playerNumber, out ReadOnlySpan<byte> name, out int colors, out int frags,
                                                 out double connectTime, out ReadOnlySpan<byte> address))
                return null;
            BeginControl(CcrepPlayerInfo);
            m.WriteByte(playerNumber);
            m.WriteString(name);
            m.WriteLong(colors);
            m.WriteLong(frags);
            m.WriteLong(CCast.I32(NetTime - connectTime));
            m.WriteString(address);
            SendControl(lan, acceptsock, ref clientaddr);
            return null;
        }

        if (command == CcreqRuleInfo)
        {
            // find the search start location, then the next server cvar
            byte* prevCvarName = r.ReadString();
            ReadOnlySpan<byte> varName = default, value = default;
            CvarSearch found = rules?.NextServerCvar(prevCvarName, out varName, out value) ?? CvarSearch.End;
            if (found == CvarSearch.Unknown)
                return null;
            BeginControl(CcrepRuleInfo);
            if (found == CvarSearch.Found)
            {
                m.WriteString(varName);
                m.WriteString(value);
            }
            SendControl(lan, acceptsock, ref clientaddr);
            return null;
        }

        if (command != CcreqConnect)
            return null;
        if (!StrEquals(r.ReadString(), "QUAKE"u8))
            return null;
        if (r.ReadByte() != NetProtocolVersion)
        {
            BeginControl(CcrepReject);
            m.WriteString("Incompatible version.\n"u8);
            SendControl(lan, acceptsock, ref clientaddr);
            return null;
        }

        // check for a ban
        if (clientaddr.Family == AfInet)
        {
            uint testAddr = *(uint*)(clientaddr.Data + 2);
            if ((testAddr & BanMask) == BanAddr)
            {
                BeginControl(CcrepReject);
                m.WriteString("You have been banned.\n"u8);
                SendControl(lan, acceptsock, ref clientaddr);
                return null;
            }
        }

        // see if this guy is already connected
        for (QSocket? s = _activeSockets; s != null; s = s.Next)
        {
            if (s.Driver != _driverLevel)
                continue;
            int ret = lan.AddrCompare(ref clientaddr, ref s.Addr);
            if (ret >= 0)
            {
                // is this a duplicate connection reqeust?
                if (ret == 0 && NetTime - s.ConnectTime < 2.0)
                {
                    // yes, so send a duplicate reply
                    BeginControl(CcrepAccept);
                    lan.GetSocketAddr(s.Socket, out QSockAddr dupaddr);
                    m.WriteLong(lan.GetSocketPort(ref dupaddr));
                    SendControl(lan, acceptsock, ref clientaddr);
                    return null;
                }
                // it's somebody coming back in from a crash/disconnect so close the old qsocket and let their retry
                // get them back in
                Close(s);
                return null;
            }
        }

        // allocate a QSocket
        QSocket? sock = NewQSocket();
        if (sock == null)
        {
            // no room; try to let him know
            BeginControl(CcrepReject);
            m.WriteString("Server is full.\n"u8);
            SendControl(lan, acceptsock, ref clientaddr);
            return null;
        }

        // allocate a network socket
        int newsock = lan.OpenSocket(0);
        if (newsock == -1)
        {
            FreeQSocket(sock);
            return null;
        }

        // connect to the client
        if (lan.Connect(newsock, ref clientaddr) == -1)
        {
            lan.CloseSocket(newsock);
            FreeQSocket(sock);
            return null;
        }

        // everything is allocated, just fill in the details
        sock.Socket = newsock;
        sock.LanDriver = _lanLevel;
        sock.Addr = clientaddr;
        lan.AddrToString(ref clientaddr, sock.Address);

        // send him back the info about the server connection he has been allocated
        BeginControl(CcrepAccept);
        lan.GetSocketAddr(newsock, out QSockAddr acceptaddr);
        m.WriteLong(lan.GetSocketPort(ref acceptaddr));
        SendControl(lan, acceptsock, ref clientaddr);
        return sock;
    }

    /// <summary>Datagram_CheckNewConnections: each initialized LAN driver's accept socket, until one gives a connection.</summary>
    public QSocket? CheckNewConnections(IServerRules? rules = null)
    {
        QSocket? ret = null;
        for (_lanLevel = 0; _lanLevel < _lans.Length; _lanLevel++)
            if (_lanInitialized[_lanLevel])
                if ((ret = CheckNewConnectionsOn(_lans[_lanLevel], rules)) != null)
                    break;
        return ret;
    }

    /// <summary>
    /// _Datagram_Connect: the host resolved, a socket opened, CCREQ_CONNECT sent up to three times with 2.5 s of
    /// waiting each (the clock the LAN driver's reads let pass), and the server's reply: a reject's reason, or the
    /// accept's port, to which the connection is switched. Prints as C (trying..., still trying..., the reason).
    /// </summary>
    [NoAllocUnchecked("connect: the console's lines")]
    private QSocket? ConnectOn(INetLan lan, byte* host)
    {
        // see if we can resolve the host name
        if (lan.GetAddrFromName(host, out QSockAddr sendaddr) == -1)
            return null;
        int newsock = lan.OpenSocket(0);
        if (newsock == -1)
            return null;
        QSocket? sock = NewQSocket();
        if (sock == null)
        {
            lan.CloseSocket(newsock);
            return null;
        }
        sock.Socket = newsock;
        sock.LanDriver = _lanLevel;

        // connect to the host
        if (lan.Connect(newsock, ref sendaddr) == -1)
            return Fail(lan, sock, newsock);

        // send the connection request
        _host.Print("trying...\n"u8);
        _host.UpdateScreen();
        double startTime = NetTime;
        SizeBuf m = NetMessage;
        int ret = 0;
        for (int reps = 0; reps < 3; reps++)
        {
            BeginControl(CcreqConnect);
            m.WriteString("QUAKE"u8);
            m.WriteByte(NetProtocolVersion);
            PutBig(m.Data, NetFlagCtl | ((uint)m.CurSize & NetFlagLengthMask));
            lan.Write(newsock, m.Data, m.CurSize, ref sendaddr);
            m.Clear();
            do
            {
                ret = lan.Read(newsock, m.Data, m.MaxSize, out QSockAddr readaddr);
                // if we got something, validate it
                if (ret > 0)
                {
                    // is it from the right place?
                    if (lan.AddrCompare(ref readaddr, ref sendaddr) != 0)
                    {
                        ret = 0;
                        continue;
                    }
                    if ((uint)ret < sizeof(int))
                    {
                        ret = 0;
                        continue;
                    }
                    m.CurSize = ret;
                    _reader.BeginReading();
                    int control = (int)GetBig(m.Data);
                    _reader.ReadLong();
                    if (control == -1)
                    {
                        ret = 0;
                        continue;
                    }
                    if (((uint)control & ~NetFlagLengthMask) != NetFlagCtl)
                    {
                        ret = 0;
                        continue;
                    }
                    if ((control & NetFlagLengthMask) != ret)
                    {
                        ret = 0;
                        continue;
                    }
                }
            }
            while (ret == 0 && SetNetTime() - startTime < 2.5);
            if (ret != 0)
                break;
            _host.Print("still trying...\n"u8);
            _host.UpdateScreen();
            startTime = SetNetTime();
        }

        if (ret == 0)
        {
            _host.Print("No Response\n"u8);
            return Fail(lan, sock, newsock);
        }
        if (ret == -1)
        {
            _host.Print("Network Error\n"u8);
            return Fail(lan, sock, newsock);
        }
        ret = _reader.ReadByte();
        if (ret == CcrepReject)
        {
            byte* reason = _reader.ReadString();
            int n = 0;
            while (reason[n] != 0)
                n++;
            _host.Print(new ReadOnlySpan<byte>(reason, n));     // Con_Printf (reason): a % in it is C's format
            return Fail(lan, sock, newsock);
        }
        if (ret == CcrepAccept)
        {
            sock.Addr = sendaddr;
            lan.SetSocketPort(ref sock.Addr, _reader.ReadLong());
        }
        else
        {
            _host.Print("Bad Response\n"u8);
            return Fail(lan, sock, newsock);
        }

        lan.GetNameFromAddr(ref sendaddr, sock.Address);
        _host.Print("Connection accepted\n"u8);
        sock.LastMessageTime = SetNetTime();

        // switch the connection to the specified address
        if (lan.Connect(newsock, ref sock.Addr) == -1)
        {
            _host.Print("Connect to Game failed\n"u8);
            return Fail(lan, sock, newsock);
        }
        return sock;
    }

    [NoAllocUnchecked("connect")]
    private QSocket? Fail(INetLan lan, QSocket sock, int newsock)
    {
        FreeQSocket(sock);
        lan.CloseSocket(newsock);
        return null;
    }

    /// <summary>Datagram_Connect: each initialized LAN driver in turn, until one connects.</summary>
    [NoAllocUnchecked("connect")]
    public QSocket? Connect(byte* host)
    {
        QSocket? ret = null;
        for (_lanLevel = 0; _lanLevel < _lans.Length; _lanLevel++)
            if (_lanInitialized[_lanLevel])
                if ((ret = ConnectOn(_lans[_lanLevel], host)) != null)
                    break;
        return ret;
    }
}

/// <summary>What CCREQ_SERVER_INFO's rule search found.</summary>
public enum CvarSearch
{
    Unknown,        // the previous name is no cvar: no reply
    Found,
    End,            // no server cvar after it: the reply without one
}

/// <summary>The server's answers to the player and rule queries (svs.clients, the cvar list).</summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public unsafe interface IServerRules
{
    /// <summary>The playerNumber-th active client (counting from 0): name, colors, frags, connection time and address.</summary>
    bool Player(int playerNumber, out ReadOnlySpan<byte> name, out int colors, out int frags, out double connectTime,
                out ReadOnlySpan<byte> address);

    /// <summary>The first server cvar after the one named (from the list's start for "").</summary>
    CvarSearch NextServerCvar(byte* previous, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value);
}
