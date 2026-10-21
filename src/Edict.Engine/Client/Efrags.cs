// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// r_efrag.c's state: the efrags of the static entities (R_AddEfrags, at CL_ParseStatic), which link a static to
/// every world leaf its box touches, so that the renderer's walk of the world can put the statics of the leafs it
/// sees into cl_visedicts (R_StoreEfrags). The lists are indices: efrags into cl_efrags, entities into
/// cl_static_entities, leafs into the world's leafs; -1 for NULL.
/// </summary>
public sealed unsafe partial class ClientState
{
    public const int MaxEfrags = 640;

    /// <summary>efrag_t: the leaf, the next efrag in the leaf, the entity (a static), the next efrag of the entity.</summary>
    public struct Efrag
    {
        public int Leaf, LeafNext, Entity, EntNext;
    }

    /// <summary>cl_efrags.</summary>
    public Efrag[] Efrags = null!;

    /// <summary>cl.free_efrags.</summary>
    public int FreeEfrag = -1;

    /// <summary>mleaf_t.efrags of the world's leafs (R_NewMap clears them).</summary>
    public int[] LeafEfrags = null!;

    /// <summary>entity_t.efrag of each static.</summary>
    public int[] StaticEfrag = null!;

    /// <summary>entity_t.visframe of each static: the renderer's frame that put it in cl_visedicts last.</summary>
    public int[] StaticVisFrame = null!;

    private Vec3 _emins, _emaxs;                      // r_emins, r_emaxs
    private int _addEnt, _lastLink;                   // r_addent; lastlink: -1 the entity's head, else that efrag's entnext

    [NoAllocUnchecked("load path")]
    private void InitEfrags()
    {
        Efrags = new Efrag[MaxEfrags];
        LeafEfrags = new int[MaxMapLeafs];
        StaticEfrag = new int[MaxStaticEntities];
        StaticVisFrame = new int[MaxStaticEntities];
    }

    /// <summary>CL_ClearState's part: cl_efrags zeroed and chained into the free list.</summary>
    private void ClearEfrags()
    {
        new Span<Efrag>(Efrags).Clear();
        FreeEfrag = 0;
        for (int i = 0; i < MaxEfrags - 1; i++)
            Efrags[i].EntNext = i + 1;
        Efrags[MaxEfrags - 1].EntNext = -1;
    }

    /// <summary>MAX_MAP_LEAFS (bspfile.h): the most leafs a map can have.</summary>
    public const int MaxMapLeafs = 8192;

    private static readonly SysErrorException TooManyLeafs = new("R_NewMap: a world with more leafs than MAX_MAP_LEAFS");

    /// <summary>R_NewMap's part: no efrag in any world leaf (the world is loaded anew, so all of them, not numleafs).</summary>
    private void NewMapEfrags()
    {
        int n = WorldModel->LeafCount;
        if (n > MaxMapLeafs)
            throw TooManyLeafs;
        new Span<int>(LeafEfrags, 0, n).Fill(-1);
    }

    /// <summary>R_AddEfrags: links a static to the leafs its box touches.</summary>
    private void AddEfrags(int stat)
    {
        ref ClientEntity ent = ref StaticEntities[stat];
        if (ent.Model == null)
            return;
        _addEnt = stat;
        _lastLink = -1;
        StaticEfrag[stat] = -1;
        Model* entmodel = ent.Model;
        _emins = new Vec3(ent.Origin.X + entmodel->Mins.X, ent.Origin.Y + entmodel->Mins.Y, ent.Origin.Z + entmodel->Mins.Z);
        _emaxs = new Vec3(ent.Origin.X + entmodel->Maxs.X, ent.Origin.Y + entmodel->Maxs.Y, ent.Origin.Z + entmodel->Maxs.Z);
        SplitEntityOnNode(0);
        // ent->topnode = r_pefragtopnode: the renderer's (R_DrawBEntitiesOnList)
    }

    /// <summary>R_SplitEntityOnNode: child is a node index, or -1 - leaf.</summary>
    private void SplitEntityOnNode(int child)
    {
        Model* world = WorldModel;
        if (child < 0)
        {
            int leaf = -1 - child;
            if (world->Leafs[leaf].Contents == Bsp.ContentsSolid)
                return;
            // grab an efrag off the free list
            int ef = FreeEfrag;
            if (ef < 0)
            {
                Con?.Printf("Too many efrags!\n"u8);
                return;     // no free fragments...
            }
            FreeEfrag = Efrags[ef].EntNext;
            Efrags[ef].Entity = _addEnt;
            // add the entity link
            if (_lastLink < 0)
                StaticEfrag[_addEnt] = ef;
            else
                Efrags[_lastLink].EntNext = ef;
            _lastLink = ef;
            Efrags[ef].EntNext = -1;
            // set the leaf links
            Efrags[ef].Leaf = leaf;
            Efrags[ef].LeafNext = LeafEfrags[leaf];
            LeafEfrags[leaf] = ef;
            return;
        }

        // NODE_MIXED
        MNode* node = world->Nodes + child;
        int sides = Mathlib.BoxOnPlaneSideMacro(_emins, _emaxs, world->Planes + node->PlaneNum);
        // sides 3: the first splitter (r_pefragtopnode), the renderer's
        if ((sides & 1) != 0)
            SplitEntityOnNode(node->Children[0]);
        if ((sides & 2) != 0)
            SplitEntityOnNode(node->Children[1]);
    }
}
