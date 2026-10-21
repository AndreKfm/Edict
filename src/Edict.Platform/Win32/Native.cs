// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Platform.Win32;

/// <summary>The Win32 calls the window needs (user32, gdi32, kernel32, winmm).</summary>
internal static unsafe partial class Native
{
    /// <summary>Why the calls a frame makes (the present, the mouse's motion) keep its constraints.</summary>
    public const string FrameCall = "a Win32 call with blittable arguments and no callback: nothing managed is allocated, nothing waits";

    public const uint WmDestroy = 0x0002, WmActivate = 0x0006, WmPaint = 0x000F, WmClose = 0x0010, WmKeyDown = 0x0100,
        WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105, WmMouseMove = 0x0200, WmLButtonDown = 0x0201,
        WmLButtonUp = 0x0202, WmRButtonDown = 0x0204, WmRButtonUp = 0x0205, WmMButtonDown = 0x0207,
        WmMButtonUp = 0x0208, WmMouseWheel = 0x020A, WmSize = 0x0005;
    public const uint WsOverlappedWindow = 0x00CF0000, WsVisible = 0x10000000;
    public const int CwUseDefault = unchecked((int)0x80000000);
    public const uint PmRemove = 0x0001, CsOwnDc = 0x0020;
    public const int SwShow = 5, ColorOnColor = 3, DibRgbColors = 0;
    public const uint SrcCopy = 0x00CC0020;
    public const int IdcArrow = 32512;
    public const uint MbOk = 0, MbIconError = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public Point Pt;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo256
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
        public fixed uint Colors[256];                // RGBQUAD: blue, green, red, reserved
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(WndClassEx* wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    public static partial int PeekMessage(Msg* msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(Msg* msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(Msg* msg);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hwnd);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hwnd, PaintStruct* ps);

    [LibraryImport("user32.dll")]
    public static partial int EndPaint(nint hwnd, PaintStruct* ps);

    [LibraryImport("user32.dll")]
    public static partial int AdjustWindowRect(Rect* rect, uint style, int menu);

    [LibraryImport("user32.dll")]
    public static partial int ShowWindow(nint hwnd, int cmd);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("user32.dll")]
    public static partial int GetClientRect(nint hwnd, Rect* rect);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("user32.dll")]
    public static partial int ClientToScreen(nint hwnd, Point* p);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("user32.dll")]
    public static partial int GetCursorPos(Point* p);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("user32.dll")]
    public static partial int SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    public static partial int ShowCursor(int show);

    [LibraryImport("user32.dll")]
    public static partial int ClipCursor(Rect* rect);

    [LibraryImport("user32.dll")]
    public static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint hwnd, string text, string caption, uint type);

    [LibraryImport("user32.dll")]
    public static partial int DestroyWindow(nint hwnd);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("gdi32.dll")]
    public static partial int SetStretchBltMode(nint hdc, int mode);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("gdi32.dll")]
    public static partial int StretchDIBits(nint hdc, int xDest, int yDest, int destWidth, int destHeight, int xSrc,
        int ySrc, int srcWidth, int srcHeight, void* bits, BitmapInfo256* info, uint usage, uint rop);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandle(nint name);

    [LibraryImport("winmm.dll")]
    public static partial uint timeBeginPeriod(uint period);

    // ---- WGL: a pixel format with depth, a legacy context to find wglCreateContextAttribsARB, then 3.3 core
    [StructLayout(LayoutKind.Sequential)]
    public struct PixelFormatDescriptor
    {
        public ushort Size, Version;
        public uint Flags;
        public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift, AlphaBits,
            AlphaShift, AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits, DepthBits, StencilBits,
            AuxBuffers, LayerType, Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }

    public const uint PfdDoubleBuffer = 1, PfdDrawToWindow = 4, PfdSupportOpenGl = 0x20;

    [LibraryImport("gdi32.dll")]
    public static partial int ChoosePixelFormat(nint hdc, PixelFormatDescriptor* pfd);

    [LibraryImport("gdi32.dll")]
    public static partial int SetPixelFormat(nint hdc, int format, PixelFormatDescriptor* pfd);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("gdi32.dll")]
    public static partial int SwapBuffers(nint hdc);

    [LibraryImport("opengl32.dll")]
    public static partial nint wglCreateContext(nint hdc);

    [LibraryImport("opengl32.dll")]
    public static partial int wglMakeCurrent(nint hdc, nint context);

    [LibraryImport("opengl32.dll")]
    public static partial int wglDeleteContext(nint context);

    [LibraryImport("opengl32.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint wglGetProcAddress(string name);
}
