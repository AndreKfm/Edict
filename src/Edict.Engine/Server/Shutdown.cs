// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>Host_ShutdownServer's part on the clients (host.c, P6 step 9): the last messages, svc_disconnect, the drops.</summary>
public sealed unsafe partial class Server
{
    private static readonly HostErrorException ShutdownWaits =
        new("Host_ShutdownServer: a client cannot take its last message, and C waits 3 s of Sys_FloatTime for it (the harness forever)");

    /// <summary>
    /// Host_ShutdownServer after sv.active is false and the local client disconnected: every client's pending message
    /// sent (a connection that cannot send is read, and the next pass tries again: C does so for 3 s), svc_disconnect
    /// to all (NET_SendToAll; the drivers the port has send at once), every active client dropped, svs.clients
    /// cleared.
    /// </summary>
    public void ShutdownClients(bool crash)
    {
        // flush any pending messages - like the score!!!
        int count, pass = 0;
        do
        {
            count = 0;
            for (int i = 0; i < MaxClients; i++)
            {
                _hostClient = i;
                Client host = Clients[i];
                if (host.Active && host.Message.CurSize != 0)
                {
                    if (host.NetConnection!.CanSendMessage())
                    {
                        host.NetConnection.SendMessage(host.Message);
                        host.Message.Clear();
                    }
                    else
                    {
                        host.NetConnection.GetMessage(NetMessage);
                        count++;
                    }
                }
            }
            // Sys_FloatTime does not advance in a host frame: a connection that could not send in this pass and did
            // not change by the read will not in the next
            if (count != 0 && ++pass > 1)
                throw ShutdownWaits;
        }
        while (count != 0);

        // make sure all the clients know we're disconnecting (NET_SendToAll: on driver 0 at once)
        SizeBuf buf = _nop;
        buf.Clear();
        buf.WriteByte(SvcDisconnect);
        for (int i = 0; i < MaxClients; i++)
        {
            _hostClient = i;
            Client host = Clients[i];
            if (host.NetConnection is { } conn && host.Active)
                conn.SendMessage(buf);
        }

        DropAll(crash);
    }

    private void DropAll(bool crash)
    {
        for (int i = 0; i < MaxClients; i++)
        {
            _hostClient = i;
            if (Clients[i].Active)
                DropClient(crash);
        }

        // clear structures: memset (svs.clients, 0, ...)
        for (int i = 0; i < MaxClients; i++)
            Clients[i].Clear();
    }

    /// <summary>
    /// <see cref="ShutdownClients"/> as a game runs it over the datagram driver (P6 step 9.4, outside the proof), with
    /// C's clock: the pending messages for at most 3 s of <paramref name="clock"/>, then NET_SendToAll (svc_disconnect,
    /// 5 s): a loopback connection gets it at once, a datagram one when it can send, and its ack is waited for.
    /// </summary>
    [NoAllocUnchecked("the server shut down")]
    public void ShutdownClients(bool crash, Func<double> clock)
    {
        // flush any pending messages - like the score!!!
        double start = clock();
        int count;
        do
        {
            count = 0;
            for (int i = 0; i < MaxClients; i++)
            {
                _hostClient = i;
                Client host = Clients[i];
                if (host.Active && host.Message.CurSize != 0)
                {
                    if (host.NetConnection!.CanSendMessage())
                    {
                        host.NetConnection.SendMessage(host.Message);
                        host.Message.Clear();
                    }
                    else
                    {
                        host.NetConnection.GetMessage(NetMessage);
                        count++;
                    }
                }
            }
            if (clock() - start > 3.0)
                break;
        }
        while (count != 0);

        // make sure all the clients know we're disconnecting
        SizeBuf buf = _nop;
        buf.Clear();
        buf.WriteByte(SvcDisconnect);
        count = SendToAll(buf, 5, clock);
        if (count != 0)
            Con?.Msg("Host_ShutdownServer: NET_SendToAll failed for "u8).MsgInt(count).Msg(" clients\n"u8).EndMsg();

        DropAll(crash);
    }

    /// <summary>NET_SendToAll: the reliable message to every active client, waiting up to blocktime seconds for the datagram ones.</summary>
    [NoAllocUnchecked("the server shut down")]
    private int SendToAll(SizeBuf data, double blocktime, Func<double> clock)
    {
        bool[] state1 = new bool[MaxClients], state2 = new bool[MaxClients];
        int count = 0;
        for (int i = 0; i < MaxClients; i++)
        {
            _hostClient = i;
            Client host = Clients[i];
            if (host.NetConnection is not { } conn)
            {
                state1[i] = state2[i] = true;   // C leaves them as the stack had them
                continue;
            }
            if (host.Active)
            {
                if (conn is not DatagramConnection)
                {
                    conn.SendMessage(data);
                    state1[i] = state2[i] = true;
                    continue;
                }
                count++;
                state1[i] = state2[i] = false;
            }
            else
                state1[i] = state2[i] = true;
        }

        double start = clock();
        while (count != 0)
        {
            count = 0;
            for (int i = 0; i < MaxClients; i++)
            {
                _hostClient = i;
                IClientConnection? conn = Clients[i].NetConnection;
                if (!state1[i])
                {
                    if (conn!.CanSendMessage())
                    {
                        state1[i] = true;
                        conn.SendMessage(data);
                    }
                    else
                        conn.GetMessage(NetMessage);
                    count++;
                    continue;
                }
                if (!state2[i])
                {
                    if (conn!.CanSendMessage())
                        state2[i] = true;
                    else
                        conn.GetMessage(NetMessage);
                    count++;
                }
            }
            if (clock() - start > blocktime)
                break;
        }
        return count;
    }
}
