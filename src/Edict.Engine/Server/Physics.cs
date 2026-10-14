// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// sv_phys.c without its QUAKE2 parts: SV_Physics and every movetype (pusher, none, noclip, step, toss, bounce, fly,
/// flymissile, and the client's walk, fly and noclip), SV_RunThink, SV_Impact, ClipVelocity, SV_FlyMove,
/// SV_AddGravity, SV_PushEntity, SV_PushMove, SV_CheckStuck, SV_CheckWater, SV_WallFriction, SV_TryUnstick,
/// SV_WalkMove, SV_CheckWaterTransition; SV_TestEntityPosition (world.c) and SV_StartSound (sv_main.c, a message).
/// host_frametime is a double in C; where C passes it to a float parameter (SV_FlyMove's time, VectorMA's and
/// VectorScale's scale) it is rounded to float there, as in C.
/// </summary>
public sealed unsafe partial class Server
{
    public const int MovetypeStep = 4, MovetypeFly = 5, MovetypeToss = 6, MovetypePush = 7, MovetypeFlyMissile = 9,
        MovetypeBounce = 10;
    public const int SolidNot = 0, SolidTrigger = 1, SolidBsp = 4;
    private const int ContentsEmpty = -1;
    private const double StopEpsilon = 0.1;
    private const int MaxClipPlanes = 5;
    private const int EfMuzzleFlash = 2;

    private static readonly SysErrorException FlyMoveNoEnt = new("SV_FlyMove: !trace.ent");
    private static readonly SysErrorException BadMovetype = new("SV_Physics: bad movetype");
    private static readonly SysErrorException BadClientMovetype = new("SV_Physics_client: bad movetype");
    private static readonly SysErrorException BadSound = new("SV_StartSound: volume, attenuation or channel out of range");

    private int _gravityField = -2;             // GetEdictFieldValue (ent, "gravity"): the field's offset, -1 none
    private int[] _movedEdict = null!;          // SV_PushMove's moved_edict[MAX_EDICTS], moved_from[MAX_EDICTS]
    private Vec3[] _movedFrom = null!;

    [NoAllocUnchecked("load path")]
    private void InitPhysics()
    {
        DDef* d = FindField("gravity"u8);
        _gravityField = d == null ? -1 : d->Ofs;
        _movedEdict = new int[MaxEdictsDefault];      // MAX_EDICTS, as C's arrays
        _movedFrom = new Vec3[MaxEdictsDefault];
        _varString = GC.AllocateArray<byte>(VarStringSize, pinned: true);
        CommandText = new SizeBuf(8192);         // cmd_text; the host loop shares its own
    }

    private static bool IsNan(float f) => (*(int*)&f & 0x7f800000) == 0x7f800000;    // IS_NAN: infinities too

    private Vec3* V3(float* v, int field) => (Vec3*)(v + field);

    /// <summary>SV_TestEntityPosition: whether the entity's box at its origin starts in something solid.</summary>
    public bool TestEntityPosition(int ent)
    {
        float* v = Fields(ent);
        Trace trace = Move(*V3(v, EntVars.Origin), *V3(v, EntVars.Mins), *V3(v, EntVars.Maxs), *V3(v, EntVars.Origin), 0, ent);
        return trace.StartSolid;
    }

    /// <summary>SV_CheckVelocity: NaN (and infinite) velocity and origin components become 0; velocity is bounded.</summary>
    private void CheckVelocity(float* v)
    {
        fixed (byte* n = "sv_maxvelocity\0"u8)
        {
            float max = Cvars.Value(n);
            for (int i = 0; i < 3; i++)
            {
                if (IsNan(v[EntVars.Velocity + i]))
                    v[EntVars.Velocity + i] = 0;    // Con_Printf ("Got a NaN velocity on %s\n", ...)
                if (IsNan(v[EntVars.Origin + i]))
                    v[EntVars.Origin + i] = 0;      // Con_Printf ("Got a NaN origin on %s\n", ...)
                if (v[EntVars.Velocity + i] > max)
                    v[EntVars.Velocity + i] = max;
                else if (v[EntVars.Velocity + i] < -max)
                    v[EntVars.Velocity + i] = -max;
            }
        }
    }

    /// <summary>SV_RunThink: the think function if its time has come in this frame; false if the entity removed itself.</summary>
    private bool RunThink(int ent)
    {
        float* v = Fields(ent);
        float thinktime = v[EntVars.Nextthink];
        if (thinktime <= 0 || thinktime > Time + FrameTime)
            return true;

        if (thinktime < Time)
            thinktime = (float)Time;   // don't let things stay in the past

        v[EntVars.Nextthink] = 0;
        *G(GlobalVars.Time) = thinktime;
        Globals[GlobalVars.Self] = ent * _edictSize;
        Globals[GlobalVars.Other] = 0;
        Hooks.ExecuteProgram(((int*)v)[EntVars.Think]);
        return EdictNum(ent)->Free == 0;
    }

    /// <summary>SV_Impact: both touch functions (of entities that are not SOLID_NOT), self and other restored after.</summary>
    private void Impact(int e1, int e2)
    {
        int oldSelf = Globals[GlobalVars.Self];
        int oldOther = Globals[GlobalVars.Other];

        *G(GlobalVars.Time) = (float)Time;
        float* v1 = Fields(e1);
        if (((int*)v1)[EntVars.Touch] != 0 && v1[EntVars.Solid] != SolidNot)
        {
            Globals[GlobalVars.Self] = e1 * _edictSize;
            Globals[GlobalVars.Other] = e2 * _edictSize;
            Hooks.ExecuteProgram(((int*)v1)[EntVars.Touch]);
        }

        float* v2 = Fields(e2);
        if (((int*)v2)[EntVars.Touch] != 0 && v2[EntVars.Solid] != SolidNot)
        {
            Globals[GlobalVars.Self] = e2 * _edictSize;
            Globals[GlobalVars.Other] = e1 * _edictSize;
            Hooks.ExecuteProgram(((int*)v2)[EntVars.Touch]);
        }

        Globals[GlobalVars.Self] = oldSelf;
        Globals[GlobalVars.Other] = oldOther;
    }

    /// <summary>ClipVelocity: slide off the plane; tiny components become 0. 1 = floor, 2 = step / wall.</summary>
    private static int ClipVelocity(in Vec3 @in, in Vec3 normal, out Vec3 @out, float overbounce)
    {
        int blocked = 0;
        if (normal.Z > 0)
            blocked |= 1;       // floor
        if (normal.Z == 0)
            blocked |= 2;       // step

        float backoff = Vec3.Dot(@in, normal) * overbounce;
        Vec3 inv = @in;         // in and out may be the same vector in C: in is read before out is written, per component
        Vec3 o;
        o.X = inv.X - normal.X * backoff;
        if (o.X > -StopEpsilon && o.X < StopEpsilon)
            o.X = 0;
        o.Y = inv.Y - normal.Y * backoff;
        if (o.Y > -StopEpsilon && o.Y < StopEpsilon)
            o.Y = 0;
        o.Z = inv.Z - normal.Z * backoff;
        if (o.Z > -StopEpsilon && o.Z < StopEpsilon)
            o.Z = 0;
        @out = o;
        return blocked;
    }

    /// <summary>
    /// SV_FlyMove: the basic solid body movement, sliding along up to five planes. Returns the clip flags (1 floor,
    /// 2 wall / step, 4 dead stop); <paramref name="steptrace"/> (if not null) gets the trace of a vertical wall hit.
    /// </summary>
    private int FlyMove(int ent, float time, Trace* steptrace)
    {
        float* v = Fields(ent);
        Vec3* velocity = V3(v, EntVars.Velocity);
        Vec3* origin = V3(v, EntVars.Origin);
        Vec3* planes = stackalloc Vec3[MaxClipPlanes];
        Vec3 newVelocity;
        int numbumps = 4;
        int blocked = 0;
        Vec3 originalVelocity = *velocity;
        Vec3 primalVelocity = *velocity;
        int numplanes = 0;
        float timeLeft = time;

        for (int bumpcount = 0; bumpcount < numbumps; bumpcount++)
        {
            if (velocity->X == 0 && velocity->Y == 0 && velocity->Z == 0)
                break;

            Vec3 end;
            end.X = origin->X + timeLeft * velocity->X;
            end.Y = origin->Y + timeLeft * velocity->Y;
            end.Z = origin->Z + timeLeft * velocity->Z;

            Trace trace = Move(*origin, *V3(v, EntVars.Mins), *V3(v, EntVars.Maxs), end, 0, ent);

            if (trace.AllSolid)
            {   // entity is trapped in another solid
                *velocity = default;
                return 3;
            }

            if (trace.Fraction > 0)
            {   // actually covered some distance
                *origin = trace.EndPos;
                originalVelocity = *velocity;
                numplanes = 0;
            }

            if (trace.Fraction == 1)
                break;      // moved the entire distance

            if (trace.Ent < 0)
                throw FlyMoveNoEnt;

            if (trace.Plane.Normal.Z > 0.7)
            {
                blocked |= 1;       // floor
                if (Fields(trace.Ent)[EntVars.Solid] == SolidBsp)
                {
                    v[EntVars.Flags] = CCast.I32(v[EntVars.Flags]) | FlOnGround;
                    ((int*)v)[EntVars.Groundentity] = trace.Ent * _edictSize;
                }
            }
            if (trace.Plane.Normal.Z == 0)
            {
                blocked |= 2;       // step
                if (steptrace != null)
                    *steptrace = trace;     // save for player extrafriction
            }

            // run the impact function
            Impact(ent, trace.Ent);
            if (EdictNum(ent)->Free != 0)
                break;      // removed by the impact function

            timeLeft -= timeLeft * trace.Fraction;

            // cliped to another plane
            if (numplanes >= MaxClipPlanes)
            {   // this shouldn't really happen
                *velocity = default;
                return 3;
            }

            planes[numplanes] = trace.Plane.Normal;
            numplanes++;

            // modify original_velocity so it parallels all of the clip planes
            int i, j;
            newVelocity = default;
            for (i = 0; i < numplanes; i++)
            {
                ClipVelocity(originalVelocity, planes[i], out newVelocity, 1);
                for (j = 0; j < numplanes; j++)
                    if (j != i)
                    {
                        if (Vec3.Dot(newVelocity, planes[j]) < 0)
                            break;  // not ok
                    }
                if (j == numplanes)
                    break;
            }

            if (i != numplanes)
            {   // go along this plane
                *velocity = newVelocity;
            }
            else
            {   // go along the crease
                if (numplanes != 2)
                {
                    *velocity = default;
                    return 7;
                }
                Vec3 dir = CrossProduct(planes[0], planes[1]);
                float d = Vec3.Dot(dir, *velocity);
                *velocity = new Vec3(dir.X * d, dir.Y * d, dir.Z * d);
            }

            // if original velocity is against the original velocity, stop dead to avoid tiny occilations in sloping corners
            if (Vec3.Dot(*velocity, primalVelocity) <= 0)
            {
                *velocity = default;
                return blocked;
            }
        }

        return blocked;
    }

    private static Vec3 CrossProduct(in Vec3 v1, in Vec3 v2) =>
        new(v1.Y * v2.Z - v1.Z * v2.Y, v1.Z * v2.X - v1.X * v2.Z, v1.X * v2.Y - v1.Y * v2.X);

    /// <summary>SV_AddGravity: velocity[2] -= gravity × sv_gravity × host_frametime (the gravity field if set, else 1).</summary>
    private void AddGravity(float* v)
    {
        float entGravity = _gravityField >= 0 && v[_gravityField] != 0 ? v[_gravityField] : 1.0f;
        fixed (byte* n = "sv_gravity\0"u8)
            v[EntVars.Velocity + 2] = (float)(v[EntVars.Velocity + 2] - entGravity * Cvars.Value(n) * FrameTime);
    }

    /// <summary>SV_PushEntity: moves the entity by <paramref name="push"/>, links it touching triggers, impact; velocity unchanged.</summary>
    private Trace PushEntity(int ent, in Vec3 push)
    {
        float* v = Fields(ent);
        Vec3 end = Vec3.Add(*V3(v, EntVars.Origin), push);
        Trace trace;
        if (v[EntVars.Movetype] == MovetypeFlyMissile)
            trace = Move(*V3(v, EntVars.Origin), *V3(v, EntVars.Mins), *V3(v, EntVars.Maxs), end, MoveMissile, ent);
        else if (v[EntVars.Solid] == SolidTrigger || v[EntVars.Solid] == SolidNot)
            // only clip against bmodels
            trace = Move(*V3(v, EntVars.Origin), *V3(v, EntVars.Mins), *V3(v, EntVars.Maxs), end, MoveNoMonsters, ent);
        else
            trace = Move(*V3(v, EntVars.Origin), *V3(v, EntVars.Mins), *V3(v, EntVars.Maxs), end, MoveNormal, ent);

        *V3(v, EntVars.Origin) = trace.EndPos;
        LinkEdict(ent, true);

        if (trace.Ent >= 0)
            Impact(ent, trace.Ent);

        return trace;
    }

    /// <summary>
    /// SV_PushMove: the pusher moves; what stands on it or is inside its new position is pushed along; if something
    /// blocks, everything moves back and the pusher's blocked function runs.
    /// </summary>
    private void PushMove(int pusher, float movetime)
    {
        float* pv = Fields(pusher);
        Vec3* pvel = V3(pv, EntVars.Velocity);
        if (pvel->X == 0 && pvel->Y == 0 && pvel->Z == 0)
        {
            pv[EntVars.Ltime] += movetime;
            return;
        }

        Vec3 move, mins, maxs;
        move.X = pvel->X * movetime;
        move.Y = pvel->Y * movetime;
        move.Z = pvel->Z * movetime;
        mins.X = pv[EntVars.Absmin] + move.X;
        mins.Y = pv[EntVars.Absmin + 1] + move.Y;
        mins.Z = pv[EntVars.Absmin + 2] + move.Z;
        maxs.X = pv[EntVars.Absmax] + move.X;
        maxs.Y = pv[EntVars.Absmax + 1] + move.Y;
        maxs.Z = pv[EntVars.Absmax + 2] + move.Z;

        Vec3 pushorig = *V3(pv, EntVars.Origin);

        // move the pusher to it's final position
        *V3(pv, EntVars.Origin) = Vec3.Add(*V3(pv, EntVars.Origin), move);
        pv[EntVars.Ltime] += movetime;
        LinkEdict(pusher, false);

        // see if any solid entities are inside the final position
        int numMoved = 0;
        for (int e = 1; e < NumEdicts; e++)
        {
            if (EdictNum(e)->Free != 0)
                continue;
            float* cv = Fields(e);
            if (cv[EntVars.Movetype] == MovetypePush || cv[EntVars.Movetype] == MovetypeNone || cv[EntVars.Movetype] == MovetypeNoclip)
                continue;

            // if the entity is standing on the pusher, it will definately be moved
            if (!((CCast.I32(cv[EntVars.Flags]) & FlOnGround) != 0 && ((int*)cv)[EntVars.Groundentity] == pusher * _edictSize))
            {
                if (cv[EntVars.Absmin] >= maxs.X || cv[EntVars.Absmin + 1] >= maxs.Y || cv[EntVars.Absmin + 2] >= maxs.Z
                    || cv[EntVars.Absmax] <= mins.X || cv[EntVars.Absmax + 1] <= mins.Y || cv[EntVars.Absmax + 2] <= mins.Z)
                    continue;

                // see if the ent's bbox is inside the pusher's final position
                if (!TestEntityPosition(e))
                    continue;
            }

            // remove the onground flag for non-players
            if (cv[EntVars.Movetype] != MovetypeWalk)
                cv[EntVars.Flags] = CCast.I32(cv[EntVars.Flags]) & ~FlOnGround;

            Vec3 entorig = *V3(cv, EntVars.Origin);
            _movedFrom[numMoved] = *V3(cv, EntVars.Origin);
            _movedEdict[numMoved] = e;
            numMoved++;

            // try moving the contacted entity
            pv[EntVars.Solid] = SolidNot;
            PushEntity(e, move);
            pv[EntVars.Solid] = SolidBsp;

            // if it is still inside the pusher, block
            if (TestEntityPosition(e))
            {   // fail the move
                if (cv[EntVars.Mins] == cv[EntVars.Maxs])
                    continue;
                if (cv[EntVars.Solid] == SolidNot || cv[EntVars.Solid] == SolidTrigger)
                {   // corpse
                    cv[EntVars.Mins] = cv[EntVars.Mins + 1] = 0;
                    *V3(cv, EntVars.Maxs) = *V3(cv, EntVars.Mins);
                    continue;
                }

                *V3(cv, EntVars.Origin) = entorig;
                LinkEdict(e, true);

                *V3(pv, EntVars.Origin) = pushorig;
                LinkEdict(pusher, false);
                pv[EntVars.Ltime] -= movetime;

                // if the pusher has a "blocked" function, call it; otherwise stay in place until the obstacle is gone
                if (((int*)pv)[EntVars.Blocked] != 0)
                {
                    Globals[GlobalVars.Self] = pusher * _edictSize;
                    Globals[GlobalVars.Other] = e * _edictSize;
                    Hooks.ExecuteProgram(((int*)pv)[EntVars.Blocked]);
                }

                // move back any entities we already moved
                for (int i = 0; i < numMoved; i++)
                {
                    *V3(Fields(_movedEdict[i]), EntVars.Origin) = _movedFrom[i];
                    LinkEdict(_movedEdict[i], false);
                }
                return;
            }
        }
    }

    /// <summary>SV_Physics_Pusher: moves up to the think time within the frame, then thinks.</summary>
    private void PhysicsPusher(int ent)
    {
        float* v = Fields(ent);
        float oldltime = v[EntVars.Ltime];
        float thinktime = v[EntVars.Nextthink];
        float movetime;
        if (thinktime < v[EntVars.Ltime] + FrameTime)
        {
            movetime = thinktime - v[EntVars.Ltime];
            if (movetime < 0)
                movetime = 0;
        }
        else
            movetime = (float)FrameTime;

        if (movetime != 0)
            PushMove(ent, movetime);    // advances ent->v.ltime if not blocked

        if (thinktime > oldltime && thinktime <= v[EntVars.Ltime])
        {
            v[EntVars.Nextthink] = 0;
            *G(GlobalVars.Time) = (float)Time;
            Globals[GlobalVars.Self] = ent * _edictSize;
            Globals[GlobalVars.Other] = 0;
            Hooks.ExecuteProgram(((int*)v)[EntVars.Think]);
        }
    }

    /// <summary>SV_CheckStuck: back to the old origin, or up to 17 units up and one to the side, if stuck in the world.</summary>
    private void CheckStuck(int ent)
    {
        float* v = Fields(ent);
        if (!TestEntityPosition(ent))
        {
            *V3(v, EntVars.Oldorigin) = *V3(v, EntVars.Origin);
            return;
        }

        Vec3 org = *V3(v, EntVars.Origin);
        *V3(v, EntVars.Origin) = *V3(v, EntVars.Oldorigin);
        if (!TestEntityPosition(ent))
        {
            LinkEdict(ent, true);       // Con_DPrintf ("Unstuck.\n")
            return;
        }

        for (int z = 0; z < 18; z++)
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    v[EntVars.Origin] = org.X + i;
                    v[EntVars.Origin + 1] = org.Y + j;
                    v[EntVars.Origin + 2] = org.Z + z;
                    if (!TestEntityPosition(ent))
                    {
                        LinkEdict(ent, true);   // Con_DPrintf ("Unstuck.\n")
                        return;
                    }
                }

        *V3(v, EntVars.Origin) = org;   // Con_DPrintf ("player is stuck.\n")
    }

    /// <summary>SV_CheckWater: waterlevel 0..3 from the contents at the feet, the middle and the eyes; true if above 1.</summary>
    private bool CheckWater(float* v)
    {
        Vec3 point;
        point.X = v[EntVars.Origin];
        point.Y = v[EntVars.Origin + 1];
        point.Z = v[EntVars.Origin + 2] + v[EntVars.Mins + 2] + 1;

        v[EntVars.Waterlevel] = 0;
        v[EntVars.Watertype] = ContentsEmpty;
        int cont = PointContents(point);
        if (cont <= Bsp.ContentsWater)
        {
            v[EntVars.Watertype] = cont;
            v[EntVars.Waterlevel] = 1;
            point.Z = (float)(v[EntVars.Origin + 2] + (v[EntVars.Mins + 2] + v[EntVars.Maxs + 2]) * 0.5);
            cont = PointContents(point);
            if (cont <= Bsp.ContentsWater)
            {
                v[EntVars.Waterlevel] = 2;
                point.Z = v[EntVars.Origin + 2] + v[EntVars.ViewOfs + 2];
                cont = PointContents(point);
                if (cont <= Bsp.ContentsWater)
                    v[EntVars.Waterlevel] = 3;
            }
        }

        return v[EntVars.Waterlevel] > 1;
    }

    /// <summary>SV_WallFriction: running into a wall at an angle cuts the velocity along it.</summary>
    private static void WallFriction(float* v, Trace* trace)
    {
        Mathlib.AngleVectors(*(Vec3*)(v + EntVars.VAngle), out Vec3 forward, out _, out _);
        float d = Vec3.Dot(trace->Plane.Normal, forward);

        d = (float)(d + 0.5);
        if (d >= 0)
            return;

        // cut the tangential velocity
        Vec3* velocity = (Vec3*)(v + EntVars.Velocity);
        float i = Vec3.Dot(trace->Plane.Normal, *velocity);
        Vec3 into = new(trace->Plane.Normal.X * i, trace->Plane.Normal.Y * i, trace->Plane.Normal.Z * i);
        Vec3 side = Vec3.Subtract(*velocity, into);

        velocity->X = side.X * (1 + d);
        velocity->Y = side.Y * (1 + d);
    }

    /// <summary>SV_TryUnstick: a stopped player is pushed two units each way in turn and the move retried.</summary>
    private int TryUnstick(int ent, in Vec3 oldvel)
    {
        float* v = Fields(ent);
        Vec3 oldorg = *V3(v, EntVars.Origin);
        Vec3 dir = default;
        Trace steptrace;

        for (int i = 0; i < 8; i++)
        {
            // try pushing a little in an axial direction
            switch (i)
            {
                case 0: dir.X = 2; dir.Y = 0; break;
                case 1: dir.X = 0; dir.Y = 2; break;
                case 2: dir.X = -2; dir.Y = 0; break;
                case 3: dir.X = 0; dir.Y = -2; break;
                case 4: dir.X = 2; dir.Y = 2; break;
                case 5: dir.X = -2; dir.Y = 2; break;
                case 6: dir.X = 2; dir.Y = -2; break;
                case 7: dir.X = -2; dir.Y = -2; break;
            }

            PushEntity(ent, dir);

            // retry the original move
            v[EntVars.Velocity] = oldvel.X;
            v[EntVars.Velocity + 1] = oldvel.Y;
            v[EntVars.Velocity + 2] = 0;
            int clip = FlyMove(ent, 0.1f, &steptrace);

            if (Math.Abs((double)(oldorg.Y - v[EntVars.Origin + 1])) > 4
                || Math.Abs((double)(oldorg.X - v[EntVars.Origin])) > 4)
                return clip;

            // go back to the original pos and try again
            *V3(v, EntVars.Origin) = oldorg;
        }

        *V3(v, EntVars.Velocity) = default;
        return 7;       // still not moving
    }

    private const int StepSizeWalk = 18;       // sv_phys.c's STEPSIZE

    /// <summary>
    /// SV_WalkMove (players only): a slide move; if it hit a step while on the ground, the move again from 18 units
    /// up and back down, kept if it ends on good ground.
    /// </summary>
    private void WalkMove(int ent)
    {
        float* v = Fields(ent);
        Trace steptrace = default, downtrace;

        // do a regular slide move unless it looks like you ran into a step
        int oldonground = CCast.I32(v[EntVars.Flags]) & FlOnGround;
        v[EntVars.Flags] = CCast.I32(v[EntVars.Flags]) & ~FlOnGround;

        Vec3 oldorg = *V3(v, EntVars.Origin);
        Vec3 oldvel = *V3(v, EntVars.Velocity);

        int clip = FlyMove(ent, (float)FrameTime, &steptrace);

        if ((clip & 2) == 0)
            return;     // move didn't block on a step

        if (oldonground == 0 && v[EntVars.Waterlevel] == 0)
            return;     // don't stair up while jumping

        if (v[EntVars.Movetype] != MovetypeWalk)
            return;     // gibbed by a trigger

        fixed (byte* n = "sv_nostep\0"u8)
            if (Cvars.Value(n) != 0)
                return;

        if ((CCast.I32(Fields(_svPlayer)[EntVars.Flags]) & FlWaterJump) != 0)
            return;

        Vec3 nosteporg = *V3(v, EntVars.Origin);
        Vec3 nostepvel = *V3(v, EntVars.Velocity);

        // try moving up and forward to go up a step
        *V3(v, EntVars.Origin) = oldorg;    // back to start pos

        Vec3 upmove = default, downmove = default;
        upmove.Z = StepSizeWalk;
        downmove.Z = (float)(-StepSizeWalk + oldvel.Z * FrameTime);

        // move up
        PushEntity(ent, upmove);    // FIXME: don't link?

        // move forward
        v[EntVars.Velocity] = oldvel.X;
        v[EntVars.Velocity + 1] = oldvel.Y;
        v[EntVars.Velocity + 2] = 0;
        clip = FlyMove(ent, (float)FrameTime, &steptrace);

        // check for stuckness, possibly due to the limited precision of floats in the clipping hulls
        if (clip != 0)
        {
            if (Math.Abs((double)(oldorg.Y - v[EntVars.Origin + 1])) < 0.03125
                && Math.Abs((double)(oldorg.X - v[EntVars.Origin])) < 0.03125)
            {   // stepping up didn't make any progress
                clip = TryUnstick(ent, oldvel);
            }
        }

        // extra friction based on view angle
        if ((clip & 2) != 0)
            WallFriction(v, &steptrace);

        // move down
        downtrace = PushEntity(ent, downmove);  // FIXME: don't link?

        if (downtrace.Plane.Normal.Z > 0.7)
        {
            if (v[EntVars.Solid] == SolidBsp)
            {
                v[EntVars.Flags] = CCast.I32(v[EntVars.Flags]) | FlOnGround;
                ((int*)v)[EntVars.Groundentity] = downtrace.Ent * _edictSize;
            }
        }
        else
        {
            // if the push down didn't end up on good ground, use the move without the step up
            *V3(v, EntVars.Origin) = nosteporg;
            *V3(v, EntVars.Velocity) = nostepvel;
        }
    }

    /// <summary>SV_Physics_Client: PlayerPreThink, the move by movetype, linking, PlayerPostThink.</summary>
    private void PhysicsClient(int ent, int num)
    {
        if (!Clients[num - 1].Active)
            return;     // unconnected slot

        float* v = Fields(ent);

        // call standard client pre-think
        *G(GlobalVars.Time) = (float)Time;
        Globals[GlobalVars.Self] = ent * _edictSize;
        Hooks.ExecuteProgram(Globals[GlobalVars.PlayerPreThink]);

        // do a move
        CheckVelocity(v);

        // decide which move function to call
        switch (CCast.I32(v[EntVars.Movetype]))
        {
            case MovetypeNone:
                if (!RunThink(ent))
                    return;
                break;

            case MovetypeWalk:
                if (!RunThink(ent))
                    return;
                if (!CheckWater(v) && (CCast.I32(v[EntVars.Flags]) & FlWaterJump) == 0)
                    AddGravity(v);
                CheckStuck(ent);
                WalkMove(ent);
                break;

            case MovetypeToss:
            case MovetypeBounce:
                PhysicsToss(ent);
                break;

            case MovetypeFly:
                if (!RunThink(ent))
                    return;
                FlyMove(ent, (float)FrameTime, null);
                break;

            case MovetypeNoclip:
                if (!RunThink(ent))
                    return;
                VectorMa(V3(v, EntVars.Origin), (float)FrameTime, *V3(v, EntVars.Velocity));
                break;

            default:
                throw BadClientMovetype;
        }

        // call standard player post-think
        LinkEdict(ent, true);

        *G(GlobalVars.Time) = (float)Time;
        Globals[GlobalVars.Self] = ent * _edictSize;
        Hooks.ExecuteProgram(Globals[GlobalVars.PlayerPostThink]);
    }

    /// <summary>VectorMA (veca, scale, vecb, veca): in place, in float.</summary>
    private static void VectorMa(Vec3* a, float scale, in Vec3 b)
    {
        a->X = a->X + scale * b.X;
        a->Y = a->Y + scale * b.Y;
        a->Z = a->Z + scale * b.Z;
    }

    /// <summary>SV_Physics_Noclip: thinks, then moves and turns without clipping.</summary>
    private void PhysicsNoclip(int ent)
    {
        // regular thinking
        if (!RunThink(ent))
            return;
        float* v = Fields(ent);
        VectorMa(V3(v, EntVars.Angles), (float)FrameTime, *V3(v, EntVars.Avelocity));
        VectorMa(V3(v, EntVars.Origin), (float)FrameTime, *V3(v, EntVars.Velocity));
        LinkEdict(ent, false);
    }

    /// <summary>
    /// SV_CheckWaterTransition: entering or leaving water plays a splash. Out of water C sets waterlevel to the
    /// contents value (negative), as here.
    /// </summary>
    private void CheckWaterTransition(int ent)
    {
        float* v = Fields(ent);
        int cont = PointContents(*V3(v, EntVars.Origin));
        if (v[EntVars.Watertype] == 0)
        {   // just spawned here
            v[EntVars.Watertype] = cont;
            v[EntVars.Waterlevel] = 1;
            return;
        }

        if (cont <= Bsp.ContentsWater)
        {
            if (v[EntVars.Watertype] == ContentsEmpty)
            {   // just crossed into water
                fixed (byte* s = "misc/h2ohit1.wav\0"u8)
                    StartSound(ent, 0, s, 255, 1);
            }
            v[EntVars.Watertype] = cont;
            v[EntVars.Waterlevel] = 1;
        }
        else
        {
            if (v[EntVars.Watertype] != ContentsEmpty)
            {   // just crossed into water
                fixed (byte* s = "misc/h2ohit1.wav\0"u8)
                    StartSound(ent, 0, s, 255, 1);
            }
            v[EntVars.Watertype] = ContentsEmpty;
            v[EntVars.Waterlevel] = cont;
        }
    }

    /// <summary>SV_Physics_Toss: toss, bounce, fly and flymissile: when on the ground, nothing.</summary>
    private void PhysicsToss(int ent)
    {
        // regular thinking
        if (!RunThink(ent))
            return;

        float* v = Fields(ent);

        // if onground, return without moving
        if ((CCast.I32(v[EntVars.Flags]) & FlOnGround) != 0)
            return;

        CheckVelocity(v);

        // add gravity
        if (v[EntVars.Movetype] != MovetypeFly && v[EntVars.Movetype] != MovetypeFlyMissile)
            AddGravity(v);

        // move angles
        VectorMa(V3(v, EntVars.Angles), (float)FrameTime, *V3(v, EntVars.Avelocity));

        // move origin
        float scale = (float)FrameTime;
        Vec3* vel = V3(v, EntVars.Velocity);
        Vec3 move = new(vel->X * scale, vel->Y * scale, vel->Z * scale);
        Trace trace = PushEntity(ent, move);
        if (trace.Fraction == 1)
            return;
        if (EdictNum(ent)->Free != 0)
            return;

        float backoff = v[EntVars.Movetype] == MovetypeBounce ? 1.5f : 1;

        ClipVelocity(*vel, trace.Plane.Normal, out *vel, backoff);

        // stop if on ground
        if (trace.Plane.Normal.Z > 0.7)
        {
            if (vel->Z < 60 || v[EntVars.Movetype] != MovetypeBounce)
            {
                v[EntVars.Flags] = CCast.I32(v[EntVars.Flags]) | FlOnGround;
                ((int*)v)[EntVars.Groundentity] = trace.Ent * _edictSize;
                *vel = default;
                *V3(v, EntVars.Avelocity) = default;
            }
        }

        // check for in water
        CheckWaterTransition(ent);
    }

    /// <summary>SV_Physics_Step: monsters fall when not on the ground (with a landing sound); otherwise they only think.</summary>
    private void PhysicsStep(int ent)
    {
        float* v = Fields(ent);

        // freefall if not onground
        if ((CCast.I32(v[EntVars.Flags]) & (FlOnGround | FlFly | FlSwim)) == 0)
        {
            bool hitsound;
            fixed (byte* n = "sv_gravity\0"u8)
                hitsound = v[EntVars.Velocity + 2] < Cvars.Value(n) * -0.1;

            AddGravity(v);
            CheckVelocity(v);
            FlyMove(ent, (float)FrameTime, null);
            LinkEdict(ent, true);

            if ((CCast.I32(v[EntVars.Flags]) & FlOnGround) != 0)  // just hit ground
            {
                if (hitsound)
                    fixed (byte* s = "demon/dland2.wav\0"u8)
                        StartSound(ent, 0, s, 255, 1);
            }
        }

        // regular thinking
        RunThink(ent);

        CheckWaterTransition(ent);
    }

    /// <summary>
    /// SV_Physics: StartFrame, then every live edict by its movetype (the clients' slots as clients), force_retouch
    /// counted down, and sv.time advanced by host_frametime.
    /// </summary>
    public void Physics()
    {
        // let the progs know that a new frame has started
        Globals[GlobalVars.Self] = 0;
        Globals[GlobalVars.Other] = 0;
        *G(GlobalVars.Time) = (float)Time;
        Hooks.ExecuteProgram(Globals[GlobalVars.StartFrame]);

        // treat each object in turn
        for (int i = 0; i < NumEdicts; i++)
        {
            if (EdictNum(i)->Free != 0)
                continue;

            float* v = Fields(i);
            if (*G(GlobalVars.ForceRetouch) != 0)
                LinkEdict(i, true);     // force retouch even for stationary

            float movetype = v[EntVars.Movetype];
            if (i > 0 && i <= MaxClients)
                PhysicsClient(i, i);
            else if (movetype == MovetypePush)
                PhysicsPusher(i);
            else if (movetype == MovetypeNone)
                RunThink(i);            // SV_Physics_None
            else if (movetype == MovetypeNoclip)
                PhysicsNoclip(i);
            else if (movetype == MovetypeStep)
                PhysicsStep(i);
            else if (movetype == MovetypeToss || movetype == MovetypeBounce || movetype == MovetypeFly
                     || movetype == MovetypeFlyMissile)
                PhysicsToss(i);
            else
                throw BadMovetype;
        }

        if (*G(GlobalVars.ForceRetouch) != 0)
            *G(GlobalVars.ForceRetouch) = *G(GlobalVars.ForceRetouch) - 1;

        Time += FrameTime;
    }

    private const int SvcSound = 6, SndVolume = 1, SndAttenuation = 2;
    private const int DefaultSoundPacketVolume = 255;
    private const float DefaultSoundPacketAttenuation = 1.0f;

    /// <summary>
    /// SV_StartSound: a sound message on the datagram, if there is room and the sound is precached (else a console
    /// message). Sys_Error for a volume, attenuation or channel out of range.
    /// </summary>
    public void StartSound(int entity, int channel, byte* sample, int volume, float attenuation)
    {
        if (volume < 0 || volume > 255 || attenuation < 0 || attenuation > 4 || channel < 0 || channel > 7)
            throw BadSound;

        if (Datagram.CurSize > MaxDatagram - 16)
            return;

        // find precache number for sound
        int soundNum;
        for (soundNum = 1; soundNum < MaxSounds && _soundPrecache[soundNum] != null; soundNum++)
            if (Same(sample, _soundPrecache[soundNum]))
                break;

        if (soundNum == MaxSounds || _soundPrecache[soundNum] == null)
            return;     // Con_Printf ("SV_StartSound: %s not precacheed\n", sample)

        channel = (entity << 3) | channel;

        int fieldMask = 0;
        if (volume != DefaultSoundPacketVolume)
            fieldMask |= SndVolume;
        if (attenuation != DefaultSoundPacketAttenuation)
            fieldMask |= SndAttenuation;

        // directed messages go only to the entity the are targeted on
        Datagram.WriteByte(SvcSound);
        Datagram.WriteByte(fieldMask);
        if ((fieldMask & SndVolume) != 0)
            Datagram.WriteByte(volume);
        if ((fieldMask & SndAttenuation) != 0)
            Datagram.WriteByte(CCast.I32(attenuation * 64));
        Datagram.WriteShort(channel);
        Datagram.WriteByte(soundNum);
        float* v = Fields(entity);
        for (int i = 0; i < 3; i++)
            Datagram.WriteCoord((float)(v[EntVars.Origin + i] + 0.5 * (v[EntVars.Mins + i] + v[EntVars.Maxs + i])));
    }

    private const int SvcParticle = 18;

    /// <summary>SV_StartParticle: a particle effect on the datagram, if there is room.</summary>
    public void StartParticle(in Vec3 org, in Vec3 dir, int color, int count)
    {
        if (Datagram.CurSize > MaxDatagram - 16)
            return;
        Datagram.WriteByte(SvcParticle);
        Datagram.WriteCoord(org.X);
        Datagram.WriteCoord(org.Y);
        Datagram.WriteCoord(org.Z);
        for (int i = 0; i < 3; i++)
        {
            int v = CCast.I32(Vec3.At(dir, i) * 16);
            if (v > 127)
                v = 127;
            else if (v < -128)
                v = -128;
            Datagram.WriteChar(v);
        }
        Datagram.WriteByte(count);
        Datagram.WriteByte(color);
    }

    /// <summary>SV_BroadcastPrintf ("%s", s): the text to every spawned client.</summary>
    public void BroadcastPrint(byte* s)
    {
        for (int i = 0; i < MaxClients; i++)
            if (Clients[i].Active && Clients[i].Spawned)
            {
                Clients[i].Message.WriteByte(SvcPrint);
                Clients[i].Message.WriteString(s);
            }
    }

    public const int VarStringSize = 256;
    private byte[] _varString = null!;          // PF_VarString's static out[256]

    /// <summary>PF_VarString's buffer.</summary>
    public byte* VarStringBuffer
    {
        get
        {
            fixed (byte* p = _varString)
                return p;
        }
    }

    /// <summary>svs.changelevel_issued: PF_changelevel issues one per level (SV_SpawnServer clears it).</summary>
    public bool ChangelevelIssued { get; set; }

    /// <summary>
    /// The host's command buffer (cmd_text), shared with the host loop that executes it; Cbuf_AddText appends,
    /// dropping text that does not fit (a console message in C).
    /// </summary>
    public SizeBuf CommandText { get; set; } = null!;

    /// <summary>
    /// The console a listen server's Con_Printf writes to (the local client's screen); null for a dedicated server,
    /// whose console output is stdout only (cls.state == ca_dedicated).
    /// </summary>
    public Screen? Con { get; set; }

    /// <summary>Cbuf_AddText.</summary>
    public void AddCommandText(byte* text)
    {
        int l = 0;
        while (text[l] != 0)
            l++;
        if (CommandText.CurSize + l >= CommandText.MaxSize)
        {
            Con?.Printf("Cbuf_AddText: overflow\n"u8);
            return;
        }
        CommandText.Write(text, l);
    }

    // ---- the end of SV_SpawnServer

    private const int SvcSpawnBaseline = 22;

    /// <summary>
    /// SV_CreateBaseline: every live edict with a model (and every client slot) gets its baseline, and the signon
    /// message the spawn baselines.
    /// </summary>
    public void CreateBaseline()
    {
        for (int entnum = 0; entnum < NumEdicts; entnum++)
        {
            // get the current server version
            EdictHeader* h = EdictNum(entnum);
            if (h->Free != 0)
                continue;
            float* v = Fields(entnum);
            if (entnum > MaxClients && v[EntVars.Modelindex] == 0)
                continue;

            // create entity baseline
            h->Baseline.Origin = *V3(v, EntVars.Origin);
            h->Baseline.Angles = *V3(v, EntVars.Angles);
            h->Baseline.Frame = CCast.I32(v[EntVars.Frame]);
            h->Baseline.Skin = CCast.I32(v[EntVars.Skin]);
            if (entnum > 0 && entnum <= MaxClients)
            {
                h->Baseline.Colormap = entnum;
                fixed (byte* player = "progs/player.mdl\0"u8)
                    h->Baseline.ModelIndex = SvModelIndex(player);
            }
            else
            {
                h->Baseline.Colormap = 0;
                h->Baseline.ModelIndex = SvModelIndex(String(((int*)v)[EntVars.Model]));
            }

            // add to the message
            Signon.WriteByte(SvcSpawnBaseline);
            Signon.WriteShort(entnum);
            Signon.WriteByte(h->Baseline.ModelIndex);
            Signon.WriteByte(h->Baseline.Frame);
            Signon.WriteByte(h->Baseline.Colormap);
            Signon.WriteByte(h->Baseline.Skin);
            Signon.WriteCoord(h->Baseline.Origin.X);
            Signon.WriteAngle(h->Baseline.Angles.X);
            Signon.WriteCoord(h->Baseline.Origin.Y);
            Signon.WriteAngle(h->Baseline.Angles.Y);
            Signon.WriteCoord(h->Baseline.Origin.Z);
            Signon.WriteAngle(h->Baseline.Angles.Z);
        }
    }

    /// <summary>SV_SpawnServer after its two settling frames: the baselines, then serverinfo to every connected client.</summary>
    public void FinishSpawn()
    {
        CreateBaseline();
        for (int i = 0; i < MaxClients; i++)
            if (Clients[i].Active)
                SendServerinfo(Clients[i]);
    }

    /// <summary>Host_ServerFrame: the frame start (clients), SV_Physics, SV_SendClientMessages.</summary>
    public void ServerFrame()
    {
        ServerFrameStart();
        if (!Paused)
            Physics();
        SendClientMessages();
    }
}
