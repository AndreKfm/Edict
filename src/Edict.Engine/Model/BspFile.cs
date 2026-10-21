// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

// The BSP29 file format (bspfile.h), as it lies in the file. All little-endian; Edict runs on little-endian hosts
// only, so id's LittleLong/LittleShort/LittleFloat are identities and the structs are read in place.

public static class Bsp
{
    public const int Version = 29;
    public const int MaxMapHulls = 4;

    public const int LumpEntities = 0;
    public const int LumpPlanes = 1;
    public const int LumpTextures = 2;
    public const int LumpVertexes = 3;
    public const int LumpVisibility = 4;
    public const int LumpNodes = 5;
    public const int LumpTexinfo = 6;
    public const int LumpFaces = 7;
    public const int LumpLighting = 8;
    public const int LumpClipnodes = 9;
    public const int LumpLeafs = 10;
    public const int LumpMarksurfaces = 11;
    public const int LumpEdges = 12;
    public const int LumpSurfedges = 13;
    public const int LumpModels = 14;
    public const int HeaderLumps = 15;

    public const int ContentsEmpty = -1;
    public const int ContentsSolid = -2;
    public const int ContentsWater = -3;
    public const int ContentsSlime = -4;
    public const int ContentsLava = -5;
    public const int ContentsSky = -6;
}

[StructLayout(LayoutKind.Sequential)]
public struct DLump
{
    public int FileOfs, FileLen;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DHeader
{
    public int Version;
    public fixed int Lumps[Bsp.HeaderLumps * 2];     // lump_t lumps[HEADER_LUMPS]: fileofs, filelen

    public readonly DLump Lump(int i) => new() { FileOfs = Lumps[2 * i], FileLen = Lumps[2 * i + 1] };
}

/// <summary><c>dmodel_t</c>; also the in-memory submodel (model.h uses the file struct).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DModel
{
    public Vec3 Mins, Maxs;
    public Vec3 Origin;
    public fixed int HeadNode[Bsp.MaxMapHulls];
    public int VisLeafs;                              // not including the solid leaf 0
    public int FirstFace, NumFaces;
}

[StructLayout(LayoutKind.Sequential)]
public struct DPlane
{
    public Vec3 Normal;
    public float Dist;
    public int Type;                                  // PLANE_X - PLANE_ANYZ
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DNode
{
    public int PlaneNum;
    public fixed short Children[2];                   // negative numbers are -(leafs+1), not nodes
    public fixed short Mins[3];
    public fixed short Maxs[3];
    public ushort FirstFace;
    public ushort NumFaces;
}

/// <summary><c>dclipnode_t</c>; also the in-memory clip node (model.h uses the file struct).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DClipNode
{
    public int PlaneNum;
    public fixed short Children[2];                   // negative numbers are contents
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DLeaf
{
    public int Contents;
    public int VisOfs;                                // -1 = no visibility info
    public fixed short Mins[3];
    public fixed short Maxs[3];
    public ushort FirstMarkSurface;
    public ushort NumMarkSurfaces;
    public fixed byte AmbientLevel[4];
}
