// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// net_main.c as a game runs it (P6 step 9, outside the proof): the loopback (driver 0) and the datagram driver
/// over a LAN driver (driver 1), listening, NET_CheckNewConnections (the loopback's pending connect first, then, when
/// listening, the datagram driver's accept socket), NET_Connect ("local" is the loopback; any other name the
/// datagram driver's, without C's search of the LAN for a server of that name first), and NET_GetMessage,
/// NET_SendMessage, NET_CanSendMessage and NET_Close for the datagram's connections (<see cref="DatagramConnection"/>).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class NetMain : INetServer
{
    private readonly DatagramConnection[] _conns;

    /// <summary>The loopback: a listen server's local client.</summary>
    public Loopback Loop { get; }

    /// <summary>The datagram driver, or null without a LAN driver.</summary>
    public Datagram? Dgrm { get; }

    /// <summary>listening: whether the datagram driver's accept socket takes connections.</summary>
    public bool Listening { get; private set; }

    /// <summary>net_messagetimeout: the seconds without a message after which a connection is closed.</summary>
    public double MessageTimeout { get; set; } = 300;

    /// <summary>The server's answers to player and rule queries, or null.</summary>
    public IServerRules? Rules { get; set; }

    private readonly INetHost? _host;

    /// <summary>What the datagram driver reads of the host, or null.</summary>
    public INetHost? Host => _host;

    [NoAllocUnchecked("load path: one connection per qsocket of the pool")]
    public NetMain(Loopback loop, Datagram? dgrm, INetHost? host)
    {
        Loop = loop;
        Dgrm = dgrm;
        _host = host;
        _conns = new DatagramConnection[dgrm?.Sockets.Length ?? 0];
        for (int i = 0; i < _conns.Length; i++)
            _conns[i] = new DatagramConnection(this, dgrm!, dgrm!.Sockets[i]);
    }

    /// <summary>The connection of a qsocket of the pool.</summary>
    private DatagramConnection Of(QSocket s)
    {
        ReadOnlySpan<QSocket> pool = Dgrm!.Sockets;
        for (int i = 0; i < pool.Length; i++)
            if (pool[i] == s)
                return _conns[i];
        throw NotInPool;
    }

    private static readonly SysErrorException NotInPool = new("NetMain: a qsocket not of the pool");

    /// <summary>NET_Listen_f's work: every driver's listen switched (the loopback has none to switch).</summary>
    [NoAllocUnchecked("console command: listen")]
    public void Listen(bool state)
    {
        Listening = state;
        Dgrm?.Listen(state);
    }

    /// <summary>NET_CheckNewConnections.</summary>
    public IClientConnection? CheckNewConnections()
    {
        Dgrm?.SetNetTime();
        if (Loop.CheckNewConnections() is { } local)
            return local;
        if (Dgrm is { } d && Listening && d.CheckNewConnections(Rules) is { } s)
            return Of(s);
        return null;
    }

    private static bool IsLocal(byte* host)
    {
        ReadOnlySpan<byte> local = "local"u8;
        int i = 0;
        for (; i < local.Length; i++)
        {
            int c = host[i];
            if (c >= 'A' && c <= 'Z')
                c += 'a' - 'A';
            if (c != local[i])
                return false;
        }
        return host[i] == 0;
    }

    /// <summary>NET_Connect: "local" to the loopback, any other host through the datagram driver; null when it fails.</summary>
    [NoAllocUnchecked("connect")]
    public IClientConnection? Connect(byte* host)
    {
        Dgrm?.SetNetTime();
        if (host != null && host[0] == 0)
            host = null;
        if (host == null)
            return null;    // C searches the LAN (slist) for the one server there is
        if (IsLocal(host))
            return Loop.Connect();
        if (Dgrm is { } d && d.Connect(host) is { } s)
            return Of(s);
        return null;
    }

    internal void Print(ReadOnlySpan<byte> text) => _host?.Print(text);
}

/// <summary>net_hostport, which a LAN driver listens on (NET_Port_f, -port).</summary>
public interface IHostPort
{
    int HostPort { get; set; }
}

/// <summary>
/// A connection of the datagram driver as net_main.c's NET_* calls see it: a disconnected socket refuses (and says
/// so), net_time is set, a socket silent for net_messagetimeout seconds is closed (-1), and a message read is copied
/// into the reader's buffer (C has one net_message; the port's server and client each have theirs).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class DatagramConnection(NetMain net, Datagram dgrm, QSocket sock) : IClientConnection
{
    public QSocket Socket => sock;

    /// <summary>NET_GetMessage.</summary>
    public int GetMessage(SizeBuf netMessage)
    {
        if (sock.Disconnected)
        {
            net.Print("NET_GetMessage: disconnected socket\n"u8);
            return -1;
        }
        dgrm.SetNetTime();
        int ret = dgrm.GetMessage(sock);
        // see if this connection has timed out
        if (ret == 0 && dgrm.NetTime - sock.LastMessageTime > net.MessageTimeout)
        {
            dgrm.Close(sock);
            return -1;
        }
        if (ret > 0)
        {
            sock.LastMessageTime = dgrm.NetTime;
            SizeBuf m = dgrm.NetMessage;
            netMessage.Clear();
            netMessage.Write(m.Data, m.CurSize);
        }
        return ret;
    }

    /// <summary>NET_SendMessage.</summary>
    public int SendMessage(SizeBuf data)
    {
        if (sock.Disconnected)
        {
            net.Print("NET_SendMessage: disconnected socket\n"u8);
            return -1;
        }
        dgrm.SetNetTime();
        return dgrm.SendMessage(sock, data);
    }

    /// <summary>NET_SendUnreliableMessage.</summary>
    public int SendUnreliableMessage(SizeBuf data)
    {
        if (sock.Disconnected)
        {
            net.Print("NET_SendMessage: disconnected socket\n"u8);
            return -1;
        }
        dgrm.SetNetTime();
        return dgrm.SendUnreliableMessage(sock, data);
    }

    /// <summary>NET_CanSendMessage.</summary>
    public bool CanSendMessage()
    {
        if (sock.Disconnected)
            return false;
        dgrm.SetNetTime();
        return dgrm.CanSendMessage(sock);
    }

    /// <summary>NET_Close.</summary>
    public void Close() => dgrm.Close(sock);

    public ReadOnlySpan<byte> Address => sock.AddressText;
}
