// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>view.c: the palette (the color shifts and gamma) and the view.</summary>
public sealed unsafe partial class Screen
{
    private const int CShiftContents = 0, CShiftDamage = 1, CShiftBonus = 2, CShiftPowerup = 3;
    private const int ItInvisibility = 1 << 19, ItInvulnerability = 1 << 20, ItSuit = 1 << 21, ItQuad = 1 << 22;

    private static readonly SysErrorException GammaNotPinned =
        new("BuildGammaTable: a gamma other than 1 needs pow, which the reference does not pin yet");

    private readonly byte[] _gammaTable;
    private float _oldGammaValue;                     // V_CheckGamma's static oldgammavalue
    private readonly byte[] _pal;                     // V_UpdatePalette's pal[768]

    /// <summary>BuildGammaTable.</summary>
    private void BuildGammaTable(float g)
    {
        if (g == 1.0f)
        {
            for (int i = 0; i < 256; i++)
                _gammaTable[i] = (byte)i;
            return;
        }
        throw GammaNotPinned;
    }

    /// <summary>V_CheckGamma.</summary>
    private bool CheckGamma()
    {
        float gamma = CvarValue("gamma\0"u8);
        if (gamma == _oldGammaValue)
            return false;
        _oldGammaValue = gamma;
        BuildGammaTable(gamma);
        _recalcRefdef = true;       // force a surface cache flush
        return true;
    }

    /// <summary>V_CalcPowerupCshift.</summary>
    private void CalcPowerupCshift()
    {
        ref ColorShift p = ref _cl.CShifts[CShiftPowerup];
        if ((_cl.Items & ItQuad) != 0)
            Set(ref p, 0, 0, 255, 30);
        else if ((_cl.Items & ItSuit) != 0)
            Set(ref p, 0, 255, 0, 20);
        else if ((_cl.Items & ItInvisibility) != 0)
            Set(ref p, 100, 100, 100, 100);
        else if ((_cl.Items & ItInvulnerability) != 0)
            Set(ref p, 255, 255, 0, 30);
        else
            p.Percent = 0;

        static void Set(ref ColorShift s, int r, int g, int b, int percent)
        {
            s.DestColor[0] = r;
            s.DestColor[1] = g;
            s.DestColor[2] = b;
            s.Percent = percent;
        }
    }

    /// <summary>V_UpdatePalette (the software renderer's): the color shifts blended into the base palette when they changed.</summary>
    private void UpdatePalette()
    {
        CalcPowerupCshift();
        bool changed = false;
        for (int i = 0; i < ClientState.NumCShifts; i++)
        {
            ref ColorShift c = ref _cl.CShifts[i];
            ref ColorShift prev = ref _cl.PrevCShifts[i];
            if (c.Percent != prev.Percent)
            {
                changed = true;
                prev.Percent = c.Percent;
            }
            for (int j = 0; j < 3; j++)
                if (c.DestColor[j] != prev.DestColor[j])
                {
                    changed = true;
                    prev.DestColor[j] = c.DestColor[j];
                }
        }

        // drop the damage value
        ref ColorShift damage = ref _cl.CShifts[CShiftDamage];
        damage.Percent = CCastInt(damage.Percent - FrameTime * 150);
        if (damage.Percent <= 0)
            damage.Percent = 0;
        // drop the bonus value
        ref ColorShift bonus = ref _cl.CShifts[CShiftBonus];
        bonus.Percent = CCastInt(bonus.Percent - FrameTime * 100);
        if (bonus.Percent <= 0)
            bonus.Percent = 0;

        bool force = CheckGamma();
        if (!changed && !force)
            return;

        for (int i = 0, b = 0; i < 256; i++, b += 3)
        {
            int r = _basePal[b], g = _basePal[b + 1], bl = _basePal[b + 2];
            for (int j = 0; j < ClientState.NumCShifts; j++)
            {
                ref ColorShift c = ref _cl.CShifts[j];
                r += (c.Percent * (c.DestColor[0] - r)) >> 8;
                g += (c.Percent * (c.DestColor[1] - g)) >> 8;
                bl += (c.Percent * (c.DestColor[2] - bl)) >> 8;
            }
            _pal[b] = _gammaTable[r];
            _pal[b + 1] = _gammaTable[g];
            _pal[b + 2] = _gammaTable[bl];
        }
        ShiftPalette(_pal);
    }

    private const int Pitch = 0, Yaw = 1, Roll = 2;
    private const int StatHealth = 0, StatWeapon = 2, StatWeaponFrame = 5;

    /// <summary>r_refdef.vieworg, r_refdef.viewangles.</summary>
    public Vec3 ViewOrg, ViewAngles;

    private Vec3 _forward, _right, _up;               // view.c's forward, right, up
    private float _oldz;                              // V_CalcRefdef's static oldz
    private float _oldYaw, _oldPitch;                 // CalcGunAngle's statics

    /// <summary>r_viewleaf's contents (R_SetupFrame), which V_SetContentsColor reads.</summary>
    private int _viewLeafContents;

    private static ref float A(ref Vec3 v, int i)
    {
        if (i == 0)
            return ref v.X;
        if (i == 1)
            return ref v.Y;
        return ref v.Z;
    }

    /// <summary>V_RenderView: the view's state and the 3D view (steps 4 and 5); with the console forced up there is none.</summary>
    private void ViewRender()
    {
        if (ForcedUp)
            return;

        // don't allow cheats in multiplayer
        if (_cl.MaxClients > 1)
        {
            CvarSet("scr_ofsx\0"u8, "0\0"u8);
            CvarSet("scr_ofsy\0"u8, "0\0"u8);
            CvarSet("scr_ofsz\0"u8, "0\0"u8);
        }

        if (_cl.Intermission != 0)
            CalcIntermissionRefdef();       // intermission / finale rendering
        else if (_cl.Paused == 0)
            ViewCalcRefdef();

        // R_PushDlights: the surfaces' light marks, the renderer's (step 4)
        if (CvarValue("lcd_x\0"u8) != 0)
            throw NotPortedYet;             // two interleaved views
        RenderView3D();

        if (CvarValue("crosshair\0"u8) != 0)
            Character((int)(_scrVrect.X + _scrVrect.Width / 2 + CvarValue("cl_crossx\0"u8)),
                      (int)(_scrVrect.Y + _scrVrect.Height / 2 + CvarValue("cl_crossy\0"u8)), '+');
    }

    private static readonly SysErrorException NullWorldModel = new("R_RenderView: NULL worldmodel");

    /// <summary>
    /// R_RenderView as far as the client's state sees it: R_SetupFrame's view leaf, R_DrawParticles' movement of the
    /// particles, V_SetContentsColor. Drawing the world, the entities and the particles is steps 4 and 5.
    /// </summary>
    private void RenderView3D()
    {
        if (_cl.Entities[0].Model == null || _cl.WorldModel == null)
            throw NullWorldModel;
        // R_SetupFrame
        if (_cl.MaxClients > 1)
        {
            CvarSet("r_draworder\0"u8, "0\0"u8);
            CvarSet("r_fullbright\0"u8, "0\0"u8);
            CvarSet("r_ambient\0"u8, "0\0"u8);
            CvarSet("r_drawflat\0"u8, "0\0"u8);
        }
        _viewLeafContents = PointInLeafContents(ViewOrg, _cl.WorldModel);
        CacheThrash = false;
        // R_MarkLeaves, R_EdgeDrawing, R_DrawEntitiesOnList, R_DrawViewModel: steps 4 and 5
        _cl.RunParticles(CvarValue("sv_gravity\0"u8));      // R_DrawParticles
        // D_WarpScreen: step 4
        SetContentsColor(_viewLeafContents);
    }

    /// <summary>Mod_PointInLeaf's leaf contents.</summary>
    private static int PointInLeafContents(in Vec3 p, Model* model)
    {
        int child = 0;
        while (child >= 0)
        {
            MNode* node = model->Nodes + child;
            MPlane* plane = model->Planes + node->PlaneNum;
            float d = Vec3.Dot(p, plane->Normal) - plane->Dist;
            child = d > 0 ? node->Children[0] : node->Children[1];
        }
        return model->Leafs[-1 - child].Contents;
    }

    /// <summary>V_SetContentsColor: the contents' color shift (cshift_empty, _lava, _slime, _water).</summary>
    private void SetContentsColor(int contents)
    {
        ref ColorShift c = ref _cl.CShifts[CShiftContents];
        switch (contents)
        {
            case Bsp.ContentsEmpty:
            case Bsp.ContentsSolid:
                Shift(ref c, 130, 80, 50, 0);
                break;
            case Bsp.ContentsLava:
                Shift(ref c, 255, 80, 0, 150);
                break;
            case Bsp.ContentsSlime:
                Shift(ref c, 0, 25, 5, 150);
                break;
            default:
                Shift(ref c, 130, 80, 50, 128);
                break;
        }

        static void Shift(ref ColorShift s, int r, int g, int b, int percent)
        {
            s.DestColor[0] = r;
            s.DestColor[1] = g;
            s.DestColor[2] = b;
            s.Percent = percent;
        }
    }

    /// <summary>V_CalcRoll: the roll of the view from the sideways velocity.</summary>
    private float CalcRoll(in Vec3 angles, in Vec3 velocity)
    {
        Mathlib.AngleVectors(angles, out _forward, out _right, out _up);
        float side = Vec3.Dot(velocity, _right);
        float sign = side < 0 ? -1 : 1;
        side = MathF.Abs(side);
        float value = CvarValue("cl_rollangle\0"u8);
        float speed = CvarValue("cl_rollspeed\0"u8);
        side = side < speed ? side * value / speed : value;
        return side * sign;
    }

    /// <summary>V_CalcBob.</summary>
    private float CalcBob()
    {
        float bobcycle = CvarValue("cl_bobcycle\0"u8), bobup = CvarValue("cl_bobup\0"u8);
        float cycle = (float)(_cl.Time - CCastInt(_cl.Time / bobcycle) * bobcycle);
        cycle /= bobcycle;
        if (cycle < bobup)
            cycle = (float)(Math.PI * cycle / bobup);
        else
            cycle = (float)(Math.PI + Math.PI * (cycle - bobup) / (1.0 - bobup));

        // bob is proportional to velocity in the xy plane (don't count Z, or jumping messes it up)
        float bob = (float)(Math.Sqrt(_cl.Velocity.X * _cl.Velocity.X + _cl.Velocity.Y * _cl.Velocity.Y) * CvarValue("cl_bob\0"u8));
        bob = (float)(bob * 0.3 + bob * 0.7 * Edict.Exact.CMath.Sin(cycle));
        if (bob > 4)
            bob = 4;
        else if (bob < -7)
            bob = -7;
        return bob;
    }

    /// <summary>V_DriftPitch: moves the client pitch angle towards cl.idealpitch sent by the server.</summary>
    private void DriftPitch()
    {
        // noclip_anglehack: set by noclip in a single player game, which the port's runs never use
        if (_cl.OnGround == 0 || _cl.DemoPlayback)
        {
            _cl.DriftMove = 0;
            _cl.PitchVel = 0;
            return;
        }
        // don't count small mouse motion
        if (_cl.NoDrift != 0)
        {
            if (MathF.Abs(_cl.CmdForwardMove) < CvarValue("cl_forwardspeed\0"u8))
                _cl.DriftMove = 0;
            else
                _cl.DriftMove = (float)(_cl.DriftMove + FrameTime);
            if (_cl.DriftMove > CvarValue("v_centermove\0"u8))
                throw NotPortedYet;         // V_StartPitchDrift
            return;
        }
        float delta = _cl.IdealPitch - _cl.ViewAngles.X;
        if (delta == 0)
        {
            _cl.PitchVel = 0;
            return;
        }
        float move = (float)(FrameTime * _cl.PitchVel);
        _cl.PitchVel = (float)(_cl.PitchVel + FrameTime * CvarValue("v_centerspeed\0"u8));
        if (delta > 0)
        {
            if (move > delta)
            {
                _cl.PitchVel = 0;
                move = delta;
            }
            _cl.ViewAngles.X += move;
        }
        else if (delta < 0)
        {
            if (move > -delta)
            {
                _cl.PitchVel = 0;
                move = -delta;
            }
            _cl.ViewAngles.X -= move;
        }
    }

    /// <summary>angledelta.</summary>
    private static float AngleDelta(float a)
    {
        a = Mathlib.Anglemod(a);
        if (a > 180)
            a -= 360;
        return a;
    }

    /// <summary>The idle sway: v_idlescale * sin (cl.time * cycle) * level, in double as C computes it.</summary>
    private double Idle(float scale, ReadOnlySpan<byte> cycle, ReadOnlySpan<byte> level) =>
        scale * Edict.Exact.CMath.Sin(_cl.Time * CvarValue(cycle)) * CvarValue(level);

    /// <summary>CalcGunAngle.</summary>
    private void CalcGunAngle()
    {
        float yaw = ViewAngles.Y;
        float pitch = -ViewAngles.X;

        yaw = (float)(AngleDelta(yaw - ViewAngles.Y) * 0.4);
        if (yaw > 10)
            yaw = 10;
        if (yaw < -10)
            yaw = -10;
        pitch = (float)(AngleDelta(-pitch - ViewAngles.X) * 0.4);
        if (pitch > 10)
            pitch = 10;
        if (pitch < -10)
            pitch = -10;
        float move = (float)(FrameTime * 20);
        if (yaw > _oldYaw)
        {
            if (_oldYaw + move < yaw)
                yaw = _oldYaw + move;
        }
        else if (_oldYaw - move > yaw)
            yaw = _oldYaw - move;
        if (pitch > _oldPitch)
        {
            if (_oldPitch + move < pitch)
                pitch = _oldPitch + move;
        }
        else if (_oldPitch - move > pitch)
            pitch = _oldPitch - move;
        _oldYaw = yaw;
        _oldPitch = pitch;

        ref ClientEntity view = ref _cl.ViewEnt;
        view.Angles.Y = ViewAngles.Y + yaw;
        view.Angles.X = -(ViewAngles.X + pitch);
        float idle = CvarValue("v_idlescale\0"u8);
        view.Angles.Z = (float)(view.Angles.Z - Idle(idle, "v_iroll_cycle\0"u8, "v_iroll_level\0"u8));
        view.Angles.X = (float)(view.Angles.X - Idle(idle, "v_ipitch_cycle\0"u8, "v_ipitch_level\0"u8));
        view.Angles.Y = (float)(view.Angles.Y - Idle(idle, "v_iyaw_cycle\0"u8, "v_iyaw_level\0"u8));
    }

    /// <summary>V_BoundOffsets: the view never outside the entity's clipping hull, so never inside a wall.</summary>
    private void BoundOffsets()
    {
        ref ClientEntity ent = ref _cl.Entities[_cl.ViewEntity];
        if (ViewOrg.X < ent.Origin.X - 14)
            ViewOrg.X = ent.Origin.X - 14;
        else if (ViewOrg.X > ent.Origin.X + 14)
            ViewOrg.X = ent.Origin.X + 14;
        if (ViewOrg.Y < ent.Origin.Y - 14)
            ViewOrg.Y = ent.Origin.Y - 14;
        else if (ViewOrg.Y > ent.Origin.Y + 14)
            ViewOrg.Y = ent.Origin.Y + 14;
        if (ViewOrg.Z < ent.Origin.Z - 22)
            ViewOrg.Z = ent.Origin.Z - 22;
        else if (ViewOrg.Z > ent.Origin.Z + 30)
            ViewOrg.Z = ent.Origin.Z + 30;
    }

    /// <summary>V_AddIdle: idle swaying.</summary>
    private void AddIdle(float idle)
    {
        ViewAngles.Z = (float)(ViewAngles.Z + Idle(idle, "v_iroll_cycle\0"u8, "v_iroll_level\0"u8));
        ViewAngles.X = (float)(ViewAngles.X + Idle(idle, "v_ipitch_cycle\0"u8, "v_ipitch_level\0"u8));
        ViewAngles.Y = (float)(ViewAngles.Y + Idle(idle, "v_iyaw_cycle\0"u8, "v_iyaw_level\0"u8));
    }

    /// <summary>V_CalcViewRoll: roll is induced by movement and damage.</summary>
    private void CalcViewRoll()
    {
        float side = CalcRoll(_cl.Entities[_cl.ViewEntity].Angles, _cl.Velocity);
        ViewAngles.Z += side;
        if (_cl.DmgTime > 0)
        {
            float kicktime = CvarValue("v_kicktime\0"u8);
            ViewAngles.Z += _cl.DmgTime / kicktime * _cl.DmgRoll;
            ViewAngles.X += _cl.DmgTime / kicktime * _cl.DmgPitch;
            _cl.DmgTime = (float)(_cl.DmgTime - FrameTime);
        }
        if (_cl.Stats[StatHealth] <= 0)
            ViewAngles.Z = 80;      // dead view angle
    }

    /// <summary>V_CalcIntermissionRefdef.</summary>
    private void CalcIntermissionRefdef()
    {
        ref ClientEntity ent = ref _cl.Entities[_cl.ViewEntity];    // the player model (visible when out of body)
        ViewOrg = ent.Origin;
        ViewAngles = ent.Angles;
        _cl.ViewEnt.Model = null;
        // allways idle in intermission (v_idlescale.value = 1 for V_AddIdle)
        AddIdle(1);
    }

    /// <summary>V_CalcRefdef.</summary>
    private void ViewCalcRefdef()
    {
        DriftPitch();

        ref ClientEntity ent = ref _cl.Entities[_cl.ViewEntity];    // the player model (visible when out of body)
        ref ClientEntity view = ref _cl.ViewEnt;                    // the weapon model (only visible from inside body)

        // transform the view offset by the model's matrix to get the offset from model origin for the view
        ent.Angles.Y = _cl.ViewAngles.Y;        // the model should face the view dir
        ent.Angles.X = -_cl.ViewAngles.X;       // the model should face the view dir

        float bob = CalcBob();

        // refresh position
        ViewOrg = ent.Origin;
        ViewOrg.Z += _cl.ViewHeight + bob;
        // never let it sit exactly on a node line, because a water plane can dissapear when viewed with the eye
        // exactly on it; the server protocol only specifies to 1/16 pixel, so add 1/32 in each axis
        ViewOrg.X = (float)(ViewOrg.X + 1.0 / 32);
        ViewOrg.Y = (float)(ViewOrg.Y + 1.0 / 32);
        ViewOrg.Z = (float)(ViewOrg.Z + 1.0 / 32);

        ViewAngles = _cl.ViewAngles;
        CalcViewRoll();
        AddIdle(CvarValue("v_idlescale\0"u8));

        // offsets: because entity pitches are actually backward
        var angles = new Vec3(-ent.Angles.X, ent.Angles.Y, ent.Angles.Z);
        Mathlib.AngleVectors(angles, out _forward, out _right, out _up);
        float ofsx = CvarValue("scr_ofsx\0"u8), ofsy = CvarValue("scr_ofsy\0"u8), ofsz = CvarValue("scr_ofsz\0"u8);
        for (int i = 0; i < 3; i++)
            A(ref ViewOrg, i) += ofsx * A(ref _forward, i) + ofsy * A(ref _right, i) + ofsz * A(ref _up, i);

        BoundOffsets();

        // set up gun position
        view.Angles = _cl.ViewAngles;
        CalcGunAngle();

        view.Origin = ent.Origin;
        view.Origin.Z += _cl.ViewHeight;
        for (int i = 0; i < 3; i++)
            A(ref view.Origin, i) = (float)(A(ref view.Origin, i) + A(ref _forward, i) * bob * 0.4);
        view.Origin.Z += bob;

        // fudge position around to keep amount of weapon visible roughly equal with different FOV
        float viewsize = CvarValue("viewsize\0"u8);
        if (viewsize == 110)
            view.Origin.Z += 1;
        else if (viewsize == 100)
            view.Origin.Z += 2;
        else if (viewsize == 90)
            view.Origin.Z += 1;
        else if (viewsize == 80)
            view.Origin.Z = (float)(view.Origin.Z + 0.5);

        view.Model = (Model*)_cl.ModelPrecache[_cl.Stats[StatWeapon]];
        view.Frame = _cl.Stats[StatWeaponFrame];
        view.Colormap = 1;      // vid.colormap

        // set up the refresh position
        ViewAngles = new Vec3(ViewAngles.X + _cl.PunchAngle.X, ViewAngles.Y + _cl.PunchAngle.Y, ViewAngles.Z + _cl.PunchAngle.Z);

        // smooth out stair step ups
        if (_cl.OnGround != 0 && ent.Origin.Z - _oldz > 0)
        {
            float steptime = (float)(_cl.Time - _cl.OldTime);
            if (steptime < 0)
                steptime = 0;
            _oldz += steptime * 80;
            if (_oldz > ent.Origin.Z)
                _oldz = ent.Origin.Z;
            if (ent.Origin.Z - _oldz > 12)
                _oldz = ent.Origin.Z - 12;
            ViewOrg.Z += _oldz - ent.Origin.Z;
            view.Origin.Z += _oldz - ent.Origin.Z;
        }
        else
            _oldz = ent.Origin.Z;

        if (CvarValue("chase_active\0"u8) != 0)
            throw NotPortedYet;     // Chase_Update
    }

    // ---- sbar.c's update state

    private int _sbUpdates;                           // sb_updates: if >= vid.numpages, no update needed

    /// <summary>Sbar_Changed.</summary>
    public void SbarChanged() => _sbUpdates = 0;     // update next frame
}
