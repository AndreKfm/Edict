// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>kbutton_t: the keys down for a button (two at most) and its state (bit 0 down, 1 impulse down, 2 impulse up).</summary>
public struct KButton
{
    public int Down0, Down1;
    public int State;
}

/// <summary>
/// cl_input.c: the buttons and their commands (+forward, -forward, ..., impulse), CL_KeyState, CL_AdjustAngles and
/// CL_BaseMove; in_win.c's IN_MouseMove for a platform's mouse (<see cref="IMouse"/>); view.c's pitch drift
/// starts and stops.
/// </summary>
public sealed unsafe partial class ClientState
{
    // the buttons in cl_input.c's order
    public const int InMlook = 0, InKlook = 1, InLeft = 2, InRight = 3, InForward = 4, InBack = 5, InLookup = 6,
        InLookdown = 7, InMoveleft = 8, InMoveright = 9, InStrafe = 10, InSpeed = 11, InUse = 12, InJump = 13,
        InAttack = 14, InUp = 15, InDown = 16, NumButtons = 17;

    /// <summary>The buttons (in_mlook, in_klook, ...).</summary>
    public readonly KButton[] Buttons;

    /// <summary>in_impulse.</summary>
    public int Impulse;

    /// <summary>The platform's mouse, or null (in_null.c's IN_Move: nothing).</summary>
    public IMouse? Mouse { get; set; }

    /// <summary>noclip_anglehack: set by noclip in a single player game (host_cmd.c), which the port does not run.</summary>
    public bool NoclipAngleHack;

    /// <summary>Registers the button commands (CL_InitInput) with the command system.</summary>
    [NoAllocUnchecked("load path")]
    public void InitInput(Cmd cmd)
    {
        (string Name, int Button)[] buttons =
        [
            ("moveup", InUp), ("movedown", InDown), ("left", InLeft), ("right", InRight), ("forward", InForward),
            ("back", InBack), ("lookup", InLookup), ("lookdown", InLookdown), ("strafe", InStrafe),
            ("moveleft", InMoveleft), ("moveright", InMoveright), ("speed", InSpeed), ("attack", InAttack),
            ("use", InUse), ("jump", InJump), ("klook", InKlook), ("mlook", InMlook),
        ];
        foreach ((string name, int b) in buttons)
        {
            int button = b;
            cmd.Add("+" + name, () => KeyDown(cmd, button));
            cmd.Add("-" + name, () => KeyUp(cmd, button));
        }
        // IN_MLookUp: after KeyUp, whatever it did
        cmd.Add("-mlook", () =>
        {
            KeyUp(cmd, InMlook);
            if ((Buttons[InMlook].State & 1) == 0 && Lookspring())
                StartPitchDrift();
        });
        cmd.Add("impulse", () => Impulse = Edict.Exact.CAtof.QAtoi(cmd.Argv(1)));
    }

    private static readonly SysErrorException KeyNumberOutOfRange =
        new("KeyDown/KeyUp: a key number outside int, where the C libraries' atoi differ");

    private static int Atoi(byte[] s) =>
        Edict.Exact.CAtof.TryAtoi(s, out int v) ? v : throw KeyNumberOutOfRange;

    /// <summary>KeyDown: a key (Cmd_Argv(1), -1 if typed at the console) holds the button.</summary>
    [NoAllocUnchecked("console commands")]
    private void KeyDown(Cmd cmd, int button)
    {
        ref KButton b = ref Buttons[button];
        byte[] c = cmd.Argv(1);
        int k = c.Length != 0 ? Atoi(c) : -1;     // -1: typed manually at the console for continuous down
        if (k == b.Down0 || k == b.Down1)
            return;     // repeating key
        if (b.Down0 == 0)
            b.Down0 = k;
        else if (b.Down1 == 0)
            b.Down1 = k;
        else
        {
            cmd.Printf("Three keys down for a button!\n"u8);
            return;
        }
        if ((b.State & 1) != 0)
            return;     // still down
        b.State |= 1 + 2;   // down + impulse down
    }

    /// <summary>KeyUp.</summary>
    [NoAllocUnchecked("console commands")]
    private void KeyUp(Cmd cmd, int button)
    {
        ref KButton b = ref Buttons[button];
        byte[] c = cmd.Argv(1);
        int k;
        if (c.Length != 0)
            k = Atoi(c);
        else
        {
            // typed manually at the console, assume for unsticking, so clear all
            b.Down0 = b.Down1 = 0;
            b.State = 4;    // impulse up
            return;
        }
        if (b.Down0 == k)
            b.Down0 = 0;
        else if (b.Down1 == k)
            b.Down1 = 0;
        else
            return;     // key up without coresponding down (menu pass through)
        if (b.Down0 != 0 || b.Down1 != 0)
            return;     // some other key is still holding it down
        if ((b.State & 1) == 0)
            return;     // still up (this should not happen)
        b.State &= ~1;      // now up
        b.State |= 4;       // impulse up
    }

    private bool Lookspring()
    {
        fixed (byte* n = "lookspring\0"u8)
            return _cvars.Value(n) != 0;
    }

    /// <summary>CL_KeyState: the fraction of the frame the button was down; its impulses cleared.</summary>
    private float KeyState(int button)
    {
        ref KButton key = ref Buttons[button];
        bool impulsedown = (key.State & 2) != 0;
        bool impulseup = (key.State & 4) != 0;
        bool down = (key.State & 1) != 0;
        float val = 0;
        if (impulsedown && !impulseup)
            val = down ? 0.5f : 0;      // pressed and held this frame
        if (impulseup && !impulsedown)
            val = 0;                    // released this frame
        if (!impulsedown && !impulseup)
            val = down ? 1.0f : 0;      // held the entire frame, or up the entire frame
        if (impulsedown && impulseup)
            val = down ? 0.75f : 0.25f; // released and re-pressed, or pressed and released this frame
        key.State &= 1;     // clear impulses
        return val;
    }

    /// <summary>CL_AdjustAngles: the view turned by the keys, the pitch and roll bounded.</summary>
    private void AdjustAngles()
    {
        float speed = (Buttons[InSpeed].State & 1) != 0
            ? (float)(FrameTime * CvarValue("cl_anglespeedkey\0"u8))
            : (float)FrameTime;
        float yawspeed = CvarValue("cl_yawspeed\0"u8), pitchspeed = CvarValue("cl_pitchspeed\0"u8);
        if ((Buttons[InStrafe].State & 1) == 0)
        {
            ViewAngles.Y -= speed * yawspeed * KeyState(InRight);
            ViewAngles.Y += speed * yawspeed * KeyState(InLeft);
            ViewAngles.Y = Mathlib.Anglemod(ViewAngles.Y);
        }
        if ((Buttons[InKlook].State & 1) != 0)
        {
            StopPitchDrift();
            ViewAngles.X -= speed * pitchspeed * KeyState(InForward);
            ViewAngles.X += speed * pitchspeed * KeyState(InBack);
        }
        float up = KeyState(InLookup);
        float down = KeyState(InLookdown);
        ViewAngles.X -= speed * pitchspeed * up;
        ViewAngles.X += speed * pitchspeed * down;
        if (up != 0 || down != 0)
            StopPitchDrift();
        if (ViewAngles.X > 80)
            ViewAngles.X = 80;
        if (ViewAngles.X < -70)
            ViewAngles.X = -70;
        if (ViewAngles.Z > 50)
            ViewAngles.Z = 50;
        if (ViewAngles.Z < -50)
            ViewAngles.Z = -50;
    }

    /// <summary>CL_BaseMove: the angles, then the move of the keys into cl.cmd's moves.</summary>
    private void BaseMove()
    {
        AdjustAngles();
        CmdViewAngles = default;            // Q_memset (cmd, 0, sizeof(*cmd))
        float side = CvarValue("cl_sidespeed\0"u8), up = CvarValue("cl_upspeed\0"u8),
            forward = CvarValue("cl_forwardspeed\0"u8), back = CvarValue("cl_backspeed\0"u8);
        float sidemove = 0, upmove = 0, forwardmove = 0;
        if ((Buttons[InStrafe].State & 1) != 0)
        {
            sidemove += side * KeyState(InRight);
            sidemove -= side * KeyState(InLeft);
        }
        sidemove += side * KeyState(InMoveright);
        sidemove -= side * KeyState(InMoveleft);
        upmove += up * KeyState(InUp);
        upmove -= up * KeyState(InDown);
        if ((Buttons[InKlook].State & 1) == 0)
        {
            forwardmove += forward * KeyState(InForward);
            forwardmove -= back * KeyState(InBack);
        }
        // adjust for speed key
        if ((Buttons[InSpeed].State & 1) != 0)
        {
            float movespeedkey = CvarValue("cl_movespeedkey\0"u8);
            forwardmove *= movespeedkey;
            sidemove *= movespeedkey;
            upmove *= movespeedkey;
        }
        CmdForwardMove = forwardmove;
        CmdSideMove = sidemove;
        CmdUpMove = upmove;
    }

    private int _oldMouseX, _oldMouseY;               // in_win.c's old_mouse_x, old_mouse_y

    /// <summary>IN_Move (in_win.c's IN_MouseMove): the mouse's motion into the view angles or the moves.</summary>
    private void InMove()
    {
        if (Mouse is not { } mouse || !mouse.Active)
            return;
        mouse.TakeMotion(out int mx, out int my);
        float mouseX, mouseY;
        if (CvarValue("m_filter\0"u8) != 0)
        {
            mouseX = (float)((mx + _oldMouseX) * 0.5);
            mouseY = (float)((my + _oldMouseY) * 0.5);
        }
        else
        {
            mouseX = mx;
            mouseY = my;
        }
        _oldMouseX = mx;
        _oldMouseY = my;
        float sensitivity = CvarValue("sensitivity\0"u8);
        mouseX *= sensitivity;
        mouseY *= sensitivity;
        bool strafe = (Buttons[InStrafe].State & 1) != 0, mlook = (Buttons[InMlook].State & 1) != 0;
        // add mouse X/Y movement to cmd
        if (strafe || (CvarValue("lookstrafe\0"u8) != 0 && mlook))
            CmdSideMove += CvarValue("m_side\0"u8) * mouseX;
        else
            ViewAngles.Y -= CvarValue("m_yaw\0"u8) * mouseX;
        if (mlook)
            StopPitchDrift();
        if (mlook && !strafe)
        {
            ViewAngles.X += CvarValue("m_pitch\0"u8) * mouseY;
            if (ViewAngles.X > 80)
                ViewAngles.X = 80;
            if (ViewAngles.X < -70)
                ViewAngles.X = -70;
        }
        else if (strafe && NoclipAngleHack)
            CmdUpMove -= CvarValue("m_forward\0"u8) * mouseY;
        else
            CmdForwardMove -= CvarValue("m_forward\0"u8) * mouseY;
    }

    /// <summary>V_StartPitchDrift.</summary>
    public void StartPitchDrift()
    {
        if (LastStop == Time)
            return;     // something else is keeping it from drifting
        if (NoDrift != 0 || PitchVel == 0)
        {
            PitchVel = CvarValue("v_centerspeed\0"u8);
            NoDrift = 0;
            DriftMove = 0;
        }
    }

    /// <summary>V_StopPitchDrift.</summary>
    public void StopPitchDrift()
    {
        LastStop = Time;
        NoDrift = 1;
        PitchVel = 0;
    }
}

/// <summary>A platform's mouse: whether it moves the view (in_win.c's mouseactive), and its motion since the last move.</summary>
public interface IMouse
{
    bool Active
    {
        [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
        get;
    }

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void TakeMotion(out int mx, out int my);
}
