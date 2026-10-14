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
/// cl_input.c's buttons and their commands (+forward, -forward, ..., impulse), as the configs run them. The movement
/// they make (CL_KeyState in CL_BaseMove) is the platform's (step 6): until then a button other than mlook held
/// down, or an impulse, stops CL_SendCmd with an error.
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

    private static readonly SysErrorException ButtonNotPorted =
        new("CL_BaseMove: a button held or an impulse; the movement of keys is not ported yet (step 6)");

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
                throw ButtonNotPorted;      // V_StartPitchDrift
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

    /// <summary>Whether CL_BaseMove sees only what the port has: no button down but mlook, no impulse.</summary>
    private void CheckButtons()
    {
        for (int i = 0; i < NumButtons; i++)
            if (i != InMlook && Buttons[i].State != 0)
                throw ButtonNotPorted;
        if (Impulse != 0)
            throw ButtonNotPorted;
    }
}
