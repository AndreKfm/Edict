// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Engine;

namespace Edict.Platform.X11;

/// <summary>
/// The game's window on Linux through Xlib (vid_x.c's way): a 32-bit XImage the palette's colors are scaled into,
/// XPutImage at every present; the keys by keysym (XLateKey), autorepeat made detectable (no release between the
/// repeats), the mouse buttons and wheel as keys, the motion from the window's centre with the pointer warped back
/// and grabbed, the cursor hidden, while the game has the mouse.
/// </summary>
internal sealed unsafe partial class X11Window : IGameWindow
{
    private const string Lib = "libX11.so.6";
    private const int Scale = 3, W = Screen.Width * Scale, H = Screen.Height * Scale;
    private const long KeyPressMask = 1 << 0, KeyReleaseMask = 1 << 1, ButtonPressMask = 1 << 2,
        ButtonReleaseMask = 1 << 3, PointerMotionMask = 1 << 6, ExposureMask = 1 << 15, StructureNotifyMask = 1 << 17,
        FocusChangeMask = 1 << 21;
    private const int KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6, FocusIn = 9,
        FocusOut = 10, Expose = 12, ConfigureNotify = 22, ClientMessage = 33;

    [LibraryImport(Lib)] private static partial nint XOpenDisplay(nint name);
    [LibraryImport(Lib)] private static partial int XDefaultScreen(nint d);
    [LibraryImport(Lib)] private static partial nint XRootWindow(nint d, int screen);
    [LibraryImport(Lib)] private static partial nint XDefaultVisual(nint d, int screen);
    [LibraryImport(Lib)] private static partial int XDefaultDepth(nint d, int screen);
    [LibraryImport(Lib)] private static partial nint XDefaultGC(nint d, int screen);
    [LibraryImport(Lib)] private static partial nint XCreateSimpleWindow(nint d, nint parent, int x, int y, uint w, uint h, uint border, nuint borderColor, nuint background);
    [LibraryImport(Lib)] private static partial int XSelectInput(nint d, nint w, long mask);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int XStoreName(nint d, nint w, string name);
    [LibraryImport(Lib)] private static partial int XMapWindow(nint d, nint w);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial nint XInternAtom(nint d, string name, int onlyIfExists);
    [LibraryImport(Lib)] private static partial int XSetWMProtocols(nint d, nint w, nint* atoms, int count);
    [LibraryImport(Lib)] private static partial nint XCreateImage(nint d, nint visual, uint depth, int format, int offset, void* data, uint w, uint h, int pad, int bytesPerLine);
    [NoAlloc(Constraints.All, Reason = "an Xlib call with blittable arguments: nothing managed is allocated")]
    [LibraryImport(Lib)] private static partial int XPutImage(nint d, nint drawable, nint gc, nint image, int sx, int sy, int dx, int dy, uint w, uint h);
    [LibraryImport(Lib)] private static partial int XPending(nint d);
    [LibraryImport(Lib)] private static partial int XNextEvent(nint d, void* ev);
    [LibraryImport(Lib)] private static partial nuint XLookupKeysym(void* ev, int index);
    [LibraryImport(Lib)] private static partial int XWarpPointer(nint d, nint src, nint dest, int sx, int sy, uint sw, uint sh, int dx, int dy);
    [LibraryImport(Lib)] private static partial int XGrabPointer(nint d, nint w, int ownerEvents, uint mask, int pointerMode, int keyboardMode, nint confine, nint cursor, nuint time);
    [LibraryImport(Lib)] private static partial int XUngrabPointer(nint d, nuint time);
    [LibraryImport(Lib)] private static partial nint XCreateBitmapFromData(nint d, nint drawable, byte* data, uint w, uint h);
    [LibraryImport(Lib)] private static partial nint XCreatePixmapCursor(nint d, nint source, nint mask, void* fg, void* bg, uint x, uint y);
    [LibraryImport(Lib)] private static partial int XDefineCursor(nint d, nint w, nint cursor);
    [LibraryImport(Lib)] private static partial int XUndefineCursor(nint d, nint w);
    [NoAlloc(Constraints.All, Reason = "an Xlib call with blittable arguments: nothing managed is allocated")]
    [LibraryImport(Lib)] private static partial int XFlush(nint d);
    [LibraryImport(Lib)] private static partial int XSync(nint d, int discard);
    [LibraryImport(Lib)] private static partial int XDestroyWindow(nint d, nint w);
    [LibraryImport(Lib)] private static partial int XCloseDisplay(nint d);
    [LibraryImport(Lib)] private static partial int XkbSetDetectableAutoRepeat(nint d, int detectable, int* supported);

    private readonly nint _display, _window, _gc, _image, _wmDelete, _blank;
    private readonly uint* _pixels;                   // W x H, 0x00RRGGBB
    private readonly uint* _colors;                   // the palette as pixels
    private readonly byte* _event;                    // XEvent: 24 longs
    private readonly byte[] _frame = new byte[Screen.Width * Screen.Height];
    private Screen? _screen;
    private bool _focused, _closed, _mouseHeld;
    private int _mx, _my;                             // the motion since the last move

    public X11Window(string title)
    {
        _display = XOpenDisplay(0);
        if (_display == 0)
            throw new InvalidOperationException("XOpenDisplay failed (is DISPLAY set?)");
        int screen = XDefaultScreen(_display);
        if (XDefaultDepth(_display, screen) < 24)
            throw new InvalidOperationException("the display is not 24 or 32 bits deep");
        _window = XCreateSimpleWindow(_display, XRootWindow(_display, screen), 0, 0, W, H, 0, 0, 0);
        XSelectInput(_display, _window, KeyPressMask | KeyReleaseMask | ButtonPressMask | ButtonReleaseMask
            | PointerMotionMask | ExposureMask | StructureNotifyMask | FocusChangeMask);
        XStoreName(_display, _window, title);
        nint del = XInternAtom(_display, "WM_DELETE_WINDOW", 0);
        _wmDelete = del;
        XSetWMProtocols(_display, _window, &del, 1);
        int supported;
        XkbSetDetectableAutoRepeat(_display, 1, &supported);
        _gc = XDefaultGC(_display, screen);
        _pixels = (uint*)NativeMemory.AllocZeroed((nuint)(W * H), sizeof(uint));
        _colors = (uint*)NativeMemory.AllocZeroed(256, sizeof(uint));
        _event = (byte*)NativeMemory.AllocZeroed(24, 8);
        _image = XCreateImage(_display, XDefaultVisual(_display, screen), (uint)XDefaultDepth(_display, screen), 2, 0,
            _pixels, W, H, 32, 0);
        byte* zero = stackalloc byte[8];
        nint bitmap = XCreateBitmapFromData(_display, _window, zero, 8, 8);
        byte* black = stackalloc byte[16];     // XColor
        _blank = XCreatePixmapCursor(_display, bitmap, bitmap, black, black, 0, 0);
        XMapWindow(_display, _window);
        XFlush(_display);
    }

    public void Attach(Screen screen) => _screen = screen;

    public bool Active => _focused && _screen is { } s && s.KeyDest == Keys.KeyGame && !s.ForcedUp;

    private T Field<T>(int offset) where T : unmanaged => *(T*)(_event + offset);

    public bool Pump()
    {
        while (XPending(_display) != 0)
        {
            XNextEvent(_display, _event);
            switch (Field<int>(0))
            {
                case KeyPress:
                case KeyRelease:
                    if (XLateKey(XLookupKeysym(_event, 0)) is var k and not 0)
                        _screen?.KeyEvent(k, Field<int>(0) == KeyPress);
                    break;
                case ButtonPress:
                case ButtonRelease:
                {
                    bool down = Field<int>(0) == ButtonPress;
                    uint button = Field<uint>(84);
                    int key = button switch
                    {
                        1 => Screen.KMouse1, 2 => Screen.KMouse3, 3 => Screen.KMouse2,
                        4 => Screen.KMWheelUp, 5 => Screen.KMWheelDown, _ => 0,
                    };
                    if (key != 0 && (button < 4 || down))
                    {
                        _screen?.KeyEvent(key, down);
                        if (button >= 4)
                            _screen?.KeyEvent(key, false);
                    }
                    break;
                }
                case MotionNotify:
                    if (_mouseHeld)
                    {
                        int dx = Field<int>(64) - W / 2, dy = Field<int>(68) - H / 2;
                        if (dx != 0 || dy != 0)
                        {
                            _mx += dx;
                            _my += dy;
                            XWarpPointer(_display, 0, _window, 0, 0, 0, 0, W / 2, H / 2);
                        }
                    }
                    break;
                case FocusIn:
                    _focused = true;
                    break;
                case FocusOut:
                    _focused = false;
                    _screen?.ClearKeyStates();
                    break;
                case Expose:
                    Put();
                    break;
                case ClientMessage:
                    if (Field<nint>(56) == _wmDelete)
                        _closed = true;
                    break;
            }
        }
        HoldMouse(Active);
        return !_closed;
    }

    private void HoldMouse(bool hold)
    {
        if (hold == _mouseHeld)
            return;
        _mouseHeld = hold;
        if (hold)
        {
            XDefineCursor(_display, _window, _blank);
            XGrabPointer(_display, _window, 1, (uint)(PointerMotionMask | ButtonPressMask | ButtonReleaseMask), 1, 1,
                _window, 0, 0);
            XWarpPointer(_display, 0, _window, 0, 0, 0, 0, W / 2, H / 2);
        }
        else
        {
            XUngrabPointer(_display, 0);
            XUndefineCursor(_display, _window);
        }
        _mx = _my = 0;
        XFlush(_display);
    }

    [NoAlloc(Constraints.All)]
    public void TakeMotion(out int mx, out int my)
    {
        mx = _mx;
        my = _my;
        _mx = _my = 0;
    }

    [NoAlloc(Constraints.All, Reason = "Xlib calls with blittable arguments: nothing managed is allocated")]
    public void Present(ReadOnlySpan<byte> frameBuffer, ReadOnlySpan<byte> palette)
    {
        frameBuffer.CopyTo(_frame);
        for (int i = 0; i < 256; i++)
            _colors[i] = (uint)(palette[i * 3] << 16 | palette[i * 3 + 1] << 8 | palette[i * 3 + 2]);
        Put();
    }

    /// <summary>The frame scaled into the image and put into the window.</summary>
    [NoAlloc(Constraints.All, Reason = "Xlib calls with blittable arguments: nothing managed is allocated")]
    private void Put()
    {
        for (int y = 0; y < H; y++)
        {
            uint* row = _pixels + y * W;
            int src = y / Scale * Screen.Width;
            for (int x = 0; x < W; x++)
                row[x] = _colors[_frame[src + x / Scale]];
        }
        XPutImage(_display, _window, _gc, _image, 0, 0, 0, 0, W, H);
        XFlush(_display);
    }

    /// <summary>XLateKey (vid_x.c): a keysym to a Quake key, 0 for none.</summary>
    private static int XLateKey(nuint keysym)
    {
        switch (keysym)
        {
            case 0xff55: case 0xff9a: return Screen.KPgUp;          // Page_Up, KP_Page_Up
            case 0xff56: case 0xff9b: return Screen.KPgDn;
            case 0xff50: case 0xff95: return Screen.KHome;
            case 0xff57: case 0xff9c: return Screen.KEnd;
            case 0xff51: case 0xff96: return Screen.KLeftArrow;
            case 0xff53: case 0xff98: return Screen.KRightArrow;
            case 0xff54: case 0xff99: return Screen.KDownArrow;
            case 0xff52: case 0xff97: return Screen.KUpArrow;
            case 0xff1b: return Screen.KEscape;
            case 0xff0d: case 0xff8d: return Screen.KEnter;         // Return, KP_Enter
            case 0xff09: return Screen.KTab;
            case 0xff08: return Screen.KBackspace;
            case 0xffff: case 0xff9f: return Screen.KDel;
            case 0xff63: case 0xff9e: return Screen.KIns;
            case 0xff13: return Screen.KPause;
            case 0xffe1: case 0xffe2: return Screen.KShift;
            case 0xffe3: case 0xffe4: return Screen.KCtrl;
            case 0xffe9: case 0xffea: case 0xffe7: case 0xffe8: return Screen.KAlt;    // Alt and Meta
            case 0xffaa: return '*';
            case 0xffab: return '+';
            case 0xffad: return '-';
            case 0xffaf: return '/';
        }
        if (keysym >= 0xffbe && keysym <= 0xffc9)
            return Screen.KF1 + (int)(keysym - 0xffbe);    // F1..F12
        if (keysym >= 0x20 && keysym < 0x7f)
        {
            int c = (int)keysym;
            return c >= 'A' && c <= 'Z' ? c - 'A' + 'a' : c;
        }
        return 0;
    }

    public void Dispose()
    {
        HoldMouse(false);
        XDestroyWindow(_display, _window);
        XCloseDisplay(_display);
        NativeMemory.Free(_colors);
        NativeMemory.Free(_event);
    }
}
