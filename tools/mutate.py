# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Source-level known-bad variants (docs/TESTING.md R7) for engine code that has no seam to swap it in a test:
# each mutant is one textual change in src/. The tree is copied once to a work directory; per mutant the file is
# restored, the change applied, the engine tests built and the given test classes run. A mutant must make them fail
# ("detected"); one that passes is a blind spot of the gates. A mutant whose text no longer matches is an error.
#
# Usage: python3 -I tools/mutate.py <work dir> [group ...]      (groups below; default all)
# The tests read the corpora from the checkout (EDICT_TRACES, EDICT_DATA, EDICT_CAPTURES are set to it) and need
# EDICT_ORACLE for the oracle-based classes.
import os, shutil, subprocess, sys

# group: (test classes or 'method:<test method>', [(name, file, old, new), ...]); xunit ANDs a class with a method
# filter, so a group names either classes or methods
# Not known-bad variants, shown rather than assumed:
# - SV_CheckBottom's midpoint (mins + maxs)*0.5 in float instead of double. Halving is exact in both, and rounding the
#   exact value to float gives the same bits (denormals included).
# - ED_ParseEdict parsing keys with a leading underscore: ED_FindField does not find them in LibreQuake's progs.dat,
#   which has no field whose name begins with one (another progs could have).
# - SV_SpawnServer setting both the coop and the deathmatch global: with coop, deathmatch has just been set to 0, and
#   without coop, coop is 0; either way the other global gets the 0 it has.
# - SV_Physics_Pusher's thinktime < ltime + host_frametime with <=: at equality both branches give movetime ==
#   host_frametime (thinktime - ltime is exact in float when the exact difference is a float, and it can only be
#   equal when host_frametime is one).
# - PF_checkclient's interval with > instead of >=: sv.time - sv.lastchecktime == 0.1 cannot hold. 0.1 as a double is
#   an odd multiple of 2^-55; with sv.time >= 1 (it starts at 1) the difference is exact and a multiple of 2^-52.

# Known blind spots: mutants the gates do not detect yet, each with the reason; reported, not counted as failures.
# They are run like the others, so that one that becomes detected shows (then move it out of here).
KNOWN = {
    'CheckStuck: 17 up': 'needs a player stuck so that only 17 units up frees it, with its old origin stuck too',
    'Frags: old_frags not updated': 'old_frags only decides svc_updatefrags messages: no gated lane sees them yet',
    'AirMove: teleport hack off': 'needs a player walking backwards within teleport_time of leaving a teleporter; the '
        'tapes do not',
    'SetIdealPitch: steps rounded': 'SV_SetIdealPitch runs once per spawn in step 2 (through Host_Spawn_f); every frame '
        'from step 3 on (SV_SendClientMessages)',
    'AirMove: walk gets upmove': 'tools/tapegen.py never sends an upmove, which walking ignores; the new tapes of cut 3, '
        'step 4 can',
    'spawn: team from top color': 'the tapes send "color 0 0", where top and bottom agree',
    'color: no clamp': 'the tapes send "color 0 0"',
    'clientdata: damage kept': 'SV_WriteClientdataToMessage runs once per spawn in step 2 (Host_Spawn_f), before any '
        'damage; every frame from step 4 on (SV_SendClientMessages)',
    'SetIdealPitch: one step enough': 'SV_SetIdealPitch runs once per spawn in step 2 (through Host_Spawn_f); every frame '
        'from step 4 on',
    'sprite bounds in float': 'differs only for a sprite of odd width or height; the three sprites of LibreQuake are '
        '16x16, 32x32 and 64x64',
    'aim: target centre in float': 'equivalent except on denormals: origin + 0.5*(mins + maxs) rounded once from double '
        'is the correctly rounded float sum (53 >= 2*24 + 2 bits make the double rounding innocuous), and halving in float '
        'is exact unless the sum is denormal. Needs denormal coordinates with an odd last bit',
}
MUTANTS = {
    'link': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.BoxOnPlaneSideTests'], [
        ('item boxes grow by 16, not 15', 'src/Edict.Engine/Server/World.cs',
         'v[EntVars.Absmin] -= 15;', 'v[EntVars.Absmin] -= 16;'),
        ('triggers inserted at the head', 'src/Edict.Engine/Server/World.cs',
         'InsertLinkBefore(ent, TriggerHead(node));', 'InsertLinkBefore(ent, _linkNext[TriggerHead(node)]);'),
        ('solids inserted at the head', 'src/Edict.Engine/Server/World.cs',
         'InsertLinkBefore(ent, SolidHead(node));', 'InsertLinkBefore(ent, _linkNext[SolidHead(node)]);'),
        ('area split axis on a tie', 'src/Edict.Engine/Server/World.cs',
         'anode.Axis = size.X > size.Y ? 0 : 1;', 'anode.Axis = size.X >= size.Y ? 0 : 1;'),
        ('solid leafs kept', 'src/Edict.Engine/Server/World.cs',
         'if (_world->Leafs[leaf].Contents == Bsp.ContentsSolid)\n                return;', ''),
        ('MAX_ENT_LEAFS 15', 'src/Edict.Engine/Server/Edicts.cs',
         'public const int MaxEntLeafs = 16;', 'public const int MaxEntLeafs = 15;'),
        ('BoxOnPlaneSide: dist1 > dist', 'src/Edict.Engine/Mathlib.cs',
         'if (dist1 >= p->Dist)', 'if (dist1 > p->Dist)'),
        ('BoxOnPlaneSide: dist2 <= dist', 'src/Edict.Engine/Mathlib.cs',
         'if (dist2 < p->Dist)', 'if (dist2 <= p->Dist)'),
        ('BOX_ON_PLANE_SIDE: dist < emins', 'src/Edict.Engine/Mathlib.cs',
         'p->Dist <= Vec3.At(emins, p->Type) ? 1', 'p->Dist < Vec3.At(emins, p->Type) ? 1'),
        ('touch without setting time', 'src/Edict.Engine/Server/World.cs',
         '*G(GlobalVars.Time) = (float)Time;\n            Hooks.ExecuteProgram', 'Hooks.ExecuteProgram'),
        ('touch recursion children swapped', 'src/Edict.Engine/Server/World.cs',
         'TouchLinks(ent, n.Child0);', 'TouchLinks(ent, n.Child1);'),
        ('world linked too', 'src/Edict.Engine/Server/World.cs',
         'if (ent == 0)\n            return;             // don\'t add the world', ''),
        ('free unlinks after clearing', 'src/Edict.Engine/Server/Edicts.cs',
         'UnlinkEdict(n);         // unlink from world bsp\n', ''),
    ]),
    'move': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.WorldFuzzTests'], [
        ('ClipToLinks: <= keeps the later entity', 'src/Edict.Engine/Server/Move.cs',
         'trace.Fraction < clip.Trace.Fraction', 'trace.Fraction <= clip.Trace.Fraction'),
        ('MoveBounds without the unit', 'src/Edict.Engine/Server/Move.cs',
         'bmin = s + Vec3.At(mins, i) - 1;', 'bmin = s + Vec3.At(mins, i);'),
        ('missile box 14', 'src/Edict.Engine/Server/Move.cs',
         'clip.Mins2 = new Vec3(-15, -15, -15);', 'clip.Mins2 = new Vec3(-14, -14, -14);'),
        ('NewChaseDir with a float abs', 'src/Edict.Engine/Server/Move.cs',
         'CAbs(deltay) > CAbs(deltax)', 'MathF.Abs(deltay) > MathF.Abs(deltax)'),
        ('MoveToGoal: step before rand', 'src/Edict.Engine/Server/Move.cs',
         'if ((Rand.Next() & 3) == 1 || !StepDirection(ent, v[EntVars.IdealYaw], dist))',
         'if (!StepDirection(ent, v[EntVars.IdealYaw], dist) || (Rand.Next() & 3) == 1)'),
        ('StepDirection: >= 45', 'src/Edict.Engine/Server/Move.cs',
         'if (delta > 45 && delta < 315)', 'if (delta >= 45 && delta < 315)'),
        ('walk yaw in float', 'src/Edict.Engine/Server/Move.cs',
         'yaw = (float)(yaw * Math.PI * 2 / 360);', 'yaw = yaw * MathF.PI * 2 / 360;'),
        ('traceline: no entity is not world', 'src/Edict.Engine/Server/Builtins.cs',
         'trace.Ent >= 0 ? trace.Ent * sv.EdictSize : 0;', 'trace.Ent * sv.EdictSize;'),
        ('ClipToLinks clips against own missiles', 'src/Edict.Engine/Server/Move.cs',
         "if (((int*)t)[EntVars.Owner] == clip.PassEdict * EdictSize)\n                    continue;   // don't clip against own missiles", ''),
        ('ClipToLinks: points interact', 'src/Edict.Engine/Server/Move.cs',
         'if (clip.PassEdict >= 0 && Fields(clip.PassEdict)[EntVars.Size] != 0 && t[EntVars.Size] == 0)\n                continue;   // points never interact', ''),
    ]),
    'physics': (['method:Edict.Engine.Tests.PhysicsFuzzTests.Physics_matches_the_reference',
                 'method:Edict.Engine.Tests.VmReplayTests.Server_frames_match_the_reference'], [
        ('RunThink: >= the frame end', 'src/Edict.Engine/Server/Physics.cs',
         'if (thinktime <= 0 || thinktime > Time + FrameTime)', 'if (thinktime <= 0 || thinktime >= Time + FrameTime)'),
        ('RunThink: the past kept', 'src/Edict.Engine/Server/Physics.cs',
         '        if (thinktime < Time)\n            thinktime = (float)Time;', ''),
        ('Impact: only the mover touches', 'src/Edict.Engine/Server/Physics.cs',
         '        float* v2 = Fields(e2);\n        if (((int*)v2)[EntVars.Touch] != 0 && v2[EntVars.Solid] != SolidNot)', '        float* v2 = Fields(e2);\n        if (((int*)v2)[EntVars.Touch] != 0 && v2[EntVars.Solid] != SolidNot && e2 < 0)'),
        ('ClipVelocity: stop epsilon 0.01', 'src/Edict.Engine/Server/Physics.cs',
         'private const double StopEpsilon = 0.1;', 'private const double StopEpsilon = 0.01;'),
        ('FlyMove: floor at 0.71', 'src/Edict.Engine/Server/Physics.cs',
         '            if (trace.Plane.Normal.Z > 0.7)\n            {\n                blocked |= 1;', '            if (trace.Plane.Normal.Z > 0.71)\n            {\n                blocked |= 1;'),
        ('FlyMove: three bumps', 'src/Edict.Engine/Server/Physics.cs',
         'int numbumps = 4;', 'int numbumps = 3;'),
        ('FlyMove: dead stop only against', 'src/Edict.Engine/Server/Physics.cs',
         'if (Vec3.Dot(*velocity, primalVelocity) <= 0)', 'if (Vec3.Dot(*velocity, primalVelocity) < 0)'),
        ('FlyMove: time left not reduced', 'src/Edict.Engine/Server/Physics.cs',
         'timeLeft -= timeLeft * trace.Fraction;', 'timeLeft -= 0;'),
        ('AddGravity in float', 'src/Edict.Engine/Server/Physics.cs',
         'v[EntVars.Velocity + 2] = (float)(v[EntVars.Velocity + 2] - entGravity * Cvars.Value(n) * FrameTime);', 'v[EntVars.Velocity + 2] = v[EntVars.Velocity + 2] - entGravity * Cvars.Value(n) * (float)FrameTime;'),
        ('AddGravity: no gravity field', 'src/Edict.Engine/Server/Physics.cs',
         'float entGravity = _gravityField >= 0 && v[_gravityField] != 0 ? v[_gravityField] : 1.0f;', 'float entGravity = 1.0f;'),
        ('PushEntity: missiles move normally', 'src/Edict.Engine/Server/Physics.cs',
         'end, MoveMissile, ent);', 'end, MoveNormal, ent);'),
        ('PushEntity: triggers clip monsters', 'src/Edict.Engine/Server/Physics.cs',
         'end, MoveNoMonsters, ent);', 'end, MoveNormal, ent);'),
        ('PushMove: still pusher keeps its time', 'src/Edict.Engine/Server/Physics.cs',
         '            pv[EntVars.Ltime] += movetime;\n            return;', '            return;'),
        ('PushMove: riders must overlap', 'src/Edict.Engine/Server/Physics.cs',
         'if (!((CCast.I32(cv[EntVars.Flags]) & FlOnGround) != 0 && ((int*)cv)[EntVars.Groundentity] == pusher * _edictSize))', 'if (true)'),
        ('PushMove: no corpse case', 'src/Edict.Engine/Server/Physics.cs',
         '                if (cv[EntVars.Solid] == SolidNot || cv[EntVars.Solid] == SolidTrigger)\n                {   // corpse', '                if (cv[EntVars.Solid] == SolidNot && cv[EntVars.Solid] == SolidTrigger)\n                {   // corpse'),
        ('PushMove: blocked not called', 'src/Edict.Engine/Server/Physics.cs',
         '                if (((int*)pv)[EntVars.Blocked] != 0)', '                if (((int*)pv)[EntVars.Blocked] < 0)'),
        ('Pusher: think before ltime only', 'src/Edict.Engine/Server/Physics.cs',
         'if (thinktime > oldltime && thinktime <= v[EntVars.Ltime])', 'if (thinktime > oldltime && thinktime < v[EntVars.Ltime])'),
        ('CheckStuck: 17 up', 'src/Edict.Engine/Server/Physics.cs',
         'for (int z = 0; z < 18; z++)', 'for (int z = 0; z < 17; z++)'),
        ('CheckWater: middle from maxs', 'src/Edict.Engine/Server/Physics.cs',
         'point.Z = (float)(v[EntVars.Origin + 2] + (v[EntVars.Mins + 2] + v[EntVars.Maxs + 2]) * 0.5);', 'point.Z = v[EntVars.Origin + 2] + v[EntVars.Maxs + 2];'),
        ('WallFriction: d + 0.4', 'src/Edict.Engine/Server/Physics.cs',
         'd = (float)(d + 0.5);', 'd = (float)(d + 0.4);'),
        ('TryUnstick: one unit', 'src/Edict.Engine/Server/Physics.cs',
         'case 0: dir.X = 2; dir.Y = 0; break;', 'case 0: dir.X = 1; dir.Y = 0; break;'),
        ('WalkMove: step 16', 'src/Edict.Engine/Server/Physics.cs',
         'private const int StepSizeWalk = 18;', 'private const int StepSizeWalk = 16;'),
        ('WalkMove: down move without the fall', 'src/Edict.Engine/Server/Physics.cs',
         'downmove.Z = (float)(-StepSizeWalk + oldvel.Z * FrameTime);', 'downmove.Z = -StepSizeWalk;'),
        ('WalkMove: stuck at 1/16', 'src/Edict.Engine/Server/Physics.cs',
         '            if (Math.Abs((double)(oldorg.Y - v[EntVars.Origin + 1])) < 0.03125\n                && Math.Abs((double)(oldorg.X - v[EntVars.Origin])) < 0.03125)', '            if (Math.Abs((double)(oldorg.Y - v[EntVars.Origin + 1])) < 0.0625\n                && Math.Abs((double)(oldorg.X - v[EntVars.Origin])) < 0.0625)'),
        ('WaterTransition: waterlevel 0 out of water', 'src/Edict.Engine/Server/Physics.cs',
         '            v[EntVars.Watertype] = ContentsEmpty;\n            v[EntVars.Waterlevel] = cont;', '            v[EntVars.Watertype] = ContentsEmpty;\n            v[EntVars.Waterlevel] = 0;'),
        ('Toss: bounce 1.4', 'src/Edict.Engine/Server/Physics.cs',
         'float backoff = v[EntVars.Movetype] == MovetypeBounce ? 1.5f : 1;', 'float backoff = v[EntVars.Movetype] == MovetypeBounce ? 1.4f : 1;'),
        ('Toss: bounces stop below 50', 'src/Edict.Engine/Server/Physics.cs',
         'if (vel->Z < 60 || v[EntVars.Movetype] != MovetypeBounce)', 'if (vel->Z < 50 || v[EntVars.Movetype] != MovetypeBounce)'),
        ('Toss: angles not turned', 'src/Edict.Engine/Server/Physics.cs',
         '        // move angles\n        VectorMa(V3(v, EntVars.Angles), (float)FrameTime, *V3(v, EntVars.Avelocity));', ''),
        ('Step: flying monsters fall', 'src/Edict.Engine/Server/Physics.cs',
         'if ((CCast.I32(v[EntVars.Flags]) & (FlOnGround | FlFly | FlSwim)) == 0)', 'if ((CCast.I32(v[EntVars.Flags]) & FlOnGround) == 0)'),
        ('Physics: force_retouch kept', 'src/Edict.Engine/Server/Physics.cs',
         '            *G(GlobalVars.ForceRetouch) = *G(GlobalVars.ForceRetouch) - 1;', ''),
        ('CheckVelocity: no bound', 'src/Edict.Engine/Server/Physics.cs',
         '                if (v[EntVars.Velocity + i] > max)\n                    v[EntVars.Velocity + i] = max;\n                else if (v[EntVars.Velocity + i] < -max)\n                    v[EntVars.Velocity + i] = -max;', ''),
        ('Noclip: no turning', 'src/Edict.Engine/Server/Physics.cs',
         '        VectorMa(V3(v, EntVars.Angles), (float)FrameTime, *V3(v, EntVars.Avelocity));\n        VectorMa(V3(v, EntVars.Origin), (float)FrameTime, *V3(v, EntVars.Velocity));\n        LinkEdict(ent, false);', '        VectorMa(V3(v, EntVars.Origin), (float)FrameTime, *V3(v, EntVars.Velocity));\n        LinkEdict(ent, false);'),
        ('CleanupEnts: muzzle flash kept', 'src/Edict.Engine/Server/Physics.cs',
         '            v[EntVars.Effects] = CCast.I32(v[EntVars.Effects]) & ~EfMuzzleFlash;', ''),
        ('Baseline: frame not kept', 'src/Edict.Engine/Server/Physics.cs',
         '            h->Baseline.Frame = CCast.I32(v[EntVars.Frame]);', ''),
        ('Frags: old_frags not updated', 'src/Edict.Engine/Server/Physics.cs',
         '                host.OldFrags = CCast.I32(frags);', ''),
    ]),
    'clients': (['Edict.Engine.Tests.VmReplayTests'], [
        ('ReadAngle unsigned', 'src/Edict.Engine/Common/SizeBuf.cs',
         'public float ReadAngle() => (float)(ReadChar() * (360.0 / 256));', 'public float ReadAngle() => (float)(ReadByte() * (360.0 / 256));'),
        ('button2 not shifted', 'src/Edict.Engine/Server/Clients.cs',
         'v[EntVars.Button2] = (bits & 2) >> 1;', 'v[EntVars.Button2] = bits & 2;'),
        ('impulse 0 stored too', 'src/Edict.Engine/Server/Clients.cs',
         '        if (i != 0)\n            v[EntVars.Impulse] = i;', '        v[EntVars.Impulse] = i;'),
        ('ClientThink: half the pitch', 'src/Edict.Engine/Server/User.cs',
         'angles->X = -vAngle.X / 3;', 'angles->X = -vAngle.X / 2;'),
        ('ClientThink: roll times 3', 'src/Edict.Engine/Server/User.cs',
         'angles->Z = CalcRoll(*angles, *(Vec3*)(p + EntVars.Velocity)) * 4;', 'angles->Z = CalcRoll(*angles, *(Vec3*)(p + EntVars.Velocity)) * 3;'),
        ('ClientThink: punch angle not added', 'src/Edict.Engine/Server/User.cs',
         'Vec3 vAngle = Vec3.Add(*(Vec3*)(p + EntVars.VAngle), *(Vec3*)(p + EntVars.Punchangle));', 'Vec3 vAngle = *(Vec3*)(p + EntVars.VAngle);'),
        ('DropPunchAngle 5 per second', 'src/Edict.Engine/Server/User.cs',
         'len = (float)(len - 10 * FrameTime);', 'len = (float)(len - 5 * FrameTime);'),
        ('UserFriction: no edge friction', 'src/Edict.Engine/Server/User.cs',
         '? Cvar("sv_friction\\0"u8) * Cvar("edgefriction\\0"u8)', '? Cvar("sv_friction\\0"u8)'),
        ('UserFriction: control always speed', 'src/Edict.Engine/Server/User.cs',
         'float control = speed < stopspeed ? stopspeed : speed;', 'float control = speed;'),
        ('UserFriction: in float', 'src/Edict.Engine/Server/User.cs',
         'float newspeed = (float)(speed - FrameTime * control * friction);', 'float newspeed = speed - (float)FrameTime * control * friction;'),
        ('Accelerate: <= 0 kept going', 'src/Edict.Engine/Server/User.cs',
         '        float addspeed = _wishspeed - currentspeed;\n        if (addspeed <= 0)\n            return;\n        float accelspeed = (float)(Cvar("sv_accelerate\\0"u8) * FrameTime * _wishspeed);',
         '        float addspeed = _wishspeed - currentspeed;\n        if (addspeed < 0)\n            return;\n        float accelspeed = (float)(Cvar("sv_accelerate\\0"u8) * FrameTime * _wishspeed);'),
        ('AirAccelerate: cap 40', 'src/Edict.Engine/Server/User.cs',
         '        if (wishspd > 30)\n            wishspd = 30;', '        if (wishspd > 40)\n            wishspd = 40;'),
        ('AirMove: walk gets upmove', 'src/Edict.Engine/Server/User.cs',
         'if (CCast.I32(p[EntVars.Movetype]) != MovetypeWalk)', 'if (CCast.I32(p[EntVars.Movetype]) == MovetypeNone)'),
        ('AirMove: teleport hack off', 'src/Edict.Engine/Server/User.cs',
         'if (Time < p[EntVars.TeleportTime] && fmove < 0)', 'if (Time < p[EntVars.TeleportTime] && fmove < 0 && fmove > 0)'),
        ('WaterMove: 0.8 of the wish speed', 'src/Edict.Engine/Server/User.cs',
         'wishspeed = (float)(wishspeed * 0.7);', 'wishspeed = (float)(wishspeed * 0.8);'),
        ('WaterMove: no drift down', 'src/Edict.Engine/Server/User.cs',
         '            wishvel.Z -= 60;        // drift towards bottom', '            wishvel.Z -= 0;'),
        ('CalcRoll: always the full angle', 'src/Edict.Engine/Server/User.cs',
         '        if (side < speed)\n            side = side * value / speed;', '        if (side < speed && side < 0)\n            side = side * value / speed;'),
        ('SetIdealPitch: steps rounded', 'src/Edict.Engine/Server/Clients.cs',
         'int step = CCast.I32(z[j] - z[j - 1]);', 'int step = CCast.I32(MathF.Round(z[j] - z[j - 1]));'),
        ('SetIdealPitch: one step enough', 'src/Edict.Engine/Server/Clients.cs',
         '        if (steps < 2)\n            return;\n        fixed (byte* n = "sv_idealpitchscale', '        if (steps < 1)\n            return;\n        fixed (byte* n = "sv_idealpitchscale'),
        ('clientdata: damage kept', 'src/Edict.Engine/Server/Clients.cs',
         '            v[EntVars.DmgTake] = 0;\n            v[EntVars.DmgSave] = 0;', ''),
        ('clientdata: fixangle kept', 'src/Edict.Engine/Server/Clients.cs',
         '            v[EntVars.Fixangle] = 0;', ''),
        ('spawn: team from top color', 'src/Edict.Engine/Server/Clients.cs',
         'v[EntVars.Team] = (host.Colors & 15) + 1;', 'v[EntVars.Team] = (host.Colors >> 4) + 1;'),
        ('color: no clamp', 'src/Edict.Engine/Server/Clients.cs',
         '        bottom &= 15;\n        if (bottom > 13)\n            bottom = 13;', '        bottom &= 15;'),
        ('connect: spawn parms from parm2', 'src/Edict.Engine/Server/Clients.cs',
         'client.SpawnParms[i] = *G(GlobalVars.Parm1 + i);', 'client.SpawnParms[i] = *G(GlobalVars.Parm2 + i);'),
        ('spawn: time not set', 'src/Edict.Engine/Server/Clients.cs',
         '        *G(GlobalVars.Time) = (float)Time;\n        Globals[GlobalVars.Self] = _svPlayer * _edictSize;', '        Globals[GlobalVars.Self] = _svPlayer * _edictSize;'),
    ]),
    'spawn': (['Edict.Engine.Tests.SpawnGateTests', 'Edict.Engine.Tests.SpawnParseTests'], [
        ('COM_Parse: unsigned char', 'src/Edict.Engine/Common/ComParse.cs',
         'while ((c = (sbyte)*data) <= \' \')', 'while ((c = *data) <= \' \')'),
        ('COM_Parse: colon not a single token', 'src/Edict.Engine/Common/ComParse.cs',
         "c == '\\'' || c == ':';", "c == '\\'';"),
        ('COM_Parse: no comments', 'src/Edict.Engine/Common/ComParse.cs',
         "if (c == '/' && data[1] == '/')", "if (c == '/' && data[1] == '/' && len < 0)"),
        ('ED_NewString: no newline escape', 'src/Edict.Engine/Server/Spawn.cs',
         "s[i] == 'n' ? (byte)'\\n' : (byte)'\\\\';", "s[i] == 'n' ? (byte)'n' : (byte)'\\\\';"),
        ('ED_NewString: backslash kept with the next character', 'src/Edict.Engine/Server/Spawn.cs',
         "            if (c == '\\\\' && i < l - 1)", "            if (Environment.ProcessorCount < 0)"),
        ('ED_ParseEdict: no anglehack', 'src/Edict.Engine/Server/Spawn.cs',
         'bool anglehack = token.SequenceEqual("angle"u8);', 'bool anglehack = false;'),
        ('ED_ParseEdict: no light hack', 'src/Edict.Engine/Server/Spawn.cs',
         'if (token.SequenceEqual("light"u8))', 'if (Environment.ProcessorCount < 0)'),
        ('ED_ParseEdict: trailing spaces kept', 'src/Edict.Engine/Server/Spawn.cs',
         "while (n != 0 && keyname[n - 1] == ' ')", "while (Environment.ProcessorCount < 0)"),
        ('ED_ParseEdict: world cleared too', 'src/Edict.Engine/Server/Spawn.cs',
         'if (ent != 0)   // hack', 'if (true)'),
        ('ED_ParseEdict: empty entity not freed', 'src/Edict.Engine/Server/Spawn.cs',
         'if (!init)\n            EdictNum(ent)->Free = 1;', 'if (!init)\n            EdictNum(ent)->Free = 0;'),
        ('ED_ParseEpair: vector split at runs of spaces', 'src/Edict.Engine/Server/Spawn.cs',
         "                    w = v = v + 1;", "                    w = v = v + 1;\n                    while (str[v] == ' ') { v++; w++; }"),
        ('ED_ParseEpair: float through Q_atof', 'src/Edict.Engine/Server/Spawn.cs',
         '*(float*)d = (float)CAtof.Atof(s);', '*(float*)d = CAtof.QAtof(s);'),
        ('ED_LoadFromFile: skill 1 uses not-hard', 'src/Edict.Engine/Server/Spawn.cs',
         '(CurrentSkill == 1 && (spawnflags & SpawnflagNotMedium) != 0)', '(CurrentSkill == 1 && (spawnflags & SpawnflagNotHard) != 0)'),
        ('ED_LoadFromFile: skill 3 not hard', 'src/Edict.Engine/Server/Spawn.cs',
         '(CurrentSkill >= 2 && (spawnflags & SpawnflagNotHard) != 0)', '(CurrentSkill == 2 && (spawnflags & SpawnflagNotHard) != 0)'),
        ('ED_LoadFromFile: deathmatch ignores spawnflags', 'src/Edict.Engine/Server/Spawn.cs',
         'if ((spawnflags & SpawnflagNotDeathmatch) != 0)', 'if (Environment.ProcessorCount < 0)'),
        ('SV_SpawnServer: skill not rounded', 'src/Edict.Engine/Server/Spawn.cs',
         'CurrentSkill = CCast.I32(CvarValue("skill"u8) + 0.5);', 'CurrentSkill = CCast.I32(CvarValue("skill"u8));'),
        ('SV_SpawnServer: coop keeps deathmatch', 'src/Edict.Engine/Server/Spawn.cs',
         'if (CvarValue("coop"u8) != 0)\n            CvarSetValue("deathmatch"u8, 0);', ''),
        ('SV_SpawnServer: sv.time 0', 'src/Edict.Engine/Server/Spawn.cs',
         '        Time = 1.0;', '        Time = 0;'),
        ('SV_SpawnServer: client slots not reserved', 'src/Edict.Engine/Server/Spawn.cs',
         '        NumEdicts = MaxClients + 1;', '        NumEdicts = 1;'),
        ('PF_lightstyle: not stored', 'src/Edict.Engine/Server/Builtins.cs',
         '        sv.SetLightstyle(style, val);', ''),
        ('sprite bounds in float', 'src/Edict.Engine/Model/ModelCache.cs',
         'm->Mins = new Vec3(-width / 2, -width / 2, -height / 2);', 'm->Mins = new Vec3(-width / 2f, -width / 2f, -height / 2f);'),
    ]),
    'check': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.WorldFuzzTests'], [
        ('PointInLeaf: d >= 0 goes to the front', 'src/Edict.Engine/Server/CheckClient.cs',
         'if (d > 0)', 'if (d >= 0)'),
        ('checkclient: leaf index without the -1', 'src/Edict.Engine/Server/CheckClient.cs',
         'int l = PointInLeaf(view) - 1;', 'int l = PointInLeaf(view);'),
        ('checkclient: PVS bits from the top', 'src/Edict.Engine/Server/CheckClient.cs',
         '(_checkPvs[l >> 3] & (1 << (l & 7)))', '(_checkPvs[l >> 3] & (0x80 >> (l & 7)))'),
        ('checkclient: lastchecktime in float', 'src/Edict.Engine/Server/CheckClient.cs',
         'LastCheckTime = Time;', 'LastCheckTime = (float)Time;'),
        ('checkclient: health < 0', 'src/Edict.Engine/Server/CheckClient.cs',
         '|| Fields(ent)[EntVars.Health] <= 0)', '|| Fields(ent)[EntVars.Health] < 0)'),
        ('newcheckclient: notarget ignored', 'src/Edict.Engine/Server/CheckClient.cs',
         'if ((CCast.I32(f[EntVars.Flags]) & FlNoTarget) != 0)\n                continue;', ''),
        ('newcheckclient: PVS from the origin', 'src/Edict.Engine/Server/CheckClient.cs',
         'Vec3 org = Vec3.Add(*(Vec3*)(v + EntVars.Origin), *(Vec3*)(v + EntVars.ViewOfs));',
         'Vec3 org = *(Vec3*)(v + EntVars.Origin);'),
        ('LeafPvs: zero runs one short', 'src/Edict.Engine/Server/CheckClient.cs',
         'int c = vis[i + 1];', 'int c = vis[i + 1] - 1;'),
        ('aim: start 16 up', 'src/Edict.Engine/Server/Builtins.cs',
         'start.Z += 20;', 'start.Z += 16;'),
        ('aim: <= keeps the later target', 'src/Edict.Engine/Server/Builtins.cs',
         'if (dist < bestdist)', 'if (dist <= bestdist)'),
        ('aim: pitch kept', 'src/Edict.Engine/Server/Builtins.cs',
         '            end.Z = dir.Z;\n', ''),
        ('aim: no straight trace', 'src/Edict.Engine/Server/Builtins.cs',
         'if (tr.Ent >= 0 && sv.Fields(tr.Ent)[EntVars.Takedamage] == DamageAim', 'if (false && tr.Ent >= 0 && sv.Fields(tr.Ent)[EntVars.Takedamage] == DamageAim'),
        ('aim: target centre in float', 'src/Edict.Engine/Server/Builtins.cs',
         'end.X = (float)(cv[EntVars.Origin] + 0.5 * (cv[EntVars.Mins] + cv[EntVars.Maxs]));',
         'end.X = cv[EntVars.Origin] + 0.5f * (cv[EntVars.Mins] + cv[EntVars.Maxs]);'),
    ]),
}

COPY = ['src', 'tests', 'analyzers', 'Directory.Build.props', 'Directory.Packages.props', 'global.json', 'nuget.config', 'Edict.slnx']


def run(cmd, cwd, env):
    return subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')


def main():
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    work = os.path.abspath(sys.argv[1])
    groups = sys.argv[2:] or list(MUTANTS)
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    for c in COPY:
        src = os.path.join(repo, c)
        dst = os.path.join(work, c)
        if os.path.isdir(src):
            shutil.copytree(src, dst, ignore=shutil.ignore_patterns('bin', 'obj'))
        else:
            shutil.copy(src, dst)
    env = dict(os.environ)
    env.setdefault('EDICT_TRACES', os.path.join(repo, 'data', 'traces'))
    env.setdefault('EDICT_DATA', os.path.join(repo, 'data', 'libreq-v0.09-beta'))
    env.setdefault('EDICT_CAPTURES', os.path.join(repo, 'data', 'captures'))
    test = os.path.join('tests', 'Edict.Engine.Tests')
    survived = 0
    for g in groups:
        classes, mutants = MUTANTS[g]
        for name, rel, old, new in mutants:
            path = os.path.join(work, rel)
            pristine = open(os.path.join(repo, rel), encoding='utf-8').read()
            if pristine.count(old) != 1:
                print(f'ERROR   {g}: {name}: the text to mutate occurs {pristine.count(old)} times in {rel}')
                survived += 1
                continue
            open(path, 'w', encoding='utf-8', newline='\n').write(pristine.replace(old, new))
            b = run(['dotnet', 'build', test, '-c', 'Release'], work, env)
            if b.returncode != 0:
                print(f'ERROR   {g}: {name}: does not build')
                survived += 1
            else:
                args = ['dotnet', 'run', '--project', test, '-c', 'Release', '--no-build', '--']
                for c in classes:     # a test class, or 'method:' and one test method
                    args += ['-method', c[7:]] if c.startswith('method:') else ['-class', c]
                t = run(args, work, env)
                if 'Total: 0,' in t.stdout:
                    print(f'ERROR   {g}: {name}: the filter ran no tests')
                    survived += 1
                    open(path, 'w', encoding='utf-8', newline='\n').write(pristine)
                    continue
                failed = t.returncode != 0
                first = next((l.strip() for l in t.stdout.splitlines() if 'first divergence' in l), '')
                known = name in KNOWN
                status = 'detected' if failed else 'KNOWN BLIND' if known else 'SURVIVED'
                print(f'{status} {g}: {name}' + (f'  -- {first[:160]}' if first else f'  -- {KNOWN[name]}' if known and not failed else ''))
                if failed and known:
                    print(f'         {name} is detected now: move it out of KNOWN')
                survived += 0 if failed or known else 1
            open(path, 'w', encoding='utf-8', newline='\n').write(pristine)
            sys.stdout.flush()
    print('all detected' if survived == 0 else f'{survived} not detected or broken')
    return 1 if survived else 0


if __name__ == '__main__':
    sys.exit(main())
