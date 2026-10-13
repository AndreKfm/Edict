// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The builtins (pr_cmds.c, sv_move.c) the port has so far, by their number in <c>pr_builtin[]</c> as compiled
/// without QUAKE2. Cut 2, step 3a: the ones that work on the VM's memory, the rand stream, cvars and the precache
/// lists; step 3b: edicts (spawn, remove, find, findradius, nextent, and makestatic's freeing of the entity). Each
/// is gated against every call the captures recorded (tests/Edict.Engine.Tests, VmReplay).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public static unsafe class Builtins
{
    public const int SetOrigin = 2, SetModel = 3, SetSize = 4;
    public const int TraceLine = 16, WalkMove = 32, DropToFloor = 34, CheckBottom = 40, PointContents = 41, MoveToGoal = 67;
    public const int MakeVectors = 1, Random = 7, Normalize = 9, Vlen = 12, VectoYaw = 13, PrecacheSound = 19,
        PrecacheModel = 20, Ftos = 26, Vtos = 27, Rint = 36, Floor = 37, Ceil = 38, Fabs = 43, Cvar = 45,
        ChangeYaw = 49, VectoAngles = 51, PrecacheFile = 68, CvarSet = 72, PrecacheModel2 = 75, PrecacheSound2 = 76,
        PrecacheFile2 = 77, Spawn = 14, Remove = 15, Find = 18, FindRadius = 22, NextEnt = 47, MakeStatic = 69;

    private const int OfsReturn = QcVm.OfsReturn, OfsParm0 = QcVm.OfsParm0, OfsParm1 = QcVm.OfsParm0 + 3;

    /// <summary>Whether <see cref="Call"/> has builtin <paramref name="num"/>.</summary>
    public static bool Has(int num) => num switch
    {
        MakeVectors or Random or Normalize or Vlen or VectoYaw or PrecacheSound or PrecacheModel or Ftos or Vtos or Rint
            or Floor or Ceil or Fabs or Cvar or ChangeYaw or VectoAngles or PrecacheFile or CvarSet or PrecacheModel2
            or PrecacheSound2 or PrecacheFile2 or Spawn or Remove or Find or FindRadius or NextEnt or MakeStatic
            or SetOrigin or SetModel or SetSize
            or TraceLine or WalkMove or DropToFloor or CheckBottom or PointContents or MoveToGoal => true,
        _ => false,
    };

    /// <summary><c>pr_builtins[num] ()</c> for a builtin <see cref="Has"/> reports.</summary>
    public static void Call(Server sv, int num)
    {
        switch (num)
        {
            case MakeVectors: PF_makevectors(sv); break;
            case Random: PF_random(sv); break;
            case Normalize: PF_normalize(sv); break;
            case Vlen: PF_vlen(sv); break;
            case VectoYaw: PF_vectoyaw(sv); break;
            case PrecacheSound: case PrecacheSound2: PF_precache_sound(sv); break;
            case PrecacheModel: case PrecacheModel2: PF_precache_model(sv); break;
            case Ftos: PF_ftos(sv); break;
            case Vtos: PF_vtos(sv); break;
            case Rint: PF_rint(sv); break;
            case Floor: PF_floor(sv); break;
            case Ceil: PF_ceil(sv); break;
            case Fabs: PF_fabs(sv); break;
            case Cvar: PF_cvar(sv); break;
            case ChangeYaw: PF_changeyaw(sv); break;
            case VectoAngles: PF_vectoangles(sv); break;
            case PrecacheFile: case PrecacheFile2: PF_precache_file(sv); break;
            case CvarSet: PF_cvar_set(sv); break;
            case Spawn: PF_Spawn(sv); break;
            case Remove: PF_Remove(sv); break;
            case Find: PF_Find(sv); break;
            case FindRadius: PF_findradius(sv); break;
            case NextEnt: PF_nextent(sv); break;
            case MakeStatic: PF_makestatic(sv); break;
            case SetOrigin: PF_setorigin(sv); break;
            case SetSize: PF_setsize(sv); break;
            case SetModel: PF_setmodel(sv); break;
            case TraceLine: PF_traceline(sv); break;
            case WalkMove: PF_walkmove(sv); break;
            case DropToFloor: PF_droptofloor(sv); break;
            case CheckBottom: *sv.G(OfsReturn) = sv.CheckBottom(GEdict(sv, OfsParm0)) ? 1 : 0; break;
            case PointContents: *sv.G(OfsReturn) = sv.PointContents(*V(sv, OfsParm0)); break;
            case MoveToGoal: sv.MoveToGoal(); break;
            default: throw sv.Vm.RunError(NotPorted);
        }
    }

    private static readonly HostErrorException NotPorted = new("builtin not ported");

    private static Vec3* V(Server sv, int ofs) => (Vec3*)sv.G(ofs);

    private static byte* S(Server sv, int ofs) => sv.String(sv.Globals[ofs]);

    /// <summary>The fields of the edict at byte offset e (docs/adr/D7).</summary>
    private static float* Fields(Server sv, int e) => (float*)(sv.Edicts + e + sv.HeaderBytes);

    /// <summary>Writes v_forward, v_up and v_right based on angles: makevectors(vector).</summary>
    private static void PF_makevectors(Server sv)
    {
        Mathlib.AngleVectors(*V(sv, OfsParm0), out Vec3 forward, out Vec3 right, out Vec3 up);
        *V(sv, GlobalVars.VForward) = forward;
        *V(sv, GlobalVars.VRight) = right;
        *V(sv, GlobalVars.VUp) = up;
    }

    /// <summary>Returns a number from 0 &lt;= num &lt; 1: <c>(rand ()&amp;0x7fff) / ((float)0x7fff)</c>.</summary>
    private static void PF_random(Server sv) => *sv.G(OfsReturn) = (sv.Rand.Next() & 0x7fff) / (float)0x7fff;

    /// <summary><c>value1[0] * value1[0] + value1[1] * value1[1] + value1[2]*value1[2]</c>, in float.</summary>
    private static float SumOfSquares(in Vec3 v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;

    /// <summary>vector normalize(vector): the length in float with a double sqrt, then 1/len in float.</summary>
    private static void PF_normalize(Server sv)
    {
        Vec3 value1 = *V(sv, OfsParm0);
        Vec3 newvalue;

        float n = SumOfSquares(value1);
        n = (float)Math.Sqrt(n);

        if (n == 0)
            newvalue = default;
        else
        {
            n = 1 / n;
            newvalue = new Vec3(value1.X * n, value1.Y * n, value1.Z * n);
        }

        *V(sv, OfsReturn) = newvalue;
    }

    /// <summary>scalar vlen(vector).</summary>
    private static void PF_vlen(Server sv) => *sv.G(OfsReturn) = (float)Math.Sqrt(SumOfSquares(*V(sv, OfsParm0)));

    /// <summary><c>(int) (atan2 (y, x) * 180 / M_PI)</c>: atan2 and the arithmetic in double, then the C cast.</summary>
    private static float Degrees(double y, double x) => CCast.I32(CMath.Atan2(y, x) * 180 / Math.PI);

    /// <summary>float vectoyaw(vector).</summary>
    private static void PF_vectoyaw(Server sv)
    {
        Vec3 value1 = *V(sv, OfsParm0);
        float yaw;

        if (value1.Y == 0 && value1.X == 0)
            yaw = 0;
        else
        {
            yaw = Degrees(value1.Y, value1.X);
            if (yaw < 0)
                yaw += 360;
        }

        *sv.G(OfsReturn) = yaw;
    }

    /// <summary>vector vectoangles(vector).</summary>
    private static void PF_vectoangles(Server sv)
    {
        Vec3 value1 = *V(sv, OfsParm0);
        float yaw, pitch;

        if (value1.Y == 0 && value1.X == 0)
        {
            yaw = 0;
            if (value1.Z > 0)
                pitch = 90;
            else
                pitch = 270;
        }
        else
        {
            yaw = Degrees(value1.Y, value1.X);
            if (yaw < 0)
                yaw += 360;

            float forward = (float)Math.Sqrt(value1.X * value1.X + value1.Y * value1.Y);
            pitch = Degrees(value1.Z, forward);
            if (pitch < 0)
                pitch += 360;
        }

        *sv.G(OfsReturn + 0) = pitch;
        *sv.G(OfsReturn + 1) = yaw;
        *sv.G(OfsReturn + 2) = 0;
    }

    private static void PF_precache_sound(Server sv)
    {
        byte* s = S(sv, OfsParm0);
        sv.Globals[OfsReturn] = sv.Globals[OfsParm0];
        sv.PrecacheSound(s);
    }

    private static void PF_precache_model(Server sv)
    {
        byte* s = S(sv, OfsParm0);
        sv.Globals[OfsReturn] = sv.Globals[OfsParm0];
        sv.PrecacheModel(s);
    }

    /// <summary>PF_precache_file: only the return value; the file is for qcc's pak building.</summary>
    private static void PF_precache_file(Server sv) => sv.Globals[OfsReturn] = sv.Globals[OfsParm0];

    /// <summary>
    /// PF_ftos into pr_string_temp. PF_vtos writes up to 148 bytes into the 128 of the temp for components of
    /// 1e37 and more (undefined behaviour in C); the port cuts the string at 127 bytes there.
    /// </summary>
    private static void PF_ftos(Server sv)
    {
        Span<byte> buf = stackalloc byte[CFormat.MaxFtosLength];
        int n = CFormat.Ftos(*sv.G(OfsParm0), buf);
        Temp(sv, buf[..n]);
    }

    private static void PF_vtos(Server sv)
    {
        Span<byte> buf = stackalloc byte[CFormat.MaxVtosLength];
        Vec3 v = *V(sv, OfsParm0);
        int n = CFormat.Vtos(v.X, v.Y, v.Z, buf);
        Temp(sv, buf[..n]);
    }

    private static void Temp(Server sv, ReadOnlySpan<byte> text)
    {
        Span<byte> temp = sv.StringTemp;
        int n = Math.Min(text.Length, Server.StringTempSize - 1);
        text[..n].CopyTo(temp);
        temp[n] = 0;
        sv.Globals[OfsReturn] = sv.StringTempOffset;
    }

    private static void PF_fabs(Server sv) => *sv.G(OfsReturn) = (float)Math.Abs((double)*sv.G(OfsParm0));

    /// <summary><c>(int)(f + 0.5)</c> or <c>(int)(f - 0.5)</c>: the sum in double, then the C cast.</summary>
    private static void PF_rint(Server sv)
    {
        float f = *sv.G(OfsParm0);
        if (f > 0)
            *sv.G(OfsReturn) = CCast.I32(f + 0.5);
        else
            *sv.G(OfsReturn) = CCast.I32(f - 0.5);
    }

    private static void PF_floor(Server sv) => *sv.G(OfsReturn) = (float)Math.Floor((double)*sv.G(OfsParm0));

    private static void PF_ceil(Server sv) => *sv.G(OfsReturn) = (float)Math.Ceiling((double)*sv.G(OfsParm0));

    /// <summary>float cvar (string).</summary>
    private static void PF_cvar(Server sv) => *sv.G(OfsReturn) = sv.Cvars.VariableValue(S(sv, OfsParm0));

    /// <summary>cvar_set (string, string).</summary>
    private static void PF_cvar_set(Server sv) => sv.Cvars.Set(S(sv, OfsParm0), S(sv, OfsParm1));

    /// <summary>PF_changeyaw, for SV_StepDirection, which calls it directly.</summary>
    internal static void RunChangeYaw(Server sv) => PF_changeyaw(sv);

    /// <summary>PF_changeyaw (sv_move.c): this was a major timewaster in progs, so it was converted to C.</summary>
    private static void PF_changeyaw(Server sv)
    {
        float* ent = Fields(sv, sv.Globals[GlobalVars.Self]);
        float current = Mathlib.Anglemod(ent[EntVars.Angles + 1]);
        float ideal = ent[EntVars.IdealYaw];
        float speed = ent[EntVars.YawSpeed];

        if (current == ideal)
            return;
        float move = ideal - current;
        if (ideal > current)
        {
            if (move >= 180)
                move = move - 360;
        }
        else
        {
            if (move <= -180)
                move = move + 360;
        }
        if (move > 0)
        {
            if (move > speed)
                move = speed;
        }
        else
        {
            if (move < -speed)
                move = -speed;
        }

        ent[EntVars.Angles + 1] = Mathlib.Anglemod(current + move);
    }

    private const int OfsParm2 = QcVm.OfsParm0 + 6, OfsParm3 = QcVm.OfsParm0 + 9;

    /// <summary>RETURN_EDICT: the edict's byte offset (EDICT_TO_PROG).</summary>
    private static void ReturnEdict(Server sv, int n) => sv.Globals[OfsReturn] = n * sv.EdictSize;

    /// <summary>G_EDICT as an edict number: PROG_TO_EDICT, unchecked as in C.</summary>
    private static int GEdict(Server sv, int ofs) => sv.Globals[ofs] / sv.EdictSize;

    /// <summary>G_EDICTNUM: NUM_FOR_EDICT (G_EDICT), checked against num_edicts.</summary>
    private static int GEdictNum(Server sv, int ofs) => sv.NumForEdict(sv.Globals[ofs]);

    private static void PF_Spawn(Server sv) => ReturnEdict(sv, sv.Alloc());

    private static void PF_Remove(Server sv) => sv.Free(GEdict(sv, OfsParm0));

    /// <summary>entity (entity start, .string field, string match) find: the next edict whose field matches.</summary>
    private static void PF_Find(Server sv)
    {
        int e = GEdictNum(sv, OfsParm0);
        int f = sv.Globals[OfsParm1];
        byte* s = S(sv, OfsParm2);              // "if (!s)" in C: a string pointer is never null here

        for (e++; e < sv.NumEdicts; e++)
        {
            if (sv.EdictNum(e)->Free != 0)
                continue;
            byte* t = sv.String(((int*)sv.Fields(e))[f]);   // E_STRING (ed, f)
            if (CString.Strcmp(t, s) == 0)
            {
                ReturnEdict(sv, e);
                return;
            }
        }

        ReturnEdict(sv, 0);
    }

    /// <summary>
    /// findradius (origin, radius): a chain of the entities whose centres lie within the radius. The centre is
    /// <c>origin + (mins + maxs)*0.5</c> with a double literal, so each component is computed in double and stored
    /// to float; the distance is Length.
    /// </summary>
    private static void PF_findradius(Server sv)
    {
        int chain = 0;
        Vec3 org = *V(sv, OfsParm0);
        float rad = *sv.G(OfsParm1);

        for (int i = 1; i < sv.NumEdicts; i++)
        {
            if (sv.EdictNum(i)->Free != 0)
                continue;
            float* v = sv.Fields(i);
            if (v[EntVars.Solid] == Solid.Not)
                continue;
            Vec3 eorg;
            eorg.X = (float)(org.X - (v[EntVars.Origin] + (v[EntVars.Mins] + v[EntVars.Maxs]) * 0.5));
            eorg.Y = (float)(org.Y - (v[EntVars.Origin + 1] + (v[EntVars.Mins + 1] + v[EntVars.Maxs + 1]) * 0.5));
            eorg.Z = (float)(org.Z - (v[EntVars.Origin + 2] + (v[EntVars.Mins + 2] + v[EntVars.Maxs + 2]) * 0.5));
            if (Mathlib.Length(eorg) > rad)
                continue;

            ((int*)v)[EntVars.Chain] = chain * sv.EdictSize;
            chain = i;
        }

        ReturnEdict(sv, chain);
    }

    /// <summary>entity nextent (entity): the next edict in use after it, or world.</summary>
    private static void PF_nextent(Server sv)
    {
        int i = GEdictNum(sv, OfsParm0);
        while (true)
        {
            i++;
            if (i == sv.NumEdicts)
            {
                ReturnEdict(sv, 0);
                return;
            }
            if (sv.EdictNum(i)->Free == 0)
            {
                ReturnEdict(sv, i);
                return;
            }
        }
    }

    /// <summary>
    /// makestatic (entity): the entity goes into the signon message as a static entity, then is freed. The signon
    /// output comes with messages; what the VM sees, the freed entity, is here.
    /// </summary>
    private static void PF_makestatic(Server sv) => sv.Free(GEdict(sv, OfsParm0));

    /// <summary>
    /// setorigin (entity, origin): the only valid way to move an object without using the physics of the world;
    /// directly changing origin would not set internal links correctly.
    /// </summary>
    private static void PF_setorigin(Server sv)
    {
        int e = GEdict(sv, OfsParm0);
        *(Vec3*)(sv.Fields(e) + EntVars.Origin) = *V(sv, OfsParm1);
        sv.LinkEdict(e, false);
    }

    /// <summary>
    /// traceline (vector1, vector2, nomonsters, forent): the trace_* globals. nomonsters is converted to the int
    /// move type by the call in C; a NULL trace entity becomes world.
    /// </summary>
    private static void PF_traceline(Server sv)
    {
        Vec3 v1 = *V(sv, OfsParm0), v2 = *V(sv, OfsParm1);
        int nomonsters = CCast.I32(*sv.G(OfsParm2));
        int ent = GEdict(sv, OfsParm3);

        Trace trace = sv.Move(v1, default, default, v2, nomonsters, ent);

        *sv.G(GlobalVars.TraceAllsolid) = trace.AllSolid ? 1 : 0;
        *sv.G(GlobalVars.TraceStartsolid) = trace.StartSolid ? 1 : 0;
        *sv.G(GlobalVars.TraceFraction) = trace.Fraction;
        *sv.G(GlobalVars.TraceInwater) = trace.InWater ? 1 : 0;
        *sv.G(GlobalVars.TraceInopen) = trace.InOpen ? 1 : 0;
        *V(sv, GlobalVars.TraceEndpos) = trace.EndPos;
        *V(sv, GlobalVars.TracePlaneNormal) = trace.Plane.Normal;
        *sv.G(GlobalVars.TracePlaneDist) = trace.Plane.Dist;
        sv.Globals[GlobalVars.TraceEnt] = trace.Ent >= 0 ? trace.Ent * sv.EdictSize : 0;
    }

    /// <summary>float droptofloor (): self moves down by up to 256 onto what is below it.</summary>
    private static void PF_droptofloor(Server sv)
    {
        int ent = sv.Globals[GlobalVars.Self] / sv.EdictSize;
        float* v = sv.Fields(ent);
        Vec3 end = *(Vec3*)(v + EntVars.Origin);
        end.Z -= 256;

        Trace trace = sv.Move(*(Vec3*)(v + EntVars.Origin), *(Vec3*)(v + EntVars.Mins), *(Vec3*)(v + EntVars.Maxs), end,
                              Server.MoveNormal, ent);

        if (trace.Fraction == 1 || trace.AllSolid)
            *sv.G(OfsReturn) = 0;
        else
        {
            *(Vec3*)(v + EntVars.Origin) = trace.EndPos;
            sv.LinkEdict(ent, false);
            v[EntVars.Flags] = CCast.I32(v[EntVars.Flags]) | Server.FlOnGround;
            ((int*)v)[EntVars.Groundentity] = trace.Ent * sv.EdictSize;
            *sv.G(OfsReturn) = 1;
        }
    }

    /// <summary>float walkmove (float yaw, float dist): self steps, if it is on the ground, flying or swimming.</summary>
    private static void PF_walkmove(Server sv)
    {
        int ent = sv.Globals[GlobalVars.Self] / sv.EdictSize;
        float yaw = *sv.G(OfsParm0);
        float dist = *sv.G(OfsParm1);

        if ((CCast.I32(sv.Fields(ent)[EntVars.Flags]) & (Server.FlOnGround | Server.FlFly | Server.FlSwim)) == 0)
        {
            *sv.G(OfsReturn) = 0;
            return;
        }

        Vec3 move = Server.YawMove(yaw, dist);

        // save program state, because SV_movestep may call other progs (pr_xfunction is the VM's: restored on return)
        int oldself = sv.Globals[GlobalVars.Self];

        *sv.G(OfsReturn) = sv.Movestep(ent, move, true) ? 1 : 0;

        // restore program state
        sv.Globals[GlobalVars.Self] = oldself;
    }

    /// <summary>setsize (entity, minvector, maxvector).</summary>
    private static void PF_setsize(Server sv) => sv.SetMinMaxSize(GEdict(sv, OfsParm0), *V(sv, OfsParm1), *V(sv, OfsParm2));

    /// <summary>setmodel (entity, model): the model must be precached; its bounds become the edict's.</summary>
    private static void PF_setmodel(Server sv)
    {
        int e = GEdict(sv, OfsParm0);
        int m = sv.Globals[OfsParm1];
        int i = sv.ModelIndex(sv.String(m));     // check to see if model was properly precached

        float* v = sv.Fields(e);
        ((int*)v)[EntVars.Model] = m;
        v[EntVars.Modelindex] = i;

        Model* mod = sv.ModelAt(CCast.I32(v[EntVars.Modelindex]));
        if (mod is not null)
            sv.SetMinMaxSize(e, mod->Mins, mod->Maxs);
        else
            sv.SetMinMaxSize(e, default, default);
    }
}
