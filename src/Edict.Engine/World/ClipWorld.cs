// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>A Sys_Error in the reference: the server cannot continue.</summary>
public sealed class SysErrorException(string message) : Exception(message);

/// <summary>
/// The edict fields <c>SV_HullForEntity</c> reads (progdefs.h; QuakeC floats), plus the edict's index for
/// <c>trace.ent</c>. Phase 2 replaces this with the edict itself.
/// </summary>
public struct ClipEntity
{
    public int Index;
    public float Solid, MoveType, ModelIndex;
    public Vec3 Origin, Mins, Maxs;
}

/// <summary><c>SOLID_*</c> and <c>MOVETYPE_*</c> (server.h), as the float values QuakeC stores.</summary>
public static class Solid
{
    public const float Not = 0, Trigger = 1, BBox = 2, SlideBox = 3, Bsp = 4;
}

public static class MoveType
{
    public const float None = 0, Walk = 3, Step = 4, Fly = 5, Toss = 6, Push = 7, NoClip = 8, FlyMissile = 9, Bounce = 10;
}

/// <summary>A variant of <c>SV_RecursiveHullCheck</c>, chosen at compile time (docs/PLAN.md section B.4).</summary>
public unsafe interface IHullCheck
{
    [NoAlloc(Constraints.All)]
    static abstract bool Check(Hull* hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, ref Trace trace);
}

public readonly unsafe struct RecursiveCheck : IHullCheck
{
    [NoAlloc(Constraints.All)]
    public static bool Check(Hull* hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, ref Trace trace) =>
        HullTrace.RecursiveHullCheck(hull, num, p1f, p2f, p1, p2, ref trace);
}

public readonly unsafe struct IterativeCheck : IHullCheck
{
    [NoAlloc(Constraints.All)]
    public static bool Check(Hull* hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, ref Trace trace) =>
        HullTrace.IterativeHullCheck(hull, num, p1f, p2f, p1, p2, ref trace);
}

/// <summary>
/// The parts of world.c that phase 1 ports: hull boxes (<c>SV_InitBoxHull</c>, <c>SV_HullForBox</c>),
/// <c>SV_HullForEntity</c> and <c>SV_ClipMoveToEntity</c>, over a model table that plays <c>sv.models</c>. C keeps
/// the box hull in statics; here it is per instance, in native memory.
/// </summary>
public sealed unsafe class ClipWorld : IDisposable
{
    public const int MaxModels = 256;

    private const string SysErrorReason = "Sys_Error conditions of SV_HullForEntity (game state), fatal for the server as in C";

    private static readonly SysErrorException SolidBspWithoutPush = new("SOLID_BSP without MOVETYPE_PUSH");
    private static readonly SysErrorException PushWithNonBspModel = new("MOVETYPE_PUSH with a non bsp model");

    [InlineArray(6)]
    private struct ClipNodes6
    {
        private DClipNode _element0;
    }

    [InlineArray(6)]
    private struct Planes6
    {
        private MPlane _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BoxHull
    {
        public Hull Hull;
        public ClipNodes6 ClipNodes;
        public Planes6 Planes;
    }

    private BoxHull* _box;
    private Model** _models;

    public ClipWorld()
    {
        _box = (BoxHull*)NativeMemory.AlignedAlloc((nuint)sizeof(BoxHull), 64);
        NativeMemory.Clear(_box, (nuint)sizeof(BoxHull));
        _models = (Model**)NativeMemory.AllocZeroed(MaxModels, (nuint)sizeof(Model*));
        InitBoxHull();
    }

    /// <summary><c>sv.models</c>: index to model, null where nothing is precached.</summary>
    public Model** Models => _models;

    /// <summary>
    /// <c>SV_InitBoxHull</c>: set up the planes and clipnodes so that the six floats of a bounding box can just be
    /// stored out and get a proper hull_t structure.
    /// </summary>
    private void InitBoxHull()
    {
        _box->Hull.ClipNodes = (DClipNode*)Unsafe.AsPointer(ref _box->ClipNodes[0]);
        _box->Hull.Planes = (MPlane*)Unsafe.AsPointer(ref _box->Planes[0]);
        _box->Hull.FirstClipNode = 0;
        _box->Hull.LastClipNode = 5;

        for (int i = 0; i < 6; i++)
        {
            ref DClipNode node = ref _box->ClipNodes[i];
            node.PlaneNum = i;

            int side = i & 1;

            node.Children[side] = Bsp.ContentsEmpty;
            if (i != 5)
                node.Children[side ^ 1] = (short)(i + 1);
            else
                node.Children[side ^ 1] = Bsp.ContentsSolid;

            ref MPlane plane = ref _box->Planes[i];
            plane.Type = (byte)(i >> 1);
            Unsafe.Add(ref plane.Normal.X, i >> 1) = 1;
        }
    }

    /// <summary>
    /// <c>SV_HullForBox</c>: to keep everything totally uniform, bounding boxes are turned into small BSP trees
    /// instead of being compared directly.
    /// </summary>
    [NoAlloc(Constraints.All)]
    public Hull* HullForBox(in Vec3 mins, in Vec3 maxs)
    {
        _box->Planes[0].Dist = maxs.X;
        _box->Planes[1].Dist = mins.X;
        _box->Planes[2].Dist = maxs.Y;
        _box->Planes[3].Dist = mins.Y;
        _box->Planes[4].Dist = maxs.Z;
        _box->Planes[5].Dist = mins.Z;

        return &_box->Hull;
    }

    /// <summary>
    /// <c>SV_HullForEntity</c>: a hull that can be used for testing or clipping an object of mins/maxs size.
    /// <paramref name="offset"/> is the adjustment that must be added to the testing object's origin to get a
    /// point to use with the returned hull. A model index outside <c>sv.models</c> counts as "no model" (C reads
    /// out of bounds).
    /// </summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = SysErrorReason)]
    public Hull* HullForEntity(in ClipEntity ent, in Vec3 mins, in Vec3 maxs, out Vec3 offset)
    {
        Hull* hull;

        // decide which clipping hull to use, based on the size
        if (ent.Solid == Solid.Bsp)
        {   // explicit hulls in the BSP model
            if (ent.MoveType != MoveType.Push)
                throw SolidBspWithoutPush;

            int index = CCast.I32(ent.ModelIndex);
            Model* model = (uint)index < MaxModels ? _models[index] : null;

            if (model is null || model->Type != ModelType.Brush)
                throw PushWithNonBspModel;

            Vec3 size = Vec3.Subtract(maxs, mins);
            if (size.X < 3)
                hull = (Hull*)Unsafe.AsPointer(ref model->Hulls[0]);
            else if (size.X <= 32)
                hull = (Hull*)Unsafe.AsPointer(ref model->Hulls[1]);
            else
                hull = (Hull*)Unsafe.AsPointer(ref model->Hulls[2]);

            // calculate an offset value to center the origin
            offset = Vec3.Subtract(hull->ClipMins, mins);
            offset = Vec3.Add(offset, ent.Origin);
        }
        else
        {   // create a temp hull from bounding box sizes
            Vec3 hullmins = Vec3.Subtract(ent.Mins, maxs);
            Vec3 hullmaxs = Vec3.Subtract(ent.Maxs, mins);
            hull = HullForBox(hullmins, hullmaxs);

            offset = ent.Origin;
        }

        return hull;
    }

    /// <summary><c>SV_ClipMoveToEntity</c> with the default hull check.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = SysErrorReason)]
    public Trace ClipMoveToEntity(in ClipEntity ent, in Vec3 start, in Vec3 mins, in Vec3 maxs, in Vec3 end) =>
        ClipMoveToEntity<RecursiveCheck>(ent, start, mins, maxs, end);

    /// <summary>
    /// <c>SV_ClipMoveToEntity</c>: handles selection or creation of a clipping hull, and offseting (and eventually
    /// rotation) of the end points. The rotation is QUAKE2-only in C and not compiled.
    /// </summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = SysErrorReason)]
    public Trace ClipMoveToEntity<TCheck>(in ClipEntity ent, in Vec3 start, in Vec3 mins, in Vec3 maxs, in Vec3 end)
        where TCheck : struct, IHullCheck
    {
        // fill in a default trace
        Trace trace = default;
        trace.Fraction = 1;
        trace.AllSolid = true;
        trace.EndPos = end;
        trace.Ent = -1;

        // get the clipping hull
        Hull* hull = HullForEntity(ent, mins, maxs, out Vec3 offset);

        Vec3 startL = Vec3.Subtract(start, offset);
        Vec3 endL = Vec3.Subtract(end, offset);

        // trace a line through the apropriate clipping hull
        TCheck.Check(hull, hull->FirstClipNode, 0, 1, startL, endL, ref trace);

        // fix trace up by the offset
        if (trace.Fraction != 1)
            trace.EndPos = Vec3.Add(trace.EndPos, offset);

        // did we clip the move?
        if (trace.Fraction < 1 || trace.StartSolid)
            trace.Ent = ent.Index;

        return trace;
    }

    public void Dispose()
    {
        if (_box is not null)
        {
            NativeMemory.AlignedFree(_box);
            _box = null;
        }
        if (_models is not null)
        {
            NativeMemory.Free(_models);
            _models = null;
        }
    }
}
