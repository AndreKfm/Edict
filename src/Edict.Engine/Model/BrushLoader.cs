// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// A loaded BSP: the world model and its inline submodels in one native arena. <c>Models[0]</c> is the world,
/// <c>Models[i]</c> is "*i", as Mod_LoadBrushModel creates them.
/// </summary>
public sealed unsafe class BrushModelSet : IDisposable
{
    private readonly NativeArena _arena;

    internal BrushModelSet(NativeArena arena, Model* models, int count)
    {
        _arena = arena;
        Models = models;
        Count = count;
    }

    public Model* Models { get; }

    public int Count { get; }

    public Model* World => Models;

    public void Dispose() => _arena.Dispose();
}

/// <summary>
/// <c>Mod_LoadBrushModel</c> (model.c) for the lumps the server's collision code reads: planes, visibility, leafs,
/// nodes, clip nodes, submodels, and <c>Mod_MakeHull0</c>. Textures, lighting, faces, edges and marksurfaces come
/// with the renderer. Errors that are Sys_Error in C throw <see cref="InvalidDataException"/>; unlike C, lumps that
/// point outside the file are an error too.
/// </summary>
public static unsafe class BrushLoader
{
    public static BrushModelSet Load(QFile file, ReadOnlySpan<byte> name)
    {
        byte* b = file.Data;
        if (file.Length < sizeof(DHeader))
            throw new InvalidDataException($"{Latin1(name)}: too short for a BSP header");
        DHeader* header = (DHeader*)b;
        if (header->Version != Bsp.Version)
            throw new InvalidDataException(
                $"Mod_LoadBrushModel: {Latin1(name)} has wrong version number ({header->Version} should be {Bsp.Version})");

        var lumps = new Lumps(file, header, name);
        int numPlanes = lumps.Count<DPlane>(Bsp.LumpPlanes);
        int numLeafs = lumps.Count<DLeaf>(Bsp.LumpLeafs);
        int numNodes = lumps.Count<DNode>(Bsp.LumpNodes);
        int numClip = lumps.Count<DClipNode>(Bsp.LumpClipnodes);
        int numSub = lumps.Count<DModel>(Bsp.LumpModels);
        int visLen = header->Lump(Bsp.LumpVisibility).FileLen;
        lumps.Check(Bsp.LumpVisibility);

        nuint size = NativeArena.SizeOf<MPlane>(numPlanes) + NativeArena.SizeOf<byte>(visLen)
            + NativeArena.SizeOf<MLeaf>(numLeafs) + NativeArena.SizeOf<MNode>(numNodes)
            + NativeArena.SizeOf<DClipNode>(numClip) + NativeArena.SizeOf<DModel>(numSub)
            + NativeArena.SizeOf<DClipNode>(numNodes) + NativeArena.SizeOf<Model>(Math.Max(numSub, 1));
        var arena = new NativeArena(size);
        try
        {
            Model* mod = arena.Alloc<Model>(Math.Max(numSub, 1));
            mod->Name.Set(name);
            mod->Type = ModelType.Brush;

            LoadPlanes(mod, arena, (DPlane*)lumps.Data(Bsp.LumpPlanes), numPlanes);
            LoadVisibility(mod, arena, lumps.Data(Bsp.LumpVisibility), visLen);
            LoadLeafs(mod, arena, (DLeaf*)lumps.Data(Bsp.LumpLeafs), numLeafs);
            LoadNodes(mod, arena, (DNode*)lumps.Data(Bsp.LumpNodes), numNodes);
            LoadClipnodes(mod, arena, (DClipNode*)lumps.Data(Bsp.LumpClipnodes), numClip);
            LoadSubmodels(mod, arena, (DModel*)lumps.Data(Bsp.LumpModels), numSub);
            MakeHull0(mod, arena, name);

            // set up the submodels (FIXME: this is confusing)
            Model* first = mod;
            Span<byte> sub = stackalloc byte[12];
            for (int i = 0; i < mod->NumSubmodels; i++)
            {
                DModel* bm = &mod->Submodels[i];

                mod->Hulls[0].FirstClipNode = bm->HeadNode[0];
                for (int j = 1; j < Bsp.MaxMapHulls; j++)
                {
                    mod->Hulls[j].FirstClipNode = bm->HeadNode[j];
                    mod->Hulls[j].LastClipNode = mod->NumClipNodes - 1;
                }

                mod->FirstModelSurface = bm->FirstFace;
                mod->NumModelSurfaces = bm->NumFaces;

                mod->Maxs = bm->Maxs;
                mod->Mins = bm->Mins;
                mod->Radius = RadiusFromBounds(mod->Mins, mod->Maxs);

                mod->NumLeafs = bm->VisLeafs;

                if (i < mod->NumSubmodels - 1)
                {   // duplicate the basic information
                    Model* next = first + i + 1;
                    *next = *mod;
                    sub[0] = (byte)'*';
                    int len = 1 + CFormat.Int(i + 1, sub[1..]);    // sprintf (name, "*%i", i+1)
                    next->Name.Set(sub[..len]);
                    mod = next;
                }
            }
            int count = Math.Max(numSub, 1);
            for (int i = 0; i < count; i++)
                for (int h = 0; h < 3; h++)
                    ValidateHull(first + i, h);
            return new BrushModelSet(arena, first, count);
        }
        catch
        {
            arena.Dispose();
            throw;
        }
    }

    private static string Latin1(ReadOnlySpan<byte> s) => System.Text.Encoding.Latin1.GetString(s);

    private readonly ref struct Lumps(QFile file, DHeader* header, ReadOnlySpan<byte> name)
    {
        private readonly ReadOnlySpan<byte> _name = name;

        public void Check(int lump)
        {
            DLump l = header->Lump(lump);
            if (l.FileOfs < 0 || l.FileLen < 0 || (long)l.FileOfs + l.FileLen > file.Length)
                throw new InvalidDataException($"{Latin1(_name)}: lump {lump} lies outside the file");
        }

        public int Count<T>(int lump) where T : unmanaged
        {
            Check(lump);
            int len = header->Lump(lump).FileLen;
            if (len % sizeof(T) != 0)
                throw new InvalidDataException($"MOD_LoadBmodel: funny lump size in {Latin1(_name)}");
            return len / sizeof(T);
        }

        public byte* Data(int lump) => file.Data + header->Lump(lump).FileOfs;
    }

    private static void LoadPlanes(Model* mod, NativeArena arena, DPlane* input, int count)
    {
        MPlane* output = arena.Alloc<MPlane>(count);
        mod->Planes = output;
        mod->NumPlanes = count;
        for (int i = 0; i < count; i++, input++, output++)
        {
            int bits = 0;
            output->Normal = input->Normal;
            for (int j = 0; j < 3; j++)
                if (Vec3.At(output->Normal, j) < 0)
                    bits |= 1 << j;
            output->Dist = input->Dist;
            output->Type = (byte)input->Type;
            output->SignBits = (byte)bits;
        }
    }

    private static void LoadVisibility(Model* mod, NativeArena arena, byte* input, int length)
    {
        if (length == 0)
        {
            mod->VisData = null;
            return;
        }
        mod->VisData = arena.Alloc<byte>(length);
        mod->VisDataLength = length;
        new ReadOnlySpan<byte>(input, length).CopyTo(new Span<byte>(mod->VisData, length));
    }

    private static void LoadLeafs(Model* mod, NativeArena arena, DLeaf* input, int count)
    {
        MLeaf* output = arena.Alloc<MLeaf>(count);
        mod->Leafs = output;
        mod->NumLeafs = count;
        mod->LeafCount = count;
        for (int i = 0; i < count; i++, input++, output++)
        {
            for (int j = 0; j < 3; j++)
            {
                output->MinMaxs[j] = input->Mins[j];
                output->MinMaxs[3 + j] = input->Maxs[j];
            }
            output->Contents = input->Contents;
            output->FirstMarkSurface = input->FirstMarkSurface;
            output->NumMarkSurfaces = input->NumMarkSurfaces;
            output->VisOfs = input->VisOfs;
            for (int j = 0; j < 4; j++)
                output->AmbientSoundLevel[j] = input->AmbientLevel[j];
        }
    }

    private static void LoadNodes(Model* mod, NativeArena arena, DNode* input, int count)
    {
        MNode* output = arena.Alloc<MNode>(count);
        mod->Nodes = output;
        mod->NumNodes = count;
        for (int i = 0; i < count; i++, input++, output++)
        {
            for (int j = 0; j < 3; j++)
            {
                output->MinMaxs[j] = input->Mins[j];
                output->MinMaxs[3 + j] = input->Maxs[j];
            }
            output->PlaneNum = input->PlaneNum;
            output->FirstSurface = input->FirstFace;
            output->NumSurfaces = input->NumFaces;
            for (int j = 0; j < 2; j++)
                output->Children[j] = input->Children[j];   // node index, or -1 - leaf index
        }
        // Mod_SetParent fills renderer-only parent pointers; it comes with the renderer.
    }

    private static void LoadClipnodes(Model* mod, NativeArena arena, DClipNode* input, int count)
    {
        DClipNode* output = arena.Alloc<DClipNode>(count);
        mod->ClipNodes = output;
        mod->NumClipNodes = count;

        ref Hull hull = ref mod->Hulls[1];
        hull.ClipNodes = output;
        hull.FirstClipNode = 0;
        hull.LastClipNode = count - 1;
        hull.Planes = mod->Planes;
        hull.ClipMins = new Vec3(-16, -16, -24);
        hull.ClipMaxs = new Vec3(16, 16, 32);

        hull = ref mod->Hulls[2];
        hull.ClipNodes = output;
        hull.FirstClipNode = 0;
        hull.LastClipNode = count - 1;
        hull.Planes = mod->Planes;
        hull.ClipMins = new Vec3(-32, -32, -24);
        hull.ClipMaxs = new Vec3(32, 32, 64);

        for (int i = 0; i < count; i++, output++, input++)
        {
            output->PlaneNum = input->PlaneNum;
            output->Children[0] = input->Children[0];
            output->Children[1] = input->Children[1];
        }
    }

    private static void LoadSubmodels(Model* mod, NativeArena arena, DModel* input, int count)
    {
        DModel* output = arena.Alloc<DModel>(count);
        mod->Submodels = output;
        mod->NumSubmodels = count;
        for (int i = 0; i < count; i++, input++, output++)
        {
            // spread the mins / maxs by a pixel
            output->Mins = new Vec3(input->Mins.X - 1, input->Mins.Y - 1, input->Mins.Z - 1);
            output->Maxs = new Vec3(input->Maxs.X + 1, input->Maxs.Y + 1, input->Maxs.Z + 1);
            output->Origin = input->Origin;
            for (int j = 0; j < Bsp.MaxMapHulls; j++)
                output->HeadNode[j] = input->HeadNode[j];
            output->VisLeafs = input->VisLeafs;
            output->FirstFace = input->FirstFace;
            output->NumFaces = input->NumFaces;
        }
    }

    /// <summary>Duplicate the drawing hull structure as a clipping hull.</summary>
    private static void MakeHull0(Model* mod, NativeArena arena, ReadOnlySpan<byte> name)
    {
        ref Hull hull = ref mod->Hulls[0];
        MNode* input = mod->Nodes;
        int count = mod->NumNodes;
        DClipNode* output = arena.Alloc<DClipNode>(count);

        hull.ClipNodes = output;
        hull.FirstClipNode = 0;
        hull.LastClipNode = count - 1;
        hull.Planes = mod->Planes;

        for (int i = 0; i < count; i++, output++, input++)
        {
            output->PlaneNum = input->PlaneNum;
            for (int j = 0; j < 2; j++)
            {
                int child = input->Children[j];
                if (child >= 0)
                {   // a node: C stores child - loadmodel->nodes in a short
                    output->Children[j] = (short)child;
                    continue;
                }
                int leaf = -1 - child;
                if (leaf >= mod->LeafCount || mod->Leafs[leaf].Contents >= 0)
                    throw new InvalidDataException($"{Latin1(name)}: node {i} has a bad leaf child");
                output->Children[j] = (short)mod->Leafs[leaf].Contents;
            }
        }
    }

    /// <summary>
    /// Every node reachable from the hull's first clip node lies in [firstclipnode, lastclipnode] and names an
    /// existing plane. C checks the node range on every visit (Sys_Error "bad node number") and the plane number
    /// never; checking once here lets the trace code run without checks. A map that fails here would fail in C
    /// only when a trace reached the bad node.
    /// </summary>
    private static void ValidateHull(Model* mod, int h)
    {
        ref Hull hull = ref mod->Hulls[h];
        if (hull.FirstClipNode < 0)
            return;
        var visited = new bool[hull.LastClipNode + 1];
        var pending = new Stack<int>();
        pending.Push(hull.FirstClipNode);
        while (pending.Count > 0)
        {
            int num = pending.Pop();
            if (num < 0)
                continue;
            if (num < hull.FirstClipNode || num > hull.LastClipNode)
                throw new InvalidDataException(
                    $"{Latin1(mod->Name.Span)}: hull {h} reaches node {num} outside [{hull.FirstClipNode}, {hull.LastClipNode}] (C: bad node number)");
            if (visited[num])
                continue;
            visited[num] = true;
            DClipNode* node = hull.ClipNodes + num;
            if ((uint)node->PlaneNum >= (uint)mod->NumPlanes)
                throw new InvalidDataException($"{Latin1(mod->Name.Span)}: hull {h} node {num} has plane {node->PlaneNum}");
            pending.Push(node->Children[0]);
            pending.Push(node->Children[1]);
        }
    }

    /// <summary><c>RadiusFromBounds</c>: <c>fabs</c> works in double (exact), Length in float.</summary>
    private static float RadiusFromBounds(in Vec3 mins, in Vec3 maxs)
    {
        Vec3 corner = default;
        for (int i = 0; i < 3; i++)
        {
            double lo = Math.Abs((double)Vec3.At(mins, i)), hi = Math.Abs((double)Vec3.At(maxs, i));
            Unsafe.Add(ref corner.X, i) = (float)(lo > hi ? lo : hi);
        }
        return Mathlib.Length(corner);
    }
}
