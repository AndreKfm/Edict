// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Engine;

namespace Edict.Gl;

/// <summary>
/// The scene (gl_rmain.c's R_RenderScene): the world, the brush entities, the alias models (gl_mesh.c's triangles of
/// the frame, lit by R_LightPoint and the dynamic lights, shaded by the light's direction as GLQuake's shadedots,
/// the players' colors translated as R_TranslatePlayerSkin), the sprites (R_DrawSpriteModel), the particles
/// (R_DrawParticles' quads) and the gun nearer in depth (R_DrawViewModel).
/// </summary>
public sealed unsafe partial class GlRenderer
{
    private const int MaxModelVerts = 3 * 4096, ModelFloats = 6, MaxParticleVerts = 6 * 2048, ParticleFloats = 8;

    private readonly Dictionary<(nint Skin, int Colors), uint> _skins = [];
    private readonly Dictionary<nint, uint> _spriteFrames = [];
    private uint _modelProg, _modelVao, _modelVbo, _partProg, _partVao, _partVbo;
    private int _umMvp, _umTex, _umTintK, _umTintM, _umAlphaTest, _upMvp, _upTintK, _upTintM;
    private float* _modelVerts, _partVerts;

    private void InitModels()
    {
        _modelProg = Gl.Program(Shaders.ModelVs, Shaders.ModelFs, "aPos", "aTex", "aLight");
        _umMvp = Gl.Uniform(_modelProg, "uMvp");
        _umTex = Gl.Uniform(_modelProg, "uTex");
        _umTintK = Gl.Uniform(_modelProg, "uTintK");
        _umTintM = Gl.Uniform(_modelProg, "uTintM");
        _umAlphaTest = Gl.Uniform(_modelProg, "uAlphaTest");
        _partProg = Gl.Program(Shaders.ParticleVs, Shaders.ParticleFs, "aPos", "aCorner", "aColor");
        _upMvp = Gl.Uniform(_partProg, "uMvp");
        _upTintK = Gl.Uniform(_partProg, "uTintK");
        _upTintM = Gl.Uniform(_partProg, "uTintM");
        _modelVerts = (float*)NativeMemory.Alloc(MaxModelVerts * ModelFloats, sizeof(float));
        _partVerts = (float*)NativeMemory.Alloc(MaxParticleVerts * ParticleFloats, sizeof(float));
        (_modelVao, _modelVbo) = DynamicVao(ModelFloats, [3, 2, 1]);
        (_partVao, _partVbo) = DynamicVao(ParticleFloats, [3, 2, 3]);
    }

    private static (uint, uint) DynamicVao(int floats, int[] sizes)
    {
        uint vao, vbo;
        Gl.GenVertexArrays(1, &vao);
        Gl.GenBuffers(1, &vbo);
        Gl.BindVertexArray(vao);
        Gl.BindBuffer(Gl.ArrayBuffer, vbo);
        int offset = 0;
        for (uint i = 0; i < sizes.Length; i++)
        {
            Gl.EnableVertexAttribArray(i);
            Gl.VertexAttribPointer(i, sizes[i], Gl.Float, 0, floats * 4, (void*)(offset * 4));
            offset += sizes[i];
        }
        Gl.BindVertexArray(0);
        return (vao, vbo);
    }

    /// <summary>R_SetupGL: GLQuake's projection (MYgluPerspective from 4 units on) and Quake's axes into GL's.</summary>
    private void SetupView(int width, int height)
    {
        // the vertical angle from fov_x and the view's aspect, as the software renderer's frustum has it: r_refdef.fov_y
        // is CalcFov of the rectangle before R_ViewChanged set the new one (screen.c), which only GLQuake reads
        double fovy = 2 * Math.Atan(Math.Tan(_screen.FovX * Math.PI / 360) * height / width) * 180 / Math.PI;
        _proj = Mat4.Perspective(fovy, width / (double)height, 4, 16384);  // GLQuake: 4096; a view outside a map sees further
        Vec3 a = _screen.ViewAngles, o = _screen.ViewOrg;
        _viewMatrix = Mat4.Identity().Rotate(-90, 1, 0, 0).Rotate(90, 0, 0, 1)
            .Rotate(-a.Z, 1, 0, 0).Rotate(-a.X, 0, 1, 0).Rotate(-a.Y, 0, 0, 1).Translate(-o.X, -o.Y, -o.Z);
    }

    /// <summary>R_RenderScene, then the gun.</summary>
    private void DrawView(int width, int height)
    {
        SetupView(width, height);
        Gl.Enable(Gl.DepthTest);
        Gl.DepthFunc(Gl.LEqual);
        Gl.DepthMask(1);
        Gl.Disable(Gl.CullFaceCap);
        Gl.Disable(Gl.Blend);
        DrawWorld();
        _drewAlias.Clear();
        ClientState cl = _screen.Client;
        if (_screen.Cvar("r_drawentities\0"u8) != 0)
        {
            for (int i = 0; i < cl.NumVisEdicts; i++)
            {
                EntityRef r = cl.VisEdicts[i];
                ref ClientEntity e = ref _screen.EntityOf(r);
                switch (e.Model->Type)
                {
                    case ModelType.Brush:
                        DrawBrushEntity(ref e);
                        break;
                    case ModelType.Alias:
                        if (r.Array == 0 && r.Index == cl.ViewEntity)
                            break;      // don't draw the player
                        if (DrawAlias(ref e, viewModel: false))
                            _drewAlias.Add(r);
                        break;
                    case ModelType.Sprite:
                        if (r.Array == 0 && r.Index == cl.ViewEntity)
                            break;
                        DrawSprite(ref e);
                        break;
                }
            }
        }
        DrawParticles();
        DrawViewModel();
        Gl.Disable(Gl.DepthTest);
    }

    /// <summary>R_DrawViewModel's conditions, the gun drawn in the near third of the depth range.</summary>
    private void DrawViewModel()
    {
        ClientState cl = _screen.Client;
        if (_screen.Cvar("r_drawviewmodel\0"u8) == 0 || _screen.Cvar("fov\0"u8) > 90)
            return;
        if ((cl.Items & 0x80000) != 0 || cl.Stats[0] <= 0)
            return;     // IT_INVISIBILITY, STAT_HEALTH
        ref ClientEntity e = ref cl.ViewEnt;
        if (e.Model == null || e.Model->Type != ModelType.Alias)
            return;
        Gl.DepthRange(0, 0.3);
        DrawAlias(ref e, viewModel: true);
        Gl.DepthRange(0, 1);
    }

    /// <summary>The light an alias model gets: R_LightPoint, the dynamic lights' share, GLQuake's clamps.</summary>
    private (float Ambient, float Shade) ModelLight(ref ClientEntity e, bool viewModel)
    {
        int light = _screen.LightAt(e.Origin);
        if (viewModel && light < 24)
            light = 24;
        float shade = light, ambient = light;
        foreach (ref DLight dl in _screen.Client.DLights.AsSpan())
        {
            if (dl.Die < _time || dl.Radius == 0)
                continue;
            float add = dl.Radius - Mathlib.Length(Vec3.Subtract(e.Origin, dl.Origin));
            if (add > 0)
            {
                ambient += add;
                shade += add;
            }
        }
        if (ambient > 128)
            ambient = 128;
        if (ambient + shade > 192)
            shade = 192 - ambient;
        return (ambient, shade);
    }

    /// <summary>The skin's GL texture, the player's colors translated (R_TranslatePlayerSkin's ranges).</summary>
    private uint SkinOf(AliasModel* m, byte* skin, int colors)
    {
        if (_skins.TryGetValue(((nint)skin, colors), out uint id))
            return id;
        int w = m->SkinWidth, h = m->SkinHeight;
        Span<byte> translate = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
            translate[i] = (byte)i;
        if (colors >= 0)
        {
            int top = colors & 0xf0, bottom = (colors & 15) << 4;
            for (int i = 0; i < 16; i++)
            {
                translate[16 + i] = (byte)(top < 128 ? top + i : top + 15 - i);
                translate[96 + i] = (byte)(bottom < 128 ? bottom + i : bottom + 15 - i);
            }
        }
        ReadOnlySpan<byte> pal = _screen.BasePalette;
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int c = translate[skin[i]];
            rgba[i * 4] = pal[c * 3];
            rgba[i * 4 + 1] = pal[c * 3 + 1];
            rgba[i * 4 + 2] = pal[c * 3 + 2];
            rgba[i * 4 + 3] = 255;
        }
        id = Upload(rgba, w, h, mip: true, Gl.ClampToEdge);
        _skins[((nint)skin, colors)] = id;
        return id;
    }

    /// <summary>The skin for the entity's skin number and the time (skin groups as R_AliasSetupSkin).</summary>
    private byte* SkinFor(AliasModel* m, ref ClientEntity e)
    {
        int skinnum = e.SkinNum >= 0 && e.SkinNum < m->NumSkins ? e.SkinNum : 0;
        AliasSkinDesc* d = m->SkinDescs + skinnum;
        if (d->Type == 0)
            return d->Skin;
        AliasSkinGroup* g = d->Group;
        float full = g->Intervals[g->NumSkins - 1];
        float t = (float)(_time + e.SyncBase);
        t -= (int)(t / full) * full;
        int i = 0;
        while (i < g->NumSkins - 1 && !(g->Intervals[i] > t))
            i++;
        return g->SkinDescs[i].Skin;
    }

    /// <summary>The frame's vertices for the entity's frame and the time (frame groups as R_AliasSetupFrame).</summary>
    private TriVertX* FrameFor(AliasModel* m, ref ClientEntity e)
    {
        int frame = e.Frame >= 0 && e.Frame < m->NumFrames ? e.Frame : 0;
        AliasFrameDesc* f = m->Frames + frame;
        if (f->Type == 0)
            return f->Frame;
        AliasGroup* g = f->Group;
        float full = g->Intervals[g->NumFrames - 1];
        float t = (float)(_time + e.SyncBase);
        t -= (int)(t / full) * full;
        int i = 0;
        while (i < g->NumFrames - 1 && !(g->Intervals[i] > t))
            i++;
        return g->Frames[i].Frame;
    }

    /// <summary>R_DrawAliasModel: the frame's triangles in the entity's frame, lit per vertex by its normal.</summary>
    private bool DrawAlias(ref ClientEntity e, bool viewModel)
    {
        var m = (AliasModel*)e.Model->Extra;
        if (m == null || m->NumTris * 3 > MaxModelVerts)
            return false;
        Mat4 model = Mat4.Identity().Translate(e.Origin.X, e.Origin.Y, e.Origin.Z)
            .Rotate(e.Angles.Y, 0, 0, 1).Rotate(-e.Angles.X, 0, 1, 0).Rotate(e.Angles.Z, 1, 0, 0);
        Mat4 mvp = Mat4.Mul(Mat4.Mul(_proj, _viewMatrix), model);
        float br = m->BoundingRadius;
        if (!viewModel && !mvp.BoxVisible(-br, -br, -br, br, br, br))
            return false;
        (float ambient, float shade) = ModelLight(ref e, viewModel);
        // shadedots: the light from the yaw's direction, in the model's frame
        float an = e.Angles.Y / 180 * MathF.PI;
        float lx = MathF.Cos(-an), ly = MathF.Sin(-an), lz = 1;
        float ll = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
        lx /= ll;
        ly /= ll;
        lz /= ll;
        TriVertX* verts = FrameFor(m, ref e);
        int colors = e.Colormap >= 2 ? _screen.Client.Scores[e.Colormap - 2].Colors : -1;
        uint skin = SkinOf(m, SkinFor(m, ref e), colors);
        ReadOnlySpan<float> normals = VertexNormals.Table;
        float* o = _modelVerts;
        int n = 0;
        float sw = m->SkinWidth, sh = m->SkinHeight;
        for (int i = 0; i < m->NumTris; i++)
        {
            MTriangle* tri = m->Triangles + i;
            for (int k = 0; k < 3; k++)
            {
                int vi = tri->VertIndex[k];
                TriVertX* v = verts + vi;
                STVert* st = m->STVerts + vi;
                float s = (st->S >> 16) + 0.5f, t = (st->T >> 16) + 0.5f;
                if (tri->FacesFront == 0 && (st->OnSeam & 0x20) != 0)
                    s += sw / 2;    // on seam, back side
                int ni = v->LightNormalIndex * 3;
                float dot = normals[ni] * lx + normals[ni + 1] * ly + normals[ni + 2] * lz;
                float l = (dot < 0 ? 1 + dot * 0.3f : 1 + dot) * (ambient + shade) * 0.5f / 128;
                o[0] = v->V[0] * m->Scale.X + m->ScaleOrigin.X;
                o[1] = v->V[1] * m->Scale.Y + m->ScaleOrigin.Y;
                o[2] = v->V[2] * m->Scale.Z + m->ScaleOrigin.Z;
                o[3] = s / sw;
                o[4] = t / sh;
                o[5] = l;
                o += ModelFloats;
                n++;
            }
        }
        Gl.UseProgram(_modelProg);
        Gl.UniformMatrix4fv(_umMvp, 1, 0, mvp.M);
        Gl.Uniform1i(_umTex, 0);
        Gl.Uniform1i(_umAlphaTest, 0);
        Gl.Uniform3f(_umTintK, _tintK0, _tintK1, _tintK2);
        Gl.Uniform3f(_umTintM, _tintM0, _tintM1, _tintM2);
        Gl.ActiveTexture(Gl.Texture0);
        Gl.BindTexture(Gl.Texture2D, skin);
        Gl.BindVertexArray(_modelVao);
        Gl.BindBuffer(Gl.ArrayBuffer, _modelVbo);
        Gl.BufferData(Gl.ArrayBuffer, n * ModelFloats * 4, _modelVerts, Gl.StreamDraw);
        Gl.DrawArrays(Gl.Triangles, 0, n);
        Gl.BindVertexArray(0);
        return true;
    }

    /// <summary>The sprite frame's GL texture (255 transparent).</summary>
    private uint SpriteTexture(SpriteFrame* f)
    {
        if (_spriteFrames.TryGetValue((nint)f, out uint id))
            return id;
        ReadOnlySpan<byte> pal = _screen.BasePalette;
        byte[] rgba = new byte[f->Width * f->Height * 4];
        for (int i = 0; i < f->Width * f->Height; i++)
        {
            int c = f->Pixels[i];
            rgba[i * 4] = pal[c * 3];
            rgba[i * 4 + 1] = pal[c * 3 + 1];
            rgba[i * 4 + 2] = pal[c * 3 + 2];
            rgba[i * 4 + 3] = (byte)(c == 255 ? 0 : 255);
        }
        id = Upload(rgba, f->Width, f->Height, mip: false, Gl.ClampToEdge);
        _spriteFrames[(nint)f] = id;
        return id;
    }

    /// <summary>R_DrawSpriteModel: the frame as a quad facing the view (oriented sprites by their angles).</summary>
    private void DrawSprite(ref ClientEntity e)
    {
        var sp = (SpriteModel*)e.Model->Extra;
        int frame = e.Frame >= 0 && e.Frame < sp->NumFrames ? e.Frame : 0;
        SpriteFrameDesc* d = sp->Frames + frame;
        SpriteFrame* f = d->Frame;
        if (d->Type != 0)
        {
            SpriteGroup* g = d->Group;
            float full = g->Intervals[g->NumFrames - 1];
            float t = (float)(_time + e.SyncBase);
            t -= (int)(t / full) * full;
            int i = 0;
            while (i < g->NumFrames - 1 && !(g->Intervals[i] > t))
                i++;
            f = g->Frames[i];
        }
        Vec3 up, right;
        if (sp->Type == 3)
            Mathlib.AngleVectors(e.Angles, out _, out right, out up);    // SPR_ORIENTED
        else
        {
            up = _screen.Vup;
            right = _screen.Vright;
        }
        Vec3 c = e.Origin;
        float* o = _modelVerts;
        void V(float* p, float u, float r, float s, float t)
        {
            p[0] = c.X + up.X * u + right.X * r;
            p[1] = c.Y + up.Y * u + right.Y * r;
            p[2] = c.Z + up.Z * u + right.Z * r;
            p[3] = s;
            p[4] = t;
            p[5] = 1;
        }
        V(o, f->Down, f->Left, 0, 1);
        V(o + 6, f->Up, f->Left, 0, 0);
        V(o + 12, f->Up, f->Right, 1, 0);
        V(o + 18, f->Down, f->Left, 0, 1);
        V(o + 24, f->Up, f->Right, 1, 0);
        V(o + 30, f->Down, f->Right, 1, 1);
        Mat4 mvp = Mat4.Mul(_proj, _viewMatrix);
        Gl.UseProgram(_modelProg);
        Gl.UniformMatrix4fv(_umMvp, 1, 0, mvp.M);
        Gl.Uniform1i(_umTex, 0);
        Gl.Uniform1i(_umAlphaTest, 1);
        Gl.Uniform3f(_umTintK, _tintK0, _tintK1, _tintK2);
        Gl.Uniform3f(_umTintM, _tintM0, _tintM1, _tintM2);
        Gl.ActiveTexture(Gl.Texture0);
        Gl.BindTexture(Gl.Texture2D, SpriteTexture(f));
        Gl.BindVertexArray(_modelVao);
        Gl.BindBuffer(Gl.ArrayBuffer, _modelVbo);
        Gl.BufferData(Gl.ArrayBuffer, 6 * ModelFloats * 4, _modelVerts, Gl.StreamDraw);
        Gl.DrawArrays(Gl.Triangles, 0, 6);
        Gl.BindVertexArray(0);
    }

    /// <summary>R_DrawParticles: a quad facing the view per active particle, larger with distance (GLQuake's scale).</summary>
    private void DrawParticles()
    {
        ClientState cl = _screen.Client;
        Vec3 up = _screen.Vup, right = _screen.Vright, pn = _screen.Vpn, org = _screen.ViewOrg;
        ReadOnlySpan<byte> pal = _screen.BasePalette;
        float* o = _partVerts;
        int n = 0;
        for (int pi = cl.ActiveHead; pi >= 0 && n + 6 <= MaxParticleVerts; pi = cl.ParticleArray[pi].Next)
        {
            ref ClientState.Particle p = ref cl.ParticleArray[pi];
            float scale = Vec3.Dot(Vec3.Subtract(p.Org, org), pn);
            scale = scale < 20 ? 1 : 1 + scale * 0.004f;
            scale *= 0.75f;
            int ci = Math.Clamp((int)p.Color, 0, 255);
            float r = pal[ci * 3] / 255f, g = pal[ci * 3 + 1] / 255f, b = pal[ci * 3 + 2] / 255f;
            ReadOnlySpan<float> corners = [-1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1];
            for (int k = 0; k < 6; k++)
            {
                float cu = corners[k * 2], cr = corners[k * 2 + 1];
                o[0] = p.Org.X + (up.X * cu + right.X * cr) * scale;
                o[1] = p.Org.Y + (up.Y * cu + right.Y * cr) * scale;
                o[2] = p.Org.Z + (up.Z * cu + right.Z * cr) * scale;
                o[3] = cu;
                o[4] = cr;
                o[5] = r;
                o[6] = g;
                o[7] = b;
                o += ParticleFloats;
            }
            n += 6;
        }
        if (n == 0)
            return;
        Mat4 mvp = Mat4.Mul(_proj, _viewMatrix);
        Gl.UseProgram(_partProg);
        Gl.UniformMatrix4fv(_upMvp, 1, 0, mvp.M);
        Gl.Uniform3f(_upTintK, _tintK0, _tintK1, _tintK2);
        Gl.Uniform3f(_upTintM, _tintM0, _tintM1, _tintM2);
        Gl.BindVertexArray(_partVao);
        Gl.BindBuffer(Gl.ArrayBuffer, _partVbo);
        Gl.BufferData(Gl.ArrayBuffer, n * ParticleFloats * 4, _partVerts, Gl.StreamDraw);
        Gl.DrawArrays(Gl.Triangles, 0, n);
        Gl.BindVertexArray(0);
    }
}
