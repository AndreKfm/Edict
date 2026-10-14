# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Source-level known-bad variants (docs/TESTING.md R7) for engine code that has no seam to swap it in a test:
# each mutant is one textual change in src/. The tree is copied once to a work directory; per mutant the file is
# restored, the change applied, the engine tests built and the given test classes run. A mutant must make them fail
# ("detected"); one that passes is a blind spot of the gates. A mutant whose text no longer matches is an error.
#
# Usage: python3 -I tools/mutate.py <work dir> [--known] [--only <text>] [group ...]      (groups below; default all)
#   --known        only the known blind spots: after a gate gained a test, the mutants it detected before stay detected
#                  (a test added to a gate can only detect more), so only these need to run again
#   --only <text>  only mutants whose name contains the text (repeatable)
# The tests of a mutant stop at their first failure (-stopOnFail): a detected mutant does not run the rest.
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
# - SV_WriteEntitiesToClient's origin threshold with >= 0.1 for > 0.1 (and <= -0.1): no float equals the double 0.1,
#   so a float miss is >= 0.1 exactly when it is > 0.1.
# - SV_FatPVS over the (numleafs + 7) >> 3 bytes of a PVS row instead of (numleafs + 31) >> 3: the bits tested are
#   leafnums of world leafs 1..numleafs (leaf - 1 < numleafs), all within the row; the three bytes more only OR in what
#   Mod_DecompressVis left past the row.
# - Loop_SendMessage and Loop_GetMessage without the 4-byte alignment: the reader steps over a message as the writer
#   placed it, either way; only how full the receive buffer gets differs, by at most 3 bytes a queued message, which
#   matters only within 3 bytes of NET_MAXMESSAGE (8192), where a reliable send is a Sys_Error. A listen side reads its
#   whole buffer every frame; the largest frame's sends of the listen corpus are 4941 + 66 bytes.
# - CL_ClearState resetting cls.signon with a local server too (Host_ClearMemory runs only without one): the state is
#   cleared only by CL_ParseServerInfo, and a listen client reaches the serverinfo with signon 0 always:
#   CL_EstablishConnection set it for the first map, Host_ClearMemory in SV_SpawnServer (and "reconnect") for every
#   later one.
# - CL_GetMessage keeping keepalive nops: SV_SendNop goes to a connected client that is neither spawned nor due a
#   signon message once realtime - last_message > 5, and a listen client answers each signon stage in the next frame,
#   so its last message is never 5 s old meanwhile (no listen run has a nop). The client's own nops (CL_KeepaliveMessage)
#   return at once with a local server.
# - Host_Reconnect_f keeping cls.signon (SV_SendReconnect's own call and the client's "reconnect" stufftext): with a
#   local server, Host_ClearMemory in the same SV_SpawnServer sets it 0, and the stufftext, read after that spawn,
#   runs in the next frame's Cbuf_Execute, before that frame's server sends the serverinfo; signon is 0 either way.
# - Con_Printf updating the screen while scr_disabled_for_loading (between the loading plaque and the signon's end):
#   SCR_UpdateScreen returns at once while it is set, unless 60 s passed, which ends it with "load failed." anyway.
# - SCR_CalcRefdef's status bar in the intermission (size 120, so sb_lines 0): R_SetVrect makes the view full screen in
#   an intermission whatever sb_lines is, and no intermission branch of SCR_UpdateScreen draws the status bar.
# - Draw_Character's clipping at the top (y between -8 and 0): no caller passes such a y. Con_DrawConsole's first row
#   is at lines - 16 - 8*rows, in [0, 8) for 16 lines or more, and draws no row below 16; the notify lines, the center
#   print and the status bar are below the top.
# - CL_ClearState clearing cl_static_entities too: of a static, CL_ParseStatic writes every field the state reads
#   but msgtime, the lerp origins and angles, forcelink, update_type, syncbase and baseline.effects, and nothing else
#   ever writes those, so they are 0 from the start and clearing them changes nothing.
# - PF_checkclient's interval with > instead of >=: sv.time - sv.lastchecktime == 0.1 cannot hold. 0.1 as a double is
#   an odd multiple of 2^-55; with sv.time >= 1 (it starts at 1) the difference is exact and a multiple of 2^-52.

# Known blind spots: mutants the gates do not detect yet, each with the reason; reported, not counted as failures.
# They are run like the others, so that one that becomes detected shows (then move it out of here).
KNOWN = {
    'con: notify time inclusive': "a notify line exactly con_notifytime old: realtime minus the line's float time, a sum of the frame clock's steps, never hits 3.0 in the corpus",
    'scr: center erase one page longer': 'the erased rows are inside the 3D view, which -edict2d clears: the full frames of steps 4 and 5',
    'scr: loading plaque keeps the console': "the plaque comes with the console up (toggleconsole during a changelevel): the platform's keys, step 6",
    'draw: tile clear not offset': "the tiles cleared at the console's edge and under the notify lines lie in the 3D view or under the redrawn status bar: the full frames of steps 4 and 5",
    'sbar: pain face ends early': "cl.time exactly the float faceanimtime (cl.time + 0.2), which the corpus' times never are",
    'sbar: item pickup does not redraw': "the redraw draws what is there unless the view covered the bar (viewsize above 100: sizeup, the platform's, step 6)",
    'view: damage fades by 149': "at the corpus' frame time both drop a whole 3 (the percent is an int): another frame time, the platform's (step 6)",
    'view: bob up inclusive': 'r_refdef.vieworg only, and a cycle exactly at cl_bobup: the full frames of steps 4 and 5',
    'view: roll speed inclusive': 'r_refdef.viewangles only, and a side speed exactly cl_rollspeed: the full frames of steps 4 and 5',
    'view: offsets bound at 15': "r_refdef.vieworg only (the walk's frustum moves a unit): the full frames of steps 4 and 5",
    'world: reject plane inclusive': 'a node corner exactly on a frustum plane: the full frames of steps 4 and 5',
    'part: gravity 0.04': "the particles' velocities are drawn only (D_DrawParticle): the full frames of step 5",
    'part: trail start does not move': "the particles' positions are drawn only: the full frames of step 5",
    'part: explode2 slows with dvel': "the particles' velocities are drawn only: the full frames of step 5",
    'cmd: alias without the trailing space': "an alias whose command reads Cmd_Args (say): LibreQuake's configs define none",
    'AirMove: teleport hack off': 'needs a player walking backwards within teleport_time of leaving a teleporter; the '
        'tapes do not',
    'AirMove: walk gets upmove': 'tools/tapegen.py never sends an upmove, which walking ignores; the new tapes of cut 3, '
        'step 4 can',
    'spawn: team from top color': 'the tapes send "color 0 0", where top and bottom agree',
    'color: no clamp': 'the tapes send "color 0 0"',
    'sprite bounds in float': 'differs only for a sprite of odd width or height; the three sprites of LibreQuake are '
        '16x16, 32x32 and 64x64',
    'aim: target centre in float': 'equivalent except on denormals: origin + 0.5*(mins + maxs) rounded once from double '
        'is the correctly rounded float sum (53 >= 2*24 + 2 bits make the double rounding innocuous), and halving in float '
        'is exact unless the sum is denormal. Needs denormal coordinates with an odd last bit',
}
MUTANTS = {
    'link': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.FrameStartReplayTests', 'Edict.Engine.Tests.FrameReplayTests',
              'Edict.Engine.Tests.BoxOnPlaneSideTests'], [
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
    'move': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.FrameStartReplayTests', 'Edict.Engine.Tests.FrameReplayTests',
              'Edict.Engine.Tests.WorldFuzzTests'], [
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
                 'method:Edict.Engine.Tests.FrameReplayTests.Server_frames_match_the_reference',
                 'method:Edict.Engine.Tests.LoopGateTests.Loop_hashes_as_the_reference_every_frame'], [
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
        ('CleanupEnts: muzzle flash kept', 'src/Edict.Engine/Server/Send.cs',
         '            v[EntVars.Effects] = CCast.I32(v[EntVars.Effects]) & ~EfMuzzleFlash;', ''),
        ('Baseline: frame not kept', 'src/Edict.Engine/Server/Physics.cs',
         '            h->Baseline.Frame = CCast.I32(v[EntVars.Frame]);', ''),
        ('Frags: old_frags not updated', 'src/Edict.Engine/Server/Send.cs',
         '                host.OldFrags = CCast.I32(frags);', ''),
    ]),
    'clients': (['method:Edict.Engine.Tests.VmReplayTests.Vm_matches_the_reference_at_every_boundary',
                 'method:Edict.Engine.Tests.FrameStartReplayTests.Server_frame_start_matches_the_reference',
                 'method:Edict.Engine.Tests.FrameReplayTests.Server_frames_match_the_reference',
                 'method:Edict.Engine.Tests.LoopGateTests.Loop_hashes_as_the_reference_every_frame'], [
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
    'messages': (['method:Edict.Engine.Tests.LoopGateTests.Loop_hashes_as_the_reference_every_frame',
                  'method:Edict.Engine.Tests.MessageFuzzTests.Client_datagram_matches_the_reference',
                  'method:Edict.Engine.Tests.MessageBuiltinTests.Message_builtins_match_the_reference'], [
        ('entities: miss threshold in float', 'src/Edict.Engine/Server/Send.cs',
         'if (miss < -0.1 || miss > 0.1)', 'if (miss < -0.1f || miss > 0.1f)'),
        ('entities: angles compared to the origin baseline', 'src/Edict.Engine/Server/Send.cs',
         'if (v[EntVars.Angles] != h->Baseline.Angles.X)', 'if (v[EntVars.Angles] != h->Baseline.Origin.X)'),
        ('entities: no NOLERP for MOVETYPE_STEP', 'src/Edict.Engine/Server/Send.cs',
         'bits |= UNoLerp;    // don\'t mess up the step animation', 'bits |= 0;'),
        ('entities: colormap compared as an int', 'src/Edict.Engine/Server/Send.cs',
         'if (h->Baseline.Colormap != v[EntVars.Colormap])', 'if (h->Baseline.Colormap != CCast.I32(v[EntVars.Colormap]))'),
        ('entities: modelindex compared as an int', 'src/Edict.Engine/Server/Send.cs',
         'if (h->Baseline.ModelIndex != v[EntVars.Modelindex])', 'if (h->Baseline.ModelIndex != CCast.I32(v[EntVars.Modelindex]))'),
        ('entities: U_LONGENTITY from 257', 'src/Edict.Engine/Server/Send.cs', 'if (e >= 256)', 'if (e > 256)'),
        ('entities: U_MOREBITS from 257', 'src/Edict.Engine/Server/Send.cs', 'if (bits >= 256)', 'if (bits > 256)'),
        ('entities: 18 bytes kept free', 'src/Edict.Engine/Server/Send.cs', 'if (msg.MaxSize - msg.CurSize < 16)', 'if (msg.MaxSize - msg.CurSize < 18)'),
        ('entities: second coordinate is the first', 'src/Edict.Engine/Server/Send.cs',
         '                msg.WriteCoord(v[EntVars.Origin + 1]);', '                msg.WriteCoord(v[EntVars.Origin]);'),
        ('entities: the client not always sent', 'src/Edict.Engine/Server/Send.cs', 'if (e != clent)     // clent is always sent', 'if (e != -1)'),
        ('entities: model string not checked', 'src/Edict.Engine/Server/Send.cs',
         'if (v[EntVars.Modelindex] == 0 || *String(((int*)v)[EntVars.Model]) == 0)', 'if (v[EntVars.Modelindex] == 0)'),
        ('entities: leaf bits from the top', 'src/Edict.Engine/Server/Send.cs',
         '(1 << (h->LeafNums[i] & 7))', '(0x80 >> (h->LeafNums[i] & 7))'),
        ('fat PVS: d >= 8 goes one side', 'src/Edict.Engine/Server/Send.cs', 'if (d > 8)', 'if (d >= 8)'),
        ('fat PVS: without the view offset', 'src/Edict.Engine/Server/Send.cs',
         'Vec3 org = Vec3.Add(*V3(c, EntVars.Origin), *V3(c, EntVars.ViewOfs));', 'Vec3 org = *V3(c, EntVars.Origin);'),
        ('fat PVS: solid leafs too', 'src/Edict.Engine/Server/Send.cs',
         'if (model->Leafs[leaf].Contents != Bsp.ContentsSolid)', 'if (model->Leafs[leaf].Contents <= 0)'),
        ('datagram: time of the next frame', 'src/Edict.Engine/Server/Send.cs', 'msg.WriteFloat((float)Time);', 'msg.WriteFloat((float)(Time + FrameTime));'),
        ('datagram: copied when it just fits', 'src/Edict.Engine/Server/Send.cs',
         'if (msg.CurSize + Datagram.CurSize < msg.MaxSize)', 'if (msg.CurSize + Datagram.CurSize <= msg.MaxSize)'),
        ('datagram: server datagram left out', 'src/Edict.Engine/Server/Send.cs',
         'msg.Write(Datagram.Data, Datagram.CurSize);', 'msg.Write(Datagram.Data, 0);'),
        ('nop: after 4 seconds', 'src/Edict.Engine/Server/Send.cs', 'if (RealTime - host.LastMessage > 5)', 'if (RealTime - host.LastMessage > 4)'),
        ('nop: last_message kept', 'src/Edict.Engine/Server/Send.cs', '        client.LastMessage = RealTime;\n', ''),
        ('reliable: last_message kept', 'src/Edict.Engine/Server/Send.cs', '                host.LastMessage = RealTime;\n', ''),
        ('reliable: signon stage kept', 'src/Edict.Engine/Server/Send.cs', '                host.SendSignon = false;\n', ''),
        ('reconnect: not sent', 'src/Edict.Engine/Host.cs', '            old.SendReconnect();', '            _ = old;'),
        ('ambientsound: volume rounded', 'src/Edict.Engine/Server/Builtins.cs', 'm.WriteByte(CCast.I32(vol * 255));', 'm.WriteByte(CCast.I32(vol * 255 + 0.5f));'),
        ('ambientsound: no message', 'src/Edict.Engine/Server/Builtins.cs', '        m.WriteByte(SvcSpawnStaticSound);\n', ''),
        ('makestatic: skin before colormap', 'src/Edict.Engine/Server/Builtins.cs',
         '        m.WriteByte(CCast.I32(v[EntVars.Colormap]));\n        m.WriteByte(CCast.I32(v[EntVars.Skin]));',
         '        m.WriteByte(CCast.I32(v[EntVars.Skin]));\n        m.WriteByte(CCast.I32(v[EntVars.Colormap]));'),
        ('makestatic: angle before origin', 'src/Edict.Engine/Server/Builtins.cs',
         '            m.WriteCoord(v[EntVars.Origin + i]);\n            m.WriteAngle(v[EntVars.Angles + i]);',
         '            m.WriteAngle(v[EntVars.Angles + i]);\n            m.WriteCoord(v[EntVars.Origin + i]);'),
        ('lightstyle: only to spawned clients', 'src/Edict.Engine/Server/Builtins.cs', 'if (c.Active || c.Spawned)', 'if (c.Spawned)'),
        ('lightstyle: sent while loading too', 'src/Edict.Engine/Server/Builtins.cs', '        if (!sv.Active)\n            return;\n        for (int j = 0; j < sv.MaxClients; j++)',
         '        for (int j = 0; j < sv.MaxClients; j++)'),
    ]),
    # the performance variants of P4: each mutant is the variant done wrong, which its gate must see
    'perf': (['method:Edict.Engine.Tests.BuiltinTests.Pure_builtins_match_the_reference_on_edge_and_random_inputs',
              'method:Edict.Engine.Tests.LoopGateTests.Loop_hashes_as_the_reference_every_frame',
              'method:Edict.Engine.Tests.WorldFuzzTests.World_matches_the_reference'], [
        ('cvar cache: hit not confirmed', 'src/Edict.Engine/Server/Cvars.cs',
         'if (c >= 0 && Is(name, _vars[c].Name))', 'if (c >= 0)'),
        ('point cache: z not compared', 'src/Edict.Engine/Server/Move.cs',
         ' && _pointKeys[k + 2] == z)', ')'),
    ]),
    # P5: the client, against every demo of the client corpus
    'client': (['Edict.Engine.Tests.ClientGateTests'], [
        ('particles: a small effect draws 4', 'src/Edict.Engine/Client/Particles.cs', 'Draw(count == 1024 ? 7 : 5);', 'Draw(count == 1024 ? 7 : 4);'),
        ('particles: the free list never runs out', 'src/Edict.Engine/Client/Particles.cs',
         '        if (FreeParticles == 0)\n            return false;\n', ''),
        ('particles: teleport splash one layer short', 'src/Edict.Engine/Client/Particles.cs', 'Particles(8 * 8 * 14, 6)', 'Particles(8 * 8 * 13, 6)'),
        ('particles: a trail particle every 2 units', 'src/Edict.Engine/Client/Particles.cs', '            dec = 3;', '            dec = 2;'),
        ('particles: R_NewMap keeps the particles', 'src/Edict.Engine/Client/ClientParse.cs', '        ClearParticles();               // R_NewMap', ''),
        ('lerp: snap above 1.0', 'src/Edict.Engine/Client/ClientMain.cs', 'if (frac > 1.01)', 'if (frac > 1.0)'),
        ('lerp: dropped packet at 0.1f', 'src/Edict.Engine/Client/ClientMain.cs', 'if (f > 0.1)', 'if (f > 0.1f)'),
        ('relink: teleport at 99 units', 'src/Edict.Engine/Client/ClientMain.cs',
         'if (At(ref delta, j) > 100 || At(ref delta, j) < -100)', 'if (At(ref delta, j) > 99 || At(ref delta, j) < -99)'),
        ('relink: view angle delta reversed', 'src/Edict.Engine/Client/ClientMain.cs',
         'float d = At(ref MViewAngles0, j) - At(ref MViewAngles1, j);', 'float d = At(ref MViewAngles1, j) - At(ref MViewAngles0, j);'),
        ('relink: muzzle flash lives 0.2 s', 'src/Edict.Engine/Client/ClientMain.cs', 'dl.Die = (float)(Time + 0.1);', 'dl.Die = (float)(Time + 0.2);'),
        ('dlight: no key match', 'src/Edict.Engine/Client/ClientMain.cs', '        if (key != 0)\n        {\n            for', '        if (key == -12345)\n        {\n            for'),
        ('dlight: decay of a light dying now', 'src/Edict.Engine/Client/ClientMain.cs', 'if (dl.Die < Time || dl.Radius == 0)', 'if (dl.Die <= Time || dl.Radius == 0)'),
        ('read: old time after the step', 'src/Edict.Engine/Client/ClientMain.cs', '        OldTime = Time;\n        Time += FrameTime;', '        Time += FrameTime;\n        OldTime = Time;'),
        ('parse: syncbase over 0x8000', 'src/Edict.Engine/Client/ClientParse.cs', '(float)(Rand.Next() & 0x7fff) / 0x7fff', '(float)(Rand.Next() & 0x7fff) / 0x8000'),
        ('parse: forcelink from the newest time', 'src/Edict.Engine/Client/ClientParse.cs', 'bool forcelink = ent.MsgTime != MTime1;', 'bool forcelink = ent.MsgTime != MTime0;'),
        ('parse: velocity times 8', 'src/Edict.Engine/Client/ClientParse.cs', 'r.ReadChar() * 16 : 0;', 'r.ReadChar() * 8 : 0;'),
        ('parse: item times not set', 'src/Edict.Engine/Client/ClientParse.cs', '                    ItemGetTime[j] = (float)Time;\n', ''),
        ('parse: damage shift clamped at 100', 'src/Edict.Engine/Client/ClientParse.cs', '        if (cs.Percent > 150)\n            cs.Percent = 150;', '        if (cs.Percent > 100)\n            cs.Percent = 100;'),
        ('parse: new slots without colormap', 'src/Edict.Engine/Client/ClientParse.cs', '                Entities[NumEntities].Colormap = 1;     // vid.colormap', ''),
        ('parse: signon completed one stage early', 'src/Edict.Engine/Client/ClientParse.cs', 'if (Signon == Signons - 1)', 'if (Signon == Signons - 2)'),
        ('parse: idealpitch kept when not sent', 'src/Edict.Engine/Client/ClientParse.cs', 'IdealPitch = (bits & SuIdealPitch) != 0 ? r.ReadChar() : 0;', 'IdealPitch = (bits & SuIdealPitch) != 0 ? r.ReadChar() : IdealPitch;'),
        ('tent: explosion light without decay', 'src/Edict.Engine/Client/ClientTEnt.cs', '                dl.Decay = 300;\n                break;\n            }\n            case TeTarExplosion:', '                break;\n            }\n            case TeTarExplosion:'),
        ('tent: beam roll not drawn', 'src/Edict.Engine/Client/ClientTEnt.cs', 'ent.Angles.Z = Rand.Next() % 360;', 'ent.Angles.Z = 0;'),
        ('tent: spike draws no sound choice', 'src/Edict.Engine/Client/ClientTEnt.cs',
         '                RunParticleEffect(10);\n                if (Rand.Next() % 5 == 0)\n                    Rand.Next();        // rnd = rand () & 3: which ricochet sound',
         '                RunParticleEffect(10);'),
    ]),
    # P5: the listen server, server and local client in one process
    'listen': (['Edict.Engine.Tests.ListenGateTests'], [
        ('loop: reading a reliable message does not free the sender', 'src/Edict.Engine/Net/Loopback.cs',
         '            if (Peer != null && ret == 1)\n                Peer.CanSend = true;\n', ''),
        ('loop: a reliable send keeps the sender free', 'src/Edict.Engine/Net/Loopback.cs', '            CanSend = false;\n            return 1;', '            return 1;'),
        ('host: the client does not send with a server', 'src/Edict.Engine/Host.cs',
         '            if (cl.ServerActive)\n                cl.SendCmd();', '            if (cl.ServerActive && FrameCount < 0)\n                cl.SendCmd();'),
        ('host: the client has a rand stream of its own', 'src/Edict.Engine/Host.cs',
         '                cl.Rand = rand;\n                cl.ReadFromServer();\n                rand = cl.Rand;', '                cl.ReadFromServer();'),
        ('host: spawn keeps cl', 'src/Edict.Engine/Host.cs', '        Cl?.ClearMemory();          // Host_ClearMemory: cls.signon 0, cl wiped', ''),
        ('client: lerps with a server', 'src/Edict.Engine/Client/ClientMain.cs', ' || ServerActive)    // cls.timedemo is false', ')    // cls.timedemo is false'),
        ('client: the first move is sent', 'src/Edict.Engine/Client/ClientMain.cs', 'if (++MoveMessages <= 2)', 'if (++MoveMessages <= 1)'),
        ('client: name from cl_name', 'src/Edict.Engine/Client/ClientParse.cs', 'Cvar("_cl_name\\0"u8)', 'Cvar("cl_name\\0"u8)'),
        ('cmd: wait does not wait', 'src/Edict.Engine/Host.cs', '                _cmdWait = true;\n', ''),
        ('cmd: exec appends the script', 'src/Edict.Engine/Host.cs', 'InsertText(nul < 0 ? f : f[..nul]);',
         'AddText(nul < 0 ? f : f[..nul]);'),
    ]),
    # P6, step 3: the console, the commands, the 2D layer, the view and the client state's part of the renderer
    'screen': (['Edict.Engine.Tests.ScreenGateTests'], [
        ('con: word wrap at the line end', 'src/Edict.Engine/Screen/Console.cs',
         'if (l != _lineWidth && _conX + l > _lineWidth)',
         'if (l != _lineWidth && _conX + l >= _lineWidth)'),
        ('con: chat mark 2 not colored', 'src/Edict.Engine/Screen/Console.cs',
         '        else if (txt[0] == 2)\n        {\n            mask = 128;',
         '        else if (txt[0] == 2)\n        {\n            mask = 0;'),
        ('con: notify time inclusive', 'src/Edict.Engine/Screen/Console.cs',
         'if (time > CvarValue("con_notifytime\\0"u8))',
         'if (time >= CvarValue("con_notifytime\\0"u8))'),
        ('con: version text one pixel over', 'src/Edict.Engine/Screen/Draw.cs',
         'byte* dest = conback.Data + 320 * 186 + 320 - 11 - 8 * ver.Length;',
         'byte* dest = conback.Data + 320 * 186 + 320 - 10 - 8 * ver.Length;'),
        ('con: text rows from the bottom', 'src/Edict.Engine/Screen/Console.cs',
         'int y = lines - 16 - (rows << 3);   // may start slightly negative',
         'int y = lines - 8 - (rows << 3);   // may start slightly negative'),
        ('con: cursor frame', 'src/Edict.Engine/Screen/Console.cs',
         'text[_keys.LinePos] = (byte)(10 + ((int)(RealTime * CursorSpeed) & 1));',
         'text[_keys.LinePos] = (byte)(11 + ((int)(RealTime * CursorSpeed) & 1));'),
        ('scr: console rises past its lines', 'src/Edict.Engine/Screen/Screen.cs',
         '            _conCurrent -= (float)(CvarValue("scr_conspeed\\0"u8) * FrameTime);\n            if (_conLines > _conCurrent)\n                _conCurrent = _conLines;',
         '            _conCurrent -= (float)(CvarValue("scr_conspeed\\0"u8) * FrameTime);'),
        ('scr: centerprint time never runs out', 'src/Edict.Engine/Screen/Screen.cs',
         '        _centerTimeOff -= (float)FrameTime;\n',
         ''),
        ('scr: four centered lines start at 48', 'src/Edict.Engine/Screen/Screen.cs',
         'int y = _centerLines <= 4 ? (int)(Height * 0.35) : 48;\n        while (true)',
         'int y = _centerLines < 4 ? (int)(Height * 0.35) : 48;\n        while (true)'),
        ('scr: finale prints one character more', 'src/Edict.Engine/Screen/Screen.cs',
         'if (remaining-- == 0)',
         'if (remaining-- == 1)'),
        ('scr: center erase one page longer', 'src/Edict.Engine/Screen/Screen.cs',
         'if (_eraseCenter++ > NumPages)',
         'if (_eraseCenter++ >= NumPages)'),
        ('scr: loading plaque keeps the console', 'src/Edict.Engine/Screen/Screen.cs',
         '        _centerTimeOff = 0;\n        _conCurrent = 0;\n        _drawLoading = true;',
         '        _centerTimeOff = 0;\n        _drawLoading = true;'),
        ('draw: index 0 transparent in pictures', 'src/Edict.Engine/Screen/Draw.cs',
         'if (t != TransparentColor)',
         'if (t != 0)'),
        ('draw: tile clear not offset', 'src/Edict.Engine/Screen/Draw.cs',
         'int tileoffsety = vr.Y % tileH;',
         'int tileoffsety = 0;'),
        ('sbar: numbers 23 pixels apart', 'src/Edict.Engine/Screen/Sbar.cs',
         'x += (digits - l) * 24;',
         'x += (digits - l) * 23;'),
        ('sbar: ammo digits one glyph off', 'src/Edict.Engine/Screen/Sbar.cs',
         "SbarCharacter((6 * i + 1) * 8 - 2, -24, 18 + num[0] - '0');",
         "SbarCharacter((6 * i + 1) * 8 - 2, -24, 17 + num[0] - '0');"),
        ('sbar: weapon flash cycle of four', 'src/Edict.Engine/Screen/Sbar.cs',
         'flashon = (flashon % 5) + 2;',
         'flashon = (flashon % 4) + 2;'),
        ('sbar: pain face ends early', 'src/Edict.Engine/Screen/Sbar.cs',
         'if (_cl.Time <= _cl.FaceAnimTime)',
         'if (_cl.Time < _cl.FaceAnimTime)'),
        ('sbar: item pickup does not redraw', 'src/Edict.Engine/Screen/Sbar.cs',
         '            SbarPic(192 + i * 16, -16, _sbItems[i]);\n            if (time != 0 && time > _cl.Time - 2)\n                _sbUpdates = 0;',
         '            SbarPic(192 + i * 16, -16, _sbItems[i]);'),
        ('sbar: frags sorted unstably', 'src/Edict.Engine/Screen/Sbar.cs',
         'if (_cl.Scores[_fragSort[j]].Frags < _cl.Scores[_fragSort[j + 1]].Frags)',
         'if (_cl.Scores[_fragSort[j]].Frags <= _cl.Scores[_fragSort[j + 1]].Frags)'),
        ('sbar: level name centered by 3', 'src/Edict.Engine/Screen/Sbar.cs',
         'SbarString(232 - l * 4, 12, new ReadOnlySpan<byte>(_cl.LevelName, 0, l));',
         'SbarString(232 - l * 3, 12, new ReadOnlySpan<byte>(_cl.LevelName, 0, l));'),
        ('sbar: intermission colon one pixel right', 'src/Edict.Engine/Screen/Sbar.cs',
         'TransPic(234, 64, _sbColon);',
         'TransPic(235, 64, _sbColon);'),
        ('sbar: deathmatch frame drawn in solo games', 'src/Edict.Engine/Screen/Sbar.cs',
         'if (_cl.MaxClients != 1)\n                DrawFrags();',
         'if (_cl.MaxClients != 0)\n                DrawFrags();'),
        ('sbar: a stat change does not redraw', 'src/Edict.Engine/Client/ClientParse.cs',
         '        Stats[stat] = value;\n        Con?.SbarChanged();',
         '        Stats[stat] = value;'),
        ('view: damage fades by 149', 'src/Edict.Engine/Screen/View.cs',
         'damage.Percent = CCastInt(damage.Percent - FrameTime * 150);',
         'damage.Percent = CCastInt(damage.Percent - FrameTime * 149);'),
        ('view: blend by 7 bits', 'src/Edict.Engine/Screen/View.cs',
         'r += (c.Percent * (c.DestColor[0] - r)) >> 8;',
         'r += (c.Percent * (c.DestColor[0] - r)) >> 7;'),
        ('view: quad at 31 percent', 'src/Edict.Engine/Screen/View.cs',
         'Set(ref p, 0, 0, 255, 30);',
         'Set(ref p, 0, 0, 255, 31);'),
        ('view: bob up inclusive', 'src/Edict.Engine/Screen/View.cs',
         'if (cycle < bobup)',
         'if (cycle <= bobup)'),
        ('view: roll speed inclusive', 'src/Edict.Engine/Screen/View.cs',
         'side = side < speed ? side * value / speed : value;',
         'side = side <= speed ? side * value / speed : value;'),
        ('view: stair step keeps oldz', 'src/Edict.Engine/Screen/View.cs',
         '        else\n            _oldz = ent.Origin.Z;',
         ''),
        ('view: offsets bound at 15', 'src/Edict.Engine/Screen/View.cs',
         '        if (ViewOrg.X < ent.Origin.X - 14)\n            ViewOrg.X = ent.Origin.X - 14;',
         '        if (ViewOrg.X < ent.Origin.X - 15)\n            ViewOrg.X = ent.Origin.X - 15;'),
        ('view: water at 127 percent', 'src/Edict.Engine/Screen/View.cs',
         'Shift(ref c, 130, 80, 50, 128);',
         'Shift(ref c, 130, 80, 50, 127);'),
        ('view: gun lags the bob', 'src/Edict.Engine/Screen/View.cs',
         'A(ref view.Origin, i) = (float)(A(ref view.Origin, i) + A(ref _forward, i) * bob * 0.4);',
         'A(ref view.Origin, i) = (float)(A(ref view.Origin, i) + A(ref _forward, i) * bob * 0.5);'),
        ('view: intermission keeps a gun', 'src/Edict.Engine/Screen/View.cs',
         '        ViewAngles = ent.Angles;\n        _cl.ViewEnt.Model = null;',
         '        ViewAngles = ent.Angles;'),
        ('view: no recalc at intermission', 'src/Edict.Engine/Client/ClientParse.cs',
         '                    Intermission = 1;\n                    CompletedTime = CCast.I32(Time);\n                    Con?.RecalcRefdef();    // go to full screen\n',
         '                    Intermission = 1;\n                    CompletedTime = CCast.I32(Time);\n'),
        ('world: reject plane inclusive', 'src/Edict.Engine/Screen/World.cs',
         'if (d <= 0)\n                    return;',
         'if (d < 0)\n                    return;'),
        ('world: front side on the plane is the back', 'src/Edict.Engine/Screen/World.cs',
         'int side = dot >= 0 ? 0 : 1;',
         'int side = dot > 0 ? 0 : 1;'),
        ('world: statics stored twice a frame', 'src/Edict.Engine/Screen/World.cs',
         'if (_cl.StaticVisFrame[stat] != _frameCount && _cl.NumVisEdicts < MaxVisEdicts)',
         'if (_cl.NumVisEdicts < MaxVisEdicts)'),
        ('world: efrags only on the front side', 'src/Edict.Engine/Client/Efrags.cs',
         '        if ((sides & 2) != 0)\n            SplitEntityOnNode(node->Children[1]);',
         ''),
        ('world: sprite frame count inclusive', 'src/Edict.Engine/Screen/World.cs',
         'if (frame >= e.Model->NumFrames || frame < 0)',
         'if (frame > e.Model->NumFrames || frame < 0)'),
        ('world: view leaf parents not marked', 'src/Edict.Engine/Screen/World.cs',
         '                if (_nodeVisFrame[node] == _visFrameCount)\n                    break;\n                _nodeVisFrame[node] = _visFrameCount;',
         '                if (_nodeVisFrame[node] == _visFrameCount)\n                    break;'),
        ('part: dies at its time, not after', 'src/Edict.Engine/Client/Particles.cs',
         'if (kill >= 0 && ParticleArray[kill].Die < Time)\n            {\n                ActiveHead',
         'if (kill >= 0 && ParticleArray[kill].Die <= Time)\n            {\n                ActiveHead'),
        ('part: fire burns a ramp step longer', 'src/Edict.Engine/Client/Particles.cs',
         'if (p.Ramp >= 6)',
         'if (p.Ramp > 6)'),
        ('part: gravity 0.04', 'src/Edict.Engine/Client/Particles.cs',
         'float grav = (float)(frametime * gravity * 0.05);',
         'float grav = (float)(frametime * gravity * 0.04);'),
        ('part: trail start does not move', 'src/Edict.Engine/Client/Particles.cs',
         'start = new Vec3(start.X + vec.X, start.Y + vec.Y, start.Z + vec.Z);',
         ''),
        ('part: explode2 slows with dvel', 'src/Edict.Engine/Client/Particles.cs',
         '                    p.Vel.X -= p.Vel.X * frametime;\n                    p.Vel.Y -= p.Vel.Y * frametime;\n                    p.Vel.Z -= p.Vel.Z * frametime;',
         '                    p.Vel.X -= p.Vel.X * dvel;\n                    p.Vel.Y -= p.Vel.Y * dvel;\n                    p.Vel.Z -= p.Vel.Z * dvel;'),
        ('cmd: alias without the trailing space', 'src/Edict.Engine/Common/Cmd.cs',
         "            cmd.AddRange(Argv(i));\n            cmd.Add((byte)' ');",
         "            cmd.AddRange(Argv(i));\n            if (i + 1 < Argc)\n                cmd.Add((byte)' ');"),
        ('cmd: exec appends the script', 'src/Edict.Engine/Common/Cmd.cs',
         '        InsertText(nul < 0 ? s : s[..nul]);',
         '        AddText(nul < 0 ? s : s[..nul]);'),
        ('cmd: echo in one print', 'src/Edict.Engine/Common/Cmd.cs',
         '        for (int i = 1; i < Argc; i++)\n            Msg(Argv(i)).Msg(" "u8).End();\n        Printf("\\n"u8);',
         '        var m = Msg(""u8);\n        for (int i = 1; i < Argc; i++)\n            m = m.Msg(Argv(i)).Msg(" "u8);\n        m.Msg("\\n"u8).End();'),
        ('keys: a single character is no key', 'src/Edict.Engine/Client/Keys.cs',
         '        if (str.Length == 1)\n            return str[0];',
         ''),
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
    'check': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.FrameStartReplayTests', 'Edict.Engine.Tests.FrameReplayTests',
              'Edict.Engine.Tests.WorldFuzzTests'], [
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
    args = sys.argv[2:]
    known_only = '--known' in args
    only = [args[i + 1] for i, a in enumerate(args) if a == '--only']
    groups = [a for i, a in enumerate(args) if not a.startswith('--') and (i == 0 or args[i - 1] != '--only')] or list(MUTANTS)
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
            if known_only and name not in KNOWN or only and not any(o in name for o in only):
                continue
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
                args = ['dotnet', 'run', '--project', test, '-c', 'Release', '--no-build', '--', '-stopOnFail']
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
                if failed and not first:     # the failing test, when its message names no divergence
                    first = next((l.strip() for l in t.stdout.splitlines() if '[FAIL]' in l), '')
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
