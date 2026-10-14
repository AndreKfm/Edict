// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The brush entities (r_main.c's R_DrawBEntitiesOnList, r_bsp.c, r_draw.c's R_RenderBmodelFace, r_efrag.c's
/// R_SplitEntityOnNode2): each visible one rotated into the view, its dynamic lights marked, and its faces into the
/// edge list — sorted by 1/z against each other when it lies in one leaf, else clipped to the world's BSP and keyed
/// by the leafs its pieces fall into.
/// </summary>
public sealed unsafe partial class Screen
{
    private const int BmodelFullyClipped = 0x10;      // BMODEL_FULLY_CLIPPED
    private const int MaxBmodelVerts = 500, MaxBmodelEdges = 1000;

    /// <summary><c>bedge_t</c>.</summary>
    private struct BEdge
    {
        public Vec3* V0, V1;
        public BEdge* Next;
    }

    private bool _inSubmodel;                         // insubmodel
    private EntityRef _currentEntity;                 // currententity
    private Model* _currentModel;                     // currententity->model
    private Vec3 _entOrigin;                          // r_entorigin
    private float* _entityRotation;                   // entity_rotation[3][3]
    private float* _rotTemp;                          // R_RotateBmodel's temp1, temp2, temp3
    private int _clipFlags;                           // r_clipflags
    private int _currentBKey;                         // r_currentbkey
    private Vec3* _bverts;                            // R_DrawSolidClippedSubmodelPolygons' bverts[MAX_BMODEL_VERTS]
    private BEdge* _bedges;                           // and bedges[MAX_BMODEL_EDGES]
    private int _numBVerts, _numBEdges;
    private Vec3* _frontEnter, _frontExit;            // pfrontenter, pfrontexit
    private bool _makeClippedEdge;
    private int _pefragTopNode;                       // r_pefragtopnode: a node, -1 - leaf, or NoTopNode
    private Vec3 _emins, _emaxs;                      // r_emins, r_emaxs

    private const int NoTopNode = int.MinValue;

    [NoAllocUnchecked("load path")]
    private void InitBModels()
    {
        _entityRotation = (float*)NativeMemory.AllocZeroed(9, sizeof(float));
        _rotTemp = (float*)NativeMemory.AllocZeroed(27, sizeof(float));
        _bverts = (Vec3*)NativeMemory.AllocZeroed(MaxBmodelVerts, (nuint)sizeof(Vec3));
        _bedges = (BEdge*)NativeMemory.AllocZeroed(MaxBmodelEdges, (nuint)sizeof(BEdge));
    }

    private static readonly SysErrorException NoBmodelEdges = new("no edges in bmodel");

    /// <summary>R_ConcatRotations (mathlib.c): out = in1 * in2, 3x3.</summary>
    private static void ConcatRotations(float* in1, float* in2, float* o)
    {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                o[i * 3 + j] = in1[i * 3 + 0] * in2[0 * 3 + j] + in1[i * 3 + 1] * in2[1 * 3 + j] + in1[i * 3 + 2] * in2[2 * 3 + j];
    }

    /// <summary>R_EntityRotate.</summary>
    private Vec3 EntityRotate(in Vec3 tvec)
    {
        float* r = _entityRotation;
        return new Vec3(
            r[0] * tvec.X + r[1] * tvec.Y + r[2] * tvec.Z,
            r[3] * tvec.X + r[4] * tvec.Y + r[5] * tvec.Z,
            r[6] * tvec.X + r[7] * tvec.Y + r[8] * tvec.Z);
    }

    /// <summary>R_RotateBmodel: the entity's rotation (yaw, pitch, roll) applied to modelorg and the view's axes.</summary>
    private void RotateBmodel()
    {
        ref ClientEntity e = ref Entity(_currentEntity);
        float* temp1 = _rotTemp, temp2 = _rotTemp + 9, temp3 = _rotTemp + 18;

        // yaw
        float angle = e.Angles.Y;
        angle = (float)(angle * Math.PI * 2 / 360);
        float s = (float)CMath.Sin(angle), c = (float)CMath.Cos(angle);
        temp1[0] = c; temp1[1] = s; temp1[2] = 0;
        temp1[3] = -s; temp1[4] = c; temp1[5] = 0;
        temp1[6] = 0; temp1[7] = 0; temp1[8] = 1;

        // pitch
        angle = e.Angles.X;
        angle = (float)(angle * Math.PI * 2 / 360);
        s = (float)CMath.Sin(angle);
        c = (float)CMath.Cos(angle);
        temp2[0] = c; temp2[1] = 0; temp2[2] = -s;
        temp2[3] = 0; temp2[4] = 1; temp2[5] = 0;
        temp2[6] = s; temp2[7] = 0; temp2[8] = c;
        ConcatRotations(temp2, temp1, temp3);

        // roll
        angle = e.Angles.Z;
        angle = (float)(angle * Math.PI * 2 / 360);
        s = (float)CMath.Sin(angle);
        c = (float)CMath.Cos(angle);
        temp1[0] = 1; temp1[1] = 0; temp1[2] = 0;
        temp1[3] = 0; temp1[4] = c; temp1[5] = s;
        temp1[6] = 0; temp1[7] = -s; temp1[8] = c;
        ConcatRotations(temp1, temp3, _entityRotation);

        // rotate modelorg and the transformation matrix
        _modelOrg = EntityRotate(_modelOrg);
        _vpn = EntityRotate(_vpn);
        _vright = EntityRotate(_vright);
        _vup = EntityRotate(_vup);
        TransformFrustum();
    }

    /// <summary>The view's axes, modelorg and frustum back to the world's after a brush entity.</summary>
    private void RestoreWorldView(in Vec3 modelOrg)
    {
        _vpn = _baseVpn;
        _vup = _baseVup;
        _vright = _baseVright;
        _modelOrg = modelOrg;
        TransformFrustum();
    }

    /// <summary>R_BmodelCheckBBox: the frustum planes the entity's box crosses, or BMODEL_FULLY_CLIPPED.</summary>
    private int BmodelCheckBBox(Model* clmodel, float* minmaxs)
    {
        int clipflags = 0;
        ref ClientEntity e = ref Entity(_currentEntity);
        if (e.Angles.X != 0 || e.Angles.Y != 0 || e.Angles.Z != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                double d = Vec3.Dot(e.Origin, _clipNormal[i]);
                d -= _clipDist[i];
                if (d <= -clmodel->Radius)
                    return BmodelFullyClipped;
                if (d <= clmodel->Radius)
                    clipflags |= 1 << i;
            }
        }
        else
        {
            for (int i = 0; i < 4; i++)
            {
                // generate accept and reject points
                int pi = i * 6;
                var rejectpt = new Vec3(minmaxs[_frustumIndexes[pi]], minmaxs[_frustumIndexes[pi + 1]], minmaxs[_frustumIndexes[pi + 2]]);
                double d = Vec3.Dot(rejectpt, _clipNormal[i]);
                d -= _clipDist[i];
                if (d <= 0)
                    return BmodelFullyClipped;
                var acceptpt = new Vec3(minmaxs[_frustumIndexes[pi + 3]], minmaxs[_frustumIndexes[pi + 4]], minmaxs[_frustumIndexes[pi + 5]]);
                d = Vec3.Dot(acceptpt, _clipNormal[i]);
                d -= _clipDist[i];
                if (d <= 0)
                    clipflags |= 1 << i;
            }
        }
        return clipflags;
    }

    /// <summary>R_SplitEntityOnNode2: the first node of the world's BSP that splits the box, or the leaf it is in.</summary>
    private void SplitEntityOnNode2(int child)
    {
        Model* world = _cl.WorldModel;
        while (true)
        {
            if (child < 0)
            {
                int leaf = -1 - child;
                if (_leafVisFrame[leaf] != _visFrameCount)
                    return;
                if (world->Leafs[leaf].Contents != Bsp.ContentsSolid)
                    _pefragTopNode = child;     // we've reached a non-solid leaf, so it's visible and not BSP clipped
                return;
            }
            if (_nodeVisFrame[child] != _visFrameCount)
                return;
            MNode* node = world->Nodes + child;
            int sides = Mathlib.BoxOnPlaneSideMacro(_emins, _emaxs, world->Planes + node->PlaneNum);
            if (sides == 3)
            {
                // remember first splitter
                _pefragTopNode = child;
                return;
            }
            // not split yet; recurse down the contacted side
            child = (sides & 1) != 0 ? node->Children[0] : node->Children[1];
        }
    }

    /// <summary>R_DrawBEntitiesOnList: the visible brush entities into the edge list.</summary>
    private void DrawBEntitiesOnList()
    {
        if (CvarValue("r_drawentities\0"u8) == 0 || !_draw)
            return;
        Vec3 oldorigin = _modelOrg;
        _inSubmodel = true;
        _dlightFrameCount = _frameCount;
        float* minmaxs = stackalloc float[6];
        for (int i = 0; i < _cl.NumVisEdicts; i++)
        {
            _currentEntity = _cl.VisEdicts[i];
            ref ClientEntity e = ref Entity(_currentEntity);
            if (e.Model->Type != ModelType.Brush)
                continue;
            Model* clmodel = e.Model;
            // see if the bounding box lets us trivially reject, also sets trivial accept status
            minmaxs[0] = e.Origin.X + clmodel->Mins.X;
            minmaxs[1] = e.Origin.Y + clmodel->Mins.Y;
            minmaxs[2] = e.Origin.Z + clmodel->Mins.Z;
            minmaxs[3] = e.Origin.X + clmodel->Maxs.X;
            minmaxs[4] = e.Origin.Y + clmodel->Maxs.Y;
            minmaxs[5] = e.Origin.Z + clmodel->Maxs.Z;
            int clipflags = BmodelCheckBBox(clmodel, minmaxs);
            if (clipflags == BmodelFullyClipped)
                continue;
            _entOrigin = e.Origin;
            _modelOrg = Vec3.Subtract(_rOrigin, _entOrigin);
            _currentModel = clmodel;
            _currentVertBase = clmodel->Vertexes;
            // FIXME: stop transforming twice
            RotateBmodel();
            // calculate dynamic lighting for bmodel if it's not an instanced model
            if (clmodel->FirstModelSurface != 0)
            {
                for (int k = 0; k < MaxDLights; k++)
                {
                    ref DLight dl = ref _cl.DLights[k];
                    if (dl.Die < _cl.Time || dl.Radius == 0)
                        continue;
                    MarkLights(ref dl, 1 << k, clmodel, clmodel->Hulls[0].FirstClipNode);
                }
            }
            _pefragTopNode = NoTopNode;
            _emins = new Vec3(minmaxs[0], minmaxs[1], minmaxs[2]);
            _emaxs = new Vec3(minmaxs[3], minmaxs[4], minmaxs[5]);
            SplitEntityOnNode2(0);
            if (_pefragTopNode != NoTopNode)
            {
                if (_pefragTopNode >= 0)
                {
                    // not a leaf; has to be clipped to the world BSP
                    _clipFlags = clipflags;
                    DrawSolidClippedSubmodelPolygons(clmodel, _pefragTopNode);
                }
                else
                {
                    // falls entirely in one leaf, so we just put all the edges in the edge list and let 1/z
                    // sorting handle drawing order
                    DrawSubmodelPolygons(clmodel, clipflags, -1 - _pefragTopNode);
                }
            }
            // put back world rotation and frustum clipping
            RestoreWorldView(oldorigin);
        }
        _inSubmodel = false;
        _currentEntity = default;
        _currentModel = _cl.WorldModel;
    }

    /// <summary>Whether the face faces modelorg (the solid and the leaf case alike).</summary>
    private bool FacesView(MSurface* psurf)
    {
        MPlane* pplane = psurf->Plane;
        float dot = Vec3.Dot(_modelOrg, pplane->Normal) - pplane->Dist;
        return ((psurf->Flags & SurfFlags.PlaneBack) != 0 && dot < -BackfaceEpsilon)
               || ((psurf->Flags & SurfFlags.PlaneBack) == 0 && dot > BackfaceEpsilon);
    }

    /// <summary>R_DrawSubmodelPolygons: every face that faces the view, keyed by the entity's leaf.</summary>
    private void DrawSubmodelPolygons(Model* pmodel, int clipflags, int topLeaf)
    {
        MSurface* psurf = pmodel->Surfaces + pmodel->FirstModelSurface;
        for (int i = 0; i < pmodel->NumModelSurfaces; i++, psurf++)
        {
            if (!FacesView(psurf))
                continue;
            _currentKey = _leafKey[topLeaf];
            RenderFace(psurf, clipflags);
        }
    }

    /// <summary>R_DrawSolidClippedSubmodelPolygons: every face that faces the view, its edges clipped down the world's BSP.</summary>
    private void DrawSolidClippedSubmodelPolygons(Model* pmodel, int topnode)
    {
        MSurface* psurf = pmodel->Surfaces + pmodel->FirstModelSurface;
        MEdge* pedges = pmodel->Edges;
        for (int i = 0; i < pmodel->NumModelSurfaces; i++, psurf++)
        {
            if (!FacesView(psurf))
                continue;
            // copy the edges to bedges, flipping if necessary so always clockwise winding
            _numBVerts = _numBEdges = 0;
            if (psurf->NumEdges <= 0)
                throw NoBmodelEdges;
            if (psurf->NumEdges > MaxBmodelEdges)
                throw BmodelEdgesOver;      // C writes past bedges
            BEdge* pbedge = _bedges + _numBEdges;
            _numBEdges += psurf->NumEdges;
            int j;
            for (j = 0; j < psurf->NumEdges; j++)
            {
                int lindex = pmodel->Surfedges[psurf->FirstEdge + j];
                if (lindex > 0)
                {
                    MEdge* pedge = pedges + lindex;
                    pbedge[j].V0 = _currentVertBase + pedge->V[0];
                    pbedge[j].V1 = _currentVertBase + pedge->V[1];
                }
                else
                {
                    MEdge* pedge = pedges - lindex;
                    pbedge[j].V0 = _currentVertBase + pedge->V[1];
                    pbedge[j].V1 = _currentVertBase + pedge->V[0];
                }
                pbedge[j].Next = pbedge + j + 1;
            }
            pbedge[j - 1].Next = null;  // mark end of edges
            RecursiveClipBPoly(pbedge, topnode, psurf);
        }
    }

    private static readonly SysErrorException BmodelEdgesOver = new("R_DrawSolidClippedSubmodelPolygons: more edges than bedges holds");

    /// <summary>R_RecursiveClipBPoly: the face's edges split by the node's plane, down to the leafs they reach.</summary>
    private void RecursiveClipBPoly(BEdge* pedges, int pnode, MSurface* psurf)
    {
        Model* world = _cl.WorldModel;
        BEdge* side0 = null, side1 = null;
        _makeClippedEdge = false;

        // transform the BSP plane into model space
        MNode* node = world->Nodes + pnode;
        MPlane* splitplane = world->Planes + node->PlaneNum;
        float tdist = splitplane->Dist - Vec3.Dot(_entOrigin, splitplane->Normal);
        float* r = _entityRotation;
        var tnormal = new Vec3(
            r[0] * splitplane->Normal.X + r[1] * splitplane->Normal.Y + r[2] * splitplane->Normal.Z,
            r[3] * splitplane->Normal.X + r[4] * splitplane->Normal.Y + r[5] * splitplane->Normal.Z,
            r[6] * splitplane->Normal.X + r[7] * splitplane->Normal.Y + r[8] * splitplane->Normal.Z);

        // clip edges to BSP plane
        for (BEdge* pnextedge; pedges != null; pedges = pnextedge)
        {
            pnextedge = pedges->Next;
            // set the status for the last point as the previous point
            Vec3* plastvert = pedges->V0;
            float lastdist = Vec3.Dot(*plastvert, tnormal) - tdist;
            int lastside = lastdist > 0 ? 0 : 1;
            Vec3* pvert = pedges->V1;
            float dist = Vec3.Dot(*pvert, tnormal) - tdist;
            int side = dist > 0 ? 0 : 1;
            if (side != lastside)
            {
                // clipped
                if (_numBVerts >= MaxBmodelVerts)
                    return;
                // generate the clipped vertex
                float frac = lastdist / (lastdist - dist);
                Vec3* ptvert = _bverts + _numBVerts++;
                ptvert->X = plastvert->X + frac * (pvert->X - plastvert->X);
                ptvert->Y = plastvert->Y + frac * (pvert->Y - plastvert->Y);
                ptvert->Z = plastvert->Z + frac * (pvert->Z - plastvert->Z);
                // split into two edges, one on each side, and remember entering and exiting points
                if (_numBEdges >= MaxBmodelEdges - 1)
                {
                    Printf("Out of edges for bmodel\n"u8);
                    return;
                }
                BEdge* ptedge = _bedges + _numBEdges;
                ptedge->Next = lastside == 0 ? side0 : side1;
                if (lastside == 0)
                    side0 = ptedge;
                else
                    side1 = ptedge;
                ptedge->V0 = plastvert;
                ptedge->V1 = ptvert;
                ptedge = _bedges + _numBEdges + 1;
                ptedge->Next = side == 0 ? side0 : side1;
                if (side == 0)
                    side0 = ptedge;
                else
                    side1 = ptedge;
                ptedge->V0 = ptvert;
                ptedge->V1 = pvert;
                _numBEdges += 2;
                if (side == 0)
                    _frontEnter = ptvert;   // entering for front, exiting for back
                else
                    _frontExit = ptvert;
                _makeClippedEdge = true;
            }
            else
            {
                // add the edge to the appropriate side
                pedges->Next = side == 0 ? side0 : side1;
                if (side == 0)
                    side0 = pedges;
                else
                    side1 = pedges;
            }
        }

        // if anything was clipped, reconstitute and add the edges along the clip plane to both sides (but in
        // opposite directions)
        if (_makeClippedEdge)
        {
            if (_numBEdges >= MaxBmodelEdges - 2)
            {
                Printf("Out of edges for bmodel\n"u8);
                return;
            }
            BEdge* ptedge = _bedges + _numBEdges;
            ptedge->Next = side0;
            side0 = ptedge;
            ptedge->V0 = _frontExit;
            ptedge->V1 = _frontEnter;
            ptedge = _bedges + _numBEdges + 1;
            ptedge->Next = side1;
            side1 = ptedge;
            ptedge->V0 = _frontEnter;
            ptedge->V1 = _frontExit;
            _numBEdges += 2;
        }

        // draw or recurse further
        for (int i = 0; i < 2; i++)
        {
            BEdge* psideedges = i == 0 ? side0 : side1;
            if (psideedges == null)
                continue;
            // draw if we've reached a non-solid leaf, done if all that's left is a solid leaf, and continue down
            // the tree if it's not a leaf
            int pn = node->Children[i];
            // we're done with this branch if the node or leaf isn't in the PVS
            if (pn < 0)
            {
                int leaf = -1 - pn;
                if (_leafVisFrame[leaf] != _visFrameCount)
                    continue;
                if (world->Leafs[leaf].Contents != Bsp.ContentsSolid)
                {
                    _currentBKey = _leafKey[leaf];
                    RenderBmodelFace(psideedges, psurf);
                }
            }
            else if (_nodeVisFrame[pn] == _visFrameCount)
                RecursiveClipBPoly(psideedges, pn, psurf);
        }
    }

    /// <summary>R_RenderBmodelFace: a clipped piece of a brush entity's face into the edge list.</summary>
    private void RenderBmodelFace(BEdge* pedges, MSurface* psurf)
    {
        // skip out if no more surfs
        if (_surfaceP >= _surfMax)
        {
            _outOfSurfaces++;
            return;
        }
        // ditto if not enough edges left, or switch to auxedges if possible
        if (_edgeP + psurf->NumEdges + 4 >= _edgeMax)
        {
            _outOfEdges += psurf->NumEdges;
            return;
        }
        _faceClip++;
        // this is a dummy to give the caching mechanism someplace to write to
        _pedge = _tedge;

        // set up clip planes
        int pclip = -1;
        for (int i = 3, mask = 0x08; i >= 0; i--, mask >>= 1)
        {
            if ((_clipFlags & mask) != 0)
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
        // FIXME: keep clipped bmodel edges in clockwise order so last vertex caching can be used?
        _lastVertValid = false;
        for (; pedges != null; pedges = pedges->Next)
        {
            _leftClipped = _rightClipped = false;
            ClipEdge(pedges->V0, pedges->V1, pclip);
            if (_leftClipped)
                _makeLeftEdge = true;
            if (_rightClipped)
                _makeRightEdge = true;
        }

        // if there was a clip off the left edge, add that edge too
        if (_makeLeftEdge)
        {
            _pedge = _tedge;
            ClipEdge(_leftExit, _leftEnter, _clipNext[pclip]);
        }
        // if there was a clip off the right edge, get the right r_nearzi
        if (_makeRightEdge)
        {
            _pedge = _tedge;
            _nearZiOnly = true;
            ClipEdge(_rightExit, _rightEnter, _clipNext[1]);
        }

        // if no edges made it out, return without posting the surface
        if (!_emitted)
            return;
        _polyCount++;

        ref RSurf s = ref _surfaces[_surfaceP];
        s.Data = psurf;
        s.NearZi = _nearZi;
        s.Flags = psurf->Flags;
        s.InSubmodel = true;
        s.SpanState = 0;
        s.Entity = _currentEntity;
        s.Key = _currentBKey;
        s.Spans = -1;
        PostGradients(ref s, psurf->Plane);
        _surfaceP++;
    }
}
