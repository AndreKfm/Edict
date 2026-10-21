// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Edict.Engine;

// ---- modelgen.h, model.h: alias models

/// <summary><c>trivertx_t</c>: a vertex packed into bytes, and its normal's index.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TriVertX
{
    public fixed byte V[3];
    public byte LightNormalIndex;
}

/// <summary><c>stvert_t</c>, s and t in 16.16 as Mod_LoadAliasModel puts them.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct STVert
{
    public int OnSeam, S, T;
}

/// <summary><c>mtriangle_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MTriangle
{
    public int FacesFront;
    public fixed int VertIndex[3];
}

/// <summary><c>maliasframedesc_t</c> (and a group's frame): its type, bounds and vertices or group.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AliasFrameDesc
{
    public int Type;                                  // ALIAS_SINGLE 0, ALIAS_GROUP 1
    public TriVertX BBoxMin, BBoxMax;
    public TriVertX* Frame;                           // ALIAS_SINGLE
    public AliasGroup* Group;                         // ALIAS_GROUP
}

/// <summary><c>maliasgroup_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AliasGroup
{
    public int NumFrames;
    public float* Intervals;
    public AliasFrameDesc* Frames;                    // each ALIAS_SINGLE
}

/// <summary><c>maliasskindesc_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AliasSkinDesc
{
    public int Type;                                  // ALIAS_SKIN_SINGLE 0, ALIAS_SKIN_GROUP 1
    public byte* Skin;                                // ALIAS_SKIN_SINGLE
    public AliasSkinGroup* Group;                     // ALIAS_SKIN_GROUP
}

/// <summary><c>maliasskingroup_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AliasSkinGroup
{
    public int NumSkins;
    public float* Intervals;
    public AliasSkinDesc* SkinDescs;
}

/// <summary><c>aliashdr_t</c> with its <c>mdl_t</c>: what Mod_LoadAliasModel moves into the cache.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AliasModel
{
    public Vec3 Scale, ScaleOrigin;
    public float BoundingRadius;
    public Vec3 EyePosition;
    public int NumSkins, SkinWidth, SkinHeight, NumVerts, NumTris, NumFrames;
    public float Size;
    public AliasSkinDesc* SkinDescs;
    public STVert* STVerts;
    public MTriangle* Triangles;
    public AliasFrameDesc* Frames;
}

// ---- spritegn.h, model.h: sprites

/// <summary><c>mspriteframe_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SpriteFrame
{
    public int Width, Height;
    public float Up, Down, Left, Right;
    public byte* Pixels;
}

/// <summary><c>mspriteframedesc_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SpriteFrameDesc
{
    public int Type;                                  // SPR_SINGLE 0, SPR_GROUP 1
    public SpriteFrame* Frame;                        // SPR_SINGLE
    public SpriteGroup* Group;                        // SPR_GROUP
}

/// <summary><c>mspritegroup_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SpriteGroup
{
    public int NumFrames;
    public float* Intervals;
    public SpriteFrame** Frames;
}

/// <summary><c>msprite_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SpriteModel
{
    public int Type, MaxWidth, MaxHeight, NumFrames;
    public float BeamLength;
    public SpriteFrameDesc* Frames;
}

/// <summary>
/// Mod_LoadAliasModel and Mod_LoadSpriteModel (model.c) for the renderer: the skins, vertices, triangles and
/// frames, the sprite frames, with C's checks (Sys_Error). Native memory the cache owns; load path.
/// </summary>
public static unsafe class AliasLoader
{
    private const int AliasVersion = 6, SpriteVersion = 1, MaxAliasVerts = 2000, MaxLbmHeight = 480;
    private const float AliasBaseSizeRatio = (float)(1.0 / 11.0);

    private sealed class Reader(ReadOnlySpan<byte> d, string name)
    {
        private readonly byte[] _d = d.ToArray();
        public int Pos;
        public readonly string Name = name;

        public int Int()
        {
            if (Pos + 4 > _d.Length)
                throw new SysErrorException($"{Name}: the file ends early");
            int v = BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(Pos));
            Pos += 4;
            return v;
        }

        public float Float() => BitConverter.Int32BitsToSingle(Int());

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || Pos + n > _d.Length)
                throw new SysErrorException($"{Name}: the file ends early");
            ReadOnlySpan<byte> s = _d.AsSpan(Pos, n);
            Pos += n;
            return s;
        }
    }

    private static T* New<T>(List<nint> own, int count) where T : unmanaged
    {
        var p = (T*)NativeMemory.AllocZeroed((nuint)Math.Max(count, 1), (nuint)sizeof(T));
        own.Add((nint)p);
        return p;
    }

    private static byte* Copy(List<nint> own, ReadOnlySpan<byte> s)
    {
        byte* p = New<byte>(own, s.Length);
        s.CopyTo(new Span<byte>(p, s.Length));
        return p;
    }

    private static TriVertX ReadTriVert(Reader r)
    {
        ReadOnlySpan<byte> b = r.Bytes(4);
        TriVertX t;
        t.V[0] = b[0];
        t.V[1] = b[1];
        t.V[2] = b[2];
        t.LightNormalIndex = b[3];
        return t;
    }

    /// <summary>Mod_LoadAliasFrame: the bounds, the name (skipped) and the vertices.</summary>
    private static void LoadFrame(Reader r, AliasFrameDesc* f, int numv, List<nint> own)
    {
        f->BBoxMin = ReadTriVert(r);
        f->BBoxMax = ReadTriVert(r);
        r.Bytes(16);    // name
        f->Frame = (TriVertX*)Copy(own, r.Bytes(numv * 4));
    }

    /// <summary>Mod_LoadAliasModel.</summary>
    [NoAllocUnchecked("load path")]
    public static AliasModel* LoadAlias(ReadOnlySpan<byte> file, string name, Model* mod, List<nint> own)
    {
        var r = new Reader(file, name);
        r.Int();    // ident
        int version = r.Int();
        if (version != AliasVersion)
            throw new SysErrorException($"{name} has wrong version number ({version} should be {AliasVersion})");
        AliasModel* m = New<AliasModel>(own, 1);
        m->Scale = new Vec3(r.Float(), r.Float(), r.Float());
        m->ScaleOrigin = new Vec3(r.Float(), r.Float(), r.Float());
        m->BoundingRadius = r.Float();
        m->EyePosition = new Vec3(r.Float(), r.Float(), r.Float());
        m->NumSkins = r.Int();
        m->SkinWidth = r.Int();
        m->SkinHeight = r.Int();
        if (m->SkinHeight > MaxLbmHeight)
            throw new SysErrorException($"model {name} has a skin taller than {MaxLbmHeight}");
        m->NumVerts = r.Int();
        if (m->NumVerts <= 0)
            throw new SysErrorException($"model {name} has no vertices");
        if (m->NumVerts > MaxAliasVerts)
            throw new SysErrorException($"model {name} has too many vertices");
        m->NumTris = r.Int();
        if (m->NumTris <= 0)
            throw new SysErrorException($"model {name} has no triangles");
        m->NumFrames = r.Int();
        r.Int();    // synctype (the cache's ModelCache.TryLoad reads it)
        r.Int();    // flags
        m->Size = r.Float() * AliasBaseSizeRatio;
        if ((m->SkinWidth & 0x03) != 0)
            throw new SysErrorException("Mod_LoadAliasModel: skinwidth not multiple of 4");

        // load the skins
        int skinsize = m->SkinHeight * m->SkinWidth;
        if (m->NumSkins < 1)
            throw new SysErrorException($"Mod_LoadAliasModel: Invalid # of skins: {m->NumSkins}");
        m->SkinDescs = New<AliasSkinDesc>(own, m->NumSkins);
        for (int i = 0; i < m->NumSkins; i++)
        {
            AliasSkinDesc* d = m->SkinDescs + i;
            d->Type = r.Int();
            if (d->Type == 0)
            {
                d->Skin = Copy(own, r.Bytes(skinsize));
                continue;
            }
            AliasSkinGroup* g = New<AliasSkinGroup>(own, 1);
            d->Group = g;
            g->NumSkins = r.Int();
            g->Intervals = New<float>(own, g->NumSkins);
            for (int k = 0; k < g->NumSkins; k++)
            {
                g->Intervals[k] = r.Float();
                if (g->Intervals[k] <= 0)
                    throw new SysErrorException("Mod_LoadAliasSkinGroup: interval<=0");
            }
            g->SkinDescs = New<AliasSkinDesc>(own, g->NumSkins);
            for (int k = 0; k < g->NumSkins; k++)
                g->SkinDescs[k].Skin = Copy(own, r.Bytes(skinsize));
        }

        // set base s and t vertices: s and t in 16.16 format
        m->STVerts = New<STVert>(own, m->NumVerts);
        for (int i = 0; i < m->NumVerts; i++)
        {
            m->STVerts[i].OnSeam = r.Int();
            m->STVerts[i].S = r.Int() << 16;
            m->STVerts[i].T = r.Int() << 16;
        }

        // set up the triangles
        m->Triangles = New<MTriangle>(own, m->NumTris);
        for (int i = 0; i < m->NumTris; i++)
        {
            m->Triangles[i].FacesFront = r.Int();
            for (int j = 0; j < 3; j++)
                m->Triangles[i].VertIndex[j] = r.Int();
        }

        // load the frames
        if (m->NumFrames < 1)
            throw new SysErrorException($"Mod_LoadAliasModel: Invalid # of frames: {m->NumFrames}");
        m->Frames = New<AliasFrameDesc>(own, m->NumFrames);
        for (int i = 0; i < m->NumFrames; i++)
        {
            AliasFrameDesc* f = m->Frames + i;
            f->Type = r.Int();
            if (f->Type == 0)
            {
                LoadFrame(r, f, m->NumVerts, own);
                continue;
            }
            // Mod_LoadAliasGroup
            AliasGroup* g = New<AliasGroup>(own, 1);
            f->Group = g;
            g->NumFrames = r.Int();
            f->BBoxMin = ReadTriVert(r);
            f->BBoxMax = ReadTriVert(r);
            g->Intervals = New<float>(own, g->NumFrames);
            for (int k = 0; k < g->NumFrames; k++)
            {
                g->Intervals[k] = r.Float();
                if (g->Intervals[k] <= 0.0)
                    throw new SysErrorException("Mod_LoadAliasGroup: interval<=0");
            }
            g->Frames = New<AliasFrameDesc>(own, g->NumFrames);
            for (int k = 0; k < g->NumFrames; k++)
                LoadFrame(r, g->Frames + k, m->NumVerts, own);
        }
        mod->Extra = m;
        return m;
    }

    /// <summary>Mod_LoadSpriteFrame.</summary>
    private static SpriteFrame* LoadSpriteFrame(Reader r, List<nint> own)
    {
        SpriteFrame* f = New<SpriteFrame>(own, 1);
        int origin0 = r.Int(), origin1 = r.Int();
        f->Width = r.Int();
        f->Height = r.Int();
        f->Up = origin1;
        f->Down = origin1 - f->Height;
        f->Left = origin0;
        f->Right = f->Width + origin0;
        f->Pixels = Copy(own, r.Bytes(f->Width * f->Height));
        return f;
    }

    /// <summary>Mod_LoadSpriteModel.</summary>
    [NoAllocUnchecked("load path")]
    public static SpriteModel* LoadSprite(ReadOnlySpan<byte> file, string name, Model* mod, List<nint> own)
    {
        var r = new Reader(file, name);
        r.Int();    // ident
        int version = r.Int();
        if (version != SpriteVersion)
            throw new SysErrorException($"{name} has wrong version number ({version} should be {SpriteVersion})");
        SpriteModel* s = New<SpriteModel>(own, 1);
        s->Type = r.Int();
        r.Float();  // boundingradius
        s->MaxWidth = r.Int();
        s->MaxHeight = r.Int();
        s->NumFrames = r.Int();
        s->BeamLength = r.Float();
        r.Int();    // synctype
        if (s->NumFrames < 1)
            throw new SysErrorException($"Mod_LoadSpriteModel: Invalid # of frames: {s->NumFrames}");
        s->Frames = New<SpriteFrameDesc>(own, s->NumFrames);
        for (int i = 0; i < s->NumFrames; i++)
        {
            SpriteFrameDesc* d = s->Frames + i;
            d->Type = r.Int();
            if (d->Type == 0)
            {
                d->Frame = LoadSpriteFrame(r, own);
                continue;
            }
            // Mod_LoadSpriteGroup
            SpriteGroup* g = New<SpriteGroup>(own, 1);
            d->Group = g;
            g->NumFrames = r.Int();
            g->Intervals = New<float>(own, g->NumFrames);
            for (int k = 0; k < g->NumFrames; k++)
            {
                g->Intervals[k] = r.Float();
                if (g->Intervals[k] <= 0.0)
                    throw new SysErrorException("Mod_LoadSpriteGroup: interval<=0");
            }
            g->Frames = (SpriteFrame**)New<nint>(own, g->NumFrames);
            for (int k = 0; k < g->NumFrames; k++)
                g->Frames[k] = LoadSpriteFrame(r, own);
        }
        mod->Extra = s;
        return s;
    }
}
