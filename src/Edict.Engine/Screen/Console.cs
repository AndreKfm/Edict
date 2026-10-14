// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>console.c: the scrollback text, Con_Printf (which redraws the screen while the console is up) and the drawing of the console and the notify lines.</summary>
public sealed unsafe partial class Screen
{
    private const int TextSize = 16384, NumConTimes = 4, MaxPrintMsg = 4096;
    private const float CursorSpeed = 4;              // con_cursorspeed

    private byte[] _conText = null!;                  // con_text
    private int _lineWidth;                           // con_linewidth: characters across screen
    private int _totalLines;                          // con_totallines: total lines in console scrollback
    private int _backscroll;                          // con_backscroll: lines up from bottom to display
    private int _current;                             // con_current: where next message will be printed
    private int _conX;                                // con_x: offset in current line for next print
    private bool _cr;                                 // Con_Print's static cr
    private float[] _conTimes = null!;                // realtime the line was generated, for transparent notify lines
    private int _visLines;                            // con_vislines
    private int _notifyLines;                         // con_notifylines: scan lines to clear for notify lines
    private bool _conInitialized;                     // con_initialized

    /// <summary>con_forcedup: because no entities to refresh.</summary>
    public bool ForcedUp;

    // the message Con_Printf formats (msg[MAXPRINTMSG]), built by Msg and printed by EndMsg
    private byte[] _msg = null!;
    private int _msgLength;

    /// <summary>Con_Init: the text buffer cleared, sized as C sizes it before the video exists (38 columns).</summary>
    [NoAllocUnchecked("load path")]
    private void InitConsole()
    {
        _conText = GC.AllocateArray<byte>(TextSize, pinned: true);
        _conTimes = new float[NumConTimes];
        _msg = GC.AllocateArray<byte>(MaxPrintMsg, pinned: true);
        new Span<byte>(_conText).Fill((byte)' ');
        _lineWidth = -1;
        CheckResize(videoWidth: 0);
        // Con_Printf ("Console initialized.\n") comes before con_initialized is set: it reaches only stdout
        _conInitialized = true;
    }

    /// <summary>Con_ClearNotify.</summary>
    public void ClearNotify()
    {
        for (int i = 0; i < NumConTimes; i++)
            _conTimes[i] = 0;
    }

    private void CheckResize() => CheckResize(Width);

    /// <summary>Con_CheckResize: if the line width has changed, reformat the buffer.</summary>
    private void CheckResize(int videoWidth)
    {
        int width = (videoWidth >> 3) - 2;
        if (width == _lineWidth)
            return;
        if (width < 1)      // video hasn't been initialized yet
        {
            width = 38;
            _lineWidth = width;
            _totalLines = TextSize / _lineWidth;
            new Span<byte>(_conText).Fill((byte)' ');
        }
        else
            throw NotPortedYet;     // a console of another width than C's first one (vid.width 320 gives 38 too)
        _backscroll = 0;
        _current = _totalLines - 1;
    }

    /// <summary>Con_Linefeed.</summary>
    private void Linefeed()
    {
        _conX = 0;
        _current++;
        new Span<byte>(_conText, (_current % _totalLines) * _lineWidth, _lineWidth).Fill((byte)' ');
    }

    /// <summary>
    /// Con_Print: handles cursor positioning, line wrapping, etc. All console printing must go through this in order
    /// to be logged to disk. If no console is visible, the notify window will pop up.
    /// </summary>
    private void Print(byte* txt)
    {
        int mask;
        _backscroll = 0;
        if (txt[0] == 1)
        {
            mask = 128;     // go to colored text
            // S_LocalSound ("misc/talk.wav"): no sound
            txt++;
        }
        else if (txt[0] == 2)
        {
            mask = 128;     // go to colored text
            txt++;
        }
        else
            mask = 0;

        int c;
        while ((c = (sbyte)*txt) != 0)
        {
            // count word length
            int l;
            for (l = 0; l < _lineWidth; l++)
                if ((sbyte)txt[l] <= ' ')
                    break;
            // word wrap
            if (l != _lineWidth && _conX + l > _lineWidth)
                _conX = 0;

            txt++;
            if (_cr)
            {
                _current--;
                _cr = false;
            }
            if (_conX == 0)
            {
                Linefeed();
                // mark time for transparent overlay
                if (_current >= 0)
                    _conTimes[_current % NumConTimes] = (float)RealTime;
            }
            switch (c)
            {
                case '\n':
                    _conX = 0;
                    break;
                case '\r':
                    _conX = 0;
                    _cr = true;
                    break;
                default:    // display character and advance
                    int y = _current % _totalLines;
                    _conText[y * _lineWidth + _conX] = (byte)(c | mask);
                    _conX++;
                    if (_conX >= _lineWidth)
                        _conX = 0;
                    break;
            }
        }
    }

    /// <summary>Con_Printf of a text without conversions.</summary>
    public void Printf(ReadOnlySpan<byte> text)
    {
        Msg(text);
        EndMsg();
    }

    /// <summary>Appends to the message Con_Printf formats (the text, or a zero-terminated string).</summary>
    public Screen Msg(ReadOnlySpan<byte> text)
    {
        int n = Math.Min(text.Length, MaxPrintMsg - 1 - _msgLength);
        if (n < text.Length)
            throw MsgTooLong;
        text[..n].CopyTo(new Span<byte>(_msg, _msgLength, n));
        _msgLength += n;
        return this;
    }

    /// <summary>Appends a zero-terminated string ("%s").</summary>
    public Screen Msg(byte* s)
    {
        int n = 0;
        while (s[n] != 0)
            n++;
        return Msg(new ReadOnlySpan<byte>(s, n));
    }

    /// <summary>Appends "%i".</summary>
    public Screen MsgInt(int value)
    {
        Span<byte> t = stackalloc byte[12];
        int n = Edict.Exact.CFormat.Int(value, t);
        return Msg(t[..n]);
    }

    private static readonly SysErrorException MsgTooLong = new("Con_Printf: a message longer than msg[MAXPRINTMSG]");

    /// <summary>
    /// Con_Printf of the message built with <see cref="Msg(ReadOnlySpan{byte})"/>: into the scrollable buffer, and
    /// while the signon is not complete the screen is updated, so the console shows it.
    /// </summary>
    public void EndMsg()
    {
        _msg[_msgLength] = 0;
        _msgLength = 0;
        // Sys_Printf (stdout) and the -condebug log are output only
        if (!_conInitialized)
            return;
        fixed (byte* m = _msg)
            Print(m);
        // update the screen if the console is displayed
        if (_cl.Signon != ClientState.Signons && !_disabledForLoading)
        {
            // protect against infinite loop if something in SCR_UpdateScreen calls Con_Printf
            if (!_inUpdate)
            {
                _inUpdate = true;
                UpdateScreen();
                _inUpdate = false;
            }
        }
    }

    /// <summary>Con_DrawInput: the input line, with the cursor, when the console is down or forced up.</summary>
    private void DrawInput()
    {
        if (_keys.Dest != Keys.KeyConsole && !ForcedUp)
            return;     // don't draw anything
        byte[] text = _keys.Lines[_keys.EditLine];
        // add the cursor frame
        text[_keys.LinePos] = (byte)(10 + ((int)(RealTime * CursorSpeed) & 1));
        // fill out remainder with spaces
        for (int i = _keys.LinePos + 1; i < _lineWidth; i++)
            text[i] = (byte)' ';
        // prestep if horizontally scrolling
        int start = _keys.LinePos >= _lineWidth ? 1 + _keys.LinePos - _lineWidth : 0;
        // draw it
        for (int i = 0; i < _lineWidth; i++)
            Character((i + 1) << 3, _visLines - 16, text[start + i]);
        // remove cursor
        text[_keys.LinePos] = 0;
    }

    /// <summary>Con_DrawNotify: draws the last few lines of output transparently over the game top.</summary>
    private void DrawNotify()
    {
        int v = 0;
        for (int i = _current - NumConTimes + 1; i <= _current; i++)
        {
            if (i < 0)
                continue;
            float time = _conTimes[i % NumConTimes];
            if (time == 0)
                continue;
            time = (float)(RealTime - time);
            if (time > CvarValue("con_notifytime\0"u8))
                continue;
            int text = (i % _totalLines) * _lineWidth;
            _clearNotify = 0;
            _copyTop = true;
            for (int x = 0; x < _lineWidth; x++)
                Character((x + 1) << 3, v, _conText[text + x]);
            v += 8;
        }
        if (_keys.Dest == Keys.KeyMessage)
            v = DrawChatLine(v);
        if (v > _notifyLines)
            _notifyLines = v;
    }

    /// <summary>Con_DrawConsole: draws the console with the solid background.</summary>
    private void DrawConsoleLines(int lines, bool drawInput)
    {
        if (lines <= 0)
            return;
        // draw the background
        ConsoleBackground(lines);
        // draw the text
        _visLines = lines;
        int rows = (lines - 16) >> 3;       // rows of text to draw
        int y = lines - 16 - (rows << 3);   // may start slightly negative
        for (int i = _current - rows + 1; i <= _current; i++, y += 8)
        {
            int j = i - _backscroll;
            if (j < 0)
                j = 0;
            int text = (j % _totalLines) * _lineWidth;
            for (int x = 0; x < _lineWidth; x++)
                Character((x + 1) << 3, y, _conText[text + x]);
        }
        // draw the input prompt, user text, and cursor if desired
        if (drawInput)
            DrawInput();
    }
}
