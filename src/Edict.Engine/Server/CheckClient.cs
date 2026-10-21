// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// PF_checkclient with its state (pr_cmds.c: <c>sv.lastcheck</c>, <c>sv.lastchecktime</c>, <c>checkpvs</c>), and
/// what it needs from model.c: Mod_PointInLeaf, Mod_DecompressVis, Mod_LeafPVS. The state is the server's own:
/// SV_SpawnServer clears <c>sv</c>, and <c>sv.time</c> starts at 1, so the first call on a map always picks a new
/// client and fills <c>checkpvs</c>.
/// </summary>
public sealed unsafe partial class Server
{
    public const int MaxMapLeafs = 8192;              // MAX_MAP_LEAFS
    public const int FlNoTarget = 128;                // FL_NOTARGET

    private static readonly SysErrorException BadVisData = new("Mod_DecompressVis: outside the visibility data");

    private readonly byte[] _checkPvs;            // checkpvs[MAX_MAP_LEAFS/8]
    private readonly byte[] _decompressed;        // Mod_DecompressVis's static buffer

    /// <summary>checkpvs, and Mod_DecompressVis's buffer after <see cref="LeafPvs"/> (for the level-1 tests).</summary>
    internal byte[] CheckPvs => _checkPvs;

    internal byte[] Decompressed => _decompressed;

    /// <summary><c>sv.lastcheck</c>: the client checkclient returns, if it might be visible.</summary>
    public int LastCheck { get; set; }

    /// <summary><c>sv.lastchecktime</c> (double).</summary>
    public double LastCheckTime { get; set; }

    /// <summary>Mod_PointInLeaf on the world: the index of the leaf <paramref name="p"/> is in.</summary>
    public int PointInLeaf(in Vec3 p)
    {
        Model* model = _world;
        int child = 0;
        while (child >= 0)
        {
            MNode* node = model->Nodes + child;
            MPlane* plane = model->Planes + node->PlaneNum;
            float d = Vec3.Dot(p, plane->Normal) - plane->Dist;
            if (d > 0)
                child = node->Children[0];
            else
                child = node->Children[1];
        }
        return -1 - child;
    }

    /// <summary>
    /// Mod_LeafPVS (Mod_DecompressVis for any leaf but leaf 0, which sees everything): the leaf's row of
    /// <c>(numleafs + 7) &gt;&gt; 3</c> bytes, into a buffer that the next call overwrites, as C's static one. On
    /// malformed data C reads past the visibility lump or writes past its buffer; the port stops with an error.
    /// </summary>
    internal void LeafPvs(int leaf)
    {
        Model* model = _world;
        byte[] decompressed = _decompressed;
        int row = (model->NumLeafs + 7) >> 3;
        int @out = 0;

        if (leaf == 0 || model->Leafs[leaf].VisOfs == -1)
        {   // mod_novis, or no vis info: all visible
            while (row != 0)
            {
                decompressed[@out++] = 0xff;
                row--;
            }
            return;
        }

        byte* vis = model->VisData;
        int i = model->Leafs[leaf].VisOfs, end = model->VisDataLength;
        do
        {
            if (i >= end)
                throw BadVisData;
            if (vis[i] != 0)
            {
                if (@out == decompressed.Length)
                    throw BadVisData;
                decompressed[@out++] = vis[i++];
                continue;
            }

            if (i + 1 >= end)
                throw BadVisData;
            int c = vis[i + 1];
            i += 2;
            while (c != 0)
            {
                if (@out == decompressed.Length)
                    throw BadVisData;
                decompressed[@out++] = 0;
                c--;
            }
        } while (@out < row);
    }

    /// <summary>PF_newcheckclient: the next living, targetable client after <paramref name="check"/>, and its PVS.</summary>
    private int NewCheckClient(int check)
    {
        int i;

        // cycle to the next one
        if (check < 1)
            check = 1;
        if (check > MaxClients)
            check = MaxClients;

        if (check == MaxClients)
            i = 1;
        else
            i = check + 1;

        for (; ; i++)
        {
            if (i == MaxClients + 1)
                i = 1;

            if (i == check)
                break;  // didn't find anything else

            float* f = Fields(i);
            if (EdictNum(i)->Free != 0)
                continue;
            if (f[EntVars.Health] <= 0)
                continue;
            if ((CCast.I32(f[EntVars.Flags]) & FlNoTarget) != 0)
                continue;

            // anything that is a client, or has a client as an enemy
            break;
        }

        // get the PVS for the entity
        float* v = Fields(i);
        Vec3 org = Vec3.Add(*(Vec3*)(v + EntVars.Origin), *(Vec3*)(v + EntVars.ViewOfs));
        int leaf = PointInLeaf(org);
        LeafPvs(leaf);
        int row = (_world->NumLeafs + 7) >> 3;
        for (int b = 0; b < row; b++)       // memcpy (checkpvs, pvs, row)
            _checkPvs[b] = _decompressed[b];

        return i;
    }

    /// <summary>
    /// PF_checkclient: a client that would be a valid target, cycled every 0.1 s; world if self's view point is not
    /// in the current client's PVS. Returns the edict number.
    /// </summary>
    public int CheckClient()
    {
        // find a new check if on a new frame
        if (Time - LastCheckTime >= 0.1)
        {
            LastCheck = NewCheckClient(LastCheck);
            LastCheckTime = Time;
        }

        // return check if it might be visible
        int ent = LastCheck;
        if (EdictNum(ent)->Free != 0 || Fields(ent)[EntVars.Health] <= 0)
            return 0;

        // if current entity can't possibly see the check entity, return 0
        float* self = Fields(Globals[GlobalVars.Self] / _edictSize);
        Vec3 view = Vec3.Add(*(Vec3*)(self + EntVars.Origin), *(Vec3*)(self + EntVars.ViewOfs));
        int l = PointInLeaf(view) - 1;
        if (l < 0 || (_checkPvs[l >> 3] & (1 << (l & 7))) == 0)
            return 0;

        // might be able to see it
        return ent;
    }
}
