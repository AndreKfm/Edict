// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The engine around the server, for what the server's code calls out to: running QuakeC from engine code
/// (<c>PR_ExecuteProgram</c> in SV_TouchLinks and later physics) and the capture points the reference has around
/// linking. The engine's implementation runs the VM and ignores the capture points; the replay harness uses them to
/// check the port against a capture.
/// </summary>
public interface IServerHooks
{
    /// <summary>PR_ExecuteProgram (fnum) called from engine code.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void ExecuteProgram(int fnum);

    /// <summary>SV_LinkEdict starts (EDICT_CAPTURE_LINK).</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void Link(int e, bool touchTriggers);

    /// <summary>SV_LinkEdict returns (EDICT_CAPTURE_LINKED).</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void Linked(int e);

    /// <summary>SV_UnlinkEdict starts (EDICT_CAPTURE_UNLINK).</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void Unlink(int e);
}

/// <summary>Which list of an area node an edict is linked into.</summary>
public enum AreaList
{
    None, Solid, Trigger,
}

/// <summary>
/// world.c's area nodes and linking: SV_ClearWorld, SV_CreateAreaNode, SV_UnlinkEdict, SV_LinkEdict,
/// SV_FindTouchedLeafs, SV_TouchLinks. C's intrusive <c>link_t</c> lists are index lists: links 0..max_edicts-1 are
/// the edicts' area links, then two heads per area node (solid, trigger); -1 is C's NULL.
/// </summary>
public sealed unsafe partial class Server
{
    public const int AreaDepth = 4, AreaNodes = 32;   // AREA_DEPTH, AREA_NODES
    public const int FlItem = 256;                    // FL_ITEM

    private struct AreaNode
    {
        public int Axis;                              // -1 = leaf node
        public float Dist;
        public int Child0, Child1;
    }

    private readonly AreaNode[] _areaNodes;
    private int _numAreaNodes;
    private int[] _linkPrev, _linkNext;
    private Model* _world;

    /// <summary>The capture points and the engine's way of running QuakeC.</summary>
    public IServerHooks Hooks { get; set; } = null!;

    /// <summary>sv.worldmodel.</summary>
    public Model* WorldModel => _world;

    private int SolidHead(int node) => MaxEdicts + 2 * node;

    private int TriggerHead(int node) => MaxEdicts + 2 * node + 1;

    private void ClearLink(int l)
    {
        _linkPrev[l] = l;
        _linkNext[l] = l;
    }

    private void RemoveLink(int l)
    {
        _linkPrev[_linkNext[l]] = _linkPrev[l];
        _linkNext[_linkPrev[l]] = _linkNext[l];
    }

    private void InsertLinkBefore(int l, int before)
    {
        _linkNext[l] = before;
        _linkPrev[l] = _linkPrev[before];
        _linkNext[_linkPrev[l]] = l;
        _linkPrev[_linkNext[l]] = l;
    }

    /// <summary>SV_ClearWorld: after the world model has been loaded, before linking any entities (load path).</summary>
    [NoAllocUnchecked("map load")]
    public void ClearWorld(Model* world)
    {
        _world = world;
        ClearPointCache();
        _linkPrev = new int[MaxEdicts + 2 * AreaNodes];
        _linkNext = new int[MaxEdicts + 2 * AreaNodes];
        Array.Fill(_linkPrev, -1);
        Array.Fill(_linkNext, -1);
        Array.Clear(_areaNodes);
        _numAreaNodes = 0;
        CreateAreaNode(0, world->Mins, world->Maxs);
        InitClip();             // SV_InitBoxHull
        for (int i = 0; i < MaxModels; i++)
            Clip.Models[i] = _models[i];
    }

    /// <summary>SV_CreateAreaNode: the split is <c>0.5 * (maxs + mins)</c>, a double product stored to float.</summary>
    private int CreateAreaNode(int depth, Vec3 mins, Vec3 maxs)
    {
        int n = _numAreaNodes++;
        ref AreaNode anode = ref _areaNodes[n];

        ClearLink(TriggerHead(n));
        ClearLink(SolidHead(n));

        if (depth == AreaDepth)
        {
            anode.Axis = -1;
            anode.Child0 = anode.Child1 = -1;
            return n;
        }

        Vec3 size = Vec3.Subtract(maxs, mins);
        anode.Axis = size.X > size.Y ? 0 : 1;

        anode.Dist = (float)(0.5 * (Vec3.At(maxs, anode.Axis) + Vec3.At(mins, anode.Axis)));
        Vec3 mins1 = mins, mins2 = mins, maxs1 = maxs, maxs2 = maxs;
        if (anode.Axis == 0)
            maxs1.X = mins2.X = anode.Dist;
        else
            maxs1.Y = mins2.Y = anode.Dist;

        int c0 = CreateAreaNode(depth + 1, mins2, maxs2);
        int c1 = CreateAreaNode(depth + 1, mins1, maxs1);
        _areaNodes[n].Child0 = c0;
        _areaNodes[n].Child1 = c1;
        return n;
    }

    /// <summary>Whether edict n is linked into an area node (<c>ent->area.prev</c>).</summary>
    public bool IsLinked(int n) => _linkPrev[n] != -1;

    /// <summary>The area node and list edict n is linked into, or (-1, None).</summary>
    public (int Node, AreaList List) AreaOf(int n)
    {
        if (_linkPrev[n] == -1)
            return (-1, AreaList.None);
        int l = _linkNext[n];
        while (l < MaxEdicts)
            l = _linkNext[l];
        int h = l - MaxEdicts;
        return (h / 2, (h & 1) == 0 ? AreaList.Solid : AreaList.Trigger);
    }

    /// <summary>The edict before edict n in its area list, or -1 if n is first (or not linked).</summary>
    public int PreviousInArea(int n)
    {
        int p = _linkPrev[n];
        return p < 0 || p >= MaxEdicts ? -1 : p;
    }

    /// <summary>SV_UnlinkEdict: call before removing an entity, and before trying to move one.</summary>
    public void UnlinkEdict(int n)
    {
        Hooks.Unlink(n);
        if (_linkPrev[n] == -1)
            return;     // not linked in anywhere
        RemoveLink(n);
        _linkPrev[n] = _linkNext[n] = -1;
    }

    /// <summary>
    /// SV_TouchLinks: the trigger edicts at this node and below whose boxes the edict's box touches run their touch
    /// function, with self the trigger and other the edict.
    /// </summary>
    private void TouchLinks(int ent, int node)
    {
        float* e = Fields(ent);
        int head = TriggerHead(node);

        // touch linked edicts
        for (int l = _linkNext[head], next; l != head; l = next)
        {
            next = _linkNext[l];
            int touch = l;
            if (touch == ent)
                continue;
            float* t = Fields(touch);
            if (((int*)t)[EntVars.Touch] == 0 || t[EntVars.Solid] != Solid.Trigger)
                continue;
            if (e[EntVars.Absmin] > t[EntVars.Absmax]
                || e[EntVars.Absmin + 1] > t[EntVars.Absmax + 1]
                || e[EntVars.Absmin + 2] > t[EntVars.Absmax + 2]
                || e[EntVars.Absmax] < t[EntVars.Absmin]
                || e[EntVars.Absmax + 1] < t[EntVars.Absmin + 1]
                || e[EntVars.Absmax + 2] < t[EntVars.Absmin + 2])
                continue;
            int oldSelf = Globals[GlobalVars.Self];
            int oldOther = Globals[GlobalVars.Other];

            Globals[GlobalVars.Self] = touch * EdictSize;
            Globals[GlobalVars.Other] = ent * EdictSize;
            *G(GlobalVars.Time) = (float)Time;
            Hooks.ExecuteProgram(((int*)t)[EntVars.Touch]);

            Globals[GlobalVars.Self] = oldSelf;
            Globals[GlobalVars.Other] = oldOther;
        }

        // recurse down both sides
        ref AreaNode n = ref _areaNodes[node];
        if (n.Axis == -1)
            return;

        if (e[EntVars.Absmax + n.Axis] > n.Dist)
            TouchLinks(ent, n.Child0);
        if (e[EntVars.Absmin + n.Axis] < n.Dist)
            TouchLinks(ent, n.Child1);
    }

    /// <summary>
    /// SV_FindTouchedLeafs: the world leafs the edict's box touches, at most MAX_ENT_LEAFS. <paramref name="child"/>
    /// is a node index, or -1 - leaf index (<see cref="MNode"/>).
    /// </summary>
    private void FindTouchedLeafs(int ent, int child)
    {
        EdictHeader* h = EdictNum(ent);
        if (child < 0)
        {
            int leaf = -1 - child;
            if (_world->Leafs[leaf].Contents == Bsp.ContentsSolid)
                return;
            // add an efrag if the node is a leaf
            if (h->NumLeafs == EdictHeader.MaxEntLeafs)
                return;
            h->LeafNums[h->NumLeafs] = (short)(leaf - 1);
            h->NumLeafs++;
            return;
        }

        // NODE_MIXED
        MNode* node = _world->Nodes + child;
        MPlane* splitplane = _world->Planes + node->PlaneNum;
        float* e = Fields(ent);
        int sides = Mathlib.BoxOnPlaneSideMacro(*(Vec3*)(e + EntVars.Absmin), *(Vec3*)(e + EntVars.Absmax), splitplane);

        // recurse down the contacted sides
        if ((sides & 1) != 0)
            FindTouchedLeafs(ent, node->Children[0]);

        if ((sides & 2) != 0)
            FindTouchedLeafs(ent, node->Children[1]);
    }

    /// <summary>SV_LinkEdict, with the capture points of the reference's wrapper (patch 0004) around it.</summary>
    public void LinkEdict(int ent, bool touchTriggers)
    {
        Hooks.Link(ent, touchTriggers);
        LinkEdict_(ent, touchTriggers);
        Hooks.Linked(ent);
    }

    private void LinkEdict_(int ent, bool touchTriggers)
    {
        if (_linkPrev[ent] != -1)
            UnlinkEdict(ent);   // unlink from old position

        if (ent == 0)
            return;             // don't add the world

        if (EdictNum(ent)->Free != 0)
            return;

        // set the abs box
        float* v = Fields(ent);
        for (int i = 0; i < 3; i++)
        {
            v[EntVars.Absmin + i] = v[EntVars.Origin + i] + v[EntVars.Mins + i];
            v[EntVars.Absmax + i] = v[EntVars.Origin + i] + v[EntVars.Maxs + i];
        }

        // to make items easier to pick up and allow them to be grabbed off of shelves, the abs sizes are expanded
        if ((CCast.I32(v[EntVars.Flags]) & FlItem) != 0)
        {
            v[EntVars.Absmin] -= 15;
            v[EntVars.Absmin + 1] -= 15;
            v[EntVars.Absmax] += 15;
            v[EntVars.Absmax + 1] += 15;
        }
        else
        {   // because movement is clipped an epsilon away from an actual edge,
            // we must fully check even when bounding boxes don't quite touch
            v[EntVars.Absmin] -= 1;
            v[EntVars.Absmin + 1] -= 1;
            v[EntVars.Absmin + 2] -= 1;
            v[EntVars.Absmax] += 1;
            v[EntVars.Absmax + 1] += 1;
            v[EntVars.Absmax + 2] += 1;
        }

        // link to PVS leafs
        EdictNum(ent)->NumLeafs = 0;
        if (v[EntVars.Modelindex] != 0)
            FindTouchedLeafs(ent, 0);

        if (v[EntVars.Solid] == Solid.Not)
            return;

        // find the first node that the ent's box crosses
        int node = 0;
        while (true)
        {
            ref AreaNode n = ref _areaNodes[node];
            if (n.Axis == -1)
                break;
            if (v[EntVars.Absmin + n.Axis] > n.Dist)
                node = n.Child0;
            else if (v[EntVars.Absmax + n.Axis] < n.Dist)
                node = n.Child1;
            else
                break;          // crosses the node
        }

        // link it in
        if (v[EntVars.Solid] == Solid.Trigger)
            InsertLinkBefore(ent, TriggerHead(node));
        else
            InsertLinkBefore(ent, SolidHead(node));

        // if touch_triggers, touch all entities at this node and decend for more
        if (touchTriggers)
            TouchLinks(ent, 0);
    }
}
