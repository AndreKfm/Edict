// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

// ---- bspfile.h: the lumps the renderer reads

/// <summary><c>dedge_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DEdge
{
    public fixed ushort V[2];
}

/// <summary><c>texinfo_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DTexInfo
{
    public fixed float Vecs[8];                       // [s/t][xyz offset]
    public int MipTex;
    public int Flags;
}

/// <summary><c>dface_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DFace
{
    public short PlaneNum;
    public short Side;
    public int FirstEdge;                             // we must support > 64k edges
    public short NumEdges;
    public short TexInfo;
    public fixed byte Styles[4];                      // lighting info
    public int LightOfs;                              // start of [numstyles*surfsize] samples
}

/// <summary><c>miptex_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DMipTex
{
    public fixed byte Name[16];
    public uint Width, Height;
    public fixed uint Offsets[4];                     // four mip maps stored
}

// ---- model.h: the renderer's in-memory brush data

/// <summary><c>medge_t</c>: two vertex indices, and the renderer's cache of the edge it emitted for this frame.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MEdge
{
    public fixed ushort V[2];
    public uint CachedEdgeOffset;
}

/// <summary>
/// <c>texture_t</c>. C stores the pixels right behind the struct and its offsets count from the struct; here
/// <see cref="Pixels"/> points to the first mip level and <see cref="MipOffsets"/> count from there.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Texture
{
    public fixed byte Name[16];
    public uint Width, Height;                        // unsigned, as in C
    public int AnimTotal;                             // total tenths in sequence (0 = no)
    public int AnimMin, AnimMax;                      // time for this frame min <=time< max
    public Texture* AnimNext;                         // in the animation sequence
    public Texture* AlternateAnims;                   // bmodels in frame 1 use these
    public fixed int MipOffsets[4];                   // four mip maps, from Pixels
    public byte* Pixels;
}

/// <summary><c>mtexinfo_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MTexInfo
{
    public fixed float Vecs[8];                       // vecs[2][4]
    public float MipAdjust;
    public Texture* Texture;
    public int Flags;
}

/// <summary><c>msurface_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MSurface
{
    public int VisFrame;                              // should be drawn when node is crossed
    public int DLightFrame;
    public int DLightBits;
    public MPlane* Plane;
    public int Flags;
    public int FirstEdge;                             // look up in model->surfedges[], negative numbers
    public int NumEdges;                              // are backwards edges
    public fixed long CacheSpots[4];                  // surfcache_t* per mip level (the surface cache's offsets, see D_SCAlloc)
    public fixed short TextureMins[2];
    public fixed short Extents[2];
    public MTexInfo* TexInfo;
    public fixed byte Styles[4];                      // lighting info
    public byte* Samples;                             // [numstyles*surfsize]
}

/// <summary>model.h's surface flags and bspfile.h's texinfo flags.</summary>
public static class SurfFlags
{
    public const int PlaneBack = 2, DrawSky = 4, DrawSprite = 8, DrawTurb = 0x10, DrawTiled = 0x20,
        DrawBackground = 0x40;
    public const int TexSpecial = 1;                  // sky or slime, no lightmap or 256 subdivision
    public const int MaxLightmaps = 4, MipLevels = 4;
}
