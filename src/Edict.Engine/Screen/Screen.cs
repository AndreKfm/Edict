// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>Where the screen presents a frame (VID_Update): the harness hashes it, a platform shows it.</summary>
public interface IScreenSink
{
    /// <summary>The frame buffer (<see cref="Screen.Width"/> x <see cref="Screen.Height"/>, row by row) and the palette it is shown through.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void Present(ReadOnlySpan<byte> frameBuffer, ReadOnlySpan<byte> palette);
}

/// <summary>
/// The screen of the software renderer (P6, docs/PLAN.md): the video driver of the reference (ref/src/edict_vid.c, a
/// 320x200 frame buffer and the palette it is shown through), and over it the 2D layer and the view — screen.c,
/// console.c, draw.c, sbar.c and view.c — and the 3D view, the software renderer's r_*.c and d_*.c (RMain.cs and
/// the files beside it) — with C's globals as fields of one object, as they share them. The world is drawn (step
/// 4); the entities and the gun are step 5. <see cref="Only2D"/> stands for the reference's -edict2d, which runs
/// the view and clears its rectangle.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe partial class Screen
{
    public const int Width = 320, Height = 200, NumPages = 1;

    private static readonly SysErrorException NotPortedYet = new("the screen reached a part not ported yet");

    // ---- the video driver (edict_vid.c): vid, vid_buffer and the palette
    private readonly byte[] _frame;                   // vid_buffer[BASEWIDTH*BASEHEIGHT], pinned
    private readonly byte* _buffer;                   // vid.buffer = vid.conbuffer
    private readonly byte[] _palette;                 // vid_palette: what VID_SetPalette and VID_ShiftPalette were given
    private readonly byte[] _basePal;                 // host_basepal (gfx/palette.lmp)
    private readonly byte[] _colormap;                // host_colormap (gfx/colormap.lmp) = vid.colormap
    private readonly int _fullbright;                 // vid.fullbright

    private readonly ClientState _cl;
    private readonly Cvars _cvars;
    private readonly Keys _keys;

    /// <summary>Where the frames go; null: nowhere.</summary>
    public IScreenSink? Sink { get; set; }

    /// <summary>The host's realtime and host_frametime, which the screen reads (set by the host every frame).</summary>
    public double RealTime, FrameTime;

    /// <summary>-edict2d: the view's rectangle is cleared after V_RenderView (the gate of step 3); what only draws is skipped.</summary>
    public bool Only2D { get; set; }

    /// <summary>The frame buffer, as the last present left it.</summary>
    public ReadOnlySpan<byte> FrameBuffer => _frame;

    /// <summary>The palette the frame buffer is shown through.</summary>
    public ReadOnlySpan<byte> Palette => _palette;

    // ---- screen.c
    private float _conCurrent;                        // scr_con_current: aproaches scr_conlines at scr_conspeed
    private float _conLines;                          // scr_conlines: lines of console to display
    private float _oldScreenSize, _oldFov, _oldViewSize, _oldLcdX;
    private int _fullUpdate;                          // scr_fullupdate
    private int _clearConsole, _clearNotify;
#pragma warning disable CS0414     // written as C writes them; they choose VID_Update's rectangles, and the port presents it all
    private bool _copyTop, _copyEverything;           // scr_copytop, scr_copyeverything
#pragma warning restore CS0414
    private bool _recalcRefdef;                       // vid.recalc_refdef
    private bool _initialized;                        // scr_initialized
    private bool _disabledForLoading;                 // scr_disabled_for_loading
    private bool _drawLoading;                        // scr_drawloading
    private float _disabledTime;                      // scr_disabled_time
    private bool _inUpdate;                           // Con_Printf's static inupdate
    private int _sbLines;                             // sb_lines
    private VRect _scrVrect;                          // scr_vrect

    // center printing
    private readonly byte[] _centerString;            // scr_centerstring[1024]
    private float _centerTimeStart, _centerTimeOff;
    private int _centerLines, _eraseLines, _eraseCenter;

    /// <summary>r_refdef.vrect: the 3D view's rectangle (R_ViewChanged).</summary>
    public VRect RefdefVrect;

    /// <summary>r_cache_thrash: set by the surface cache when its rover laps itself in a frame (D_SCAlloc); SCR_DrawRam shows it.</summary>
    public bool CacheThrash;

    /// <summary>vrect_t.</summary>
    public struct VRect
    {
        public int X, Y, Width, Height;
    }

    /// <summary>
    /// Con_Init (its place in Host_Init: what is printed from here on is in the console), and the palette and the
    /// colormap Host_Init loads before VID_Init. Then the host prints its lines and calls <see cref="InitVideo"/> and
    /// <see cref="InitScreen"/>, as Host_Init does.
    /// </summary>
    [NoAllocUnchecked("load path")]
    public Screen(QFileSystem files, Cvars cvars, ClientState cl, Keys keys)
    {
        _cvars = cvars;
        _cl = cl;
        _keys = keys;
        _files = files;
        InitConsole();
        _basePal = Load(files, "gfx/palette.lmp"u8);
        _colormap = Load(files, "gfx/colormap.lmp"u8);
        _frame = GC.AllocateArray<byte>(Width * Height, pinned: true);
        _buffer = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_frame, 0);
        _fullbright = 256 - BitConverter.ToInt32(_colormap, 2048 * 4);
        _palette = new byte[768];
        InitWorld();
        InitRender();
        _centerString = new byte[1024];
        _gammaTable = new byte[256];
        _pal = new byte[768];
    }

    /// <summary>VID_Init (host_basepal through VID_SetPalette; D_InitCaches prints the surface cache's size) and Draw_Init.</summary>
    [NoAllocUnchecked("load path")]
    public void InitVideo()
    {
        _basePal.CopyTo(_palette, 0);
        Printf("256k surface cache\n"u8);   // D_InitCaches: Con_Printf ("%ik surface cache\n", size/1024)
        InitDraw(_files);
    }

    /// <summary>SCR_Init: the icons; from here on Con_Printf updates the screen.</summary>
    [NoAllocUnchecked("load path")]
    public void InitScreen()
    {
        _ram = PicFromWad("ram"u8);
        _net = PicFromWad("net"u8);
        _turtle = PicFromWad("turtle"u8);
        _initialized = true;
    }

    [NoAllocUnchecked("load path")]
    private static byte[] Load(QFileSystem files, ReadOnlySpan<byte> name)
    {
        QFile f = files.Find(name) ?? throw new FileNotFoundException(System.Text.Encoding.Latin1.GetString(name));
        return f.Span.ToArray();
    }

    private readonly QFileSystem _files;

    private float CvarValue(ReadOnlySpan<byte> name)
    {
        fixed (byte* n = name)
            return _cvars.Value(n);
    }

    private void CvarSet(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        fixed (byte* n = name)
        fixed (byte* v = value)
            _cvars.Set(n, v);
    }

    /// <summary>vid.recalc_refdef = true: the next update recalculates the view's rectangle (SCR_CalcRefdef).</summary>
    public void RecalcRefdef() => _recalcRefdef = true;

    /// <summary>VID_Update: the present of the frame.</summary>
    private void Present() => Sink?.Present(_frame, _palette);

    /// <summary>VID_ShiftPalette.</summary>
    private void ShiftPalette(ReadOnlySpan<byte> pal) => pal.CopyTo(_palette);

    // ---- screen.c

    /// <summary>SCR_CenterPrint: called for important messages that should stay in the center of the screen for a few moments.</summary>
    public void CenterPrint(byte* str)
    {
        int i = 0;
        for (; i < _centerString.Length - 1 && str[i] != 0; i++)     // strncpy (.., sizeof-1): the last byte stays 0
            _centerString[i] = str[i];
        for (int k = i; k < _centerString.Length - 1; k++)
            _centerString[k] = 0;
        _centerTimeOff = CvarValue("scr_centertime\0"u8);
        _centerTimeStart = (float)_cl.Time;
        // count the number of lines for centering
        _centerLines = 1;
        for (; *str != 0; str++)
            if (*str == '\n')
                _centerLines++;
    }

    private void EraseCenterString()
    {
        if (_eraseCenter++ > NumPages)
        {
            _eraseLines = 0;
            return;
        }
        int y = _centerLines <= 4 ? (int)(Height * 0.35) : 48;
        _copyTop = true;
        TileClear(0, y, Width, 8 * _eraseLines);
    }

    private void DrawCenterString()
    {
        // the finale prints the characters one at a time
        int remaining = _cl.Intermission != 0
            ? CCastInt(CvarValue("scr_printspeed\0"u8) * (_cl.Time - _centerTimeStart))
            : 9999;
        _eraseCenter = 0;
        int start = 0;
        int y = _centerLines <= 4 ? (int)(Height * 0.35) : 48;
        while (true)
        {
            // scan the width of the line
            int l;
            for (l = 0; l < 40; l++)
                if (_centerString[start + l] == '\n' || _centerString[start + l] == 0)
                    break;
            int x = (Width - l * 8) / 2;
            for (int j = 0; j < l; j++, x += 8)
            {
                Character(x, y, _centerString[start + j]);
                if (remaining-- == 0)
                    return;
            }
            y += 8;
            while (_centerString[start] != 0 && _centerString[start] != '\n')
                start++;
            if (_centerString[start] == 0)
                break;
            start++;    // skip the \n
        }
    }

    /// <summary>(int) of a double, as C converts it (saturating, -fno-strict-float-cast-overflow).</summary>
    private static int CCastInt(double v) => Edict.Exact.CCast.I32(v);

    private void CheckDrawCenterString()
    {
        _copyTop = true;
        if (_centerLines > _eraseLines)
            _eraseLines = _centerLines;
        _centerTimeOff -= (float)FrameTime;
        if (_centerTimeOff <= 0 && _cl.Intermission == 0)
            return;
        if (_keys.Dest != Keys.KeyGame)
            return;
        DrawCenterString();
    }

    /// <summary>SCR_CalcRefdef: must be called whenever vid changes.</summary>
    private void CalcRefdef()
    {
        _fullUpdate = 0;        // force a background redraw
        _recalcRefdef = false;
        SbarChanged();          // force the status bar to redraw

        // bound viewsize
        if (CvarValue("viewsize\0"u8) < 30)
            CvarSet("viewsize\0"u8, "30\0"u8);
        if (CvarValue("viewsize\0"u8) > 120)
            CvarSet("viewsize\0"u8, "120\0"u8);
        // bound field of view
        if (CvarValue("fov\0"u8) < 10)
            CvarSet("fov\0"u8, "10\0"u8);
        if (CvarValue("fov\0"u8) > 170)
            CvarSet("fov\0"u8, "170\0"u8);

        FovX = CvarValue("fov\0"u8);
        FovY = CalcFov(FovX, RefdefVrect.Width, RefdefVrect.Height);

        // intermission is always full screen
        float size = _cl.Intermission != 0 ? 120 : CvarValue("viewsize\0"u8);
        if (size >= 120)
            _sbLines = 0;           // no status bar at all
        else if (size >= 110)
            _sbLines = 24;          // no inventory
        else
            _sbLines = 24 + 16 + 8;

        // these calculations mirror those in R_Init() for r_refdef, but take no account of water warping
        var vrect = new VRect { X = 0, Y = 0, Width = Width, Height = Height };
        SetVrect(vrect, ref _scrVrect, _sbLines);
        // guard against going from one mode to another that's less than half the vertical resolution
        if (_conCurrent > Height)
            _conCurrent = Height;
        // notify the refresh of the change
        ViewChanged(vrect, _sbLines);
    }

    /// <summary>r_refdef.fov_x, fov_y.</summary>
    public float FovX, FovY;

    /// <summary>CalcFov (screen.c).</summary>
    private static float CalcFov(float fovX, float width, float height)
    {
        if (fovX < 1 || fovX > 179)
            throw BadFov;
        double x = width / Edict.Exact.CMath.Tan(fovX / 360 * Math.PI);
        double a = Edict.Exact.CMath.Atan(height / x);
        a = a * 360 / Math.PI;
        return (float)a;
    }

    private static readonly SysErrorException BadFov = new("Bad fov");

    /// <summary>R_SetVrect (r_misc.c): the view's rectangle for a viewsize and status bar.</summary>
    private void SetVrect(VRect pvrectin, ref VRect pvrect, int lineadj)
    {
        float size = CvarValue("viewsize\0"u8) > 100 ? 100 : CvarValue("viewsize\0"u8);
        if (_cl.Intermission != 0)
        {
            size = 100;
            lineadj = 0;
        }
        size /= 100;
        int h = pvrectin.Height - lineadj;
        pvrect.Width = (int)(pvrectin.Width * size);
        if (pvrect.Width < 96)
        {
            size = (float)(96.0 / pvrectin.Width);
            pvrect.Width = 96;  // min for icons
        }
        pvrect.Width &= ~7;
        pvrect.Height = (int)(pvrectin.Height * size);
        if (pvrect.Height > pvrectin.Height - lineadj)
            pvrect.Height = pvrectin.Height - lineadj;
        pvrect.Height &= ~1;
        pvrect.X = (pvrectin.Width - pvrect.Width) / 2;
        pvrect.Y = (h - pvrect.Height) / 2;
        if (CvarValue("lcd_x\0"u8) != 0)
        {
            pvrect.Y >>= 1;
            pvrect.Height >>= 1;
        }
    }

    /// <summary>SCR_DrawRam.</summary>
    private void DrawRam()
    {
        if (CvarValue("showram\0"u8) == 0)
            return;
        if (!CacheThrash)
            return;
        Pic(_scrVrect.X + 32, _scrVrect.Y, _ram);
    }

    private int _turtleCount;

    /// <summary>SCR_DrawTurtle.</summary>
    private void DrawTurtle()
    {
        if (CvarValue("showturtle\0"u8) == 0)
            return;
        if (FrameTime < 0.1)
        {
            _turtleCount = 0;
            return;
        }
        _turtleCount++;
        if (_turtleCount < 3)
            return;
        Pic(_scrVrect.X, _scrVrect.Y, _turtle);
    }

    /// <summary>SCR_DrawNet.</summary>
    private void DrawNet()
    {
        if (RealTime - _cl.LastReceivedMessage < 0.3)
            return;
        if (_cl.DemoPlayback)
            return;
        Pic(_scrVrect.X + 64, _scrVrect.Y, _net);
    }

    /// <summary>SCR_DrawPause.</summary>
    private void DrawPause()
    {
        if (CvarValue("showpause\0"u8) == 0)       // turn off for screenshots
            return;
        if (_cl.Paused == 0)
            return;
        QPic pic = CachePic("gfx/pause.lmp"u8);
        Pic((Width - pic.Width) / 2, (Height - 48 - pic.Height) / 2, pic);
    }

    /// <summary>SCR_DrawLoading.</summary>
    private void DrawLoading()
    {
        if (!_drawLoading)
            return;
        QPic pic = CachePic("gfx/loading.lmp"u8);
        Pic((Width - pic.Width) / 2, (Height - 48 - pic.Height) / 2, pic);
    }

    /// <summary>SCR_SetUpToDrawConsole.</summary>
    private void SetUpToDrawConsole()
    {
        CheckResize();
        if (_drawLoading)
            return;     // never a console with loading plaque

        // decide on the height of the console
        ForcedUp = _cl.WorldModel == null || _cl.Signon != ClientState.Signons;
        if (ForcedUp)
        {
            _conLines = Height;     // full screen
            _conCurrent = _conLines;
        }
        else if (_keys.Dest == Keys.KeyConsole)
            _conLines = Height / 2; // half screen
        else
            _conLines = 0;          // none visible

        if (_conLines < _conCurrent)
        {
            _conCurrent -= (float)(CvarValue("scr_conspeed\0"u8) * FrameTime);
            if (_conLines > _conCurrent)
                _conCurrent = _conLines;
        }
        else if (_conLines > _conCurrent)
        {
            _conCurrent += (float)(CvarValue("scr_conspeed\0"u8) * FrameTime);
            if (_conLines < _conCurrent)
                _conCurrent = _conLines;
        }

        if (_clearConsole++ < NumPages)
        {
            _copyTop = true;
            TileClear(0, (int)_conCurrent, Width, Height - (int)_conCurrent);
            SbarChanged();
        }
        else if (_clearNotify++ < NumPages)
        {
            _copyTop = true;
            TileClear(0, 0, Width, _notifyLines);
        }
        else
            _notifyLines = 0;
    }

    /// <summary>SCR_DrawConsole.</summary>
    private void DrawConsole()
    {
        if (_conCurrent != 0)
        {
            _copyEverything = true;
            DrawConsoleLines((int)_conCurrent, true);
            _clearConsole = 0;
        }
        else if (_keys.Dest == Keys.KeyGame || _keys.Dest == Keys.KeyMessage)
            DrawNotify();   // only draw notify in game
    }

    /// <summary>SCR_BeginLoadingPlaque.</summary>
    public void BeginLoadingPlaque()
    {
        // S_StopAllSounds (true): no sound
        if (_cl.State != ClientState.CaConnected)
            return;
        if (_cl.Signon != ClientState.Signons)
            return;
        // redraw with no console and the loading plaque
        ClearNotify();
        _centerTimeOff = 0;
        _conCurrent = 0;
        _drawLoading = true;
        _fullUpdate = 0;
        SbarChanged();
        UpdateScreen();
        _drawLoading = false;
        _disabledForLoading = true;
        _disabledTime = (float)RealTime;
        _fullUpdate = 0;
    }

    /// <summary>SCR_EndLoadingPlaque.</summary>
    public void EndLoadingPlaque()
    {
        _disabledForLoading = false;
        _fullUpdate = 0;
        ClearNotify();
    }

    /// <summary>
    /// SCR_UpdateScreen: called every frame, and by Con_Printf while the console is up, to fill and present the frame
    /// buffer.
    /// </summary>
    public void UpdateScreen()
    {
        // scr_skipupdate (-edictclient) and block_drawing: the hosts that show no screen have no Screen
        _copyTop = false;
        _copyEverything = false;

        if (_disabledForLoading)
        {
            if (RealTime - _disabledTime > 60)
            {
                _disabledForLoading = false;
                Printf("load failed.\n"u8);
            }
            else
                return;
        }
        if (!_initialized || !_conInitialized)
            return;     // not initialized yet

        if (CvarValue("viewsize\0"u8) != _oldViewSize)
        {
            _oldViewSize = CvarValue("viewsize\0"u8);
            _recalcRefdef = true;
        }
        // check for vid changes
        if (_oldFov != CvarValue("fov\0"u8))
        {
            _oldFov = CvarValue("fov\0"u8);
            _recalcRefdef = true;
        }
        if (_oldLcdX != CvarValue("lcd_x\0"u8))
        {
            _oldLcdX = CvarValue("lcd_x\0"u8);
            _recalcRefdef = true;
        }
        if (_oldScreenSize != CvarValue("viewsize\0"u8))
        {
            _oldScreenSize = CvarValue("viewsize\0"u8);
            _recalcRefdef = true;
        }
        if (_recalcRefdef)
            CalcRefdef();   // something changed, so reorder the screen

        // do 3D refresh drawing, and then update the screen
        if (_fullUpdate++ < NumPages)
        {
            // clear the entire screen
            _copyEverything = true;
            TileClear(0, 0, Width, Height);
            SbarChanged();
        }

        SetUpToDrawConsole();
        EraseCenterString();

        RenderView();

        if (_drawLoading)
        {
            DrawLoading();
            SbarDraw();
        }
        else if (_cl.Intermission == 1 && _keys.Dest == Keys.KeyGame)
            IntermissionOverlay();
        else if (_cl.Intermission == 2 && _keys.Dest == Keys.KeyGame)
        {
            FinaleOverlay();
            CheckDrawCenterString();
        }
        else if (_cl.Intermission == 3 && _keys.Dest == Keys.KeyGame)
            CheckDrawCenterString();
        else
        {
            DrawRam();
            DrawNet();
            DrawTurtle();
            DrawPause();
            CheckDrawCenterString();
            SbarDraw();
            DrawConsole();
            // M_Draw: the reference's menu is a stub (edict_menu.c)
        }

        UpdatePalette();
        Present();
    }

    /// <summary>
    /// V_RenderView, and with <see cref="Only2D"/> the reference's -edict2d after it: the view's rectangle cleared.
    /// </summary>
    private void RenderView()
    {
        ViewRender();
        if (!Only2D)
            return;
        for (int y = RefdefVrect.Y; y < RefdefVrect.Y + RefdefVrect.Height; y++)
            new Span<byte>(_buffer + y * Width + RefdefVrect.X, RefdefVrect.Width).Clear();
    }
}
