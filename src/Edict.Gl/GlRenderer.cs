// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Engine;

namespace Edict.Gl;

/// <summary>
/// The GPU renderer (P6, step 7), GLQuake's look: the world's surfaces with filtered textures and lightmaps
/// multiplied in, the sky's two scrolling layers, the water's turbulence, brush entities, alias
/// models lit from the floor below them, sprites, particles, the gun. The 2D layer is the exact software layer of
/// the screen, put over the view: with <see cref="Screen.GpuView"/> the screen clears the view's rectangle to 0
/// before drawing the crosshair, the center print and the console over it, and those zero pixels are where the 3D
/// view shows through. The palette's color shifts (damage, powerups, contents) tint the 3D view as they tint the
/// 2D layer. It reads the screen's and the client's state and writes neither.
/// </summary>
[NoAllocUnchecked("the GPU renderer: outside the proof; its frame keeps to preallocated buffers")]
public sealed unsafe partial class GlRenderer : IDisposable
{
    private readonly Screen _screen;
    private readonly uint _frameTex, _paletteTex, _compositeProg, _quadVao, _quadVbo;
    private readonly int _uFrame, _uPalette, _uVrect, _uViewDrawn;
    private readonly byte* _paletteRgba;
    private float _tintK0 = 1, _tintK1 = 1, _tintK2 = 1, _tintM0, _tintM1, _tintM2;
    private double _time;

    /// <summary>The renderer for the screen's view, in the current GL context (<see cref="Gl.Load"/> done).</summary>
    public GlRenderer(Screen screen)
    {
        _screen = screen;
        uint t;
        Gl.GenTextures(1, &t);
        _frameTex = t;
        Gl.BindTexture(Gl.Texture2D, t);
        SetFilter(Gl.Nearest, Gl.ClampToEdge);
        Gl.PixelStorei(Gl.UnpackAlignment, 1);
        Gl.TexImage2D(Gl.Texture2D, 0, (int)Gl.R8, Screen.Width, Screen.Height, 0, Gl.Red, Gl.UnsignedByte, null);
        Gl.GenTextures(1, &t);
        _paletteTex = t;
        Gl.BindTexture(Gl.Texture2D, t);
        SetFilter(Gl.Nearest, Gl.ClampToEdge);
        Gl.TexImage2D(Gl.Texture2D, 0, (int)Gl.Rgba8, 256, 1, 0, Gl.Rgba, Gl.UnsignedByte, null);
        _paletteRgba = (byte*)NativeMemory.AllocZeroed(1024);

        _compositeProg = Gl.Program(Shaders.CompositeVs, Shaders.CompositeFs, "aPos");
        _uFrame = Gl.Uniform(_compositeProg, "uFrame");
        _uPalette = Gl.Uniform(_compositeProg, "uPalette");
        _uVrect = Gl.Uniform(_compositeProg, "uVrect");
        _uViewDrawn = Gl.Uniform(_compositeProg, "uViewDrawn");
        float* quad = stackalloc float[] { -1, -1, 1, -1, -1, 1, 1, 1 };
        uint vao, vbo;
        Gl.GenVertexArrays(1, &vao);
        Gl.GenBuffers(1, &vbo);
        _quadVao = vao;
        _quadVbo = vbo;
        Gl.BindVertexArray(vao);
        Gl.BindBuffer(Gl.ArrayBuffer, vbo);
        Gl.BufferData(Gl.ArrayBuffer, 8 * sizeof(float), quad, Gl.StaticDraw);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(0, 2, Gl.Float, 0, 8, null);
        Gl.BindVertexArray(0);
        InitWorld();
        InitModels();
    }

    internal static void SetFilter(int filter, int wrap)
    {
        Gl.TexParameteri(Gl.Texture2D, Gl.TextureMinFilter, filter);
        Gl.TexParameteri(Gl.Texture2D, Gl.TextureMagFilter, filter == Gl.LinearMipmapLinear ? Gl.Linear : filter);
        Gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapS, wrap);
        Gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapT, wrap);
    }

    /// <summary>
    /// The palette's color shifts as a per-channel linear map from the base palette (fitted over the 256 colors):
    /// V_UpdatePalette's blends toward each shift's color, which the 3D view gets as a tint.
    /// </summary>
    private void FitTint(ReadOnlySpan<byte> palette)
    {
        ReadOnlySpan<byte> b = _screen.BasePalette;
        for (int c = 0; c < 3; c++)
        {
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < 256; i++)
            {
                double x = b[i * 3 + c] / 255.0, y = palette[i * 3 + c] / 255.0;
                sx += x;
                sy += y;
                sxx += x * x;
                sxy += x * y;
            }
            double den = 256 * sxx - sx * sx;
            double k = den != 0 ? (256 * sxy - sx * sy) / den : 1;
            double m = (sy - k * sx) / 256;
            (c == 0 ? ref _tintK0 : ref c == 1 ? ref _tintK1 : ref _tintK2) = (float)k;
            (c == 0 ? ref _tintM0 : ref c == 1 ? ref _tintM1 : ref _tintM2) = (float)m;
        }
    }

    /// <summary>A present: the 3D view if the update drew one, then the 2D layer over it, into a window of the size.</summary>
    public void DrawFrame(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> palette, int width, int height)
    {
        _time = _screen.Client.Time;
        Gl.Viewport(0, 0, width, height);
        Gl.ClearColor(0, 0, 0, 1);
        Gl.DepthMask(1);
        Gl.Clear(Gl.ColorBufferBit | Gl.DepthBufferBit);
        bool view = _screen.ViewDrawn && _screen.Client.WorldModel != null;
        if (view)
        {
            FitTint(palette);
            Screen.VRect r = _screen.RefdefVrect;
            float sx = width / (float)Screen.Width, sy = height / (float)Screen.Height;
            int vx = (int)(r.X * sx), vy = (int)(height - (r.Y + r.Height) * sy), vw = (int)(r.Width * sx), vh = (int)(r.Height * sy);
            Gl.Viewport(vx, vy, vw, vh);
            DrawView(vw, vh);
            Gl.Viewport(0, 0, width, height);
        }
        Composite(frame, palette, view);
    }

    private void Composite(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> palette, bool view)
    {
        for (int i = 0; i < 256; i++)
        {
            _paletteRgba[i * 4] = palette[i * 3];
            _paletteRgba[i * 4 + 1] = palette[i * 3 + 1];
            _paletteRgba[i * 4 + 2] = palette[i * 3 + 2];
            _paletteRgba[i * 4 + 3] = 255;
        }
        Gl.Disable(Gl.DepthTest);
        Gl.Disable(Gl.CullFaceCap);
        Gl.Disable(Gl.Blend);
        Gl.ActiveTexture(Gl.Texture0);
        Gl.BindTexture(Gl.Texture2D, _frameTex);
        fixed (byte* f = frame)
            Gl.TexSubImage2D(Gl.Texture2D, 0, 0, 0, Screen.Width, Screen.Height, Gl.Red, Gl.UnsignedByte, f);
        Gl.ActiveTexture(Gl.Texture0 + 1);
        Gl.BindTexture(Gl.Texture2D, _paletteTex);
        Gl.TexSubImage2D(Gl.Texture2D, 0, 0, 0, 256, 1, Gl.Rgba, Gl.UnsignedByte, _paletteRgba);
        Gl.UseProgram(_compositeProg);
        Gl.Uniform1i(_uFrame, 0);
        Gl.Uniform1i(_uPalette, 1);
        Screen.VRect r = _screen.RefdefVrect;
        Gl.Uniform4f(_uVrect, r.X, r.Y, r.X + r.Width, r.Y + r.Height);
        Gl.Uniform1i(_uViewDrawn, view ? 1 : 0);
        Gl.BindVertexArray(_quadVao);
        Gl.DrawArrays(Gl.TriangleStrip, 0, 4);
        Gl.BindVertexArray(0);
        Gl.ActiveTexture(Gl.Texture0);
    }

    /// <summary>The window's pixels after a <see cref="DrawFrame"/>, bottom row first, RGB.</summary>
    public static byte[] ReadBack(int width, int height)
    {
        byte[] rgb = new byte[width * height * 3];
        Gl.PixelStorei(Gl.PackAlignment, 1);
        fixed (byte* p = rgb)
            Gl.ReadPixels(0, 0, width, height, Gl.Rgb, Gl.UnsignedByte, p);
        return rgb;
    }

    /// <summary>A 24-bit BMP of what <see cref="ReadBack"/> read.</summary>
    public static void WriteBmp(string path, byte[] rgb, int width, int height)
    {
        int stride = (width * 3 + 3) & ~3;
        using var w = new BinaryWriter(File.Create(path));
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + stride * height); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24);
        w.Write(0); w.Write(stride * height); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        byte[] row = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = rgb[(y * width + x) * 3 + 2];
                row[x * 3 + 1] = rgb[(y * width + x) * 3 + 1];
                row[x * 3 + 2] = rgb[(y * width + x) * 3];
            }
            w.Write(row);
        }
    }

    public void Dispose()
    {
        NativeMemory.Free(_paletteRgba);
        DisposeWorld();
    }
}
