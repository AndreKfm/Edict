// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The builtins (pr_cmds.c, sv_move.c) the port has so far, by their number in <c>pr_builtin[]</c> as compiled
/// without QUAKE2. Cut 2, step 3a: the ones that work on the VM's memory, the rand stream, cvars and the precache
/// lists. Each is gated against every call the captures recorded (tests/Edict.Engine.Tests, VmReplay).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public static unsafe class Builtins
{
    public const int MakeVectors = 1, Random = 7, Normalize = 9, Vlen = 12, VectoYaw = 13, PrecacheSound = 19,
        PrecacheModel = 20, Ftos = 26, Vtos = 27, Rint = 36, Floor = 37, Ceil = 38, Fabs = 43, Cvar = 45,
        ChangeYaw = 49, VectoAngles = 51, PrecacheFile = 68, CvarSet = 72, PrecacheModel2 = 75, PrecacheSound2 = 76,
        PrecacheFile2 = 77;

    private const int OfsReturn = QcVm.OfsReturn, OfsParm0 = QcVm.OfsParm0, OfsParm1 = QcVm.OfsParm0 + 3;

    /// <summary>Whether <see cref="Call"/> has builtin <paramref name="num"/>.</summary>
    public static bool Has(int num) => num switch
    {
        MakeVectors or Random or Normalize or Vlen or VectoYaw or PrecacheSound or PrecacheModel or Ftos or Vtos or Rint
            or Floor or Ceil or Fabs or Cvar or ChangeYaw or VectoAngles or PrecacheFile or CvarSet or PrecacheModel2
            or PrecacheSound2 or PrecacheFile2 => true,
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
}
