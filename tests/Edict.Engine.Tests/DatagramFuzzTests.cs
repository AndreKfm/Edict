// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Edict.Exact;

namespace Edict.Engine.Tests;

/// <summary>
/// The tests' LAN driver in memory, the twin of ref/src/edict_memnet.c: net_udp.c's answers towards net_dgrm.c,
/// the packets written into an outbox the test empties, the packets read from inboxes the test fills (and a port no
/// socket has yet keeps them until one opens it), the clock advanced by a step on every read.
/// </summary>
internal sealed unsafe class MemNet : INetLan, INetHost
{
    private const int Sockets = 32, Queue = 64, Packet = Datagram.NetDatagramSize + 64;

    internal sealed class Pkt
    {
        public byte[] Data = [];
        public QSockAddr Addr;
        public int From;                                // the writing socket's port (outbox), the port waited for (pending)
    }

    private sealed class Sock
    {
        public bool Open;
        public int Port;
        public readonly Queue<Pkt> Inbox = new();
    }

    private readonly Sock[] _socks = new Sock[Sockets];
    public readonly List<Pkt> Out = [];
    private readonly List<Pkt> _pending = [];
    private readonly uint _host;
    private int _nextPort = 27001, _accept = -1;
    private readonly double _step;
    public double Clock;
    public readonly StringBuilder Printed = new();

    public int MaxClients { get; set; }
    public int ActiveConnections { get; set; }
    public byte[] HostnameText = "UNNAMED"u8.ToArray(), MapText = [];

    public MemNet(uint host, double clock, double step)
    {
        for (int i = 0; i < Sockets; i++)
            _socks[i] = new Sock();
        _host = host;
        Clock = clock;
        _step = step;
    }

    public static void SetAddr(out QSockAddr a, uint ip, int port)
    {
        QSockAddr r = default;
        r.Family = Datagram.AfInet;
        r.Data[0] = (byte)(port >> 8);
        r.Data[1] = (byte)port;
        *(uint*)(r.Data + 2) = ip;
        a = r;
    }

    private static int AddrPort(ref QSockAddr a)
    {
        fixed (QSockAddr* p = &a)
            return (((byte*)p)[2] << 8) | ((byte*)p)[3];
    }

    public static string ZString(byte* s)
    {
        int n = 0;
        while (s[n] != 0)
            n++;
        return Encoding.Latin1.GetString(s, n);
    }

    // ---- INetHost

    public double FloatTime() => Clock;

    public ReadOnlySpan<byte> Hostname => HostnameText;

    public ReadOnlySpan<byte> MapName => MapText;

    public void Print(ReadOnlySpan<byte> text) => Printed.Append(Encoding.Latin1.GetString(text));

    public void UpdateScreen()
    {
    }

    // ---- INetLan

    public int Init() => OpenSocket(0);

    public void Shutdown()
    {
    }

    public void Listen(bool state)
    {
        if (state)
        {
            if (_accept == -1)
                _accept = OpenSocket(Datagram.DefaultHostPort);
        }
        else if (_accept != -1)
            CloseSocket(_accept);
    }

    public int OpenSocket(int port)
    {
        for (int i = 0; i < Sockets; i++)
            if (!_socks[i].Open)
            {
                Sock s = _socks[i];
                s.Open = true;
                s.Port = port != 0 ? port : _nextPort++;
                s.Inbox.Clear();
                for (int j = 0; j < _pending.Count; j++)
                    if (_pending[j].From == s.Port && s.Inbox.Count < Queue)
                    {
                        s.Inbox.Enqueue(_pending[j]);
                        _pending.RemoveAt(j--);
                    }
                return i;
            }
        return -1;
    }

    public int CloseSocket(int socket)
    {
        if (socket == _accept)
            _accept = -1;
        _socks[socket].Open = false;
        return 0;
    }

    public int Connect(int socket, ref QSockAddr addr) => 0;

    public int CheckNewConnections() => _accept == -1 ? -1 : _socks[_accept].Inbox.Count != 0 ? _accept : -1;

    public int Read(int socket, byte* buf, int len, out QSockAddr addr)
    {
        Clock += _step;
        addr = default;
        Sock s = _socks[socket];
        if (s.Inbox.Count == 0)
            return 0;
        Pkt p = s.Inbox.Dequeue();
        int n = Math.Min(p.Data.Length, len);
        p.Data.AsSpan(0, n).CopyTo(new Span<byte>(buf, n));
        addr = p.Addr;
        return n;
    }

    public int Write(int socket, byte* buf, int len, ref QSockAddr addr)
    {
        Out.Add(new Pkt { Data = new ReadOnlySpan<byte>(buf, len).ToArray(), Addr = addr, From = _socks[socket].Port });
        return len;
    }

    public int Broadcast(int socket, byte* buf, int len)
    {
        SetAddr(out QSockAddr a, 0xffffffff, Datagram.DefaultHostPort);
        return Write(socket, buf, len, ref a);
    }

    public void AddrToString(ref QSockAddr addr, Span<byte> text)
    {
        byte[] s;
        fixed (QSockAddr* p = &addr)
        {
            byte* ip = (byte*)p + 4;
            s = Encoding.Latin1.GetBytes($"{ip[0]}.{ip[1]}.{ip[2]}.{ip[3]}:{AddrPort(ref addr)}\0");
        }
        s.CopyTo(text);
    }

    public int StringToAddr(byte* text, out QSockAddr addr) => throw new NotSupportedException();

    public int GetSocketAddr(int socket, out QSockAddr addr)
    {
        SetAddr(out addr, _host, _socks[socket].Port);
        return 0;
    }

    public int GetNameFromAddr(ref QSockAddr addr, Span<byte> name)
    {
        AddrToString(ref addr, name);
        return 0;
    }

    public int GetAddrFromName(byte* name, out QSockAddr addr)
    {
        addr = default;
        string s = ZString(name);
        if (s.Length == 0 || s[0] < '0' || s[0] > '9')
            return -1;
        string[] parts = s.Split(':')[0].Split('.');
        if (parts.Length != 4)
            return -1;
        uint ip = 0;
        for (int i = 0; i < 4; i++)
            ip |= (uint)(int.Parse(parts[i]) & 255) << (8 * i);
        int port = s.Contains(':') ? int.Parse(s[(s.IndexOf(':') + 1)..]) : Datagram.DefaultHostPort;
        SetAddr(out addr, ip, port);
        return 0;
    }

    public int AddrCompare(ref QSockAddr a, ref QSockAddr b)
    {
        if (a.Family != b.Family)
            return -1;
        fixed (QSockAddr* pa = &a, pb = &b)
            if (*(uint*)((byte*)pa + 4) != *(uint*)((byte*)pb + 4))
                return -1;
        if (AddrPort(ref a) != AddrPort(ref b))
            return 1;
        return 0;
    }

    public int GetSocketPort(ref QSockAddr addr) => AddrPort(ref addr);

    public int SetSocketPort(ref QSockAddr addr, int port)
    {
        fixed (QSockAddr* p = &addr)
        {
            ((byte*)p)[2] = (byte)(port >> 8);
            ((byte*)p)[3] = (byte)port;
        }
        return 0;
    }

    /// <summary>EdictOracle_NetGive's twin: 1 into the socket's inbox, 2 waiting for the port, 0 refused.</summary>
    public int Give(int port, byte[] data, QSockAddr from)
    {
        foreach (Sock s in _socks)
            if (s.Open && s.Port == port)
            {
                if (s.Inbox.Count == Queue)
                    return 0;
                s.Inbox.Enqueue(new Pkt { Data = data, Addr = from });
                return 1;
            }
        if (_pending.Count == Queue)
            return 0;
        _pending.Add(new Pkt { Data = data, Addr = from, From = port });
        return 2;
    }
}

/// <summary>
/// Level 1 of net_dgrm.c (P6 step 9): the datagram driver of the reference (EdictOracle_Net*: net_dgrm.c itself on
/// ref/src/edict_memnet.c) and the port's (<see cref="Datagram"/> on <see cref="MemNet"/>) given the same calls and
/// the same packets: control requests on the accept socket (connect, a repeated connect, other versions, a full
/// server, server info, malformed headers), a client's handshake (accept, reject, a bad reply, no reply until the
/// retries end), and on the connections reliable messages over several packets with their acks, unreliable ones,
/// lost, repeated, reordered, forged and short packets, stale sequences and acks, resends after a second. After every
/// step the returns, net_message, the packets written and each socket's state are compared.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class DatagramFuzzTests
{
    private static readonly nint Lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("EDICT_ORACLE")
        ?? throw new InvalidOperationException("EDICT_ORACLE is not set (docs/TESTING.md)"));

    private static readonly delegate* unmanaged<uint, double, double, int, int, void> Reset =
        (delegate* unmanaged<uint, double, double, int, int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_NetReset");
    private static readonly delegate* unmanaged<double, void> SetTime =
        (delegate* unmanaged<double, void>)NativeLibrary.GetExport(Lib, "EdictOracle_NetSetTime");
    private static readonly delegate* unmanaged<double> NetTime =
        (delegate* unmanaged<double>)NativeLibrary.GetExport(Lib, "EdictOracle_NetTime");
    private static readonly delegate* unmanaged<double> NetClock =
        (delegate* unmanaged<double>)NativeLibrary.GetExport(Lib, "EdictOracle_NetClock");
    private static readonly delegate* unmanaged<int> OutCount =
        (delegate* unmanaged<int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetOutCount");
    private static readonly delegate* unmanaged<byte*, int, int*, byte*, int> TakeOut =
        (delegate* unmanaged<byte*, int, int*, byte*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetTakeOut");
    private static readonly delegate* unmanaged<int, byte*, int, byte*, int> Give =
        (delegate* unmanaged<int, byte*, int, byte*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetGive");
    private static readonly delegate* unmanaged<int> CheckNew =
        (delegate* unmanaged<int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetCheckNewConnections");
    private static readonly delegate* unmanaged<byte*, int> ConnectFn =
        (delegate* unmanaged<byte*, int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetConnect");
    private static readonly delegate* unmanaged<int, int> GetMessageFn =
        (delegate* unmanaged<int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetGetMessage");
    private static readonly delegate* unmanaged<int, int> CanSendFn =
        (delegate* unmanaged<int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetCanSendMessage");
    private static readonly delegate* unmanaged<int, void> CloseFn =
        (delegate* unmanaged<int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_NetClose");
    private static readonly delegate* unmanaged<int, byte*, int, int> SendFn =
        (delegate* unmanaged<int, byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetSendMessage");
    private static readonly delegate* unmanaged<int, byte*, int, int> SendUnreliableFn =
        (delegate* unmanaged<int, byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetSendUnreliableMessage");
    private static readonly delegate* unmanaged<byte*, int, int> MessageFn =
        (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_NetMessage");
    private static readonly delegate* unmanaged<int, int*, double*, byte*, byte*, void> StateFn =
        (delegate* unmanaged<int, int*, double*, byte*, byte*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_NetSocketState");
    private static readonly delegate* unmanaged<int*, void> CountersFn =
        (delegate* unmanaged<int*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_NetCounters");
    private static readonly delegate* unmanaged<byte*, byte*, int, void> ServerInfoFn =
        (delegate* unmanaged<byte*, byte*, int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_NetServerInfo");

    private const uint ServerIp = 10 | 1u << 24;           // 10.0.0.1
    private const int Pool = 8;

    /// <summary>Both sides of one run, and what the run reached (so that a green run can be judged).</summary>
    private sealed class Pair
    {
        public required MemNet Net;
        public required Datagram Port;
        public string? Failure;
        public readonly Dictionary<string, int> Seen = [];

        public void Count(string what) => Seen[what] = Seen.GetValueOrDefault(what) + 1;

        public string Stats => string.Join(", ", Seen.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"));
    }

    private static Pair NewPair(double clock, double step, int maxclients, bool listen)
    {
        Reset(ServerIp, clock, step, maxclients, listen ? 1 : 0);
        var net = new MemNet(ServerIp, clock, step) { MaxClients = maxclients };
        var port = new Datagram(net, [net], Pool, driverLevel: 1) { NetTime = clock };
        port.Init(false);
        if (listen)
            port.Listen(true);
        return new Pair { Net = net, Port = port };
    }

    private static QSockAddr ClientAddr(int host, int port)
    {
        MemNet.SetAddr(out QSockAddr a, 10 | (uint)host << 24, port);
        return a;
    }

    /// <summary>The same packet to both sides' port.</summary>
    private static void GiveBoth(Pair p, int port, byte[] data, QSockAddr from)
    {
        int r;
        fixed (byte* d = data)
            r = Give(port, d, data.Length, (byte*)&from);
        int r2 = p.Net.Give(port, data, from);
        if (r != r2)
            p.Failure ??= $"give to {port}: the reference {r}, the port {r2}";
    }

    /// <summary>Every packet both sides wrote since the last check, in order, and the clocks.</summary>
    private static List<MemNet.Pkt> CompareOut(Pair p, string what)
    {
        var taken = new List<MemNet.Pkt>();
        int rc = OutCount(), pc = p.Net.Out.Count;
        if (rc != pc)
        {
            p.Failure ??= $"{what}: the reference wrote {rc} packets, the port {pc}";
            return taken;
        }
        byte* buf = stackalloc byte[Datagram.NetDatagramSize + 64];
        for (int i = 0; i < rc && p.Failure is null; i++)
        {
            int from;
            QSockAddr addr;
            int len = TakeOut(buf, Datagram.NetDatagramSize + 64, &from, (byte*)&addr);
            MemNet.Pkt q = p.Net.Out[i];
            QSockAddr qa = q.Addr;
            if (len != q.Data.Length || !new ReadOnlySpan<byte>(buf, len).SequenceEqual(q.Data) || from != q.From
                || !new ReadOnlySpan<byte>(&addr, 16).SequenceEqual(new ReadOnlySpan<byte>(&qa, 16)))
                p.Failure = $"{what}: packet {i} differs (reference {len} bytes from {from}, port {q.Data.Length} from {q.From}): "
                            + $"{Convert.ToHexString(new ReadOnlySpan<byte>(buf, Math.Min(len, 24)))} / {Convert.ToHexString(q.Data.AsSpan(0, Math.Min(q.Data.Length, 24)))}";
            taken.Add(q);
        }
        p.Net.Out.Clear();
        if (BitConverter.DoubleToInt64Bits(NetClock()) != BitConverter.DoubleToInt64Bits(p.Net.Clock))
            p.Failure ??= $"{what}: the clock {NetClock():R}, the port's {p.Net.Clock:R}";
        if (BitConverter.DoubleToInt64Bits(NetTime()) != BitConverter.DoubleToInt64Bits(p.Port.NetTime))
            p.Failure ??= $"{what}: net_time {NetTime():R}, the port's {p.Port.NetTime:R}";
        return taken;
    }

    private static void CompareMessage(Pair p, string what)
    {
        byte* buf = stackalloc byte[QSocket.NetMaxMessage];
        int n = MessageFn(buf, QSocket.NetMaxMessage);
        SizeBuf m = p.Port.NetMessage;
        if (n != m.CurSize || !new ReadOnlySpan<byte>(buf, n).SequenceEqual(new ReadOnlySpan<byte>(m.Data, m.CurSize)))
            p.Failure ??= $"{what}: net_message differs (reference {n} bytes, port {m.CurSize})";
    }

    private static void CompareSockets(Pair p, string what)
    {
        int* ints = stackalloc int[12];
        double* times = stackalloc double[3];
        QSockAddr addr;
        byte* address = stackalloc byte[64];
        for (int i = 0; i < Pool && p.Failure is null; i++)
        {
            StateFn(i, ints, times, (byte*)&addr, address);
            QSocket s = p.Port.Sockets[i];
            int[] mine = [s.Disconnected ? 1 : 0, s.CanSend ? 1 : 0, s.SendNext ? 1 : 0, s.Socket, (int)s.AckSequence,
                (int)s.SendSequence, (int)s.UnreliableSendSequence, s.SendMessageLength, (int)s.ReceiveSequence,
                (int)s.UnreliableReceiveSequence, s.ReceiveMessageLength, p.Net.ActiveConnections];
            for (int k = 0; k < 12; k++)
                if (ints[k] != mine[k])
                    p.Failure = $"{what}: socket {i} field {k}: reference {ints[k]}, port {mine[k]}";
            if (s.Disconnected)
                continue;
            double[] t = [s.ConnectTime, s.LastMessageTime, s.LastSendTime];
            for (int k = 0; k < 3; k++)
                if (BitConverter.DoubleToInt64Bits(times[k]) != BitConverter.DoubleToInt64Bits(t[k]))
                    p.Failure ??= $"{what}: socket {i} time {k}: reference {times[k]:R}, port {t[k]:R}";
            QSockAddr sa = s.Addr;
            if (!new ReadOnlySpan<byte>(&addr, 16).SequenceEqual(new ReadOnlySpan<byte>(&sa, 16)))
                p.Failure ??= $"{what}: socket {i} address differs";
            string a = MemNet.ZString(address);
            if (a != Encoding.Latin1.GetString(s.AddressText))
                p.Failure ??= $"{what}: socket {i} address text \"{a}\", port \"{Encoding.Latin1.GetString(s.AddressText)}\"";
        }
    }

    private static int Index(Pair p, QSocket? s)
    {
        if (s is null)
            return -1;
        for (int i = 0; i < Pool; i++)
            if (p.Port.Sockets[i] == s)
                return i;
        return -2;
    }

    private static void After(Pair p, string what)
    {
        CompareOut(p, what);
        CompareMessage(p, what);
        CompareSockets(p, what);
        int* c = stackalloc int[6];
        CountersFn(c);
        Datagram d = p.Port;
        int[] mine = [d.PacketsSent, d.PacketsReSent, d.PacketsReceived, d.ReceivedDuplicateCount, d.ShortPacketCount, d.DroppedDatagrams];
        for (int i = 0; i < 6; i++)
            if (c[i] != mine[i])
                p.Failure ??= $"{what}: counter {i}: reference {c[i]}, port {mine[i]}";
    }

    // ---- packets as a client or a server writes them

    private static byte[] Header(uint flags, int len, uint sequence)
    {
        byte[] b = new byte[Datagram.NetHeaderSize];
        uint v = (uint)len | flags;
        b[0] = (byte)(v >> 24); b[1] = (byte)(v >> 16); b[2] = (byte)(v >> 8); b[3] = (byte)v;
        b[4] = (byte)(sequence >> 24); b[5] = (byte)(sequence >> 16); b[6] = (byte)(sequence >> 8); b[7] = (byte)sequence;
        return b;
    }

    private static byte[] Control(int command, params byte[][] parts)
    {
        var b = new List<byte> { 0, 0, 0, 0, (byte)command };
        foreach (byte[] x in parts)
            b.AddRange(x);
        byte[] r = [.. b];
        uint v = Datagram.NetFlagCtl | (uint)r.Length;
        r[0] = (byte)(v >> 24); r[1] = (byte)(v >> 16); r[2] = (byte)(v >> 8); r[3] = (byte)v;
        return r;
    }

    private static byte[] Z(string s) => Encoding.Latin1.GetBytes(s + "\0");

    private static byte[] Long(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];

    // ---- the runs

    public static IEnumerable<TheoryDataRow<int>> Seeds => Enumerable.Range(1, 24).Select(s => new TheoryDataRow<int>(s));

    /// <summary>
    /// The server's side: connect requests (some repeated, some returning after a crash, some refused) on the accept
    /// socket, and on the connections the clients' packets — data in fragments with lost, repeated and reordered
    /// ones, unreliable ones in and out of order, acks for the server's own reliable messages (split over packets),
    /// forged and short packets, control packets on a connection — with the clock moved for the resends.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Server_side_matches_the_reference(int seed)
    {
        var rng = new Random(seed);
        Pair p = NewPair(100.0, 0.001, maxclients: 3 + rng.Next(6), listen: true);
        byte[] hostname = Z(rng.Next(2) == 0 ? "UNNAMED" : "a server"), map = Z("start");
        void ServerInfo()
        {
            fixed (byte* h = hostname, mp = map)
                ServerInfoFn(h, mp, p.Net.ActiveConnections);
        }
        ServerInfo();
        p.Net.HostnameText = hostname[..^1];
        p.Net.MapText = map[..^1];
        After(p, "reset");
        var conns = new List<(int Index, QSockAddr Client, uint Seq, uint UnreliableSeq)>();
        double clock = 100.0;
        int nextHost = 2;
        for (int step = 0; step < 1500 && p.Failure is null; step++)
        {
            int k = rng.Next(100);
            string what = $"step {step}";
            bool connect = conns.Count == 0 || conns.Count < 3 && k < 8;
            if (connect || k < 14)
            {   // a control packet on the accept socket: a new client's connect, or another request
                int kind = connect ? 6 : rng.Next(10);
                QSockAddr from = connect || conns.Count == 0 ? ClientAddr(nextHost++ % 250 + 2, 40000 + rng.Next(3))
                    : rng.Next(2) == 0 ? conns[rng.Next(conns.Count)].Client        // a repeated connect, or a returning client
                    : ClientAddr(2 + rng.Next(nextHost), 40000 + rng.Next(3));
                byte[] pkt = kind switch
                {
                    0 => Control(Datagram.CcreqServerInfo, Z("QUAKE")),
                    1 => Control(Datagram.CcreqServerInfo, Z("QUAKF")),
                    2 => Control(Datagram.CcreqConnect, Z("QUAKE"), [2]),
                    3 => [1, 2, 3],
                    4 => [0xff, 0xff, 0xff, 0xff, 1],
                    5 => rng.Next(2) == 0 ? Control(Datagram.CcreqServerInfo, Z("QUAKE"))
                        : Control(Datagram.CcreqConnect, Z("QUAKE"), [Datagram.NetProtocolVersion]),
                    _ => Control(Datagram.CcreqConnect, Z("QUAKE"), [Datagram.NetProtocolVersion]),
                };
                if (kind == 5)
                    pkt[3] ^= 1;    // a wrong length
                GiveBoth(p, Datagram.DefaultHostPort, pkt, from);
                int r = CheckNew();
                int r2 = Index(p, p.Port.CheckNewConnections());
                what += $" control {kind}";
                if (r != r2)
                    p.Failure ??= $"{what}: CheckNewConnections the reference {r}, the port {r2}";
                p.Count(r >= 0 ? "accepted" : kind == 6 ? "connect refused, repeated or returning" : $"control {kind}");
                if (r >= 0 && r == r2)
                    conns.Add((r, from, 0, 0));
                if (rng.Next(8) == 0)
                {   // the server's count of connections changes (SV_CheckForNewClients, SV_DropClient)
                    p.Net.ActiveConnections = Math.Clamp(p.Net.ActiveConnections + rng.Next(-1, 2), 0, 8);
                    ServerInfo();
                }
                After(p, what);
                conns.RemoveAll(cn => p.Port.Sockets[cn.Index].Disconnected);
                continue;
            }
            int c = rng.Next(conns.Count);
            (int idx, QSockAddr client, uint seq, uint useq) = conns[c];
            QSocket sock = p.Port.Sockets[idx];
            int serverPort = SocketPort(p, idx);
            if (k < 60)
            {   // packets from the client, then a read
                int n = 1 + rng.Next(4);
                for (int i = 0; i < n; i++)
                {
                    int m = rng.Next(14);
                    QSockAddr from = m == 0 ? ClientAddr(251, 40000) : client;   // forged
                    byte[] pkt;
                    if (m <= 4)
                    {   // data: a fragment, or the end of a message; sometimes an old sequence again, or one ahead
                        int d = rng.Next(6);
                        uint sq = d == 0 && seq > 0 ? seq - 1 : d == 1 ? seq + 1 : seq;
                        int len = rng.Next(rng.Next(6) == 0 ? Datagram.MaxDatagram : 40);
                        bool eom = rng.Next(3) != 0;
                        pkt = [.. Header(Datagram.NetFlagData | (eom ? Datagram.NetFlagEom : 0), Datagram.NetHeaderSize + len, sq), .. Bytes(rng, len)];
                        if (rng.Next(10) == 0 && len > 4)
                            pkt = pkt[..(pkt.Length - 3)];              // the header claims more than came: the buffer's old bytes
                        if (sq == seq && m != 0)
                            seq++;
                    }
                    else if (m <= 7)
                    {   // unreliable: ahead, in order or stale
                        uint sq = (uint)Math.Max(0, (int)useq + rng.Next(-2, 3));
                        int len = rng.Next(60);
                        pkt = [.. Header(Datagram.NetFlagUnreliable, Datagram.NetHeaderSize + len, sq), .. Bytes(rng, len)];
                        if (sq >= useq && m != 0)
                            useq = sq + 1;
                    }
                    else if (m <= 11)       // acks: of the last packet (mostly), of the one before, repeated
                        pkt = Header(Datagram.NetFlagAck, Datagram.NetHeaderSize, sock.SendSequence - (uint)(rng.Next(4) == 0 ? 1 : 0) - 1);
                    else if (m == 12)
                        pkt = Bytes(rng, rng.Next(Datagram.NetHeaderSize));   // short
                    else
                    {   // control on a connection, some with the flags of data, acks or unreliable ones too
                        pkt = Control(Datagram.CcreqServerInfo, Z("QUAKE"));
                        pkt[1] |= (byte)(rng.Next(8) << 1);
                    }
                    GiveBoth(p, serverPort, pkt, from);
                }
                int resent = p.Port.PacketsReSent;
                int r = GetMessageFn(idx);
                int r2 = p.Port.GetMessage(sock);
                p.Count($"read {r}");
                if (p.Port.PacketsReSent != resent)
                    p.Count("resend");
                what += " read";
                if (r != r2)
                    p.Failure ??= $"{what}: GetMessage the reference {r}, the port {r2}";
            }
            else if (k < 75)
            {   // a reliable message from the server, maybe over several packets
                int canSend = CanSendFn(idx), canSend2 = p.Port.CanSendMessage(sock) ? 1 : 0;
                what += " can send";
                if (canSend != canSend2)
                    p.Failure ??= $"{what}: the reference {canSend}, the port {canSend2}";
                p.Count(canSend == 1 ? "can send" : "cannot send");
                if (canSend == 1)
                {
                    byte[] msg = Bytes(rng, 1 + rng.Next(rng.Next(3) == 0 ? 3500 : 200));
                    var sb = new SizeBuf(QSocket.NetMaxMessage);
                    fixed (byte* d = msg)
                    {
                        sb.Write(d, msg.Length);
                        int r = SendFn(idx, d, msg.Length), r2 = p.Port.SendMessage(sock, sb);
                        p.Count(msg.Length > Datagram.MaxDatagram ? "reliable over packets" : "reliable in one");
                        what += $" send {msg.Length}";
                        if (r != r2)
                            p.Failure ??= $"{what}: SendMessage the reference {r}, the port {r2}";
                    }
                }
            }
            else if (k < 83)
            {   // an unreliable message from the server
                byte[] msg = Bytes(rng, 1 + rng.Next(Datagram.MaxDatagram));
                var sb = new SizeBuf(QSocket.NetMaxMessage);
                fixed (byte* d = msg)
                {
                    sb.Write(d, msg.Length);
                    int r = SendUnreliableFn(idx, d, msg.Length), r2 = p.Port.SendUnreliableMessage(sock, sb);
                    what += " unreliable";
                    if (r != r2)
                        p.Failure ??= $"{what}: SendUnreliableMessage the reference {r}, the port {r2}";
                }
            }
            else if (k < 97)
            {   // time passes: an unacked packet is resent at the next read
                clock += rng.Next(3) == 0 ? 1.25 : 0.2;
                SetTime(clock);
                p.Net.Clock = clock;
                p.Port.NetTime = clock;
                what += " time";
            }
            else
            {
                CloseFn(idx);
                p.Port.Close(sock);
                conns.RemoveAt(c);
                what += " close";
                p.Count("close");
                After(p, what);
                continue;
            }
            conns[c] = (idx, client, seq, useq);
            After(p, what);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"seed {seed}: {p.Stats}");
        Assert.True(p.Failure is null, $"seed {seed}: {p.Failure}");
    }

    private static int SocketPort(Pair p, int idx)
    {
        p.Net.GetSocketAddr(p.Port.Sockets[idx].Socket, out QSockAddr a);
        return p.Net.GetSocketPort(ref a);
    }

    private static byte[] Bytes(Random rng, int n)
    {
        byte[] b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>
    /// The client's handshake: the reply waiting before the connect (accept with a port, a reject's reason, a reply
    /// of another kind, one from another address, one of a wrong length), or none, so that the three tries of 2.5 s
    /// end with "No Response"; then data on the accepted connection both ways.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Client_side_matches_the_reference(int seed)
    {
        var rng = new Random(seed * 31 + 7);
        Pair p = NewPair(50.0, 0.25, maxclients: 4, listen: false);
        After(p, "reset");
        QSockAddr server = ClientAddr(1, Datagram.DefaultHostPort);
        for (int attempt = 0; attempt < 6 && p.Failure is null; attempt++)
        {
            int kind = rng.Next(7);
            // the next socket the connect opens has the next port (the control socket took 27001)
            int clientPort = 27002 + attempt;
            byte[]? reply = kind switch
            {
                0 => null,
                1 => Control(Datagram.CcrepReject, Z("Server is full.\n")),
                2 => Control(0x99),
                3 => [.. Control(Datagram.CcrepAccept, Long(28000 + attempt)), 0],
                _ => Control(Datagram.CcrepAccept, Long(28000 + attempt)),
            };
            if (reply != null)
            {
                QSockAddr from = kind == 3 ? server : rng.Next(5) == 0 ? ClientAddr(7, 26000) : server;
                GiveBoth(p, clientPort, reply, from);
            }
            byte[] host = Z(rng.Next(6) == 0 ? "nohost" : "10.0.0.1");
            int r, r2;
            fixed (byte* h = host)
            {
                r = ConnectFn(h);
                r2 = Index(p, p.Port.Connect(h));
            }
            string what = $"connect {attempt} ({kind})";
            if (r != r2)
                p.Failure ??= $"{what}: the reference {r}, the port {r2}";
            p.Count(r >= 0 ? "connected" : $"connect {kind} failed");
            After(p, what);
            if (r < 0 || r != r2)
                continue;
            // a few messages on the connection
            QSocket sock = p.Port.Sockets[r];
            QSockAddr peer = sock.Addr;
            uint seq = 0;
            for (int i = 0; i < 20 && p.Failure is null; i++)
            {
                byte[] data = Bytes(rng, rng.Next(30));
                byte[] pkt = rng.Next(2) == 0
                    ? [.. Header(Datagram.NetFlagData | Datagram.NetFlagEom, Datagram.NetHeaderSize + data.Length, seq++), .. data]
                    : [.. Header(Datagram.NetFlagUnreliable, Datagram.NetHeaderSize + data.Length, (uint)i), .. data];
                GiveBoth(p, clientPort, pkt, peer);
                int g = GetMessageFn(r), g2 = p.Port.GetMessage(sock);
                if (g != g2)
                    p.Failure ??= $"{what} read {i}: the reference {g}, the port {g2}";
                After(p, $"{what} read {i}");
            }
            CloseFn(r);
            p.Port.Close(sock);
            After(p, $"{what} close");
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"seed {seed}: {p.Stats}; printed: {p.Net.Printed.ToString().Replace("\n", "|")}");
        Assert.True(p.Failure is null, $"seed {seed}: {p.Failure}");
    }
}
