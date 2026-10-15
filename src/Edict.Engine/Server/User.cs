// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// sv_user.c's movement: SV_ClientThink, SV_AirMove, SV_WaterMove, SV_WaterJump, SV_UserFriction, SV_Accelerate,
/// SV_AirAccelerate, DropPunchAngle, and V_CalcRoll (view.c) that it uses for the roll angle. C keeps the moving
/// player's state in file-scope variables (forward, right, up, wishdir, wishspeed, onground, cmd); so does the port.
/// host_frametime is a double in C: the expressions that mix it in are kept in double, as there.
/// </summary>
public sealed unsafe partial class Server
{
    public const int MovetypeNone = 0, MovetypeWalk = 3, MovetypeNoclip = 8;

    private Vec3 _forward, _right, _up;           // sv_user.c's static forward, right, up
    private Vec3 _wishdir;
    private float _wishspeed;
    private bool _onground;
    private UserCmd _cmd;                          // sv_user.c's cmd: the last client's that got this far

    private float Cvar(ReadOnlySpan<byte> nameWithZero)
    {
        fixed (byte* n = nameWithZero)
            return Cvars.Value(n);
    }

    /// <summary>
    /// SV_ClientThink: the punch angle decays; a dead player does nothing more; the angles follow the view (a third of
    /// the pitch, the roll from V_CalcRoll) unless fixangle; then water jump, swimming or walking.
    /// </summary>
    private void ClientThink()
    {
        float* p = Fields(_svPlayer);
        if (p[EntVars.Movetype] == MovetypeNone)
            return;

        _onground = (CCast.I32(p[EntVars.Flags]) & FlOnGround) != 0;

        DropPunchAngle(p);

        // if dead, behave differently
        if (p[EntVars.Health] <= 0)
            return;

        // angles: show 1/3 the pitch angle and all the roll angle
        _cmd = Clients[_hostClient].Cmd;
        Vec3* angles = (Vec3*)(p + EntVars.Angles);

        Vec3 vAngle = Vec3.Add(*(Vec3*)(p + EntVars.VAngle), *(Vec3*)(p + EntVars.Punchangle));
        angles->Z = CalcRoll(*angles, *(Vec3*)(p + EntVars.Velocity)) * 4;
        if (p[EntVars.Fixangle] == 0)
        {
            angles->X = -vAngle.X / 3;
            angles->Y = vAngle.Y;
        }

        if ((CCast.I32(p[EntVars.Flags]) & FlWaterJump) != 0)
        {
            WaterJump(p);
            return;
        }

        // walk
        if (p[EntVars.Waterlevel] >= 2 && p[EntVars.Movetype] != MovetypeNoclip)
        {
            WaterMove(p);
            return;
        }

        AirMove(p);
    }

    /// <summary>V_CalcRoll (view.c): the roll for strafing, cl_rollangle at cl_rollspeed and above.</summary>
    private float CalcRoll(in Vec3 angles, in Vec3 velocity)
    {
        Mathlib.AngleVectors(angles, out _, out Vec3 right, out _);     // view.c's own forward, right, up
        float side = Vec3.Dot(velocity, right);
        float sign = side < 0 ? -1 : 1;
        side = MathF.Abs(side);
        float value = Cvar("cl_rollangle\0"u8);
        float speed = Cvar("cl_rollspeed\0"u8);
        if (side < speed)
            side = side * value / speed;
        else
            side = value;
        return side * sign;
    }

    /// <summary>DropPunchAngle: the punch angle's length falls by 10 per second.</summary>
    private void DropPunchAngle(float* p)
    {
        ref Vec3 punch = ref *(Vec3*)(p + EntVars.Punchangle);
        float len = Mathlib.VectorNormalize(ref punch);
        len = (float)(len - 10 * FrameTime);
        if (len < 0)
            len = 0;
        punch = new Vec3(punch.X * len, punch.Y * len, punch.Z * len);
    }

    /// <summary>SV_WaterJump: until teleport_time or out of the water, the jump keeps its horizontal velocity.</summary>
    private void WaterJump(float* p)
    {
        if (Time > p[EntVars.TeleportTime] || p[EntVars.Waterlevel] == 0)
        {
            p[EntVars.Flags] = CCast.I32(p[EntVars.Flags]) & ~FlWaterJump;
            p[EntVars.TeleportTime] = 0;
        }
        p[EntVars.Velocity] = p[EntVars.Movedir];
        p[EntVars.Velocity + 1] = p[EntVars.Movedir + 1];
    }

    /// <summary>SV_WaterMove: swimming along the view, with water friction and acceleration.</summary>
    private void WaterMove(float* p)
    {
        ref Vec3 velocity = ref *(Vec3*)(p + EntVars.Velocity);

        // user intentions
        Mathlib.AngleVectors(*(Vec3*)(p + EntVars.VAngle), out _forward, out _right, out _up);
        Vec3 wishvel;
        wishvel.X = _forward.X * _cmd.ForwardMove + _right.X * _cmd.SideMove;
        wishvel.Y = _forward.Y * _cmd.ForwardMove + _right.Y * _cmd.SideMove;
        wishvel.Z = _forward.Z * _cmd.ForwardMove + _right.Z * _cmd.SideMove;

        if (_cmd.ForwardMove == 0 && _cmd.SideMove == 0 && _cmd.UpMove == 0)
            wishvel.Z -= 60;        // drift towards bottom
        else
            wishvel.Z += _cmd.UpMove;

        float maxspeed = Cvar("sv_maxspeed\0"u8);
        float wishspeed = Mathlib.Length(wishvel);
        if (wishspeed > maxspeed)
        {
            float scale = maxspeed / wishspeed;
            wishvel = new Vec3(wishvel.X * scale, wishvel.Y * scale, wishvel.Z * scale);
            wishspeed = maxspeed;
        }
        wishspeed = (float)(wishspeed * 0.7);

        // water friction
        float speed = Mathlib.Length(velocity), newspeed;
        if (speed != 0)
        {
            newspeed = (float)(speed - FrameTime * speed * Cvar("sv_friction\0"u8));
            if (newspeed < 0)
                newspeed = 0;
            float scale = newspeed / speed;
            velocity = new Vec3(velocity.X * scale, velocity.Y * scale, velocity.Z * scale);
        }
        else
            newspeed = 0;

        // water acceleration
        if (wishspeed == 0)
            return;

        float addspeed = wishspeed - newspeed;
        if (addspeed <= 0)
            return;

        Mathlib.VectorNormalize(ref wishvel);
        float accelspeed = (float)(Cvar("sv_accelerate\0"u8) * wishspeed * FrameTime);
        if (accelspeed > addspeed)
            accelspeed = addspeed;

        velocity.X += accelspeed * wishvel.X;
        velocity.Y += accelspeed * wishvel.Y;
        velocity.Z += accelspeed * wishvel.Z;
    }

    /// <summary>SV_AirMove: walking (friction and acceleration on the ground, a little in the air) or noclip flying.</summary>
    private void AirMove(float* p)
    {
        Mathlib.AngleVectors(*(Vec3*)(p + EntVars.Angles), out _forward, out _right, out _up);

        float fmove = _cmd.ForwardMove;
        float smove = _cmd.SideMove;

        // hack to not let you back into teleporter
        if (Time < p[EntVars.TeleportTime] && fmove < 0)
            fmove = 0;

        Vec3 wishvel;
        wishvel.X = _forward.X * fmove + _right.X * smove;
        wishvel.Y = _forward.Y * fmove + _right.Y * smove;
        wishvel.Z = _forward.Z * fmove + _right.Z * smove;

        if (CCast.I32(p[EntVars.Movetype]) != MovetypeWalk)
            wishvel.Z = _cmd.UpMove;
        else
            wishvel.Z = 0;

        _wishdir = wishvel;
        _wishspeed = Mathlib.VectorNormalize(ref _wishdir);
        float maxspeed = Cvar("sv_maxspeed\0"u8);
        if (_wishspeed > maxspeed)
        {
            float scale = maxspeed / _wishspeed;
            wishvel = new Vec3(wishvel.X * scale, wishvel.Y * scale, wishvel.Z * scale);
            _wishspeed = maxspeed;
        }

        if (p[EntVars.Movetype] == MovetypeNoclip)
            *(Vec3*)(p + EntVars.Velocity) = wishvel;   // noclip
        else if (_onground)
        {
            UserFriction(p);
            Accelerate(p);
        }
        else
            AirAccelerate(p, ref wishvel);  // not on ground, so little effect on velocity
    }

    /// <summary>SV_UserFriction: ground friction, doubled (edgefriction) where the leading edge is over a drop.</summary>
    private void UserFriction(float* p)
    {
        ref Vec3 vel = ref *(Vec3*)(p + EntVars.Velocity);
        Vec3 origin = *(Vec3*)(p + EntVars.Origin);

        float speed = (float)Math.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
        if (speed == 0)
            return;

        // if the leading edge is over a dropoff, increase friction
        Vec3 start, stop;
        start.X = stop.X = origin.X + vel.X / speed * 16;
        start.Y = stop.Y = origin.Y + vel.Y / speed * 16;
        start.Z = origin.Z + p[EntVars.Mins + 2];
        stop.Z = start.Z - 34;

        Trace trace = Move(start, default, default, stop, 1, _svPlayer);

        float friction = trace.Fraction == 1.0
            ? Cvar("sv_friction\0"u8) * Cvar("edgefriction\0"u8)
            : Cvar("sv_friction\0"u8);

        // apply friction
        float stopspeed = Cvar("sv_stopspeed\0"u8);
        float control = speed < stopspeed ? stopspeed : speed;
        float newspeed = (float)(speed - FrameTime * control * friction);

        if (newspeed < 0)
            newspeed = 0;
        newspeed /= speed;

        vel.X = vel.X * newspeed;
        vel.Y = vel.Y * newspeed;
        vel.Z = vel.Z * newspeed;
    }

    /// <summary>SV_Accelerate: towards wishdir up to wishspeed, sv_accelerate × wishspeed per second.</summary>
    private void Accelerate(float* p)
    {
        ref Vec3 velocity = ref *(Vec3*)(p + EntVars.Velocity);
        float currentspeed = Vec3.Dot(velocity, _wishdir);
        float addspeed = _wishspeed - currentspeed;
        if (addspeed <= 0)
            return;
        float accelspeed = (float)(Cvar("sv_accelerate\0"u8) * FrameTime * _wishspeed);
        if (accelspeed > addspeed)
            accelspeed = addspeed;

        velocity.X += accelspeed * _wishdir.X;
        velocity.Y += accelspeed * _wishdir.Y;
        velocity.Z += accelspeed * _wishdir.Z;
    }

    /// <summary>SV_AirAccelerate: in the air the wish speed is capped at 30 (the acceleration still uses wishspeed).</summary>
    private void AirAccelerate(float* p, ref Vec3 wishveloc)
    {
        ref Vec3 velocity = ref *(Vec3*)(p + EntVars.Velocity);
        float wishspd = Mathlib.VectorNormalize(ref wishveloc);
        if (wishspd > 30)
            wishspd = 30;
        float currentspeed = Vec3.Dot(velocity, wishveloc);
        float addspeed = wishspd - currentspeed;
        if (addspeed <= 0)
            return;
        float accelspeed = (float)(Cvar("sv_accelerate\0"u8) * _wishspeed * FrameTime);
        if (accelspeed > addspeed)
            accelspeed = addspeed;

        velocity.X += accelspeed * wishveloc.X;
        velocity.Y += accelspeed * wishveloc.Y;
        velocity.Z += accelspeed * wishveloc.Z;
    }
}
