// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Engine;
using Edict.Gl;
using static Edict.Platform.Win32.Native;

namespace Edict.Platform.Win32;

/// <summary>
/// The game's window on Windows (vid_win.c, in_win.c without DirectInput): with OpenGL (WGL, a 3.3 core context) the
/// GPU renderer draws the view and the 2D layer over it, else the 8-bit frame buffer is shown through the palette by
/// StretchDIBits; the keys by scan code (vid_win.c's scantokey), the mouse buttons and wheel as keys, the
/// mouse's motion from the window's centre, the cursor hidden and held there while the game has the mouse.
/// </summary>
internal sealed unsafe class Win32Window : IGameWindow
{
    private const string ClassName = "EdictWindow";
    private const int Scale = 3;

    private static Win32Window? _current;             // the one window, for the window procedure

    private readonly nint _hwnd;
    private readonly byte[] _frame = new byte[Screen.Width * Screen.Height];
    private readonly BitmapInfo256* _bmi;
    private Screen? _screen;
    private readonly nint _hdc, _glContext;
    private GlRenderer? _renderer;
    private bool _active, _closed, _mouseHeld;
    private int _mouseState;                          // in_win.c's mouse_oldbuttonstate

    public Win32Window(string title, bool gl, bool visible = true)
    {
        _current = this;
        timeBeginPeriod(1);
        nint instance = GetModuleHandle(0);
        fixed (char* cls = ClassName)
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                Style = CsOwnDc,
                WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                Instance = instance,
                Cursor = LoadCursor(0, IdcArrow),
                ClassName = (nint)cls,
            };
            RegisterClassEx(&wc);
        }
        var r = new Rect { Right = Screen.Width * Scale, Bottom = Screen.Height * Scale };
        AdjustWindowRect(&r, WsOverlappedWindow, 0);
        _hwnd = CreateWindowEx(0, ClassName, title, WsOverlappedWindow | (visible ? WsVisible : 0), CwUseDefault, CwUseDefault,
            r.Right - r.Left, r.Bottom - r.Top, 0, 0, instance, 0);
        if (_hwnd == 0)
            throw new InvalidOperationException("CreateWindowEx failed");
        _bmi = (BitmapInfo256*)NativeMemory.AllocZeroed((nuint)sizeof(BitmapInfo256));
        _bmi->Size = 40;
        _bmi->Width = Screen.Width;
        _bmi->Height = -Screen.Height;   // top-down rows
        _bmi->Planes = 1;
        _bmi->BitCount = 8;
        _bmi->ClrUsed = 256;
        if (visible)
            ShowWindow(_hwnd, SwShow);
        _hdc = GetDC(_hwnd);
        if (gl)
            _glContext = CreateGlContext(_hdc);
    }

    /// <summary>A pixel format with a depth buffer, a legacy context to find wglCreateContextAttribsARB, a 3.3 core context; 0 if any step fails.</summary>
    private static nint CreateGlContext(nint hdc)
    {
        var pfd = new PixelFormatDescriptor
        {
            Size = (ushort)sizeof(PixelFormatDescriptor), Version = 1,
            Flags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer, ColorBits = 32, DepthBits = 24, StencilBits = 8,
        };
        int format = ChoosePixelFormat(hdc, &pfd);
        if (format == 0 || SetPixelFormat(hdc, format, &pfd) == 0)
            return 0;
        nint legacy = wglCreateContext(hdc);
        if (legacy == 0 || wglMakeCurrent(hdc, legacy) == 0)
            return 0;
        nint create = wglGetProcAddress("wglCreateContextAttribsARB");
        nint context = 0;
        if (create != 0)
        {
            int* attribs = stackalloc int[] { 0x2091, 3, 0x2092, 3, 0x9126, 1, 0 };   // 3.3, core profile
            context = ((delegate* unmanaged<nint, nint, int*, nint>)create)(hdc, 0, attribs);
        }
        wglMakeCurrent(0, 0);
        wglDeleteContext(legacy);
        if (context == 0 || wglMakeCurrent(hdc, context) == 0)
            return 0;
        nint opengl32 = NativeLibrary.Load("opengl32.dll");
        Gl.Gl.Load(name =>
        {
            nint p = wglGetProcAddress(name);
            if (p is 0 or 1 or 2 or 3 or -1)
                NativeLibrary.TryGetExport(opengl32, name, out p);
            return p;
        });
        return context;
    }

    public bool HasGl => _glContext != 0;

    public void Attach(Screen screen)
    {
        _screen = screen;
        if (HasGl)
            _renderer = new GlRenderer(screen);
    }

    public bool Active => _active && _screen is { } s && s.KeyDest == Keys.KeyGame && !s.ForcedUp;

    /// <summary>The window's messages; false once it was closed.</summary>
    public bool Pump()
    {
        Msg msg;
        while (PeekMessage(&msg, 0, 0, 0, PmRemove) != 0)
        {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
        HoldMouse(Active);
        return !_closed;
    }

    private readonly byte[] _palette = new byte[768];
    private bool _pending;

    /// <summary>VID_Update: the frame and its palette kept for <see cref="Render"/>, after the host's frame.</summary>
    public void Present(ReadOnlySpan<byte> frameBuffer, ReadOnlySpan<byte> palette)
    {
        frameBuffer.CopyTo(_frame);
        palette.CopyTo(_palette);
        _pending = true;
    }

    /// <summary>The last present drawn again and saved: the GPU renderer's window, or the 8-bit frame.</summary>
    public bool Capture(string path)
    {
        if (_renderer == null)
            return false;
        Rect c;
        GetClientRect(_hwnd, &c);
        _renderer.DrawFrame(_frame, _palette, c.Right, c.Bottom);
        GlRenderer.WriteBmp(path, GlRenderer.ReadBack(c.Right, c.Bottom), c.Right, c.Bottom);
        return true;
    }

    /// <summary>The last present shown: the GPU renderer's view and the 2D layer, or the 8-bit frame through StretchDIBits.</summary>
    public void Render()
    {
        if (!_pending)
            return;
        _pending = false;
        if (_renderer != null)
        {
            Rect c;
            GetClientRect(_hwnd, &c);
            _renderer.DrawFrame(_frame, _palette, c.Right, c.Bottom);
            SwapBuffers(_hdc);
            return;
        }
        for (int i = 0; i < 256; i++)
            _bmi->Colors[i] = (uint)(_palette[i * 3 + 2] | _palette[i * 3 + 1] << 8 | _palette[i * 3] << 16);
        Blit(_hdc);
    }

    [NoAlloc(Constraints.All)]
    private void Blit(nint hdc)
    {
        Rect c;
        GetClientRect(_hwnd, &c);
        SetStretchBltMode(hdc, ColorOnColor);
        fixed (byte* bits = _frame)
            StretchDIBits(hdc, 0, 0, c.Right, c.Bottom, 0, 0, Screen.Width, Screen.Height, bits, _bmi, DibRgbColors, SrcCopy);
    }

    /// <summary>The window's centre on the screen (in_win.c's window_center_x, window_center_y).</summary>
    [NoAlloc(Constraints.All)]
    private Point Centre()
    {
        Rect c;
        GetClientRect(_hwnd, &c);
        var p = new Point { X = c.Right / 2, Y = c.Bottom / 2 };
        ClientToScreen(_hwnd, &p);
        return p;
    }

    /// <summary>IN_ActivateMouse, IN_DeactivateMouse: the cursor hidden, confined and centred while the game has the mouse.</summary>
    private void HoldMouse(bool hold)
    {
        if (hold == _mouseHeld)
            return;
        _mouseHeld = hold;
        if (hold)
        {
            Rect c;
            GetClientRect(_hwnd, &c);
            var tl = new Point { X = c.Left, Y = c.Top };
            var br = new Point { X = c.Right, Y = c.Bottom };
            ClientToScreen(_hwnd, &tl);
            ClientToScreen(_hwnd, &br);
            var clip = new Rect { Left = tl.X, Top = tl.Y, Right = br.X, Bottom = br.Y };
            ClipCursor(&clip);
            SetCapture(_hwnd);
            ShowCursor(0);
            Point p = Centre();
            SetCursorPos(p.X, p.Y);
        }
        else
        {
            ClipCursor(null);
            ReleaseCapture();
            ShowCursor(1);
        }
    }

    /// <summary>The mouse's motion since the last move, the cursor put back to the centre (in_win.c without DirectInput).</summary>
    public void TakeMotion(out int mx, out int my)
    {
        Point c = Centre(), p;
        GetCursorPos(&p);
        mx = p.X - c.X;
        my = p.Y - c.Y;
        // if the mouse has moved, force it to the center, so there's room to move
        if (mx != 0 || my != 0)
            SetCursorPos(c.X, c.Y);
    }

    private static ReadOnlySpan<byte> ScanToKey =>
    [
        0, 27, (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6',
        (byte)'7', (byte)'8', (byte)'9', (byte)'0', (byte)'-', (byte)'=', Screen.KBackspace, 9,
        (byte)'q', (byte)'w', (byte)'e', (byte)'r', (byte)'t', (byte)'y', (byte)'u', (byte)'i',
        (byte)'o', (byte)'p', (byte)'[', (byte)']', 13, Screen.KCtrl, (byte)'a', (byte)'s',
        (byte)'d', (byte)'f', (byte)'g', (byte)'h', (byte)'j', (byte)'k', (byte)'l', (byte)';',
        (byte)'\'', (byte)'`', Screen.KShift, (byte)'\\', (byte)'z', (byte)'x', (byte)'c', (byte)'v',
        (byte)'b', (byte)'n', (byte)'m', (byte)',', (byte)'.', (byte)'/', Screen.KShift, (byte)'*',
        Screen.KAlt, (byte)' ', 0, Screen.KF1, Screen.KF1 + 1, Screen.KF1 + 2, Screen.KF1 + 3, Screen.KF1 + 4,
        Screen.KF1 + 5, Screen.KF1 + 6, Screen.KF1 + 7, Screen.KF1 + 8, Screen.KF1 + 9, Screen.KPause, 0, Screen.KHome,
        Screen.KUpArrow, Screen.KPgUp, (byte)'-', Screen.KLeftArrow, (byte)'5', Screen.KRightArrow, (byte)'+', Screen.KEnd,
        Screen.KDownArrow, Screen.KPgDn, Screen.KIns, Screen.KDel, 0, 0, 0, Screen.KF1 + 10,
        Screen.KF12, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>MapKey: the scan code of a key message's lParam to a Quake key.</summary>
    private static int MapKey(nint lParam)
    {
        int key = (int)((lParam >> 16) & 255);
        return key > 127 ? 0 : ScanToKey[key];
    }

    /// <summary>IN_MouseEvent: the buttons that changed, as key events.</summary>
    private void MouseEvent(int mstate)
    {
        for (int i = 0; i < 3; i++)
        {
            if ((mstate & (1 << i)) != 0 && (_mouseState & (1 << i)) == 0)
                _screen?.KeyEvent(Screen.KMouse1 + i, true);
            if ((mstate & (1 << i)) == 0 && (_mouseState & (1 << i)) != 0)
                _screen?.KeyEvent(Screen.KMouse1 + i, false);
        }
        _mouseState = mstate;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        Win32Window? w = _current;
        if (w == null)
            return DefWindowProc(hwnd, msg, wParam, lParam);
        Screen? s = w._screen;
        switch (msg)
        {
            case WmKeyDown:
            case WmSysKeyDown:
                if (MapKey(lParam) is var kd and not 0)
                    s?.KeyEvent(kd, true);
                return 0;
            case WmKeyUp:
            case WmSysKeyUp:
                if (MapKey(lParam) is var ku and not 0)
                    s?.KeyEvent(ku, false);
                return 0;
            case WmMouseMove:
            case WmLButtonDown:
            case WmLButtonUp:
            case WmRButtonDown:
            case WmRButtonUp:
            case WmMButtonDown:
            case WmMButtonUp:
            {
                int w32 = (int)wParam, mstate = 0;
                if ((w32 & 0x0001) != 0)
                    mstate |= 1;    // MK_LBUTTON
                if ((w32 & 0x0002) != 0)
                    mstate |= 2;    // MK_RBUTTON
                if ((w32 & 0x0010) != 0)
                    mstate |= 4;    // MK_MBUTTON
                w.MouseEvent(mstate);
                return 0;
            }
            case WmMouseWheel:
            {
                int key = (short)((long)wParam >> 16) > 0 ? Screen.KMWheelUp : Screen.KMWheelDown;
                s?.KeyEvent(key, true);
                s?.KeyEvent(key, false);
                return 0;
            }
            case WmActivate:
                w._active = (wParam & 0xFFFF) != 0;
                if (!w._active)
                {
                    s?.ClearKeyStates();    // ClearAllStates
                    w.HoldMouse(false);
                    w._mouseState = 0;
                }
                return 0;
            case WmPaint:
            {
                PaintStruct ps;
                nint hdc = BeginPaint(hwnd, &ps);
                if (w._renderer == null)
                    w.Blit(hdc);
                EndPaint(hwnd, &ps);
                return 0;
            }
            case WmClose:
                w._closed = true;
                return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        HoldMouse(false);
        _renderer?.Dispose();
        if (_glContext != 0)
        {
            wglMakeCurrent(0, 0);
            wglDeleteContext(_glContext);
        }
        ReleaseDC(_hwnd, _hdc);
        DestroyWindow(_hwnd);
        NativeMemory.Free(_bmi);
        _current = null;
    }
}
