// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// cl_main.c and cl_input.c as far as a playback reaches them: CL_SendCmd (with no key held, CL_AdjustAngles and
/// CL_BaseMove still do their arithmetic), CL_ReadFromServer, CL_LerpPoint, CL_RelinkEntities, CL_AllocDlight,
/// CL_DecayLights.
/// </summary>
public sealed unsafe partial class ClientState
{
    private const int EfBrightField = 1, EfMuzzleFlash = 2, EfBrightLight = 4, EfDimLight = 8;
    private const int ModRocket = 1, ModGrenade = 2, ModGib = 4, ModRotate = 8, ModTracer = 16, ModZomGib = 32,
        ModTracer2 = 64, ModTracer3 = 128;

    private static readonly HostErrorException LostConnection = new("CL_ReadFromServer: lost server connection");

    /// <summary>
    /// CL_SendCmd: with the signon complete the movement of no held key (CL_BaseMove, CL_SendMove: cl.cmd; a
    /// playback sends nothing and counts no move messages), then in a playback the reliable message is dropped.
    /// </summary>
    public void SendCmd()
    {
        if (State != CaConnected)
            return;

        if (Signon == Signons)
        {
            // get basic movement from keyboard (CL_BaseMove: CL_AdjustAngles, the cmd from the key states)
            CheckButtons();
            AdjustAngles();
            CmdViewAngles = default;            // Q_memset (cmd, 0, sizeof(*cmd)), then += and -= of zero key states
            float side = CvarValue("cl_sidespeed\0"u8), up = CvarValue("cl_upspeed\0"u8),
                forward = CvarValue("cl_forwardspeed\0"u8), back = CvarValue("cl_backspeed\0"u8);
            float sidemove = 0, upmove = 0, forwardmove = 0;
            sidemove += side * 0f;
            sidemove -= side * 0f;
            upmove += up * 0f;
            upmove -= up * 0f;
            forwardmove += forward * 0f;
            forwardmove -= back * 0f;
            // CL_SendMove: cl.cmd = *cmd; a playback returns before cl.movemessages counts up
            CmdForwardMove = forwardmove;
            CmdSideMove = sidemove;
            CmdUpMove = upmove;
            if (!DemoPlayback)
                SendMove();
        }

        if (DemoPlayback)
        {
            Message.Clear();
            return;
        }

        // send the reliable message
        if (Message.CurSize == 0)
            return;                 // no message at all
        if (!NetCon!.CanSendMessage())
            return;                 // Con_DPrintf ("CL_WriteToServer: can't send\n")
        if (NetCon.SendMessage(Message) == -1)
            throw LostConnectionWrite;
        Message.Clear();
    }

    private static readonly HostErrorException LostConnectionWrite = new("CL_WriteToServer: lost server connection");
    private static readonly HostErrorException LostConnectionMove = new("CL_SendMove: lost server connection (CL_Disconnect is not ported)");
    private const int ClcMove = 3;

    /// <summary>
    /// CL_SendMove's message: clc_move, the last message's time, the view angles, the moves (of no held key: 0),
    /// no buttons, no impulse; the first two after a connect are dropped.
    /// </summary>
    private void SendMove()
    {
        SizeBuf buf = _move;
        buf.Clear();
        buf.WriteByte(ClcMove);
        buf.WriteFloat((float)MTime0);     // so server can get ping times
        for (int i = 0; i < 3; i++)
            buf.WriteAngle(At(ref ViewAngles, i));
        buf.WriteShort(CCast.I32(CmdForwardMove));
        buf.WriteShort(CCast.I32(CmdSideMove));
        buf.WriteShort(CCast.I32(CmdUpMove));
        buf.WriteByte(0);                  // in_attack and in_jump are up
        buf.WriteByte(0);                  // in_impulse

        // allways dump the first two message, because it may contain leftover inputs from the last level
        if (++MoveMessages <= 2)
            return;
        if (NetCon!.SendUnreliableMessage(buf) == -1)
            throw LostConnectionMove;      // Con_Printf ("CL_SendMove: lost server connection\n"), CL_Disconnect
    }

    /// <summary>CL_AdjustAngles with no key held (in_speed, in_strafe, in_klook up; every key state 0).</summary>
    private void AdjustAngles()
    {
        float speed = (float)FrameTime;
        float yawspeed = CvarValue("cl_yawspeed\0"u8), pitchspeed = CvarValue("cl_pitchspeed\0"u8);

        ViewAngles.Y -= speed * yawspeed * 0f;
        ViewAngles.Y += speed * yawspeed * 0f;
        ViewAngles.Y = Mathlib.Anglemod(ViewAngles.Y);

        ViewAngles.X -= speed * pitchspeed * 0f;
        ViewAngles.X += speed * pitchspeed * 0f;

        if (ViewAngles.X > 80)
            ViewAngles.X = 80;
        if (ViewAngles.X < -70)
            ViewAngles.X = -70;
        if (ViewAngles.Z > 50)
            ViewAngles.Z = 50;
        if (ViewAngles.Z < -50)
            ViewAngles.Z = -50;
    }

    /// <summary>CL_ReadFromServer: cl.time advanced, every message that is due parsed, the entities relinked.</summary>
    public void ReadFromServer()
    {
        OldTime = Time;
        Time += FrameTime;

        int ret;
        do
        {
            ret = GetMessage();
            if (ret == -1)
                throw LostConnection;
            if (ret == 0)
                break;
            LastReceivedMessage = (float)RealTime;
            ParseServerMessage();
        } while (ret != 0 && State == CaConnected);

        RelinkEntities();
        UpdateTEnts();
    }

    /// <summary>CL_LerpPoint: the fraction between the last two messages that the objects should be put at.</summary>
    private float LerpPoint()
    {
        float f = (float)(MTime0 - MTime1);
        if (f == 0 || CvarValue("cl_nolerp\0"u8) != 0 || ServerActive)    // cls.timedemo is false
        {
            Time = MTime0;
            return 1;
        }

        if (f > 0.1)
        {   // dropped packet, or start of demo
            MTime1 = MTime0 - 0.1;
            f = 0.1f;
        }
        float frac = (float)((Time - MTime1) / f);
        if (frac < 0)
        {
            if (frac < -0.01)
                Time = MTime1;
            frac = 0;
        }
        else if (frac > 1)
        {
            if (frac > 1.01)
                Time = MTime0;
            frac = 1;
        }
        return frac;
    }

    /// <summary>
    /// CL_RelinkEntities: the player's velocity and (in a playback) view angles interpolated, every entity of the
    /// last message moved to its interpolated place, its lights and trails made, and the visible ones listed.
    /// </summary>
    private void RelinkEntities()
    {
        // determine partial update time
        float frac = LerpPoint();

        NumVisEdicts = 0;

        // interpolate player info
        for (int i = 0; i < 3; i++)
            At(ref Velocity, i) = At(ref MVelocity1, i) + frac * (At(ref MVelocity0, i) - At(ref MVelocity1, i));

        if (DemoPlayback)
        {   // interpolate the angles
            for (int j = 0; j < 3; j++)
            {
                float d = At(ref MViewAngles0, j) - At(ref MViewAngles1, j);
                if (d > 180)
                    d -= 360;
                else if (d < -180)
                    d += 360;
                At(ref ViewAngles, j) = At(ref MViewAngles1, j) + frac * d;
            }
        }

        float bobjrotate = Mathlib.Anglemod((float)(100 * Time));
        bool chase = CvarValue("chase_active\0"u8) != 0;

        // start on the entity after the world
        for (int i = 1; i < NumEntities; i++)
        {
            ref ClientEntity ent = ref Entities[i];
            if (ent.Model == null)
                continue;           // empty slot (R_RemoveEfrags when it just became empty: the renderer's)

            // if the object wasn't included in the last packet, remove it
            if (ent.MsgTime != MTime0)
            {
                ent.Model = null;
                continue;
            }

            Vec3 oldorg = ent.Origin;

            if (ent.ForceLink != 0)
            {   // the entity was not updated in the last message so move to the final spot
                ent.Origin = ent.MsgOrigin0;
                ent.Angles = ent.MsgAngles0;
            }
            else
            {   // if the delta is large, assume a teleport and don't lerp
                float f = frac;
                Vec3 delta = default;
                for (int j = 0; j < 3; j++)
                {
                    At(ref delta, j) = At(ref ent.MsgOrigin0, j) - At(ref ent.MsgOrigin1, j);
                    if (At(ref delta, j) > 100 || At(ref delta, j) < -100)
                        f = 1;      // assume a teleportation, not a motion
                }

                // interpolate the origin and angles
                for (int j = 0; j < 3; j++)
                {
                    At(ref ent.Origin, j) = At(ref ent.MsgOrigin1, j) + f * At(ref delta, j);
                    float d = At(ref ent.MsgAngles0, j) - At(ref ent.MsgAngles1, j);
                    if (d > 180)
                        d -= 360;
                    else if (d < -180)
                        d += 360;
                    At(ref ent.Angles, j) = At(ref ent.MsgAngles1, j) + f * d;
                }
            }

            int flags = ent.Model->Flags;

            // rotate binary objects locally
            if ((flags & ModRotate) != 0)
                ent.Angles.Y = bobjrotate;

            if ((ent.Effects & EfBrightField) != 0)
                EntityParticles();
            if ((ent.Effects & EfMuzzleFlash) != 0)
            {
                ref DLight dl = ref AllocDlight(i);
                dl.Origin = ent.Origin;
                dl.Origin.Z += 16;
                Mathlib.AngleVectors(ent.Angles, out Vec3 fv, out _, out _);
                dl.Origin = new Vec3(dl.Origin.X + 18 * fv.X, dl.Origin.Y + 18 * fv.Y, dl.Origin.Z + 18 * fv.Z);   // VectorMA
                dl.Radius = 200 + (Rand.Next() & 31);
                dl.MinLight = 32;
                dl.Die = (float)(Time + 0.1);
            }
            if ((ent.Effects & EfBrightLight) != 0)
            {
                ref DLight dl = ref AllocDlight(i);
                dl.Origin = ent.Origin;
                dl.Origin.Z += 16;
                dl.Radius = 400 + (Rand.Next() & 31);
                dl.Die = (float)(Time + 0.001);
            }
            if ((ent.Effects & EfDimLight) != 0)
            {
                ref DLight dl = ref AllocDlight(i);
                dl.Origin = ent.Origin;
                dl.Radius = 200 + (Rand.Next() & 31);
                dl.Die = (float)(Time + 0.001);
            }

            if ((flags & ModGib) != 0)
                RocketTrail(oldorg, ent.Origin, 2);
            else if ((flags & ModZomGib) != 0)
                RocketTrail(oldorg, ent.Origin, 4);
            else if ((flags & ModTracer) != 0)
                RocketTrail(oldorg, ent.Origin, 3);
            else if ((flags & ModTracer2) != 0)
                RocketTrail(oldorg, ent.Origin, 5);
            else if ((flags & ModRocket) != 0)
            {
                RocketTrail(oldorg, ent.Origin, 0);
                ref DLight dl = ref AllocDlight(i);
                dl.Origin = ent.Origin;
                dl.Radius = 200;
                dl.Die = (float)(Time + 0.01);
            }
            else if ((flags & ModGrenade) != 0)
                RocketTrail(oldorg, ent.Origin, 1);
            else if ((flags & ModTracer3) != 0)
                RocketTrail(oldorg, ent.Origin, 6);

            ent.ForceLink = 0;

            if (i == ViewEntity && !chase)
                continue;

            if (NumVisEdicts < MaxVisEdicts)
                VisEdicts[NumVisEdicts++] = new EntityRef { Array = 0, Index = i };
        }
    }

    /// <summary>CL_AllocDlight: the light with this key, else one that has died, else the first; cleared.</summary>
    private ref DLight AllocDlight(int key)
    {
        // first look for an exact key match
        if (key != 0)
        {
            for (int i = 0; i < MaxDLights; i++)
                if (DLights[i].Key == key)
                {
                    DLights[i] = default;
                    DLights[i].Key = key;
                    return ref DLights[i];
                }
        }

        // then look for anything else
        for (int i = 0; i < MaxDLights; i++)
            if (DLights[i].Die < Time)
            {
                DLights[i] = default;
                DLights[i].Key = key;
                return ref DLights[i];
            }

        DLights[0] = default;
        DLights[0].Key = key;
        return ref DLights[0];
    }

    /// <summary>CL_DecayLights: every live light's radius shrinks by its decay over the frame.</summary>
    public void DecayLights()
    {
        float time = (float)(Time - OldTime);
        for (int i = 0; i < MaxDLights; i++)
        {
            ref DLight dl = ref DLights[i];
            if (dl.Die < Time || dl.Radius == 0)
                continue;
            dl.Radius -= time * dl.Decay;
            if (dl.Radius < 0)
                dl.Radius = 0;
        }
    }
}
