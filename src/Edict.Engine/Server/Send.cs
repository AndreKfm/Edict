// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// What the server sends its clients at the end of a frame (sv_main.c): SV_SendClientMessages with
/// SV_UpdateToReliableMessages, SV_SendClientDatagram, SV_WriteEntitiesToClient with SV_FatPVS, SV_SendNop and
/// SV_CleanupEnts; and SV_SendReconnect when a new map spawns. Every message goes to the client's connection, whose
/// bytes the messages lane hashes; a connection that fails or overflows drops its client.
/// </summary>
public sealed unsafe partial class Server
{
    private const int SvcNop = 1, SvcStufftext = 9;
    private const int UMoreBits = 1 << 0, UOrigin1 = 1 << 1, UAngle2 = 1 << 4, UNoLerp = 1 << 5, UFrame = 1 << 6,
        USignal = 1 << 7, UAngle1 = 1 << 8, UAngle3 = 1 << 9, UModel = 1 << 10, UColormap = 1 << 11, USkin = 1 << 12,
        UEffects = 1 << 13, ULongEntity = 1 << 14;

    private static readonly SysErrorException FatPvsTooLarge = new("SV_FatPVS: more leafs than fatpvs[MAX_MAP_LEAFS/8] holds");

    private SizeBuf _clientDatagram = null!;    // SV_SendClientDatagram's buf[MAX_DATAGRAM]; overflowing it stops
    private SizeBuf _nop = null!;               // SV_SendNop's buf[4]
    private SizeBuf _reconnect = null!;         // SV_SendReconnect's data[128]
    private byte[] _fatPvs = null!;             // fatpvs[MAX_MAP_LEAFS/8]
    private int _fatBytes;

    /// <summary>realtime (host.c): the host's clock, which SV_SendNop and last_message read.</summary>
    public double RealTime { get; set; }

    [NoAllocUnchecked("load path")]
    private void InitSend()
    {
        _clientDatagram = new SizeBuf(MaxDatagram);
        _nop = new SizeBuf(4);
        _reconnect = new SizeBuf(128);
        _fatPvs = new byte[MaxMapLeafs / 8];
    }

    // ---- the PVS of a client, fattened

    /// <summary>SV_AddToFatPVS: the PVS of every non-solid leaf within 8 units of <paramref name="org"/>.</summary>
    private void AddToFatPvs(in Vec3 org, int node)
    {
        Model* model = _world;
        while (true)
        {
            // if this is a leaf, accumulate the pvs bits
            if (node < 0)
            {
                int leaf = -1 - node;
                if (model->Leafs[leaf].Contents != Bsp.ContentsSolid)
                {
                    LeafPvs(leaf);
                    byte[] pvs = _decompressed;
                    for (int i = 0; i < _fatBytes; i++)
                        _fatPvs[i] |= pvs[i];
                }
                return;
            }

            MNode* n = model->Nodes + node;
            MPlane* plane = model->Planes + n->PlaneNum;
            float d = Vec3.Dot(org, plane->Normal) - plane->Dist;
            if (d > 8)
                node = n->Children[0];
            else if (d < -8)
                node = n->Children[1];
            else
            {   // go down both
                AddToFatPvs(org, n->Children[0]);
                node = n->Children[1];
            }
        }
    }

    /// <summary>
    /// SV_FatPVS: the inclusive or of the PVS of all leafs within 8 units of the point, over
    /// <c>(numleafs + 31) &gt;&gt; 3</c> bytes (three more than a PVS row; the bits past the row are never tested).
    /// </summary>
    private void FatPvs(in Vec3 org)
    {
        _fatBytes = (_world->NumLeafs + 31) >> 3;
        if (_fatBytes > _fatPvs.Length)
            throw FatPvsTooLarge;   // C writes past fatpvs
        new Span<byte>(_fatPvs, 0, _fatBytes).Clear();
        AddToFatPvs(org, 0);
    }

    // ---- entity updates

    /// <summary>
    /// SV_WriteEntitiesToClient: every entity with a model that touches the client's fat PVS (the client's own edict
    /// always), as its difference to the baseline, while 16 bytes are left in <paramref name="msg"/>.
    /// </summary>
    internal void WriteEntitiesToClient(int clent, SizeBuf msg)
    {
        // find the client's PVS
        float* c = Fields(clent);
        Vec3 org = Vec3.Add(*V3(c, EntVars.Origin), *V3(c, EntVars.ViewOfs));
        FatPvs(org);

        // send over all entities (except the client) that touch the pvs
        for (int e = 1; e < NumEdicts; e++)
        {
            EdictHeader* h = EdictNum(e);
            float* v = Fields(e);

            // ignore if not touching a PV leaf
            if (e != clent)     // clent is always sent
            {
                // ignore ents without visible models
                if (v[EntVars.Modelindex] == 0 || *String(((int*)v)[EntVars.Model]) == 0)
                    continue;

                int i;
                for (i = 0; i < h->NumLeafs; i++)
                    if ((_fatPvs[h->LeafNums[i] >> 3] & (1 << (h->LeafNums[i] & 7))) != 0)
                        break;
                if (i == h->NumLeafs)
                    continue;       // not visible
            }

            if (msg.MaxSize - msg.CurSize < 16)
                return;             // Con_Printf ("packet overflow\n")

            // send an update
            int bits = 0;
            for (int i = 0; i < 3; i++)
            {
                float miss = v[EntVars.Origin + i] - (&h->Baseline.Origin.X)[i];
                if (miss < -0.1 || miss > 0.1)
                    bits |= UOrigin1 << i;
            }

            if (v[EntVars.Angles] != h->Baseline.Angles.X)
                bits |= UAngle1;
            if (v[EntVars.Angles + 1] != h->Baseline.Angles.Y)
                bits |= UAngle2;
            if (v[EntVars.Angles + 2] != h->Baseline.Angles.Z)
                bits |= UAngle3;

            if (v[EntVars.Movetype] == MovetypeStep)
                bits |= UNoLerp;    // don't mess up the step animation

            // int against float: the int converts to float
            if (h->Baseline.Colormap != v[EntVars.Colormap])
                bits |= UColormap;
            if (h->Baseline.Skin != v[EntVars.Skin])
                bits |= USkin;
            if (h->Baseline.Frame != v[EntVars.Frame])
                bits |= UFrame;
            if (h->Baseline.Effects != v[EntVars.Effects])
                bits |= UEffects;
            if (h->Baseline.ModelIndex != v[EntVars.Modelindex])
                bits |= UModel;

            if (e >= 256)
                bits |= ULongEntity;
            if (bits >= 256)
                bits |= UMoreBits;

            // write the message
            msg.WriteByte(bits | USignal);
            if ((bits & UMoreBits) != 0)
                msg.WriteByte(bits >> 8);
            if ((bits & ULongEntity) != 0)
                msg.WriteShort(e);
            else
                msg.WriteByte(e);

            if ((bits & UModel) != 0)
                msg.WriteByte(CCast.I32(v[EntVars.Modelindex]));
            if ((bits & UFrame) != 0)
                msg.WriteByte(CCast.I32(v[EntVars.Frame]));
            if ((bits & UColormap) != 0)
                msg.WriteByte(CCast.I32(v[EntVars.Colormap]));
            if ((bits & USkin) != 0)
                msg.WriteByte(CCast.I32(v[EntVars.Skin]));
            if ((bits & UEffects) != 0)
                msg.WriteByte(CCast.I32(v[EntVars.Effects]));
            if ((bits & UOrigin1) != 0)
                msg.WriteCoord(v[EntVars.Origin]);
            if ((bits & UAngle1) != 0)
                msg.WriteAngle(v[EntVars.Angles]);
            if ((bits & (UOrigin1 << 1)) != 0)
                msg.WriteCoord(v[EntVars.Origin + 1]);
            if ((bits & UAngle2) != 0)
                msg.WriteAngle(v[EntVars.Angles + 1]);
            if ((bits & (UOrigin1 << 2)) != 0)
                msg.WriteCoord(v[EntVars.Origin + 2]);
            if ((bits & UAngle3) != 0)
                msg.WriteAngle(v[EntVars.Angles + 2]);
        }
    }

    /// <summary>SV_CleanupEnts: clear muzzle flashes.</summary>
    private void CleanupEnts()
    {
        for (int e = 1; e < NumEdicts; e++)
        {
            float* v = Fields(e);
            v[EntVars.Effects] = CCast.I32(v[EntVars.Effects]) & ~EfMuzzleFlash;
        }
    }

    // ---- sending

    /// <summary>
    /// SV_SendClientDatagram: svc_time, the client data, the entities, and the server datagram if there is room, as
    /// one unreliable message. C's buffer has an uninitialized allowoverflow; overflowing it (an entity update is up
    /// to 18 bytes, the check leaves 16) stops the port with an error.
    /// </summary>
    internal bool SendClientDatagram(Client client)
    {
        SizeBuf msg = _clientDatagram;
        msg.Clear();
        msg.WriteByte(SvcTime);
        msg.WriteFloat((float)Time);

        // add the client specific data to the datagram
        WriteClientdataToMessage(client.Edict, msg);
        WriteEntitiesToClient(client.Edict, msg);

        // copy the server datagram if there is space
        if (msg.CurSize + Datagram.CurSize < msg.MaxSize)
            msg.Write(Datagram.Data, Datagram.CurSize);

        // send the datagram
        if (client.NetConnection!.SendUnreliableMessage(msg) == -1)
        {
            DropClient(true);       // if the message couldn't send, kick off
            return false;
        }
        return true;
    }

    /// <summary>SV_UpdateToReliableMessages: frag changes to every active client, then the reliable datagram.</summary>
    private void UpdateToReliableMessages()
    {
        // check for changes to be sent over the reliable streams
        for (int i = 0; i < MaxClients; i++)
        {
            _hostClient = i;
            Client host = Clients[i];
            float frags = Fields(host.Edict)[EntVars.Frags];
            if (host.OldFrags != frags)
            {
                for (int j = 0; j < MaxClients; j++)
                {
                    Client c = Clients[j];
                    if (!c.Active)
                        continue;
                    c.Message.WriteByte(SvcUpdateFrags);
                    c.Message.WriteByte(i);
                    c.Message.WriteShort(CCast.I32(frags));
                }
                host.OldFrags = CCast.I32(frags);
            }
        }

        for (int j = 0; j < MaxClients; j++)
        {
            Client c = Clients[j];
            if (!c.Active)
                continue;
            c.Message.Write(ReliableDatagram.Data, ReliableDatagram.CurSize);
        }
        ReliableDatagram.Clear();
    }

    /// <summary>SV_SendNop: a nop without trashing or sending the accumulated client message buffer.</summary>
    private void SendNop(Client client)
    {
        SizeBuf msg = _nop;
        msg.Clear();
        msg.WriteChar(SvcNop);
        if (client.NetConnection!.SendUnreliableMessage(msg) == -1)
            DropClient(true);       // if the message couldn't send, kick off
        client.LastMessage = RealTime;
    }

    /// <summary>
    /// SV_SendClientMessages: the reliable updates, each spawned client's datagram, a nop to a client still signing
    /// on after five seconds of silence, the client's reliable message when there is one, SV_CleanupEnts.
    /// </summary>
    public void SendClientMessages()
    {
        // update frags, names, etc
        UpdateToReliableMessages();

        // build individual updates
        for (int i = 0; i < MaxClients; i++)
        {
            _hostClient = i;
            Client host = Clients[i];
            if (!host.Active)
                continue;

            if (host.Spawned)
            {
                if (!SendClientDatagram(host))
                    continue;
            }
            else
            {
                // the player isn't totally in the game yet: send small keepalive messages if too much time has
                // passed, a full message when the next signon stage has been requested
                if (!host.SendSignon)
                {
                    if (RealTime - host.LastMessage > 5)
                        SendNop(host);
                    continue;   // don't send out non-signon messages
                }
            }

            // check for an overflowed message. Should only happen on a very fucked up connection that backs up a lot,
            // then changes level
            if (host.Message.Overflowed)
            {
                DropClient(true);
                host.Message.Overflowed = false;
                continue;
            }

            if (host.Message.CurSize != 0 || host.DropAsap)
            {
                if (!host.NetConnection!.CanSendMessage())
                    continue;
                if (host.DropAsap)
                    DropClient(false);  // went to another level (nothing in WinQuake sets dropasap)
                else
                {
                    if (host.NetConnection.SendMessage(host.Message) == -1)
                        DropClient(true);   // if the message couldn't send, kick off
                    host.Message.Clear();
                    host.LastMessage = RealTime;
                    host.SendSignon = false;
                }
            }
        }

        // clear muzzle flashes
        CleanupEnts();
    }

    /// <summary>
    /// SV_SendReconnect: "reconnect" to every active client (NET_SendToAll, whose loop sends at once on the one
    /// driver there is). A dedicated server does not execute it itself.
    /// </summary>
    public void SendReconnect()
    {
        SizeBuf msg = _reconnect;
        msg.Clear();
        msg.WriteChar(SvcStufftext);
        fixed (byte* s = "reconnect\n\0"u8)
            msg.WriteString(s);
        for (int i = 0; i < MaxClients; i++)
        {
            Client c = Clients[i];
            if (c.NetConnection is null || !c.Active)
                continue;
            c.NetConnection.SendMessage(msg);
        }
    }
}
