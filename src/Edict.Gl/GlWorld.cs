// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Engine;

namespace Edict.Gl;

/// <summary>
/// The brush models' surfaces (gl_rsurf.c, gl_warp.c, gl_rmain.c): the polygons of a model's faces in one vertex
/// buffer (GLQuake's BuildSurfaceDisplayList), their lightmaps in atlas pages rebuilt when a style's value or a
/// dynamic light changes (R_BuildLightMap: the styles summed, the lights added; 128 the texture's own brightness,
/// up to twice it, as the software renderer's colormap), the
/// textures with mipmaps through the palette, the visible faces of the PVS and the frustum each frame, by texture.
/// </summary>
public sealed unsafe partial class GlRenderer
{
    private const int LmPage = 1024, MaxIndices = 1 << 21, VertexFloats = 7;
    private const int MaxLmSize = 18 * 18;

    /// <summary>A brush model set's GPU side: its faces' vertex ranges, lightmap places and last lighting.</summary>
    private sealed class BrushData
    {
        public uint Vao, Vbo;
        public MSurface* Surfaces;
        public int Count;
        public int[] First = null!, Verts = null!;    // per surface: first vertex, vertex count
        public int[] LmPage = null!, LmX = null!, LmY = null!;
        public long[] LmKey = null!;                  // the styles' values (and dlights) the lightmap was built for
        public int[] Mark = null!;                    // the frame the surface was last found visible
        public int[] Drawn = null!;                   // the frame the surface was last drawn (the world's)
    }

    private readonly Dictionary<nint, BrushData> _brush = [];
    private readonly Dictionary<nint, uint> _textures = [];
    private readonly Dictionary<nint, (uint Solid, uint Alpha)> _skies = [];
    private readonly List<uint> _lmPages = [];
    private int _lmX, _lmY, _lmRowH;                  // the shelf packer of the last page
    private uint _surfProg, _ebo;
    private int _uMvp, _uModel, _uTex, _uLm, _uSkyAlpha, _uTexSize, _uMode, _uTime, _uEye, _uTintK, _uTintM, _uAlpha;
    private uint* _indices;
    private long* _batchKeys;                         // texture << 40 | page << 32 | surface, per visible surface
    private int _nBatch;
    private uint* _blocklights;
    private byte* _lmBytes;
    private int _visMark;
    private Mat4 _proj, _viewMatrix;

    private void InitWorld()
    {
        _surfProg = Gl.Program(Shaders.SurfaceVs, Shaders.SurfaceFs, "aPos", "aTex", "aLm");
        _uMvp = Gl.Uniform(_surfProg, "uMvp");
        _uModel = Gl.Uniform(_surfProg, "uModel");
        _uTex = Gl.Uniform(_surfProg, "uTex");
        _uLm = Gl.Uniform(_surfProg, "uLm");
        _uSkyAlpha = Gl.Uniform(_surfProg, "uSkyAlpha");
        _uTexSize = Gl.Uniform(_surfProg, "uTexSize");
        _uMode = Gl.Uniform(_surfProg, "uMode");
        _uTime = Gl.Uniform(_surfProg, "uTime");
        _uEye = Gl.Uniform(_surfProg, "uEye");
        _uTintK = Gl.Uniform(_surfProg, "uTintK");
        _uTintM = Gl.Uniform(_surfProg, "uTintM");
        _uAlpha = Gl.Uniform(_surfProg, "uAlpha");
        uint b;
        Gl.GenBuffers(1, &b);
        _ebo = b;
        _indices = (uint*)NativeMemory.Alloc(MaxIndices, sizeof(uint));
        _batchKeys = (long*)NativeMemory.Alloc(1 << 18, sizeof(long));
        _blocklights = (uint*)NativeMemory.Alloc(MaxLmSize, sizeof(uint));
        _lmBytes = (byte*)NativeMemory.Alloc(MaxLmSize);
    }

    private void DisposeWorld()
    {
        NativeMemory.Free(_indices);
        NativeMemory.Free(_batchKeys);
        NativeMemory.Free(_blocklights);
        NativeMemory.Free(_lmBytes);
    }

    /// <summary>The texture's GL texture: its four mips' first through the palette, GL's mipmaps, filtered (GL_Upload8).</summary>
    private uint TextureOf(Texture* t)
    {
        if (_textures.TryGetValue((nint)t, out uint id))
            return id;
        int w = (int)t->Width, h = (int)t->Height;
        byte[] rgba = new byte[w * h * 4];
        ReadOnlySpan<byte> pal = _screen.BasePalette;
        byte* src = t->Pixels + t->MipOffsets[0];
        for (int i = 0; i < w * h; i++)
        {
            int c = src[i];
            rgba[i * 4] = pal[c * 3];
            rgba[i * 4 + 1] = pal[c * 3 + 1];
            rgba[i * 4 + 2] = pal[c * 3 + 2];
            rgba[i * 4 + 3] = 255;
        }
        id = Upload(rgba, w, h, mip: true, Gl.Repeat);
        _textures[(nint)t] = id;
        return id;
    }

    internal static uint Upload(byte[] rgba, int w, int h, bool mip, int wrap)
    {
        uint id;
        Gl.GenTextures(1, &id);
        Gl.BindTexture(Gl.Texture2D, id);
        Gl.PixelStorei(Gl.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            Gl.TexImage2D(Gl.Texture2D, 0, (int)Gl.Rgba8, w, h, 0, Gl.Rgba, Gl.UnsignedByte, p);
        if (mip)
            Gl.GenerateMipmap(Gl.Texture2D);
        SetFilter(mip ? Gl.LinearMipmapLinear : Gl.Linear, wrap);
        return id;
    }

    /// <summary>R_InitSky (gl_warp.c): the right half the solid layer, the left half the layer over it (0 transparent).</summary>
    private (uint Solid, uint Alpha) SkyOf(Texture* t)
    {
        if (_skies.TryGetValue((nint)t, out var s))
            return s;
        ReadOnlySpan<byte> pal = _screen.BasePalette;
        byte* src = t->Pixels + t->MipOffsets[0];
        byte[] solid = new byte[128 * 128 * 4], alpha = new byte[128 * 128 * 4];
        for (int i = 0; i < 128; i++)
            for (int j = 0; j < 128; j++)
            {
                int k = (i * 128 + j) * 4;
                int c = src[i * 256 + j + 128];
                solid[k] = pal[c * 3];
                solid[k + 1] = pal[c * 3 + 1];
                solid[k + 2] = pal[c * 3 + 2];
                solid[k + 3] = 255;
                c = src[i * 256 + j];
                alpha[k] = pal[c * 3];
                alpha[k + 1] = pal[c * 3 + 1];
                alpha[k + 2] = pal[c * 3 + 2];
                alpha[k + 3] = (byte)(c == 0 ? 0 : 255);
            }
        s = (Upload(solid, 128, 128, false, Gl.Repeat), Upload(alpha, 128, 128, false, Gl.Repeat));
        _skies[(nint)t] = s;
        return s;
    }

    /// <summary>A place for a lightmap of w x h in the atlas pages (a shelf packer).</summary>
    private (int Page, int X, int Y) AllocLightmap(int w, int h)
    {
        if (_lmPages.Count == 0 || _lmX + w > LmPage)
        {
            _lmX = 0;
            _lmY += _lmRowH;
            _lmRowH = 0;
        }
        if (_lmPages.Count == 0 || _lmY + h > LmPage)
        {
            uint id;
            Gl.GenTextures(1, &id);
            Gl.BindTexture(Gl.Texture2D, id);
            byte[] zero = new byte[LmPage * LmPage];
            Gl.PixelStorei(Gl.UnpackAlignment, 1);
            fixed (byte* z = zero)
                Gl.TexImage2D(Gl.Texture2D, 0, (int)Gl.R8, LmPage, LmPage, 0, Gl.Red, Gl.UnsignedByte, z);
            SetFilter(Gl.Linear, Gl.ClampToEdge);
            _lmPages.Add(id);
            _lmX = _lmY = _lmRowH = 0;
        }
        (int, int, int) r = (_lmPages.Count - 1, _lmX, _lmY);
        _lmX += w + 1;
        _lmRowH = Math.Max(_lmRowH, h + 1);
        return r;
    }

    /// <summary>BuildSurfaceDisplayList for every face of the model's surface array, the lightmaps placed.</summary>
    private BrushData BrushOf(Model* m)
    {
        if (_brush.TryGetValue((nint)m->Surfaces, out BrushData? b))
            return b;
        b = new BrushData { Surfaces = m->Surfaces, Count = m->NumSurfaces };
        int n = m->NumSurfaces;
        b.First = new int[n];
        b.Verts = new int[n];
        b.LmPage = new int[n];
        b.LmX = new int[n];
        b.LmY = new int[n];
        b.LmKey = new long[n];
        b.Mark = new int[n];
        b.Drawn = new int[n];
        var verts = new List<float>();
        for (int i = 0; i < n; i++)
        {
            MSurface* s = m->Surfaces + i;
            int smax = (s->Extents[0] >> 4) + 1, tmax = (s->Extents[1] >> 4) + 1;
            bool lit = (s->Flags & (SurfFlags.DrawSky | SurfFlags.DrawTurb)) == 0;
            if (lit)
                (b.LmPage[i], b.LmX[i], b.LmY[i]) = AllocLightmap(smax, tmax);
            b.LmKey[i] = -1;
            b.First[i] = verts.Count / VertexFloats;
            b.Verts[i] = s->NumEdges;
            MTexInfo* ti = s->TexInfo;
            for (int e = 0; e < s->NumEdges; e++)
            {
                int lindex = m->Surfedges[s->FirstEdge + e];
                Vec3 v = lindex > 0 ? m->Vertexes[m->Edges[lindex].V[0]] : m->Vertexes[m->Edges[-lindex].V[1]];
                float ss = v.X * ti->Vecs[0] + v.Y * ti->Vecs[1] + v.Z * ti->Vecs[2] + ti->Vecs[3];
                float tt = v.X * ti->Vecs[4] + v.Y * ti->Vecs[5] + v.Z * ti->Vecs[6] + ti->Vecs[7];
                // lightmap texture coordinates: the sample's centre, in the page
                float ls = (ss - s->TextureMins[0] + b.LmX[i] * 16 + 8) / (LmPage * 16f);
                float lt = (tt - s->TextureMins[1] + b.LmY[i] * 16 + 8) / (LmPage * 16f);
                verts.Add(v.X);
                verts.Add(v.Y);
                verts.Add(v.Z);
                verts.Add(ss);
                verts.Add(tt);
                verts.Add(ls);
                verts.Add(lt);
            }
        }
        uint vao, vbo;
        Gl.GenVertexArrays(1, &vao);
        Gl.GenBuffers(1, &vbo);
        b.Vao = vao;
        b.Vbo = vbo;
        Gl.BindVertexArray(vao);
        Gl.BindBuffer(Gl.ArrayBuffer, vbo);
        float[] data = verts.ToArray();
        fixed (float* d = data)
            Gl.BufferData(Gl.ArrayBuffer, data.Length * sizeof(float), d, Gl.StaticDraw);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(0, 3, Gl.Float, 0, VertexFloats * 4, null);
        Gl.EnableVertexAttribArray(1);
        Gl.VertexAttribPointer(1, 2, Gl.Float, 0, VertexFloats * 4, (void*)12);
        Gl.EnableVertexAttribArray(2);
        Gl.VertexAttribPointer(2, 2, Gl.Float, 0, VertexFloats * 4, (void*)20);
        Gl.BindBuffer(Gl.ElementArrayBuffer, _ebo);
        Gl.BindVertexArray(0);
        _brush[(nint)m->Surfaces] = b;
        return b;
    }

    /// <summary>R_TextureAnimation: the texture of the base's sequence for the time and the entity's frame.</summary>
    private Texture* Animated(Texture* t, int frame)
    {
        if (frame != 0 && t->AlternateAnims != null)
            t = t->AlternateAnims;
        if (t->AnimTotal == 0)
            return t;
        int reletive = (int)(_time * 10) % t->AnimTotal;
        for (int count = 0; t->AnimMin > reletive || t->AnimMax <= reletive; count++)
        {
            t = t->AnimNext;
            if (t == null || count > 100)
                return t == null ? null : t;
        }
        return t;
    }

    /// <summary>R_BuildLightMap: the styles summed, the dynamic lights that reach the face, 2 x clamped, uploaded.</summary>
    private void UpdateLightmap(BrushData b, int i, Vec3 entityOrigin)
    {
        MSurface* s = b.Surfaces + i;
        ReadOnlySpan<int> styles = _screen.LightStyleValues;
        long key = 0;
        for (int m = 0; m < 4 && s->Styles[m] != 255; m++)
            key = key * 1031 + styles[s->Styles[m]];
        int smax = (s->Extents[0] >> 4) + 1, tmax = (s->Extents[1] >> 4) + 1, size = smax * tmax;
        int dlit = DynamicLightBits(s, entityOrigin);
        if (dlit != 0)
            key = key * 7919 + dlit + (long)(_time * 1000) * 104729;
        if (key == b.LmKey[i])
            return;
        b.LmKey[i] = key;
        byte* lightmap = s->Samples;
        if (_screen.Client.WorldModel->LightData == null)
            for (int k = 0; k < size; k++)
                _blocklights[k] = 128 * 256;      // no light data: as full light
        else
        {
            for (int k = 0; k < size; k++)
                _blocklights[k] = 0;
            if (lightmap != null)
                for (int m = 0; m < 4 && s->Styles[m] != 255; m++)
                {
                    uint scale = (uint)styles[s->Styles[m]];
                    for (int k = 0; k < size; k++)
                        _blocklights[k] += lightmap[k] * scale;
                    lightmap += size;
                }
            if (dlit != 0)
                AddDynamicLights(s, entityOrigin, smax, tmax);
        }
        for (int k = 0; k < size; k++)
        {
            uint t = _blocklights[k] >> 8;     // the shader doubles: 128 is the texture, 255 twice it (the software's range)
            _lmBytes[k] = (byte)Math.Min(255u, t);
        }
        Gl.BindTexture(Gl.Texture2D, _lmPages[b.LmPage[i]]);
        Gl.PixelStorei(Gl.UnpackAlignment, 1);
        Gl.TexSubImage2D(Gl.Texture2D, 0, b.LmX[i], b.LmY[i], smax, tmax, Gl.Red, Gl.UnsignedByte, _lmBytes);
    }

    /// <summary>The dynamic lights whose sphere reaches the face's plane (R_MarkLights' test, per face).</summary>
    private int DynamicLightBits(MSurface* s, Vec3 origin)
    {
        int bits = 0;
        DLight[] lights = _screen.Client.DLights;
        for (int l = 0; l < lights.Length; l++)
        {
            ref DLight dl = ref lights[l];
            if (dl.Die < _time || dl.Radius == 0)
                continue;
            Vec3 o = Vec3.Subtract(dl.Origin, origin);
            float dist = Vec3.Dot(o, s->Plane->Normal) - s->Plane->Dist;
            if (dist > dl.Radius || dist < -dl.Radius)
                continue;
            bits |= 1 << l;
        }
        return bits;
    }

    /// <summary>R_AddDynamicLights.</summary>
    private void AddDynamicLights(MSurface* s, Vec3 origin, int smax, int tmax)
    {
        MTexInfo* tex = s->TexInfo;
        DLight[] lights = _screen.Client.DLights;
        for (int l = 0; l < lights.Length; l++)
        {
            ref DLight dl = ref lights[l];
            if (dl.Die < _time || dl.Radius == 0)
                continue;
            Vec3 o = Vec3.Subtract(dl.Origin, origin);
            float dist = Vec3.Dot(o, s->Plane->Normal) - s->Plane->Dist;
            float rad = dl.Radius - MathF.Abs(dist);
            float minlight = dl.MinLight;
            if (rad < minlight)
                continue;
            minlight = rad - minlight;
            Vec3 impact = new(o.X - s->Plane->Normal.X * dist, o.Y - s->Plane->Normal.Y * dist, o.Z - s->Plane->Normal.Z * dist);
            float l0 = impact.X * tex->Vecs[0] + impact.Y * tex->Vecs[1] + impact.Z * tex->Vecs[2] + tex->Vecs[3] - s->TextureMins[0];
            float l1 = impact.X * tex->Vecs[4] + impact.Y * tex->Vecs[5] + impact.Z * tex->Vecs[6] + tex->Vecs[7] - s->TextureMins[1];
            for (int t = 0; t < tmax; t++)
            {
                int td = Math.Abs((int)(l1 - t * 16));
                for (int ss = 0; ss < smax; ss++)
                {
                    int sd = Math.Abs((int)(l0 - ss * 16));
                    float d = sd > td ? sd + (td >> 1) : td + (sd >> 1);
                    if (d < minlight)
                        _blocklights[t * smax + ss] += (uint)((rad - d) * 256);
                }
            }
        }
    }

    /// <summary>The surface's plane faces the eye (in the model's space).</summary>
    private static bool Faces(MSurface* s, Vec3 eye)
    {
        float dot = Vec3.Dot(eye, s->Plane->Normal) - s->Plane->Dist;
        return (s->Flags & SurfFlags.PlaneBack) != 0 ? dot < -0.01f : dot > 0.01f;
    }

    /// <summary>A visible face into the batch list, by its texture and lightmap page.</summary>
    private void AddBatch(BrushData b, int i, int frame)
    {
        MSurface* s = b.Surfaces + i;
        Texture* t = Animated(s->TexInfo->Texture, frame);
        if (t == null)
            return;
        uint tex = (s->Flags & SurfFlags.DrawSky) != 0 ? SkyOf(t).Solid : TextureOf(t);
        _batchKeys[_nBatch++] = (long)tex << 40 | (long)b.LmPage[i] << 32 | (uint)i;
    }

    /// <summary>The batched faces drawn, a run per texture and page; sky and turbulence by their flags.</summary>
    private void DrawBatches(BrushData b, Mat4 model, Vec3 eye, int frame)
    {
        new Span<long>(_batchKeys, _nBatch).Sort();
        Mat4 mvp = Mat4.Mul(Mat4.Mul(_proj, _viewMatrix), model);
        Gl.UseProgram(_surfProg);
        Gl.UniformMatrix4fv(_uMvp, 1, 0, mvp.M);
        Gl.UniformMatrix4fv(_uModel, 1, 0, model.M);
        Gl.Uniform1i(_uTex, 0);
        Gl.Uniform1i(_uLm, 1);
        Gl.Uniform1i(_uSkyAlpha, 2);
        Gl.Uniform1f(_uTime, (float)_time);
        Vec3 world = _screen.ViewOrg;
        Gl.Uniform3f(_uEye, world.X, world.Y, world.Z);
        Gl.Uniform3f(_uTintK, _tintK0, _tintK1, _tintK2);
        Gl.Uniform3f(_uTintM, _tintM0, _tintM1, _tintM2);
        Gl.Uniform1f(_uAlpha, 1);
        Gl.BindVertexArray(b.Vao);
        Gl.BindBuffer(Gl.ElementArrayBuffer, _ebo);
        int k = 0;
        while (k < _nBatch)
        {
            long run = _batchKeys[k] >> 32;
            int n = 0;
            int first = k;
            for (; k < _nBatch && _batchKeys[k] >> 32 == run; k++)
            {
                int i = (int)(uint)_batchKeys[k];
                int f = b.First[i], c = b.Verts[i];
                for (int v = 1; v + 1 < c && n + 3 <= MaxIndices; v++)
                {
                    _indices[n++] = (uint)f;
                    _indices[n++] = (uint)(f + v);
                    _indices[n++] = (uint)(f + v + 1);
                }
            }
            MSurface* s = b.Surfaces + (int)(uint)_batchKeys[first];
            Texture* t = Animated(s->TexInfo->Texture, frame);
            Gl.ActiveTexture(Gl.Texture0);
            Gl.BindTexture(Gl.Texture2D, (uint)(run >> 8));
            int mode = (s->Flags & SurfFlags.DrawSky) != 0 ? 2 : (s->Flags & SurfFlags.DrawTurb) != 0 ? 1 : 0;
            if (mode == 2)
            {
                Gl.ActiveTexture(Gl.Texture0 + 2);
                Gl.BindTexture(Gl.Texture2D, SkyOf(t).Alpha);
            }
            else if (mode == 0)
            {
                Gl.ActiveTexture(Gl.Texture0 + 1);
                Gl.BindTexture(Gl.Texture2D, _lmPages[(int)(run & 0xFF)]);
                Gl.Uniform2f(_uTexSize, t->Width, t->Height);
            }
            Gl.Uniform1i(_uMode, mode);
            Gl.BufferData(Gl.ElementArrayBuffer, n * sizeof(uint), _indices, Gl.StreamDraw);
            Gl.DrawElements(Gl.Triangles, n, Gl.UnsignedInt, null);
        }
        Gl.BindVertexArray(0);
        Gl.ActiveTexture(Gl.Texture0);
    }

    /// <summary>R_DrawWorld: the faces of the PVS's leafs in the frustum that face the eye.</summary>
    private void DrawWorld()
    {
        Model* world = _screen.Client.WorldModel;
        BrushData b = BrushOf(world);
        Mat4 mvp = Mat4.Mul(_proj, _viewMatrix);
        int vis = _screen.VisFrameCount;
        ReadOnlySpan<int> leafVis = _screen.LeafVisFrames;
        int mark = ++_visMark;
        _nBatch = 0;
        Vec3 eye = _screen.ViewOrg;
        for (int l = 1; l <= world->NumLeafs; l++)
        {
            if (leafVis[l] != vis)
                continue;
            MLeaf* leaf = world->Leafs + l;
            if (!mvp.BoxVisible(leaf->MinMaxs[0], leaf->MinMaxs[1], leaf->MinMaxs[2], leaf->MinMaxs[3], leaf->MinMaxs[4], leaf->MinMaxs[5]))
                continue;
            MSurface** ms = world->MarkSurfaces + leaf->FirstMarkSurface;
            for (int k = 0; k < leaf->NumMarkSurfaces; k++)
            {
                int i = (int)(ms[k] - world->Surfaces);
                if (b.Mark[i] == mark)
                    continue;
                b.Mark[i] = mark;
                MSurface* s = world->Surfaces + i;
                if (!Faces(s, eye))
                    continue;
                if ((s->Flags & (SurfFlags.DrawSky | SurfFlags.DrawTurb)) == 0)
                    UpdateLightmap(b, i, default);
                b.Drawn[i] = mark;
                AddBatch(b, i, 0);
            }
        }
        DrawBatches(b, Mat4.Identity(), eye, 0);
    }

    /// <summary>The last frame drew the world's surface (for the test against the software renderer's set).</summary>
    public bool DrewWorldSurface(int surface) =>
        _screen.Client.WorldModel is var w && w != null && _brush.TryGetValue((nint)w->Surfaces, out BrushData? b)
        && surface < b.Count && b.Drawn[surface] == _visMark;

    /// <summary>Why the world's surface was not drawn: its leafs' PVS and frustum tests, its facing (a test's diagnosis).</summary>
    public string Explain(int surface)
    {
        Model* world = _screen.Client.WorldModel;
        Mat4 mvp = Mat4.Mul(_proj, _viewMatrix);
        var sb = new System.Text.StringBuilder();
        MSurface* s = world->Surfaces + surface;
        sb.Append($"eye {_screen.ViewOrg.X} {_screen.ViewOrg.Y} {_screen.ViewOrg.Z} angles {_screen.ViewAngles.X} {_screen.ViewAngles.Y} {_screen.ViewAngles.Z} fov {_screen.FovX}/{_screen.FovY} vrect {_screen.RefdefVrect.Width}x{_screen.RefdefVrect.Height}; ");
        sb.Append($"faces {Faces(s, _screen.ViewOrg)} (dot {Vec3.Dot(_screen.ViewOrg, s->Plane->Normal) - s->Plane->Dist}, back {(s->Flags & SurfFlags.PlaneBack) != 0});");
        for (int l = 1; l <= world->NumLeafs; l++)
        {
            MLeaf* leaf = world->Leafs + l;
            for (int k = 0; k < leaf->NumMarkSurfaces; k++)
                if (world->MarkSurfaces[leaf->FirstMarkSurface + k] == s)
                    sb.Append($" leaf {l} [{leaf->MinMaxs[0]} {leaf->MinMaxs[1]} {leaf->MinMaxs[2]} .. {leaf->MinMaxs[3]} {leaf->MinMaxs[4]} {leaf->MinMaxs[5]}]: vis {_screen.LeafVisFrames[l] == _screen.VisFrameCount}, frustum " +
                              $"{mvp.BoxVisible(leaf->MinMaxs[0], leaf->MinMaxs[1], leaf->MinMaxs[2], leaf->MinMaxs[3], leaf->MinMaxs[4], leaf->MinMaxs[5])};");
        }
        return sb.ToString();
    }

    /// <summary>The alias entities the last frame drew (not the gun).</summary>
    public IReadOnlyList<EntityRef> DrewAlias => _drewAlias;

    private readonly List<EntityRef> _drewAlias = [];

    /// <summary>Renders into an offscreen framebuffer of the size from now on (tests and captures without a window).</summary>
    public void UseOffscreen(int width, int height)
    {
        uint fbo, color, depth;
        Gl.GenFramebuffers(1, &fbo);
        Gl.BindFramebuffer(Gl.Framebuffer, fbo);
        Gl.GenRenderbuffers(1, &color);
        Gl.BindRenderbuffer(Gl.Renderbuffer, color);
        Gl.RenderbufferStorage(Gl.Renderbuffer, Gl.Rgba8, width, height);
        Gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.ColorAttachment0, Gl.Renderbuffer, color);
        Gl.GenRenderbuffers(1, &depth);
        Gl.BindRenderbuffer(Gl.Renderbuffer, depth);
        Gl.RenderbufferStorage(Gl.Renderbuffer, Gl.DepthComponent24, width, height);
        Gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.DepthAttachment, Gl.Renderbuffer, depth);
        if (Gl.CheckFramebufferStatus(Gl.Framebuffer) != Gl.FramebufferComplete)
            throw new InvalidOperationException("OpenGL: the offscreen framebuffer is incomplete");
    }

    /// <summary>R_DrawBrushModel: an entity's faces that face the eye, in the entity's frame (R_RotateForEntity).</summary>
    private void DrawBrushEntity(ref ClientEntity e)
    {
        Model* m = e.Model;
        BrushData b = BrushOf(m);
        Mat4 model = Mat4.Identity().Translate(e.Origin.X, e.Origin.Y, e.Origin.Z)
            .Rotate(e.Angles.Y, 0, 0, 1).Rotate(-e.Angles.X, 0, 1, 0).Rotate(e.Angles.Z, 1, 0, 0);
        Mat4 mvp = Mat4.Mul(Mat4.Mul(_proj, _viewMatrix), model);
        if (!mvp.BoxVisible(m->Mins.X, m->Mins.Y, m->Mins.Z, m->Maxs.X, m->Maxs.Y, m->Maxs.Z))
            return;
        // the eye in the entity's frame (the inverse rotation of R_RotateForEntity)
        Vec3 d = Vec3.Subtract(_screen.ViewOrg, e.Origin);
        Mathlib.AngleVectors(e.Angles, out Vec3 forward, out Vec3 right, out Vec3 up);
        var eye = new Vec3(Vec3.Dot(d, forward), -Vec3.Dot(d, right), Vec3.Dot(d, up));
        _nBatch = 0;
        for (int i = m->FirstModelSurface; i < m->FirstModelSurface + m->NumModelSurfaces; i++)
        {
            MSurface* s = b.Surfaces + i;
            if (!Faces(s, eye))
                continue;
            if ((s->Flags & (SurfFlags.DrawSky | SurfFlags.DrawTurb)) == 0)
                UpdateLightmap(b, i, e.Origin);
            AddBatch(b, i, e.Frame);
        }
        DrawBatches(b, model, eye, e.Frame);
    }
}
