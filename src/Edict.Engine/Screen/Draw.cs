// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>A qpic_t: its size and its pixels (palette indices, row by row).</summary>
[NoAlloc(Constraints.All)]
public readonly unsafe struct QPic(int width, int height, byte* data)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte* Data { get; } = data;
}

/// <summary>draw.c (the 8-bit paths: r_pixbytes is 1) and wad.c: the 2D primitives over the frame buffer and the pictures they draw.</summary>
public sealed unsafe partial class Screen
{
    private const int TransparentColor = 255;
    private const int MaxCachedPics = 128;

    private static readonly SysErrorException BadPicCoordinates = new("Draw_Pic: bad coordinates");
    private static readonly SysErrorException BadTransPicCoordinates = new("Draw_TransPic: bad coordinates");

    // ---- wad.c: gfx.wad, loaded once (W_LoadWadFile)
    private byte[] _wad = null!;                      // wad_base, pinned
    private byte* _wadBase;
    private int _numLumps;
    private int _infoTableOfs;

    private QPic _ram, _net, _turtle;                 // scr_ram, scr_net, scr_turtle
    private byte* _chars;                             // draw_chars: 8*8 graphic characters, 128 x 128
    private QPic _backtile;                           // draw_backtile (r_rectdesc)

    // Draw_CachePic: menu_cachepics by name, each a copy of its file (COM_LoadCacheFile), as the console's picture is
    // written to. C loads a picture at its first draw; the port loads every one outside the menu at Draw_Init, which
    // gives the same pixels and keeps the frame free of loads.
    private byte[][] _cachePicNames = null!;
    private byte[][] _cachePicData = null!;
    private int _numCachePics;

    private static readonly string[] CachedPics =
        ["gfx/conback.lmp", "gfx/pause.lmp", "gfx/loading.lmp", "gfx/complete.lmp", "gfx/inter.lmp", "gfx/finale.lmp",
         "gfx/ranking.lmp"];

    private static readonly SysErrorException CachePicMissing = new("Draw_CachePic: failed to load the picture");

    [NoAllocUnchecked("load path: W_LoadWadFile, Draw_Init")]
    private void InitDraw(QFileSystem files)
    {
        QFile f = files.Find("gfx.wad"u8) ?? throw new FileNotFoundException("W_LoadWadFile: couldn't load gfx.wad");
        _wad = GC.AllocateArray<byte>(f.Length, pinned: true);
        f.Span.CopyTo(_wad);
        _wadBase = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_wad, 0);
        if (_wad[0] != 'W' || _wad[1] != 'A' || _wad[2] != 'D' || _wad[3] != '2')
            throw new InvalidDataException("Wad file gfx.wad doesn't have WAD2 id");
        _numLumps = BinaryPrimitives.ReadInt32LittleEndian(_wad.AsSpan(4));
        _infoTableOfs = BinaryPrimitives.ReadInt32LittleEndian(_wad.AsSpan(8));
        // W_CleanupName on every lump's name: lower case, zero-padded
        for (int i = 0; i < _numLumps; i++)
        {
            Span<byte> name = _wad.AsSpan(_infoTableOfs + i * 32 + 16, 16);
            int k = 0;
            for (; k < 16 && name[k] != 0; k++)
                if (name[k] >= 'A' && name[k] <= 'Z')
                    name[k] += 'a' - 'A';
            name[k..].Clear();
        }
        _chars = _wadBase + LumpPos("conchars"u8);
        // draw_disc: the disc icon goes through D_BeginDirectRect, which the reference's video driver leaves empty
        _backtile = PicFromWad("backtile"u8);

        _cachePicNames = new byte[MaxCachedPics][];
        _cachePicData = new byte[MaxCachedPics][];
        foreach (string pic in CachedPics)
        {
            byte[] name = System.Text.Encoding.Latin1.GetBytes(pic);
            if (files.Find(name) is not { } file)
                continue;       // drawing it stops, as Draw_CachePic does
            _cachePicNames[_numCachePics] = name;
            _cachePicData[_numCachePics] = GC.AllocateArray<byte>(file.Length, pinned: true);
            file.Span.CopyTo(_cachePicData[_numCachePics]);
            _numCachePics++;
        }
    }

    /// <summary>W_GetLumpinfo's filepos (lumpinfo_t: filepos, disksize, size, type, compression, pad, name[16]).</summary>
    [NoAllocUnchecked("load path")]
    private int LumpPos(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < _numLumps; i++)
        {
            ReadOnlySpan<byte> n = _wad.AsSpan(_infoTableOfs + i * 32 + 16, 16);
            int len = n.IndexOf((byte)0);
            if ((len < 0 ? n : n[..len]).SequenceEqual(name))
                return BinaryPrimitives.ReadInt32LittleEndian(_wad.AsSpan(_infoTableOfs + i * 32));
        }
        throw new InvalidDataException("W_GetLumpinfo: " + System.Text.Encoding.Latin1.GetString(name) + " not found");
    }

    /// <summary>Draw_PicFromWad.</summary>
    [NoAllocUnchecked("load path")]
    private QPic PicFromWad(ReadOnlySpan<byte> name)
    {
        byte* p = _wadBase + LumpPos(name);
        return new QPic(*(int*)p, *(int*)(p + 4), p + 8);
    }

    /// <summary>Draw_CachePic: a picture of the file system (loaded at Draw_Init).</summary>
    public QPic CachePic(ReadOnlySpan<byte> path)
    {
        for (int i = 0; i < _numCachePics; i++)
        {
            byte[] name = _cachePicNames[i];
            if (name.Length != path.Length)
                continue;
            int k = 0;
            while (k < name.Length && name[k] == path[k])
                k++;
            if (k < name.Length)
                continue;
            fixed (byte* d = _cachePicData[i])       // pinned: the pointer stays valid
                return new QPic(*(int*)d, *(int*)(d + 4), d + 8);
        }
        throw CachePicMissing;
    }

    /// <summary>
    /// Draw_Character: draws one 8*8 graphics character with 0 being transparent. It can be clipped to the top of the
    /// screen to allow the console to be smoothly scrolled off.
    /// </summary>
    public void Character(int x, int y, int num)
    {
        num &= 255;
        if (y <= -8)
            return;     // totally off screen
        int row = num >> 4, col = num & 15;
        byte* source = _chars + (row << 10) + (col << 3);
        int drawline;
        if (y < 0)
        {
            // clipped
            drawline = 8 + y;
            source -= 128 * y;
            y = 0;
        }
        else
            drawline = 8;
        byte* dest = _buffer + y * Width + x;
        CheckRows(y, drawline);
        while (drawline-- != 0)
        {
            for (int i = 0; i < 8; i++)
                if (source[i] != 0)
                    dest[i] = source[i];
            source += 128;
            dest += Width;
        }
    }

    private static readonly SysErrorException OutsideFrameBuffer =
        new("a 2D primitive would write outside the frame buffer (C writes past vid_buffer)");

    // C has no bounds there (PARANOID is off) and would write past vid_buffer; the port stops instead
    private static void CheckRows(int y, int rows)
    {
        if (y < 0 || y + rows > Height)
            throw OutsideFrameBuffer;
    }

    /// <summary>Draw_String.</summary>
    public void String(int x, int y, ReadOnlySpan<byte> str)
    {
        foreach (byte c in str)
        {
            if (c == 0)
                break;
            Character(x, y, c);
            x += 8;
        }
    }

    /// <summary>Draw_Pic.</summary>
    public void Pic(int x, int y, QPic pic)
    {
        if (x < 0 || x + pic.Width > Width || y < 0 || y + pic.Height > Height)
            throw BadPicCoordinates;
        byte* source = pic.Data;
        byte* dest = _buffer + y * Width + x;
        for (int v = 0; v < pic.Height; v++)
        {
            new ReadOnlySpan<byte>(source, pic.Width).CopyTo(new Span<byte>(dest, pic.Width));
            dest += Width;
            source += pic.Width;
        }
    }

    /// <summary>Draw_TransPic: palette index 255 is transparent.</summary>
    public void TransPic(int x, int y, QPic pic)
    {
        if (x < 0 || (uint)(x + pic.Width) > Width || y < 0 || (uint)(y + pic.Height) > Height)
            throw BadTransPicCoordinates;
        byte* source = pic.Data;
        byte* dest = _buffer + y * Width + x;
        for (int v = 0; v < pic.Height; v++)
        {
            for (int u = 0; u < pic.Width; u++)
            {
                byte t = source[u];
                if (t != TransparentColor)
                    dest[u] = t;
            }
            dest += Width;
            source += pic.Width;
        }
    }

    /// <summary>Draw_CharToConback.</summary>
    private void CharToConback(int num, byte* dest)
    {
        int row = num >> 4, col = num & 15;
        byte* source = _chars + (row << 10) + (col << 3);
        for (int drawline = 8; drawline-- != 0;)
        {
            for (int x = 0; x < 8; x++)
                if (source[x] != 0)
                    dest[x] = (byte)(0x60 + source[x]);
            source += 128;
            dest += 320;
        }
    }

    /// <summary>Draw_ConsoleBackground: the console's picture with the version written into it (WinQuake's, patch 0008).</summary>
    private void ConsoleBackground(int lines)
    {
        QPic conback = CachePic("gfx/conback.lmp"u8);
        // hack the version number directly into the pic
        ReadOnlySpan<byte> ver = "(WinQuake) 1.09"u8;      // sprintf (ver, "(WinQuake) %4.2f", (float)VERSION)
        byte* dest = conback.Data + 320 * 186 + 320 - 11 - 8 * ver.Length;
        for (int x = 0; x < ver.Length; x++)
            CharToConback(ver[x], dest + (x << 3));
        // draw the pic
        dest = _buffer;
        for (int y = 0; y < lines; y++, dest += Width)
        {
            int v = (Height - lines + y) * 200 / Height;
            new ReadOnlySpan<byte>(conback.Data + v * 320, Width).CopyTo(new Span<byte>(dest, Width));
        }
    }

    /// <summary>R_DrawRect8 without transparency (the only use: Draw_TileClear).</summary>
    private void DrawRect8(VRect r, int rowbytes, byte* psrc)
    {
        byte* pdest = _buffer + r.Y * Width + r.X;
        for (int i = 0; i < r.Height; i++)
        {
            new ReadOnlySpan<byte>(psrc, r.Width).CopyTo(new Span<byte>(pdest, r.Width));
            psrc += rowbytes;
            pdest += Width;
        }
    }

    /// <summary>Draw_TileClear: fills a box of pixels with the back tile, for the areas the view does not cover.</summary>
    public void TileClear(int x, int y, int w, int h)
    {
        int tileW = _backtile.Width, tileH = _backtile.Height;
        VRect vr = default;
        vr.Y = y;
        int height = h;
        int tileoffsety = vr.Y % tileH;
        while (height > 0)
        {
            vr.X = x;
            int width = w;
            vr.Height = tileoffsety != 0 ? tileH - tileoffsety : tileH;
            if (vr.Height > height)
                vr.Height = height;
            int tileoffsetx = vr.X % tileW;
            while (width > 0)
            {
                vr.Width = tileoffsetx != 0 ? tileW - tileoffsetx : tileW;
                if (vr.Width > width)
                    vr.Width = width;
                byte* psrc = _backtile.Data + tileoffsety * tileW + tileoffsetx;
                DrawRect8(vr, tileW, psrc);
                vr.X += vr.Width;
                width -= vr.Width;
                tileoffsetx = 0;    // only the left tile can be left-clipped
            }
            vr.Y += vr.Height;
            height -= vr.Height;
            tileoffsety = 0;        // only the top tile can be top-clipped
        }
    }

    /// <summary>Draw_Fill: fills a box of pixels with a single color.</summary>
    public void Fill(int x, int y, int w, int h, int c)
    {
        byte* dest = _buffer + y * Width + x;
        for (int v = 0; v < h; v++, dest += Width)
            new Span<byte>(dest, w).Fill((byte)c);
    }
}
