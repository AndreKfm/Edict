// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Edict.Engine;

// The in-memory brush model (model.h), restricted to what the server uses so far: collision (planes, nodes,
// leafs, clip nodes, hulls) and submodels. Lives in native memory (NativeArena); cross references are pointers
// into the same arena, as in C, or indices where C used a pointer that can point to two kinds of object.

/// <summary><c>mplane_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MPlane
{
    public Vec3 Normal;
    public float Dist;
    public byte Type;                                 // for texture axis selection and fast side tests
    public byte SignBits;                             // signx + signy<<1 + signz<<2
    public byte Pad0, Pad1;
}

/// <summary>
/// <c>mnode_t</c>. C's children are pointers to either a node or a leaf; here they are encoded as in the file:
/// a node index, or <c>-1 - leaf index</c>. Renderer fields (visframe, parent) come with the renderer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MNode
{
    public int Contents;                              // 0, to differentiate from leafs
    public fixed short MinMaxs[6];                    // for bounding box culling
    public int PlaneNum;
    public fixed int Children[2];
    public ushort FirstSurface;
    public ushort NumSurfaces;
}

/// <summary><c>mleaf_t</c>. The visibility pointer is an offset into the model's visibility data.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MLeaf
{
    public int Contents;                              // a negative contents number
    public fixed short MinMaxs[6];
    public int VisOfs;                                // -1: no visibility data (C: compressed_vis == NULL)
    public ushort FirstMarkSurface;
    public ushort NumMarkSurfaces;
    public fixed byte AmbientSoundLevel[4];
}

/// <summary>
/// A clip node together with its plane, in one 32-byte record: everything one visit of
/// <c>SV_RecursiveHullCheck</c> or <c>SV_HullPointContents</c> reads, so that a visit touches one cache line (two
/// nodes per line, none straddling) instead of a <c>dclipnode_t</c> and, dependent on it, an <c>mplane_t</c>.
/// Built from the two arrays when the model is loaded; the layout of the default trace variant (docs/PLAN.md
/// section B.4), not a change of any value. Normal and Dist are 16-byte aligned (the arena is 64-aligned).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public unsafe struct PackedNode
{
    public Vec3 Normal;                               // mplane_t.normal
    public float Dist;                                // mplane_t.dist
    public int Type;                                  // mplane_t.type (an int: compared as one, no byte extension)
    public fixed short Children[2];                   // dclipnode_t.children; negative numbers are contents
}

/// <summary><c>hull_t</c>, plus the packed copy of the clip nodes (<see cref="PackedNode"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Hull
{
    public DClipNode* ClipNodes;
    public MPlane* Planes;
    public PackedNode* Packed;                        // the same nodes as ClipNodes, with their planes
    public int FirstClipNode;
    public int LastClipNode;
    public Vec3 ClipMins;
    public Vec3 ClipMaxs;
}

[InlineArray(Bsp.MaxMapHulls)]
public struct HullArray
{
    private Hull _element0;
}

/// <summary><c>char name[MAX_QPATH]</c>.</summary>
[InlineArray(Length)]
public struct ModelName
{
    public const int Length = 64;

    private byte _element0;

    /// <summary>The name up to its terminating zero.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> Span
    {
        get
        {
            ReadOnlySpan<byte> all = this;
            int n = all.IndexOf((byte)0);
            return n < 0 ? all : all[..n];
        }
    }

    /// <summary><c>strcpy</c> into the buffer; longer names are cut to fit, with the terminating zero.</summary>
    public void Set(ReadOnlySpan<byte> name)
    {
        Span<byte> all = this;
        all.Clear();
        name[..Math.Min(name.Length, Length - 1)].CopyTo(all);
    }
}

public enum ModelType
{
    Brush,
    Sprite,
    Alias,
}

/// <summary>
/// <c>model_t</c>, brush part. As in C, the world and its inline submodels ("*1", "*2", ...) are separate model_t
/// values that share the arrays and differ in hulls, bounds, radius and visleafs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Model
{
    public ModelName Name;
    public ModelType Type;
    public Vec3 Mins, Maxs;
    public float Radius;

    public int FirstModelSurface, NumModelSurfaces;

    public int NumSubmodels;
    public DModel* Submodels;                         // bounds spread by one unit, as Mod_LoadSubmodels does

    public int NumPlanes;
    public MPlane* Planes;

    /// <summary>The submodel's visleafs, as in C (Mod_LoadBrushModel overwrites the lump count with it).</summary>
    public int NumLeafs;
    public MLeaf* Leafs;
    /// <summary>Leafs in the lump; C keeps no copy of this count after loading.</summary>
    public int LeafCount;

    public int NumNodes;
    public MNode* Nodes;

    public int NumClipNodes;
    public DClipNode* ClipNodes;

    public HullArray Hulls;

    public int VisDataLength;
    public byte* VisData;

    /// <summary>
    /// The entity lump (Mod_LoadEntities), zero-terminated; null if the lump is empty. C copies the lump into the
    /// hunk without adding a terminator and relies on the one the map compiler writes (and on the hunk's zeroed
    /// rounding); the port adds one.
    /// </summary>
    public byte* Entities;
}
