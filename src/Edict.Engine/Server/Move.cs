// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// world.c's moves against the world and the linked entities (SV_ClipMoveToEntity per edict, SV_MoveBounds,
/// SV_ClipToLinks, SV_Move, SV_PointContents) and sv_move.c, monster movement (SV_CheckBottom, SV_movestep,
/// SV_StepDirection, SV_NewChaseDir, SV_CloseEnough, SV_MoveToGoal).
/// </summary>
public sealed unsafe partial class Server
{
    public const int MoveNormal = 0, MoveNoMonsters = 1, MoveMissile = 2;
    public const int FlFly = 1, FlSwim = 2, FlMonster = 32, FlOnGround = 512, FlPartialGround = 1024;
    public const int StepSize = 18;
    private const float DiNoDir = -1;

    private static readonly SysErrorException TriggerInClippingList = new("Trigger in clipping list");

    private ClipWorld _clip = null!;

    /// <summary>The hull tracing of phase 1, over sv.models; set up with the precache lists.</summary>
    private ClipWorld Clip => _clip;

    [NoAllocUnchecked("map load")]
    private void InitClip()
    {
        _clip?.Dispose();
        _clip = new ClipWorld();
    }

    /// <summary>The fields SV_HullForEntity reads, of edict n.</summary>
    private ClipEntity ClipEntityOf(int n)
    {
        float* v = Fields(n);
        return new ClipEntity
        {
            Index = n,
            Solid = v[EntVars.Solid],
            MoveType = v[EntVars.Movetype],
            ModelIndex = v[EntVars.Modelindex],
            Origin = *(Vec3*)(v + EntVars.Origin),
            Mins = *(Vec3*)(v + EntVars.Mins),
            Maxs = *(Vec3*)(v + EntVars.Maxs),
        };
    }

    /// <summary>SV_ClipMoveToEntity of edict n (the default hull check, docs/PLAN.md section C).</summary>
    public Trace ClipMoveToEntity(int n, in Vec3 start, in Vec3 mins, in Vec3 maxs, in Vec3 end) =>
        Clip.ClipMoveToEntity(ClipEntityOf(n), start, mins, maxs, end);

    private struct MoveClip
    {
        public Vec3 BoxMins, BoxMaxs;   // enclose the test object along entire move
        public Vec3 Mins, Maxs;         // size of the moving object
        public Vec3 Mins2, Maxs2;       // size when clipping against monsters
        public Vec3 Start, End;
        public Trace Trace;
        public int Type;
        public int PassEdict;           // -1: NULL
    }

    /// <summary>SV_MoveBounds: the box the move sweeps, one unit larger on every side.</summary>
    private static void MoveBounds(in Vec3 start, in Vec3 mins, in Vec3 maxs, in Vec3 end, out Vec3 boxmins, out Vec3 boxmaxs)
    {
        boxmins = default;
        boxmaxs = default;
        for (int i = 0; i < 3; i++)
        {
            float s = Vec3.At(start, i), e = Vec3.At(end, i);
            ref float bmin = ref (i == 0 ? ref boxmins.X : ref i == 1 ? ref boxmins.Y : ref boxmins.Z);
            ref float bmax = ref (i == 0 ? ref boxmaxs.X : ref i == 1 ? ref boxmaxs.Y : ref boxmaxs.Z);
            if (e > s)
            {
                bmin = s + Vec3.At(mins, i) - 1;
                bmax = e + Vec3.At(maxs, i) + 1;
            }
            else
            {
                bmin = e + Vec3.At(mins, i) - 1;
                bmax = s + Vec3.At(maxs, i) + 1;
            }
        }
    }

    /// <summary>SV_ClipToLinks: mins and maxs enclose the entire area swept by the move.</summary>
    private void ClipToLinks(int node, ref MoveClip clip)
    {
        int head = SolidHead(node);

        // touch linked edicts
        for (int l = _linkNext[head], next; l != head; l = next)
        {
            next = _linkNext[l];
            int touch = l;
            float* t = Fields(touch);
            if (t[EntVars.Solid] == Solid.Not)
                continue;
            if (touch == clip.PassEdict)
                continue;
            if (t[EntVars.Solid] == Solid.Trigger)
                throw TriggerInClippingList;

            if (clip.Type == MoveNoMonsters && t[EntVars.Solid] != Solid.Bsp)
                continue;

            if (clip.BoxMins.X > t[EntVars.Absmax]
                || clip.BoxMins.Y > t[EntVars.Absmax + 1]
                || clip.BoxMins.Z > t[EntVars.Absmax + 2]
                || clip.BoxMaxs.X < t[EntVars.Absmin]
                || clip.BoxMaxs.Y < t[EntVars.Absmin + 1]
                || clip.BoxMaxs.Z < t[EntVars.Absmin + 2])
                continue;

            if (clip.PassEdict >= 0 && Fields(clip.PassEdict)[EntVars.Size] != 0 && t[EntVars.Size] == 0)
                continue;   // points never interact

            // might intersect, so do an exact clip
            if (clip.Trace.AllSolid)
                return;
            if (clip.PassEdict >= 0)
            {
                if (((int*)t)[EntVars.Owner] == clip.PassEdict * EdictSize)
                    continue;   // don't clip against own missiles
                if (((int*)Fields(clip.PassEdict))[EntVars.Owner] == touch * EdictSize)
                    continue;   // don't clip against owner
            }

            Trace trace = (CCast.I32(t[EntVars.Flags]) & FlMonster) != 0
                ? ClipMoveToEntity(touch, clip.Start, clip.Mins2, clip.Maxs2, clip.End)
                : ClipMoveToEntity(touch, clip.Start, clip.Mins, clip.Maxs, clip.End);
            if (trace.AllSolid || trace.StartSolid || trace.Fraction < clip.Trace.Fraction)
            {
                trace.Ent = touch;
                if (clip.Trace.StartSolid)
                {
                    clip.Trace = trace;
                    clip.Trace.StartSolid = true;
                }
                else
                    clip.Trace = trace;
            }
            else if (trace.StartSolid)
                clip.Trace.StartSolid = true;
        }

        // recurse down both sides
        ref AreaNode n = ref _areaNodes[node];
        if (n.Axis == -1)
            return;

        if (Vec3.At(clip.BoxMaxs, n.Axis) > n.Dist)
            ClipToLinks(n.Child0, ref clip);
        if (Vec3.At(clip.BoxMins, n.Axis) < n.Dist)
            ClipToLinks(n.Child1, ref clip);
    }

    /// <summary>
    /// SV_Move: mins and maxs are relative; passedict is not clipped against (-1: none). If the entire move stays in a
    /// solid volume, trace.allsolid is set; if the starting point is in a solid, it is allowed to move out.
    /// </summary>
    public Trace Move(in Vec3 start, in Vec3 mins, in Vec3 maxs, in Vec3 end, int type, int passedict)
    {
        MoveClip clip = default;

        // clip to world
        clip.Trace = ClipMoveToEntity(0, start, mins, maxs, end);

        clip.Start = start;
        clip.End = end;
        clip.Mins = mins;
        clip.Maxs = maxs;
        clip.Type = type;
        clip.PassEdict = passedict;

        if (type == MoveMissile)
        {
            clip.Mins2 = new Vec3(-15, -15, -15);
            clip.Maxs2 = new Vec3(15, 15, 15);
        }
        else
        {
            clip.Mins2 = mins;
            clip.Maxs2 = maxs;
        }

        // create the bounding box of the entire move
        MoveBounds(start, clip.Mins2, clip.Maxs2, end, out clip.BoxMins, out clip.BoxMaxs);

        // clip to entities
        ClipToLinks(0, ref clip);

        return clip.Trace;
    }

    // SV_PointContents's answers by the point's bits: the world's hull 0 does not change while a server runs (a new map
    // is a new Server, and ClearWorld empties the cache), so the contents are a function of the point. A monster that
    // stands still asks for the same origin in every frame (SV_CheckWaterTransition). Keyed by the bits, so -0 and +0
    // are different keys, which is stricter than the walk needs.
    private const int PointCacheSize = 1024;
    private uint[] _pointKeys = null!;          // x, y, z bits per entry
    private int[] _pointContents = null!;       // 1 = empty entry (contents are never positive)

    [NoAllocUnchecked("map load")]
    private void ClearPointCache()
    {
        _pointKeys ??= new uint[3 * PointCacheSize];
        _pointContents ??= new int[PointCacheSize];
        Array.Fill(_pointContents, 1);
    }

    /// <summary>
    /// SV_PointContents: the world's hull 0 contents, currents counted as water. Over the packed nodes (the same planes
    /// and children, one load per visit) and through the point cache: SV_CheckWaterTransition calls it for every step
    /// monster in every frame.
    /// </summary>
    public int PointContents(in Vec3 p)
    {
        Vec3 q = p;
        uint x = *(uint*)&q.X, y = *(uint*)&q.Y, z = *(uint*)&q.Z;
        int slot = (int)(((x * 0x9E3779B1u) ^ (y * 0x85EBCA77u) ^ (z * 0xC2B2AE3Du)) >> 22);     // 10 bits: PointCacheSize
        int k = 3 * slot;
        if (_pointContents[slot] != 1 && _pointKeys[k] == x && _pointKeys[k + 1] == y && _pointKeys[k + 2] == z)
            return _pointContents[slot];
        int cont = WorldContents(p);
        _pointKeys[k] = x;
        _pointKeys[k + 1] = y;
        _pointKeys[k + 2] = z;
        _pointContents[slot] = cont;
        return cont;
    }

    internal int WorldContents(in Vec3 p)
    {
        int cont = HullTrace.PackedPointContents((Hull*)&_world->Hulls, 0, p);
        if (cont <= ContentsCurrent0 && cont >= ContentsCurrentDown)
            cont = Bsp.ContentsWater;
        return cont;
    }

    private const int ContentsCurrent0 = -9, ContentsCurrentDown = -14;

    /// <summary>
    /// SV_CheckBottom: false if any part of the bottom of the entity is off an edge that is not a staircase. The
    /// midpoint is <c>(mins + maxs)*0.5</c> with a double literal.
    /// </summary>
    public bool CheckBottom(int ent)
    {
        float* v = Fields(ent);
        Vec3 mins = Vec3.Add(*(Vec3*)(v + EntVars.Origin), *(Vec3*)(v + EntVars.Mins));
        Vec3 maxs = Vec3.Add(*(Vec3*)(v + EntVars.Origin), *(Vec3*)(v + EntVars.Maxs));
        Vec3 start, stop;

        // if all of the points under the corners are solid world, don't bother with the tougher checks
        // the corners must be within 16 of the midpoint
        start.Z = mins.Z - 1;
        for (int x = 0; x <= 1; x++)
            for (int y = 0; y <= 1; y++)
            {
                start.X = x != 0 ? maxs.X : mins.X;
                start.Y = y != 0 ? maxs.Y : mins.Y;
                if (PointContents(start) != Bsp.ContentsSolid)
                    goto realcheck;
            }

        return true;        // we got out easy

    realcheck:
        // check it for real...
        start.Z = mins.Z;

        // the midpoint must be within 16 of the bottom
        start.X = stop.X = (float)((mins.X + maxs.X) * 0.5);
        start.Y = stop.Y = (float)((mins.Y + maxs.Y) * 0.5);
        stop.Z = start.Z - 2 * StepSize;
        Trace trace = Move(start, default, default, stop, MoveNoMonsters, ent);

        if (trace.Fraction == 1.0)
            return false;
        float mid, bottom;
        mid = bottom = trace.EndPos.Z;

        // the corners must be within 16 of the midpoint
        for (int x = 0; x <= 1; x++)
            for (int y = 0; y <= 1; y++)
            {
                start.X = stop.X = x != 0 ? maxs.X : mins.X;
                start.Y = stop.Y = y != 0 ? maxs.Y : mins.Y;

                trace = Move(start, default, default, stop, MoveNoMonsters, ent);

                if (trace.Fraction != 1.0 && trace.EndPos.Z > bottom)
                    bottom = trace.EndPos.Z;
                if (trace.Fraction == 1.0 || mid - trace.EndPos.Z > StepSize)
                    return false;
            }

        return true;
    }

    private int FlagsOf(int ent) => CCast.I32(Fields(ent)[EntVars.Flags]);

    /// <summary>
    /// SV_movestep, called by monster program code: the move is adjusted for slopes and stairs; if it is not possible,
    /// nothing moves and false is returned.
    /// </summary>
    public bool Movestep(int ent, in Vec3 move, bool relink)
    {
        float* v = Fields(ent);
        ref Vec3 origin = ref *(Vec3*)(v + EntVars.Origin);
        Vec3 oldorg = origin;
        Vec3 neworg = Vec3.Add(origin, move);
        Trace trace;

        // flying monsters don't step up
        if ((FlagsOf(ent) & (FlSwim | FlFly)) != 0)
        {
            // try one move with vertical motion, then one without
            for (int i = 0; i < 2; i++)
            {
                neworg = Vec3.Add(origin, move);
                int enemy = ((int*)v)[EntVars.Enemy];
                if (i == 0 && enemy != 0)
                {
                    float dz = origin.Z - FieldsAt(enemy)[EntVars.Origin + 2];
                    if (dz > 40)
                        neworg.Z -= 8;
                    if (dz < 30)
                        neworg.Z += 8;
                }
                trace = Move(origin, *(Vec3*)(v + EntVars.Mins), *(Vec3*)(v + EntVars.Maxs), neworg, MoveNormal, ent);

                if (trace.Fraction == 1)
                {
                    if ((FlagsOf(ent) & FlSwim) != 0 && PointContents(trace.EndPos) == Bsp.ContentsEmpty)
                        return false;   // swim monster left water

                    origin = trace.EndPos;
                    if (relink)
                        LinkEdict(ent, true);
                    return true;
                }

                if (enemy == 0)
                    break;
            }

            return false;
        }

        // push down from a step height above the wished position
        neworg.Z += StepSize;
        Vec3 end = neworg;
        end.Z -= StepSize * 2;

        trace = Move(neworg, *(Vec3*)(v + EntVars.Mins), *(Vec3*)(v + EntVars.Maxs), end, MoveNormal, ent);

        if (trace.AllSolid)
            return false;

        if (trace.StartSolid)
        {
            neworg.Z -= StepSize;
            trace = Move(neworg, *(Vec3*)(v + EntVars.Mins), *(Vec3*)(v + EntVars.Maxs), end, MoveNormal, ent);
            if (trace.AllSolid || trace.StartSolid)
                return false;
        }
        if (trace.Fraction == 1)
        {
            // if monster had the ground pulled out, go ahead and fall
            if ((FlagsOf(ent) & FlPartialGround) != 0)
            {
                origin = Vec3.Add(origin, move);
                if (relink)
                    LinkEdict(ent, true);
                v[EntVars.Flags] = FlagsOf(ent) & ~FlOnGround;
                return true;
            }

            return false;       // walked off an edge
        }

        // check point traces down for dangling corners
        origin = trace.EndPos;

        if (!CheckBottom(ent))
        {
            if ((FlagsOf(ent) & FlPartialGround) != 0)
            {   // entity had floor mostly pulled out from underneath it and is trying to correct
                if (relink)
                    LinkEdict(ent, true);
                return true;
            }
            origin = oldorg;
            return false;
        }

        if ((FlagsOf(ent) & FlPartialGround) != 0)
            v[EntVars.Flags] = FlagsOf(ent) & ~FlPartialGround;
        ((int*)v)[EntVars.Groundentity] = trace.Ent * EdictSize;

        // the move is ok
        if (relink)
            LinkEdict(ent, true);
        return true;
    }

    /// <summary><c>yaw*M_PI*2 / 360</c> in double, stored to float, then the move <c>cos(yaw)*dist</c> in double.</summary>
    internal static Vec3 YawMove(float yaw, float dist)
    {
        yaw = (float)(yaw * Math.PI * 2 / 360);
        return new Vec3((float)(CMath.Cos(yaw) * dist), (float)(CMath.Sin(yaw) * dist), 0);
    }

    /// <summary>SV_StepDirection: turns to the movement direction, and walks the current distance if facing it.</summary>
    private bool StepDirection(int ent, float yaw, float dist)
    {
        float* v = Fields(ent);
        v[EntVars.IdealYaw] = yaw;
        Builtins.RunChangeYaw(this);        // PF_changeyaw on self, which is ent here

        Vec3 move = YawMove(yaw, dist);

        Vec3 oldorigin = *(Vec3*)(v + EntVars.Origin);
        if (Movestep(ent, move, false))
        {
            float delta = v[EntVars.Angles + Mathlib.Yaw] - v[EntVars.IdealYaw];
            if (delta > 45 && delta < 315)
            {   // not turned far enough, so don't take the step
                *(Vec3*)(v + EntVars.Origin) = oldorigin;
            }
            LinkEdict(ent, true);
            return true;
        }
        LinkEdict(ent, true);

        return false;
    }

    /// <summary>C's integer abs of a float argument: the float is truncated to int first (clang -Wabsolute-value).</summary>
    private static int CAbs(float f)
    {
        int i = CCast.I32(f);
        return i < 0 ? unchecked(-i) : i;
    }

    /// <summary>SV_NewChaseDir: rand draws as in C, the first one on every call.</summary>
    internal void NewChaseDir(int actor, int enemy, float dist)
    {
        float* a = Fields(actor);
        float d1, d2, tdir;

        float olddir = Mathlib.Anglemod(CCast.I32(a[EntVars.IdealYaw] / 45) * 45);
        float turnaround = Mathlib.Anglemod(olddir - 180);

        float deltax = FieldsAt(enemy * EdictSize)[EntVars.Origin] - a[EntVars.Origin];
        float deltay = FieldsAt(enemy * EdictSize)[EntVars.Origin + 1] - a[EntVars.Origin + 1];
        if (deltax > 10)
            d1 = 0;
        else if (deltax < -10)
            d1 = 180;
        else
            d1 = DiNoDir;
        if (deltay < -10)
            d2 = 270;
        else if (deltay > 10)
            d2 = 90;
        else
            d2 = DiNoDir;

        // try direct route
        if (d1 != DiNoDir && d2 != DiNoDir)
        {
            if (d1 == 0)
                tdir = d2 == 90 ? 45 : 315;
            else
                tdir = d2 == 90 ? 135 : 215;

            if (tdir != turnaround && StepDirection(actor, tdir, dist))
                return;
        }

        // try other directions
        if (((Rand.Next() & 3) & 1) != 0 || CAbs(deltay) > CAbs(deltax))
        {
            tdir = d1;
            d1 = d2;
            d2 = tdir;
        }

        if (d1 != DiNoDir && d1 != turnaround && StepDirection(actor, d1, dist))
            return;

        if (d2 != DiNoDir && d2 != turnaround && StepDirection(actor, d2, dist))
            return;

        // there is no direct path to the player, so pick another direction
        if (olddir != DiNoDir && StepDirection(actor, olddir, dist))
            return;

        if ((Rand.Next() & 1) != 0)     // randomly determine direction of search
        {
            for (tdir = 0; tdir <= 315; tdir += 45)
                if (tdir != turnaround && StepDirection(actor, tdir, dist))
                    return;
        }
        else
        {
            for (tdir = 315; tdir >= 0; tdir -= 45)
                if (tdir != turnaround && StepDirection(actor, tdir, dist))
                    return;
        }

        if (turnaround != DiNoDir && StepDirection(actor, turnaround, dist))
            return;

        a[EntVars.IdealYaw] = olddir;   // can't move

        // if a bridge was pulled out from underneath a monster, it may not have a valid standing position at all
        if (!CheckBottom(actor))
            a[EntVars.Flags] = FlagsOf(actor) | FlPartialGround;   // SV_FixCheckBottom
    }

    /// <summary>SV_CloseEnough.</summary>
    private bool CloseEnough(int ent, int goal, float dist)
    {
        float* e = Fields(ent), g = Fields(goal);
        for (int i = 0; i < 3; i++)
        {
            if (g[EntVars.Absmin + i] > e[EntVars.Absmax + i] + dist)
                return false;
            if (g[EntVars.Absmax + i] < e[EntVars.Absmin + i] - dist)
                return false;
        }
        return true;
    }

    /// <summary>SV_MoveToGoal (movetogoal builtin). Returns early without setting the return value, as in C.</summary>
    public void MoveToGoal()
    {
        int ent = Globals[GlobalVars.Self] / EdictSize;
        float* v = Fields(ent);
        int goal = ((int*)v)[EntVars.Goalentity] / EdictSize;
        float dist = *G(QcVm.OfsParm0);

        if ((FlagsOf(ent) & (FlOnGround | FlFly | FlSwim)) == 0)
        {
            *G(QcVm.OfsReturn) = 0;
            return;
        }

        // if the next step hits the enemy, return immediately
        if (((int*)v)[EntVars.Enemy] != 0 && CloseEnough(ent, goal, dist))
            return;

        // bump around...
        if ((Rand.Next() & 3) == 1 || !StepDirection(ent, v[EntVars.IdealYaw], dist))
            NewChaseDir(ent, goal, dist);
    }
}
