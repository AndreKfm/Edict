// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// A face into the edge list (r_draw.c): R_RenderFace clips its edges to the view's planes (R_ClipEdge), projects
/// them (R_EmitEdge) or reuses the edge it emitted this frame for the neighbouring face (R_EmitCachedEdge), and
/// posts the surface with its 1/z gradients. The model edges' cachededgeoffset holds an edge index here: C's byte
/// offset over sizeof(edge_t), which orders and compares the same.
/// </summary>
public sealed unsafe partial class Screen
{
    private const uint FullyClippedCached = 0x80000000, FramecountMask = 0x7FFFFFFF;

    private uint _cacheOffset;                        // cacheoffset
    private MEdge* _pedge;                            // r_pedge
    private MEdge* _tedge;                            // R_RenderFace's tedge: a dummy for the caching to write to
    private bool _leftClipped, _rightClipped, _makeLeftEdge, _makeRightEdge, _nearZiOnly;
    private Vec3* _leftEnter, _leftExit, _rightEnter, _rightExit;   // r_leftenter, r_leftexit, ...
    private int[] _clipNext = null!;                  // view_clipplanes[i].next: a plane index, -1 for NULL
    private bool _emitted;                            // r_emitted
    private float _nearZi;                            // r_nearzi
    private float _u1, _v1, _lzi1;                    // r_u1, r_v1, r_lzi1
    private int _ceilv1;                              // r_ceilv1
    private bool _lastVertValid;                      // r_lastvertvalid
    private Vec3* _currentVertBase;                   // r_pcurrentvertbase

    [NoAllocUnchecked("load path")]
    private void InitFaces()
    {
        _tedge = (MEdge*)NativeMemory.AllocZeroed((nuint)sizeof(MEdge));
        Vec3* v = (Vec3*)NativeMemory.AllocZeroed(4, (nuint)sizeof(Vec3));
        _leftEnter = v;
        _leftExit = v + 1;
        _rightEnter = v + 2;
        _rightExit = v + 3;
        _clipNext = new int[4];
    }

    /// <summary>R_EmitEdge: the edge projected and, unless horizontal, sorted into the scan it starts on.</summary>
    private void EmitEdge(Vec3* pv0, Vec3* pv1)
    {
        float u0, v0, lzi0, scale;
        int ceilv0;
        if (_lastVertValid)
        {
            u0 = _u1;
            v0 = _v1;
            lzi0 = _lzi1;
            ceilv0 = _ceilv1;
        }
        else
        {
            // transform and project
            Vec3 transformed = TransformVector(Vec3.Subtract(*pv0, _modelOrg));
            if (transformed.Z < NearClip)
                transformed.Z = (float)NearClip;
            lzi0 = (float)(1.0 / transformed.Z);
            // FIXME: build x/yscale into transform?
            scale = _xscale * lzi0;
            u0 = _xcenter + scale * transformed.X;
            if (u0 < _fvrectxAdj)
                u0 = _fvrectxAdj;
            if (u0 > _fvrectrightAdj)
                u0 = _fvrectrightAdj;
            scale = _yscale * lzi0;
            v0 = _ycenter - scale * transformed.Y;
            if (v0 < _fvrectyAdj)
                v0 = _fvrectyAdj;
            if (v0 > _fvrectbottomAdj)
                v0 = _fvrectbottomAdj;
            ceilv0 = CCast.I32(Math.Ceiling(v0));
        }

        // transform and project
        Vec3 t1 = TransformVector(Vec3.Subtract(*pv1, _modelOrg));
        if (t1.Z < NearClip)
            t1.Z = (float)NearClip;
        _lzi1 = (float)(1.0 / t1.Z);
        scale = _xscale * _lzi1;
        _u1 = _xcenter + scale * t1.X;
        if (_u1 < _fvrectxAdj)
            _u1 = _fvrectxAdj;
        if (_u1 > _fvrectrightAdj)
            _u1 = _fvrectrightAdj;
        scale = _yscale * _lzi1;
        _v1 = _ycenter - scale * t1.Y;
        if (_v1 < _fvrectyAdj)
            _v1 = _fvrectyAdj;
        if (_v1 > _fvrectbottomAdj)
            _v1 = _fvrectbottomAdj;

        if (_lzi1 > lzi0)
            lzi0 = _lzi1;
        if (lzi0 > _nearZi)     // for mipmap finding
            _nearZi = lzi0;
        // for right edges, all we want is the effect on 1/z
        if (_nearZiOnly)
            return;
        _emitted = true;
        _ceilv1 = CCast.I32(Math.Ceiling(_v1));

        // create the edge
        if (ceilv0 == _ceilv1)
        {
            // we cache unclipped horizontal edges as fully clipped
            if (_cacheOffset != 0x7FFFFFFF)
                _cacheOffset = FullyClippedCached | ((uint)_frameCount & FramecountMask);
            return;     // horizontal edge
        }

        bool side = ceilv0 > _ceilv1;
        int edgeIndex = _edgeP++;
        ref REdge edge = ref _edges[edgeIndex];
        edge.Owner = _pedge;
        edge.NearZi = lzi0;
        int v, v2;
        float u, uStep;
        if (!side)
        {
            // trailing edge (go from p1 to p2)
            v = ceilv0;
            v2 = _ceilv1 - 1;
            edge.Surf0 = (ushort)_surfaceP;
            edge.Surf1 = 0;
            uStep = (_u1 - u0) / (_v1 - v0);
            u = u0 + ((float)v - v0) * uStep;
        }
        else
        {
            // leading edge (go from p2 to p1)
            v2 = ceilv0 - 1;
            v = _ceilv1;
            edge.Surf0 = 0;
            edge.Surf1 = (ushort)_surfaceP;
            uStep = (u0 - _u1) / (v0 - _v1);
            u = _u1 + ((float)v - _v1) * uStep;
        }
        edge.UStep = CCast.I32(uStep * 0x100000);
        edge.U = CCast.I32(u * 0x100000 + 0xFFFFF);

        // we need to do this to avoid stepping off the edges if a very nearly horizontal edge is less than epsilon
        // above a scan, and numeric error causes it to incorrectly extend to the scan, and the extension of the
        // line goes off the edge of the screen
        if (edge.U < _vrectXAdjShift20)
            edge.U = _vrectXAdjShift20;
        if (edge.U > _vrectRightAdjShift20)
            edge.U = _vrectRightAdjShift20;

        // sort the edge in normally
        int uCheck = edge.U;
        if (edge.Surf0 != 0)
            uCheck++;   // sort trailers after leaders
        int head = _newEdges[v];
        if (head < 0 || _edges[head].U >= uCheck)
        {
            edge.Next = head;
            _newEdges[v] = edgeIndex;
        }
        else
        {
            int pcheck = head;
            while (_edges[pcheck].Next >= 0 && _edges[_edges[pcheck].Next].U < uCheck)
                pcheck = _edges[pcheck].Next;
            edge.Next = _edges[pcheck].Next;
            _edges[pcheck].Next = edgeIndex;
        }
        edge.NextRemove = _removeEdges[v2];
        _removeEdges[v2] = edgeIndex;
    }

    /// <summary>R_ClipEdge: the edge clipped against the planes from <paramref name="clip"/> on, then emitted.</summary>
    private void ClipEdge(Vec3* pv0, Vec3* pv1, int clip)
    {
        while (clip >= 0)
        {
            Vec3 normal = _clipNormal[clip];
            float dist = _clipDist[clip];
            float d0 = Vec3.Dot(*pv0, normal) - dist;
            float d1 = Vec3.Dot(*pv1, normal) - dist;
            if (d0 >= 0)
            {
                // point 0 is unclipped
                if (d1 >= 0)
                {
                    // both points are unclipped
                    clip = _clipNext[clip];
                    continue;
                }
                // only point 1 is clipped; we don't cache clipped edges
                _cacheOffset = 0x7FFFFFFF;
                Vec3 clipvert = Intersect(pv0, pv1, d0 / (d0 - d1));
                if (clip == 0)
                {
                    _leftClipped = true;
                    *_leftExit = clipvert;
                }
                else if (clip == 1)
                {
                    _rightClipped = true;
                    *_rightExit = clipvert;
                }
                ClipEdge(pv0, &clipvert, _clipNext[clip]);
                return;
            }
            // point 0 is clipped
            if (d1 < 0)
            {
                // both points are clipped; we do cache fully clipped edges
                if (!_leftClipped)
                    _cacheOffset = FullyClippedCached | ((uint)_frameCount & FramecountMask);
                return;
            }
            // only point 0 is clipped
            _lastVertValid = false;
            // we don't cache partially clipped edges
            _cacheOffset = 0x7FFFFFFF;
            Vec3 enter = Intersect(pv0, pv1, d0 / (d0 - d1));
            if (clip == 0)
            {
                _leftClipped = true;
                *_leftEnter = enter;
            }
            else if (clip == 1)
            {
                _rightClipped = true;
                *_rightEnter = enter;
            }
            ClipEdge(&enter, pv1, _clipNext[clip]);
            return;
        }
        // add the edge
        EmitEdge(pv0, pv1);

        static Vec3 Intersect(Vec3* a, Vec3* b, float f) => new(
            a->X + f * (b->X - a->X),
            a->Y + f * (b->Y - a->Y),
            a->Z + f * (b->Z - a->Z));
    }

    /// <summary>R_EmitCachedEdge: the edge this frame emitted for the neighbouring face, given this surface too.</summary>
    private void EmitCachedEdge()
    {
        ref REdge e = ref _edges[(int)_pedge->CachedEdgeOffset];
        if (e.Surf0 == 0)
            e.Surf0 = (ushort)_surfaceP;
        else
            e.Surf1 = (ushort)_surfaceP;
        if (e.NearZi > _nearZi)     // for mipmap finding
            _nearZi = e.NearZi;
        _emitted = true;
    }

    /// <summary>One of the face's edges, from v[a] to v[b]: reused when cached, else clipped and emitted (R_RenderFace).</summary>
    private void FaceEdge(MEdge* pedge, int a, int b, int pclip)
    {
        _pedge = pedge;
        // if the edge is cached, we can just reuse the edge
        if (!_inSubmodel)
        {
            uint offset = pedge->CachedEdgeOffset;
            if ((offset & FullyClippedCached) != 0)
            {
                if ((offset & FramecountMask) == (uint)_frameCount)
                {
                    _lastVertValid = false;
                    return;
                }
            }
            else if ((uint)_edgeP > offset && _edges[(int)offset].Owner == pedge)
            {
                EmitCachedEdge();
                _lastVertValid = false;
                return;
            }
        }
        // assume it's cacheable
        _cacheOffset = (uint)_edgeP;
        _leftClipped = _rightClipped = false;
        ClipEdge(_currentVertBase + pedge->V[a], _currentVertBase + pedge->V[b], pclip);
        pedge->CachedEdgeOffset = _cacheOffset;
        if (_leftClipped)
            _makeLeftEdge = true;
        if (_rightClipped)
            _makeRightEdge = true;
        _lastVertValid = true;
    }

    /// <summary>R_RenderFace: the face's edges into the edge list, and the face as a surface with its gradients.</summary>
    private void RenderFace(MSurface* fa, int clipflags)
    {
        // skip out if no more surfs
        if (_surfaceP >= _surfMax)
        {
            _outOfSurfaces++;
            return;
        }
        // ditto if not enough edges left, or switch to auxedges if possible
        if (_edgeP + fa->NumEdges + 4 >= _edgeMax)
        {
            _outOfEdges += fa->NumEdges;
            return;
        }
        _faceClip++;

        // set up clip planes
        int pclip = -1;
        for (int i = 3, mask = 0x08; i >= 0; i--, mask >>= 1)
        {
            if ((clipflags & mask) != 0)
            {
                _clipNext[i] = pclip;
                pclip = i;
            }
        }

        // push the edges through
        _emitted = false;
        _nearZi = 0;
        _nearZiOnly = false;
        _makeLeftEdge = _makeRightEdge = false;
        Model* model = _currentModel;       // currententity->model
        MEdge* pedges = model->Edges;
        _lastVertValid = false;
        for (int i = 0; i < fa->NumEdges; i++)
        {
            int lindex = model->Surfedges[fa->FirstEdge + i];
            if (lindex > 0)
                FaceEdge(pedges + lindex, 0, 1, pclip);
            else
                FaceEdge(pedges - lindex, 1, 0, pclip);
        }

        // if there was a clip off the left edge, add that edge too
        if (_makeLeftEdge)
        {
            _pedge = _tedge;
            _lastVertValid = false;
            ClipEdge(_leftExit, _leftEnter, _clipNext[pclip]);
        }
        // if there was a clip off the right edge, get the right r_nearzi
        if (_makeRightEdge)
        {
            _pedge = _tedge;
            _lastVertValid = false;
            _nearZiOnly = true;
            ClipEdge(_rightExit, _rightEnter, _clipNext[1]);
        }

        // if no edges made it out, return without posting the surface
        if (!_emitted)
            return;
        _polyCount++;

        ref RSurf s = ref _surfaces[_surfaceP];
        s.Data = fa;
        s.NearZi = _nearZi;
        s.Flags = fa->Flags;
        s.InSubmodel = _inSubmodel;
        s.SpanState = 0;
        s.Entity = _currentEntity;
        s.Key = _currentKey++;
        s.Spans = -1;
        PostGradients(ref s, fa->Plane);
        _surfaceP++;
    }

    /// <summary>The surface's 1/z gradients on the screen from its plane (R_RenderFace's end).</summary>
    private void PostGradients(ref RSurf s, MPlane* pplane)
    {
        Vec3 pNormal = TransformVector(pplane->Normal);
        float distinv = (float)(1.0 / (pplane->Dist - Vec3.Dot(_modelOrg, pplane->Normal)));
        s.DZiStepU = pNormal.X * _xscaleinv * distinv;
        s.DZiStepV = -pNormal.Y * _yscaleinv * distinv;
        s.DZiOrigin = pNormal.Z * distinv - _xcenter * s.DZiStepU - _ycenter * s.DZiStepV;
    }
}
