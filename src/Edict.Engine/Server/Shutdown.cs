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
}
