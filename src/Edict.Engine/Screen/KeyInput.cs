// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// keys.c's events (Key_Event, Key_Console, Key_Message) and console.c's key_dest commands (toggleconsole,
/// messagemode, messagemode2, clear): the platform's keys into the bindings, the console's input line and the chat
/// line. The menu is the reference's stub (ref/src/edict_menu.c): M_ToggleMenu_f and M_Keydown do nothing.
/// </summary>
public sealed unsafe partial class Screen
{
    // the key numbers of keys.h
    public const int KTab = 9, KEnter = 13, KEscape = 27, KSpace = 32, KBackspace = 127, KUpArrow = 128,
        KDownArrow = 129, KLeftArrow = 130, KRightArrow = 131, KAlt = 132, KCtrl = 133, KShift = 134, KF1 = 135,
        KF12 = 146, KIns = 147, KDel = 148, KPgDn = 149, KPgUp = 150, KHome = 151, KEnd = 152, KMouse1 = 200,
        KMouse2 = 201, KMouse3 = 202, KMWheelUp = 239, KMWheelDown = 240, KPause = 255;

    /// <summary>key_dest.</summary>
    public int KeyDest => _keys.Dest;

    /// <summary>ClearAllStates (vid_win.c): an up event for every key, so that the server clears them all, then Key_ClearStates.</summary>
    [NoAllocUnchecked("input path: a key event, outside the host's frame")]
    public void ClearKeyStates()
    {
        for (int i = 0; i < 256; i++)
            KeyEvent(i, false);
        for (int i = 0; i < 256; i++)
        {
            _keys.KeyDown[i] = false;
            _keys.Repeats[i] = 0;
        }
    }

    /// <summary>Sys_Quit asked for: the quit command from the console.</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>Con_Init's and Host_InitCommands' console commands of the screen.</summary>
    [NoAllocUnchecked("load path")]
    public void AddConsoleCommands(Cmd cmd)
    {
        _cmdForKeys = cmd;
        cmd.Add("toggleconsole", ToggleConsole);
        cmd.Add("messagemode", () => { _keys.Dest = Keys.KeyMessage; _keys.TeamMessage = false; });
        cmd.Add("messagemode2", () => { _keys.Dest = Keys.KeyMessage; _keys.TeamMessage = true; });
        cmd.Add("clear", () => new Span<byte>(_conText).Fill((byte)' '));
        cmd.Add("quit", Quit);
    }

    private Cmd _cmdForKeys = null!;

    /// <summary>Con_ToggleConsole_f.</summary>
    private void ToggleConsole()
    {
        if (_keys.Dest == Keys.KeyConsole)
        {
            if (_cl.State == ClientState.CaConnected)
            {
                _keys.Dest = Keys.KeyGame;
                _keys.Lines[_keys.EditLine][1] = 0;     // clear any typing
                _keys.LinePos = 1;
            }
            // else M_Menu_Main_f: the reference's stub
        }
        else
            _keys.Dest = Keys.KeyConsole;
        EndLoadingPlaque();
        ClearNotify();
    }

    /// <summary>Host_Quit_f: from the console only (elsewhere M_Menu_Quit_f, the reference's stub).</summary>
    private void Quit()
    {
        if (_keys.Dest != Keys.KeyConsole)
            return;
        QuitRequested = true;   // CL_Disconnect, Host_ShutdownServer, Sys_Quit: the platform ends
    }

    /// <summary>Cbuf_AddText of "fmt kb rest key\n" without allocating: prefix, the binding from start, the key number.</summary>
    [NoAllocUnchecked("input path: a key event, outside the host's frame")]
    private void AddButtonCommand(ReadOnlySpan<byte> prefix, byte[] binding, int start, int key)
    {
        Span<byte> buf = stackalloc byte[1100];
        int n = 0;
        prefix.CopyTo(buf);
        n += prefix.Length;
        int len = Math.Min(binding.Length - start, 1024);
        binding.AsSpan(start, len).CopyTo(buf[n..]);
        n += len;
        buf[n++] = (byte)' ';
        n += Edict.Exact.CFormat.Int(key, buf[n..]);
        buf[n++] = (byte)'\n';
        _cmdForKeys.AddText(buf[..n]);
    }

    /// <summary>Key_KeynumToString.</summary>
    [NoAllocUnchecked("input path: a key event, outside the host's frame")]
    private static ReadOnlySpan<byte> KeynumToString(int keynum, Span<byte> one)
    {
        if (keynum == -1)
            return "<KEY NOT FOUND>"u8;
        if (keynum > 32 && keynum < 127)
        {   // printable ascii
            one[0] = (byte)keynum;
            return one[..1];
        }
        ReadOnlySpan<byte> name = Keys.NameOf(keynum);
        return name.Length != 0 ? name : "<UNKNOWN KEYNUM>"u8;
    }

    /// <summary>Key_Event: a key down or up from the platform.</summary>
    [NoAllocUnchecked("input path: a key event, outside the host's frame")]
    public void KeyEvent(int key, bool down)
    {
        Keys k = _keys;
        k.KeyDown[key] = down;
        if (!down)
            k.Repeats[key] = 0;
        k.LastPress = key;
        k.Count++;
        if (k.Count <= 0)
            return;     // just catching keys for Con_NotifyBox

        // update auto-repeat status
        if (down)
        {
            k.Repeats[key]++;
            if (key != KBackspace && key != KPause && k.Repeats[key] > 1)
                return;     // ignore most autorepeats
            if (key >= 200 && k.Bindings[key] == null)
            {
                Span<byte> one = stackalloc byte[1];
                Msg(KeynumToString(key, one)).Msg(" is unbound, hit F4 to set.\n"u8).EndMsg();
            }
        }
        if (key == KShift)
            k.ShiftDown = down;

        // handle escape specialy, so the user can never unbind it
        if (key == KEscape)
        {
            if (!down)
                return;
            if (k.Dest == Keys.KeyMessage)
                KeyMessage(key);
            // key_menu: M_Keydown; key_game, key_console: M_ToggleMenu_f (the reference's stubs)
            return;
        }

        // key up events only generate commands if the game key binding is a button command (leading + sign).
        // These will occur even in console mode, to keep the character from continuing an action started before
        // a console switch. Button commands include the kenum as a parameter, so multiple downs can be matched
        // with ups
        if (!down)
        {
            byte[]? kb = k.Bindings[key];
            if (kb is { Length: > 0 } && kb[0] == '+')
                AddButtonCommand("-"u8, kb, 1, key);
            if (k.KeyShift[key] != key)
            {
                kb = k.Bindings[k.KeyShift[key]];
                if (kb is { Length: > 0 } && kb[0] == '+')
                    AddButtonCommand("-"u8, kb, 1, key);
            }
            return;
        }

        // during demo playback, most keys bring up the main menu (M_ToggleMenu_f: the stub)
        if (_cl.DemoPlayback && k.ConsoleKeys[key] && k.Dest == Keys.KeyGame)
            return;

        // if not a consolekey, send to the interpreter no matter what mode is
        if ((k.Dest == Keys.KeyMenu && k.MenuBound[key])
            || (k.Dest == Keys.KeyConsole && !k.ConsoleKeys[key])
            || (k.Dest == Keys.KeyGame && (!ForcedUp || !k.ConsoleKeys[key])))
        {
            byte[]? kb = k.Bindings[key];
            if (kb != null)
            {
                if (kb.Length > 0 && kb[0] == '+')
                    AddButtonCommand(""u8, kb, 0, key);     // button commands add keynum as a parm
                else
                {
                    _cmdForKeys.AddText(kb);
                    _cmdForKeys.AddText("\n"u8);
                }
            }
            return;
        }

        if (k.ShiftDown)
            key = k.KeyShift[key];
        switch (k.Dest)
        {
            case Keys.KeyMessage:
                KeyMessage(key);
                break;
            case Keys.KeyMenu:
                break;      // M_Keydown: the stub
            default:
                KeyConsole(key);
                break;
        }
    }

    /// <summary>The length of a zero-terminated input line.</summary>
    private static int LineLength(byte[] line)
    {
        int n = 0;
        while (n < line.Length && line[n] != 0)
            n++;
        return n;
    }

    /// <summary>Key_Console: interactive line editing and console scrollback.</summary>
    [NoAllocUnchecked("input path: a key event, outside the host's frame")]
    private void KeyConsole(int key)
    {
        Keys k = _keys;
        byte[] line = k.Lines[k.EditLine];
        if (key == KEnter)
        {
            int n = LineLength(line);
            _cmdForKeys.AddText(line.AsSpan(1, n - 1));     // skip the >
            _cmdForKeys.AddText("\n"u8);
            Msg(line.AsSpan(0, n)).Msg("\n"u8).EndMsg();
            k.EditLine = (k.EditLine + 1) & 31;
            k.HistoryLine = k.EditLine;
            k.Lines[k.EditLine][0] = (byte)']';
            k.LinePos = 1;
            if (_cl.State == ClientState.CaDisconnected)
                UpdateScreen();     // force an update, because the command may take some time
            return;
        }
        if (key == KTab)
        {
            // command completion
            byte[]? cmd = _cmdForKeys.Complete(line.AsSpan(1, LineLength(line) - 1));
            if (cmd != null && cmd.Length + 3 < Keys.MaxCmdLine)
            {
                cmd.CopyTo(line, 1);
                k.LinePos = cmd.Length + 1;
                line[k.LinePos] = (byte)' ';
                k.LinePos++;
                line[k.LinePos] = 0;
                return;
            }
        }
        if (key == KBackspace || key == KLeftArrow)
        {
            if (k.LinePos > 1)
                k.LinePos--;
            return;
        }
        if (key == KUpArrow)
        {
            do
            {
                k.HistoryLine = (k.HistoryLine - 1) & 31;
            } while (k.HistoryLine != k.EditLine && k.Lines[k.HistoryLine][1] == 0);
            if (k.HistoryLine == k.EditLine)
                k.HistoryLine = (k.EditLine + 1) & 31;
            CopyLine(k.Lines[k.HistoryLine], line);
            k.LinePos = LineLength(line);
            return;
        }
        if (key == KDownArrow)
        {
            if (k.HistoryLine == k.EditLine)
                return;
            do
            {
                k.HistoryLine = (k.HistoryLine + 1) & 31;
            } while (k.HistoryLine != k.EditLine && k.Lines[k.HistoryLine][1] == 0);
            if (k.HistoryLine == k.EditLine)
            {
                line[0] = (byte)']';
                k.LinePos = 1;
            }
            else
            {
                CopyLine(k.Lines[k.HistoryLine], line);
                k.LinePos = LineLength(line);
            }
            return;
        }
        if (key == KPgUp || key == KMWheelUp)
        {
            _backscroll += 2;
            if (_backscroll > _totalLines - (Height >> 3) - 1)
                _backscroll = _totalLines - (Height >> 3) - 1;
            return;
        }
        if (key == KPgDn || key == KMWheelDown)
        {
            _backscroll -= 2;
            if (_backscroll < 0)
                _backscroll = 0;
            return;
        }
        if (key == KHome)
        {
            _backscroll = _totalLines - (Height >> 3) - 1;
            return;
        }
        if (key == KEnd)
        {
            _backscroll = 0;
            return;
        }
        if (key < 32 || key > 127)
            return;     // non printable
        if (k.LinePos < Keys.MaxCmdLine - 1)
        {
            line[k.LinePos] = (byte)key;
            k.LinePos++;
            line[k.LinePos] = 0;
        }

        static void CopyLine(byte[] from, byte[] to)
        {
            int n = LineLength(from);
            from.AsSpan(0, n).CopyTo(to);
            to[n] = 0;
        }
    }

    /// <summary>Key_Message: the chat line.</summary>
    [NoAllocUnchecked("input path: a key event, outside the host's frame")]
    private void KeyMessage(int key)
    {
        Keys k = _keys;
        if (key == KEnter)
        {
            _cmdForKeys.AddText(k.TeamMessage ? "say_team \""u8 : "say \""u8);
            _cmdForKeys.AddText(k.ChatBuffer.AsSpan(0, k.ChatBufferLen));
            _cmdForKeys.AddText("\"\n"u8);
            k.Dest = Keys.KeyGame;
            k.ChatBufferLen = 0;
            k.ChatBuffer[0] = 0;
            return;
        }
        if (key == KEscape)
        {
            k.Dest = Keys.KeyGame;
            k.ChatBufferLen = 0;
            k.ChatBuffer[0] = 0;
            return;
        }
        if (key < 32 || key > 127)
            return;     // non printable
        if (key == KBackspace)
        {
            if (k.ChatBufferLen != 0)
            {
                k.ChatBufferLen--;
                k.ChatBuffer[k.ChatBufferLen] = 0;
            }
            return;
        }
        if (k.ChatBufferLen == 31)
            return;     // all full
        k.ChatBuffer[k.ChatBufferLen++] = (byte)key;
        k.ChatBuffer[k.ChatBufferLen] = 0;
    }

    /// <summary>Con_DrawNotify's chat line: "say:", the text and the cursor.</summary>
    private int DrawChatLine(int v)
    {
        _clearNotify = 0;
        _copyTop = true;
        int x = 0;
        Character(8, v, 's');
        Character(16, v, 'a');
        Character(24, v, 'y');
        Character(32, v, ':');
        while (_keys.ChatBuffer[x] != 0)
        {
            Character((x + 5) << 3, v, _keys.ChatBuffer[x]);
            x++;
        }
        Character((x + 5) << 3, v, 10 + ((int)(RealTime * CursorSpeed) & 1));
        return v + 8;
    }
}
