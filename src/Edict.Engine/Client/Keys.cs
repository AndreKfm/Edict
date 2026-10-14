// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// keys.c as far as the console and the configs need it: the key names, the bindings and their commands (bind,
/// unbind, unbindall), key_dest and the console's input lines. Key events are the platform's (step 6).
/// </summary>
public sealed class Keys
{
    // keydest_t
    public const int KeyGame = 0, KeyConsole = 1, KeyMessage = 2, KeyMenu = 3;

    public const int MaxCmdLine = 256;

    /// <summary>key_dest.</summary>
    public int Dest;

    /// <summary>key_lines[32][MAXCMDLINE]: the console's input history, each "]" at first.</summary>
    public readonly byte[][] Lines = new byte[32][];

    /// <summary>edit_line, key_linepos.</summary>
    public int EditLine, LinePos;

    /// <summary>keybindings[256]: null where none was ever set.</summary>
    public readonly byte[]?[] Bindings = new byte[256][];

    private readonly Cmd _cmd;

    private static readonly (string Name, int Key)[] KeyNames =
    [
        ("TAB", 9), ("ENTER", 13), ("ESCAPE", 27), ("SPACE", 32), ("BACKSPACE", 127),
        ("UPARROW", 128), ("DOWNARROW", 129), ("LEFTARROW", 130), ("RIGHTARROW", 131),
        ("ALT", 132), ("CTRL", 133), ("SHIFT", 134),
        ("F1", 135), ("F2", 136), ("F3", 137), ("F4", 138), ("F5", 139), ("F6", 140), ("F7", 141), ("F8", 142),
        ("F9", 143), ("F10", 144), ("F11", 145), ("F12", 146),
        ("INS", 147), ("DEL", 148), ("PGDN", 149), ("PGUP", 150), ("HOME", 151), ("END", 152),
        ("MOUSE1", 200), ("MOUSE2", 201), ("MOUSE3", 202),
        ("JOY1", 203), ("JOY2", 204), ("JOY3", 205), ("JOY4", 206),
        ("AUX1", 207), ("AUX2", 208), ("AUX3", 209), ("AUX4", 210), ("AUX5", 211), ("AUX6", 212), ("AUX7", 213),
        ("AUX8", 214), ("AUX9", 215), ("AUX10", 216), ("AUX11", 217), ("AUX12", 218), ("AUX13", 219),
        ("AUX14", 220), ("AUX15", 221), ("AUX16", 222), ("AUX17", 223), ("AUX18", 224), ("AUX19", 225),
        ("AUX20", 226), ("AUX21", 227), ("AUX22", 228), ("AUX23", 229), ("AUX24", 230), ("AUX25", 231),
        ("AUX26", 232), ("AUX27", 233), ("AUX28", 234), ("AUX29", 235), ("AUX30", 236), ("AUX31", 237),
        ("AUX32", 238),
        ("PAUSE", 255), ("MWHEELUP", 239), ("MWHEELDOWN", 240),
        ("SEMICOLON", ';'),     // because a raw semicolon seperates commands
    ];

    /// <summary>Key_Init: the input lines, and the binding commands registered.</summary>
    public Keys(Cmd cmd)
    {
        _cmd = cmd;
        for (int i = 0; i < Lines.Length; i++)
        {
            Lines[i] = new byte[MaxCmdLine];
            Lines[i][0] = (byte)']';
        }
        LinePos = 1;
        cmd.Add("bind", Bind);
        cmd.Add("unbind", Unbind);
        cmd.Add("unbindall", UnbindAll);
    }

    /// <summary>Key_StringToKeynum: a single character is its own key, otherwise a name of the table (any case); -1 for none.</summary>
    public static int StringToKeynum(ReadOnlySpan<byte> str)
    {
        if (str.Length == 0)
            return -1;
        if (str.Length == 1)
            return str[0];
        foreach ((string name, int key) in KeyNames)
        {
            if (name.Length != str.Length)
                continue;
            int i = 0;
            for (; i < name.Length; i++)
            {
                int c = str[i] >= 'a' && str[i] <= 'z' ? str[i] - ('a' - 'A') : str[i];
                if (c != name[i])
                    break;
            }
            if (i == name.Length)
                return key;
        }
        return -1;
    }

    /// <summary>Key_SetBinding.</summary>
    public void SetBinding(int keynum, ReadOnlySpan<byte> binding)
    {
        if (keynum == -1)
            return;
        Bindings[keynum] = binding.ToArray();
    }

    private void Unbind()
    {
        if (_cmd.Argc != 2)
        {
            _cmd.Printf("unbind <key> : remove commands from a key\n"u8);
            return;
        }
        int b = StringToKeynum(_cmd.Argv(1));
        if (b == -1)
        {
            _cmd.Msg("\""u8).Msg(_cmd.Argv(1)).Msg("\" isn't a valid key\n"u8).End();
            return;
        }
        SetBinding(b, ""u8);
    }

    private void UnbindAll()
    {
        for (int i = 0; i < 256; i++)
            if (Bindings[i] != null)
                SetBinding(i, ""u8);
    }

    private void Bind()
    {
        int c = _cmd.Argc;
        if (c != 2 && c != 3)
        {
            _cmd.Printf("bind <key> [command] : attach a command to a key\n"u8);
            return;
        }
        int b = StringToKeynum(_cmd.Argv(1));
        if (b == -1)
        {
            _cmd.Msg("\""u8).Msg(_cmd.Argv(1)).Msg("\" isn't a valid key\n"u8).End();
            return;
        }
        if (c == 2)
        {
            if (Bindings[b] is { } binding)
                _cmd.Msg("\""u8).Msg(_cmd.Argv(1)).Msg("\" = \""u8).Msg(binding).Msg("\"\n"u8).End();
            else
                _cmd.Msg("\""u8).Msg(_cmd.Argv(1)).Msg("\" is not bound\n"u8).End();
            return;
        }
        // copy the rest of the command line
        var cmd = new List<byte>();
        for (int i = 2; i < c; i++)
        {
            if (i > 2)
                cmd.Add((byte)' ');
            cmd.AddRange(_cmd.Argv(i));
        }
        SetBinding(b, cmd.ToArray());
    }
}
