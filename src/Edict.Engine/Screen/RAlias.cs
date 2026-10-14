// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// Alias models (r_alias.c, r_aclip.c, r_light.c's R_LightPoint, r_main.c's R_DrawEntitiesOnList and
/// R_DrawViewModel): the bounding box test with its trivial accept, the transform into the view, the vertices lit
/// by the normal and projected, the triangles clipped to the near plane and the view's edges, and handed to the
/// polygon drawer (DPolyse.cs).
/// </summary>
public sealed unsafe partial class Screen
{
    private const int MaxAliasVerts = 2000;
    private const float AliasZClipPlane = 5;
    private const int AliasLeftClip = 0x0001, AliasTopClip = 0x0002, AliasRightClip = 0x0004, AliasBottomClip = 0x0008,
        AliasZClip = 0x0010, AliasOnSeam = 0x0020, AliasXyClipMask = 0x000F;
    private const int LightMin = 5;                   // lowest light value we'll allow
    private const int VidGrades = 64, VidCBits = 6;

    /// <summary><c>finalvert_t</c>: u, v, s, t, light, 1/z, and the clip flags (its reserved float is never read).</summary>
    private struct FinalVert
    {
        public fixed int V[6];
        public int Flags;
    }

    /// <summary><c>auxvert_t</c>.</summary>
    private struct AuxVert
    {
        public fixed float Fv[3];
    }

    private float* _aliasTransform;                   // aliastransform[3][4]
    private float* _aliasT;                           // tmatrix[3][4], viewmatrix[3][4] (statics), rotationmatrix, t2matrix
    private FinalVert* _finalVerts;                   // R_AliasDrawModel's finalverts
    private AuxVert* _auxVerts;                       // and auxverts
    private FinalVert* _clipFv;                       // r_aclip.c's fv[2][8]
    private AuxVert* _clipAv;                         // and av[8]
    private AliasModel* _pmdl;                        // pmdl (with paliashdr)
    private TriVertX* _apverts;                       // r_apverts
    private Vec3 _aliasForward, _aliasRight, _aliasUp;
    private Vec3 _plightvec;                          // r_plightvec
    private int _aliasAmbientLight;                   // r_ambientlight
    private float _shadeLight;                        // r_shadelight
    private float _ziScale;                           // ziscale
    private int _trivialAccept;                       // currententity->trivial_accept (cl.viewent's is never set: 0)
    private int _amodelsDrawn;                        // r_amodels_drawn

    [NoAllocUnchecked("load path")]
    private void InitAlias()
    {
        _aliasTransform = (float*)NativeMemory.AllocZeroed(12, sizeof(float));
        _aliasT = (float*)NativeMemory.AllocZeroed(48, sizeof(float));
        _finalVerts = (FinalVert*)NativeMemory.AlignedAlloc((nuint)(MaxAliasVerts * sizeof(FinalVert)), 32);
        new Span<byte>(_finalVerts, MaxAliasVerts * sizeof(FinalVert)).Clear();
        _auxVerts = (AuxVert*)NativeMemory.AllocZeroed(MaxAliasVerts, (nuint)sizeof(AuxVert));
        _clipFv = (FinalVert*)NativeMemory.AllocZeroed(16, (nuint)sizeof(FinalVert));
        _clipAv = (AuxVert*)NativeMemory.AllocZeroed(8, (nuint)sizeof(AuxVert));
        InitPolyset();
    }

    private static readonly SysErrorException NullColormap = new("R_AliasDrawModel: !currententity->colormap");

    /// <summary>R_ConcatTransforms (mathlib.c): out = in1 * in2, 3x4 with the translation column.</summary>
    private static void ConcatTransforms(float* in1, float* in2, float* o)
    {
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 4; j++)
                o[i * 4 + j] = in1[i * 4 + 0] * in2[0 * 4 + j] + in1[i * 4 + 1] * in2[1 * 4 + j] + in1[i * 4 + 2] * in2[2 * 4 + j];
            o[i * 4 + 3] += in1[i * 4 + 3];
        }
    }

    /// <summary>R_AliasTransformVector.</summary>
    private Vec3 AliasTransformVector(float x, float y, float z)
    {
        float* a = _aliasTransform;
        return new Vec3(
            x * a[0] + y * a[1] + z * a[2] + a[3],
            x * a[4] + y * a[5] + z * a[6] + a[7],
            x * a[8] + y * a[9] + z * a[10] + a[11]);
    }

    /// <summary>R_AliasSetUpTransform: model to view space, scaled to the screen and down by 2^31 for a trivial accept.</summary>
    private void AliasSetUpTransform(ref ClientEntity e, int trivialAccept)
    {
        float* tmatrix = _aliasT, viewmatrix = _aliasT + 12, rotationmatrix = _aliasT + 24, t2matrix = _aliasT + 36;
        var angles = new Vec3(-e.Angles.X, e.Angles.Y, e.Angles.Z);
        Mathlib.AngleVectors(angles, out _aliasForward, out _aliasRight, out _aliasUp);
        tmatrix[0] = _pmdl->Scale.X;
        tmatrix[5] = _pmdl->Scale.Y;
        tmatrix[10] = _pmdl->Scale.Z;
        tmatrix[3] = _pmdl->ScaleOrigin.X;
        tmatrix[7] = _pmdl->ScaleOrigin.Y;
        tmatrix[11] = _pmdl->ScaleOrigin.Z;
        for (int i = 0; i < 3; i++)
        {
            t2matrix[i * 4 + 0] = Vec3.At(_aliasForward, i);
            t2matrix[i * 4 + 1] = -Vec3.At(_aliasRight, i);
            t2matrix[i * 4 + 2] = Vec3.At(_aliasUp, i);
        }
        t2matrix[3] = -_modelOrg.X;
        t2matrix[7] = -_modelOrg.Y;
        t2matrix[11] = -_modelOrg.Z;
        ConcatTransforms(t2matrix, tmatrix, rotationmatrix);
        viewmatrix[0] = _vright.X;
        viewmatrix[1] = _vright.Y;
        viewmatrix[2] = _vright.Z;
        viewmatrix[4] = -_vup.X;
        viewmatrix[5] = -_vup.Y;
        viewmatrix[6] = -_vup.Z;
        viewmatrix[8] = _vpn.X;
        viewmatrix[9] = _vpn.Y;
        viewmatrix[10] = _vpn.Z;
        ConcatTransforms(viewmatrix, rotationmatrix, _aliasTransform);
        // do the scaling up of x and y to screen coordinates as part of the transform for the unclipped case
        if (trivialAccept != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                _aliasTransform[i] = (float)(_aliasTransform[i] * (_aliasXScale * (1.0 / ((float)0x8000 * 0x10000))));
                _aliasTransform[4 + i] = (float)(_aliasTransform[4 + i] * (_aliasYScale * (1.0 / ((float)0x8000 * 0x10000))));
                _aliasTransform[8 + i] = (float)(_aliasTransform[8 + i] * (1.0 / ((float)0x8000 * 0x10000)));
            }
        }
    }

    private static ReadOnlySpan<int> AEdges => [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 5, 1, 4, 2, 7, 3, 6];

    /// <summary>R_AliasCheckBBox: the frame's box against the near plane and the view; sets the trivial accept.</summary>
    private bool AliasCheckBBox(ref ClientEntity e)
    {
        _trivialAccept = 0;
        Model* pmodel = e.Model;
        _pmdl = (AliasModel*)pmodel->Extra;
        AliasSetUpTransform(ref e, 0);
        // construct the base bounding box for this frame
        int frame = e.Frame;
        if (frame >= _pmdl->NumFrames || frame < 0)
        {
            DPrintfNoSuchFrame("No such frame "u8, frame, pmodel);
            frame = 0;
        }
        AliasFrameDesc* pframedesc = _pmdl->Frames + frame;
        float minx = pframedesc->BBoxMin.V[0], miny = pframedesc->BBoxMin.V[1], minz0 = pframedesc->BBoxMin.V[2];
        float maxx = pframedesc->BBoxMax.V[0], maxy = pframedesc->BBoxMax.V[1], maxz0 = pframedesc->BBoxMax.V[2];
        Vec3* basepts = stackalloc Vec3[8];
        basepts[0] = new Vec3(minx, miny, minz0);
        basepts[1] = new Vec3(minx, maxy, minz0);
        basepts[2] = new Vec3(minx, maxy, maxz0);
        basepts[3] = new Vec3(minx, miny, maxz0);
        basepts[4] = new Vec3(maxx, maxy, minz0);
        basepts[5] = new Vec3(maxx, miny, minz0);
        basepts[6] = new Vec3(maxx, miny, maxz0);
        basepts[7] = new Vec3(maxx, maxy, maxz0);

        Vec3* viewaux = stackalloc Vec3[16];
        int* viewflags = stackalloc int[16];
        bool zclipped = false, zfullyclipped = true;
        int minz = 9999;
        for (int i = 0; i < 8; i++)
        {
            viewaux[i] = AliasTransformVector(basepts[i].X, basepts[i].Y, basepts[i].Z);
            if (viewaux[i].Z < AliasZClipPlane)
            {
                // we must clip points that are closer than the near clip plane
                viewflags[i] = AliasZClip;
                zclipped = true;
            }
            else
            {
                if (viewaux[i].Z < minz)
                    minz = CCast.I32(viewaux[i].Z);
                viewflags[i] = 0;
                zfullyclipped = false;
            }
        }
        if (zfullyclipped)
            return false;   // everything was near-z-clipped

        int numv = 8;
        if (zclipped)
        {
            // organize points by edges, use edges to get new points (possible trivial reject)
            for (int i = 0; i < 12; i++)
            {
                // edge endpoints
                int i0 = AEdges[i * 2], i1 = AEdges[i * 2 + 1];
                // if one end is clipped and the other isn't, make a new point
                if ((viewflags[i0] ^ viewflags[i1]) != 0)
                {
                    Vec3 pa0 = viewaux[i0], pa1 = viewaux[i1];
                    float frac = (AliasZClipPlane - pa0.Z) / (pa1.Z - pa0.Z);
                    viewaux[numv] = new Vec3(pa0.X + (pa1.X - pa0.X) * frac, pa0.Y + (pa1.Y - pa0.Y) * frac, AliasZClipPlane);
                    viewflags[numv] = 0;
                    numv++;
                }
            }
        }

        // project the vertices that remain after clipping
        uint anyclip = 0, allclip = AliasXyClipMask;
        for (int i = 0; i < numv; i++)
        {
            // we don't need to bother with vertices that were z-clipped
            if ((viewflags[i] & AliasZClip) != 0)
                continue;
            float zi = (float)(1.0 / viewaux[i].Z);
            float v0 = viewaux[i].X * _xscale * zi + _xcenter;
            float v1 = viewaux[i].Y * _yscale * zi + _ycenter;
            uint flags = 0;
            if (v0 < RefdefVrect.X)
                flags |= AliasLeftClip;
            if (v1 < RefdefVrect.Y)
                flags |= AliasTopClip;
            if (v0 > _vrectRight)
                flags |= AliasRightClip;
            if (v1 > _vrectBottom)
                flags |= AliasBottomClip;
            anyclip |= flags;
            allclip &= flags;
        }
        if (allclip != 0)
            return false;   // trivial reject off one side

        _trivialAccept = (anyclip == 0 ? 1 : 0) & (!zclipped ? 1 : 0);
        if (_trivialAccept != 0 && minz > _aliasTransition + _pmdl->Size * _resFudge)
            _trivialAccept |= 2;
        return true;
    }

    /// <summary>Con_DPrintf ("%s%d %s\n", text, frame, model's name), with developer set.</summary>
    private void DPrintfNoSuchFrame(ReadOnlySpan<byte> text, int frame, Model* model)
    {
        if (CvarValue("developer\0"u8) == 0)
            return;
        Msg(text).MsgInt(frame).Msg(" "u8).Msg((byte*)&model->Name).Msg("\n"u8).EndMsg();
    }

    /// <summary>The light of a vertex: ambient, and shade where the normal faces away from the light (both C paths).</summary>
    private int VertexLight(TriVertX* pverts)
    {
        ReadOnlySpan<float> n = VertexNormals.Table.Slice(pverts->LightNormalIndex * 3, 3);
        float lightcos = n[0] * _plightvec.X + n[1] * _plightvec.Y + n[2] * _plightvec.Z;
        int temp = _aliasAmbientLight;
        if (lightcos < 0)
        {
            temp += CCast.I32(_shadeLight * lightcos);
            // clamp; because we limited the minimum ambient and shading light, we don't have to clamp low light,
            // just bright
            if (temp < 0)
                temp = 0;
        }
        return temp;
    }

    /// <summary>R_AliasTransformFinalVert: into view space (auxvert), s, t, seam and light.</summary>
    private void AliasTransformFinalVert(FinalVert* fv, AuxVert* av, TriVertX* pverts, STVert* pstverts)
    {
        Vec3 t = AliasTransformVector(pverts->V[0], pverts->V[1], pverts->V[2]);
        av->Fv[0] = t.X;
        av->Fv[1] = t.Y;
        av->Fv[2] = t.Z;
        fv->V[2] = pstverts->S;
        fv->V[3] = pstverts->T;
        fv->Flags = pstverts->OnSeam;
        fv->V[4] = VertexLight(pverts);
    }

    /// <summary>R_AliasTransformAndProjectFinalVerts: the trivially accepted case, 1/z scaled by 2^31 for free.</summary>
    private void AliasTransformAndProjectFinalVerts(FinalVert* fv, STVert* pstverts, int numverts)
    {
        float* a = _aliasTransform;
        TriVertX* pverts = _apverts;
        for (int i = 0; i < numverts; i++, fv++, pverts++, pstverts++)
        {
            float x = pverts->V[0], y = pverts->V[1], z = pverts->V[2];
            // transform and project
            float zi = (float)(1.0 / (x * a[8] + y * a[9] + z * a[10] + a[11]));
            // x, y, and z are scaled down by 1/2**31 in the transform, so 1/z is scaled up by 1/2**31, and the
            // scaling cancels out for x and y in the projection
            fv->V[5] = CCast.I32(zi);
            fv->V[0] = CCast.I32((x * a[0] + y * a[1] + z * a[2] + a[3]) * zi + _aliasXCenter);
            fv->V[1] = CCast.I32((x * a[4] + y * a[5] + z * a[6] + a[7]) * zi + _aliasYCenter);
            fv->V[2] = pstverts->S;
            fv->V[3] = pstverts->T;
            fv->Flags = pstverts->OnSeam;
            fv->V[4] = VertexLight(pverts);
        }
    }

    /// <summary>R_AliasProjectFinalVert.</summary>
    private void AliasProjectFinalVert(FinalVert* fv, AuxVert* av)
    {
        // project points
        float zi = (float)(1.0 / av->Fv[2]);
        fv->V[5] = CCast.I32(zi * _ziScale);
        fv->V[0] = CCast.I32(av->Fv[0] * _aliasXScale * zi + _aliasXCenter);
        fv->V[1] = CCast.I32(av->Fv[1] * _aliasYScale * zi + _aliasYCenter);
    }

    /// <summary>The alias view's clip flags of a projected vertex.</summary>
    private int AliasClipFlags(FinalVert* fv)
    {
        int flags = 0;
        if (fv->V[0] < _aliasVrect.X)
            flags |= AliasLeftClip;
        if (fv->V[1] < _aliasVrect.Y)
            flags |= AliasTopClip;
        if (fv->V[0] > _aliasVrectRight)
            flags |= AliasRightClip;
        if (fv->V[1] > _aliasVrectBottom)
            flags |= AliasBottomClip;
        return flags;
    }

    /// <summary>R_AliasPreparePoints: the general clipped case.</summary>
    private void AliasPreparePoints()
    {
        STVert* pstverts = _pmdl->STVerts;
        int numverts = _pmdl->NumVerts;
        FinalVert* fv = _finalVerts;
        AuxVert* av = _auxVerts;
        TriVertX* pverts = _apverts;
        for (int i = 0; i < numverts; i++, fv++, av++, pverts++, pstverts++)
        {
            AliasTransformFinalVert(fv, av, pverts, pstverts);
            if (av->Fv[2] < AliasZClipPlane)
                fv->Flags |= AliasZClip;
            else
            {
                AliasProjectFinalVert(fv, av);
                fv->Flags |= AliasClipFlags(fv);
            }
        }

        // clip and draw all triangles
        MTriangle* ptri = _pmdl->Triangles;
        for (int i = 0; i < _pmdl->NumTris; i++, ptri++)
        {
            FinalVert* p0 = _finalVerts + ptri->VertIndex[0];
            FinalVert* p1 = _finalVerts + ptri->VertIndex[1];
            FinalVert* p2 = _finalVerts + ptri->VertIndex[2];
            if ((p0->Flags & p1->Flags & p2->Flags & (AliasXyClipMask | AliasZClip)) != 0)
                continue;   // completely clipped
            if (((p0->Flags | p1->Flags | p2->Flags) & (AliasXyClipMask | AliasZClip)) == 0)
                PolysetDraw(_finalVerts, ptri, 1);  // totally unclipped
            else
                AliasClipTriangle(ptri);    // partially clipped
        }
    }

    /// <summary>R_AliasPrepareUnclippedPoints.</summary>
    private void AliasPrepareUnclippedPoints()
    {
        AliasTransformAndProjectFinalVerts(_finalVerts, _pmdl->STVerts, _pmdl->NumVerts);
        if (_drawType)
            PolysetDrawFinalVerts(_finalVerts, _pmdl->NumVerts);
        PolysetDraw(_finalVerts, _pmdl->Triangles, _pmdl->NumTris);
    }

    /// <summary>R_AliasSetupSkin: the skin, or a group's skin for the time.</summary>
    private void AliasSetupSkin(ref ClientEntity e)
    {
        int skinnum = e.SkinNum;
        if (skinnum >= _pmdl->NumSkins || skinnum < 0)
        {
            if (CvarValue("developer\0"u8) != 0)
                Msg("R_AliasSetupSkin: no such skin # "u8).MsgInt(skinnum).Msg("\n"u8).EndMsg();
            skinnum = 0;
        }
        AliasSkinDesc* pskindesc = _pmdl->SkinDescs + skinnum;
        byte* skin = pskindesc->Skin;
        if (pskindesc->Type == 1)
        {
            AliasSkinGroup* g = pskindesc->Group;
            float* pskinintervals = g->Intervals;
            int numskins = g->NumSkins;
            float fullskininterval = pskinintervals[numskins - 1];
            float skintime = (float)(_cl.Time + e.SyncBase);
            // when loading in Mod_LoadAliasSkinGroup, we guaranteed all interval values are positive, so we don't
            // have to worry about division by 0
            float skintargettime = skintime - CCast.I32(skintime / fullskininterval) * fullskininterval;
            int i;
            for (i = 0; i < numskins - 1; i++)
                if (pskinintervals[i] > skintargettime)
                    break;
            skin = g->SkinDescs[i].Skin;
        }
        _pskin = skin;
        _skinWidth = _pmdl->SkinWidth;
        _seamFixupX16 = (_pmdl->SkinWidth >> 1) << 16;
        _skinHeight = _pmdl->SkinHeight;
    }

    /// <summary>R_AliasSetupLighting: ambient and shade light, the light vector in the model's frame.</summary>
    private void AliasSetupLighting(int ambientlight, int shadelight, in Vec3 plightvec)
    {
        // guarantee that no vertex will ever be lit below LIGHT_MIN, so we don't have to clamp off the bottom
        _aliasAmbientLight = ambientlight;
        if (_aliasAmbientLight < LightMin)
            _aliasAmbientLight = LightMin;
        _aliasAmbientLight = (255 - _aliasAmbientLight) << VidCBits;
        if (_aliasAmbientLight < LightMin)
            _aliasAmbientLight = LightMin;
        _shadeLight = shadelight;
        if (_shadeLight < 0)
            _shadeLight = 0;
        _shadeLight *= VidGrades;
        // rotate the lighting vector into the model's frame of reference
        _plightvec = new Vec3(Vec3.Dot(plightvec, _aliasForward), -Vec3.Dot(plightvec, _aliasRight), Vec3.Dot(plightvec, _aliasUp));
    }

    /// <summary>R_AliasSetupFrame: r_apverts, the frame's vertices, or a group's for the time.</summary>
    private void AliasSetupFrame(ref ClientEntity e)
    {
        int frame = e.Frame;
        if (frame >= _pmdl->NumFrames || frame < 0)
        {
            if (CvarValue("developer\0"u8) != 0)
                Msg("R_AliasSetupFrame: no such frame "u8).MsgInt(frame).Msg("\n"u8).EndMsg();
            frame = 0;
        }
        AliasFrameDesc* f = _pmdl->Frames + frame;
        if (f->Type == 0)
        {
            _apverts = f->Frame;
            return;
        }
        AliasGroup* g = f->Group;
        float* pintervals = g->Intervals;
        int numframes = g->NumFrames;
        float fullinterval = pintervals[numframes - 1];
        float time = (float)(_cl.Time + e.SyncBase);
        // when loading in Mod_LoadAliasGroup, we guaranteed all interval values are positive, so we don't have to
        // worry about division by 0
        float targettime = time - CCast.I32(time / fullinterval) * fullinterval;
        int i;
        for (i = 0; i < numframes - 1; i++)
            if (pintervals[i] > targettime)
                break;
        _apverts = g->Frames[i].Frame;
    }

    /// <summary>R_AliasDrawModel.</summary>
    private void AliasDrawModel(ref ClientEntity e, bool viewEnt, int ambientlight, int shadelight, in Vec3 plightvec)
    {
        _amodelsDrawn++;
        _pmdl = (AliasModel*)e.Model->Extra;
        AliasSetupSkin(ref e);
        AliasSetUpTransform(ref e, _trivialAccept);
        AliasSetupLighting(ambientlight, shadelight, plightvec);
        AliasSetupFrame(ref e);
        if (e.Colormap == 0)
            throw NullColormap;
        _drawType = _trivialAccept == 3;    // && r_recursiveaffinetriangles
        _aColormap = ColormapOf(e.Colormap);
        _ziScale = viewEnt ? (float)0x8000 * (float)0x10000 * 3.0f : (float)0x8000 * (float)0x10000;
        if (_trivialAccept != 0)
            AliasPrepareUnclippedPoints();
        else
            AliasPreparePoints();
    }

    // ---- r_aclip.c

    /// <summary>R_Alias_clip_z: pfv0 is the unclipped vertex, pfv1 the z-clipped one (both in fv[0]).</summary>
    private void AliasClipZ(FinalVert* pfv0, FinalVert* pfv1, FinalVert* o)
    {
        AuxVert* pav0 = _clipAv + (pfv0 - _clipFv);
        AuxVert* pav1 = _clipAv + (pfv1 - _clipFv);
        AuxVert avout;
        if (pfv0->V[1] >= pfv1->V[1])
            ClipZ(pfv0, pfv1, pav0, pav1, o, &avout);
        else
            ClipZ(pfv1, pfv0, pav1, pav0, o, &avout);
        AliasProjectFinalVert(o, &avout);
        o->Flags |= AliasClipFlags(o);

        static void ClipZ(FinalVert* a, FinalVert* b, AuxVert* pa, AuxVert* pb, FinalVert* o, AuxVert* avout)
        {
            float scale = (AliasZClipPlane - pa->Fv[2]) / (pb->Fv[2] - pa->Fv[2]);
            avout->Fv[0] = pa->Fv[0] + (pb->Fv[0] - pa->Fv[0]) * scale;
            avout->Fv[1] = pa->Fv[1] + (pb->Fv[1] - pa->Fv[1]) * scale;
            avout->Fv[2] = AliasZClipPlane;
            o->V[2] = CCast.I32(a->V[2] + (b->V[2] - a->V[2]) * scale);
            o->V[3] = CCast.I32(a->V[3] + (b->V[3] - a->V[3]) * scale);
            o->V[4] = CCast.I32(a->V[4] + (b->V[4] - a->V[4]) * scale);
        }
    }

    /// <summary>R_Alias_clip_left/right/top/bottom: the edge cut at the view's edge (coordinate index, edge value).</summary>
    private static void AliasClipEdge(FinalVert* pfv0, FinalVert* pfv1, FinalVert* o, int index, int edge)
    {
        FinalVert* a = pfv0, b = pfv1;
        if (pfv0->V[1] < pfv1->V[1])
        {
            a = pfv1;
            b = pfv0;
        }
        float scale = (float)(edge - a->V[index]) / (b->V[index] - a->V[index]);
        for (int i = 0; i < 6; i++)
            o->V[i] = CCast.I32(a->V[i] + (b->V[i] - a->V[i]) * scale + 0.5);
    }

    /// <summary>R_AliasClip: the polygon in against one clip flag, into out; the number of vertices out.</summary>
    private int AliasClip(FinalVert* inp, FinalVert* o, int flag, int count)
    {
        int j = count - 1;
        int k = 0;
        for (int i = 0; i < count; j = i, i++)
        {
            int oldflags = inp[j].Flags & flag;
            int flags = inp[i].Flags & flag;
            if (flags != 0 && oldflags != 0)
                continue;
            if ((oldflags ^ flags) != 0)
            {
                switch (flag)
                {
                    case AliasZClip:
                        AliasClipZ(inp + j, inp + i, o + k);
                        break;
                    case AliasLeftClip:
                        AliasClipEdge(inp + j, inp + i, o + k, 0, _aliasVrect.X);
                        break;
                    case AliasRightClip:
                        AliasClipEdge(inp + j, inp + i, o + k, 0, _aliasVrectRight);
                        break;
                    case AliasBottomClip:
                        AliasClipEdge(inp + j, inp + i, o + k, 1, _aliasVrectBottom);
                        break;
                    default:
                        AliasClipEdge(inp + j, inp + i, o + k, 1, _aliasVrect.Y);
                        break;
                }
                o[k].Flags = AliasClipFlags(o + k);
                k++;
            }
            if (flags == 0)
            {
                o[k] = inp[i];
                k++;
            }
        }
        return k;
    }

    /// <summary>R_AliasClipTriangle: the triangle clipped, its vertices clamped to the view, drawn as a fan.</summary>
    private void AliasClipTriangle(MTriangle* ptri)
    {
        FinalVert* fv0 = _clipFv, fv1 = _clipFv + 8;
        // copy vertexes and fix seam texture coordinates
        for (int i = 0; i < 3; i++)
        {
            fv0[i] = _finalVerts[ptri->VertIndex[i]];
            if (ptri->FacesFront == 0 && (fv0[i].Flags & AliasOnSeam) != 0)
                fv0[i].V[2] += _seamFixupX16;
        }

        // clip
        int clipflags = fv0[0].Flags | fv0[1].Flags | fv0[2].Flags;
        int k, pingpong;
        if ((clipflags & AliasZClip) != 0)
        {
            for (int i = 0; i < 3; i++)
                _clipAv[i] = _auxVerts[ptri->VertIndex[i]];
            k = AliasClip(fv0, fv1, AliasZClip, 3);
            if (k == 0)
                return;
            pingpong = 1;
            clipflags = fv1[0].Flags | fv1[1].Flags | fv1[2].Flags;
        }
        else
        {
            pingpong = 0;
            k = 3;
        }
        foreach (int flag in (ReadOnlySpan<int>)[AliasLeftClip, AliasRightClip, AliasBottomClip, AliasTopClip])
        {
            if ((clipflags & flag) == 0)
                continue;
            k = AliasClip(_clipFv + pingpong * 8, _clipFv + (pingpong ^ 1) * 8, flag, k);
            if (k == 0)
                return;
            pingpong ^= 1;
        }

        FinalVert* fv = _clipFv + pingpong * 8;
        for (int i = 0; i < k; i++)
        {
            if (fv[i].V[0] < _aliasVrect.X)
                fv[i].V[0] = _aliasVrect.X;
            else if (fv[i].V[0] > _aliasVrectRight)
                fv[i].V[0] = _aliasVrectRight;
            if (fv[i].V[1] < _aliasVrect.Y)
                fv[i].V[1] = _aliasVrect.Y;
            else if (fv[i].V[1] > _aliasVrectBottom)
                fv[i].V[1] = _aliasVrectBottom;
            fv[i].Flags = 0;
        }

        // draw triangles
        MTriangle mtri;
        mtri.FacesFront = ptri->FacesFront;
        mtri.VertIndex[0] = 0;
        for (int i = 1; i < k - 1; i++)
        {
            mtri.VertIndex[1] = i;
            mtri.VertIndex[2] = i + 1;
            PolysetDraw(fv, &mtri, 1);
        }
    }

    // ---- r_light.c's R_LightPoint, r_main.c's entity lists

    /// <summary>RecursiveLightPoint: the light of the first lit surface the line from start to end hits, or -1.</summary>
    private int RecursiveLightPoint(int child, in Vec3 start, in Vec3 end)
    {
        if (child < 0)
            return -1;  // didn't hit anything
        Model* world = _cl.WorldModel;
        MNode* node = world->Nodes + child;
        // calculate mid point
        MPlane* plane = world->Planes + node->PlaneNum;
        float front = Vec3.Dot(start, plane->Normal) - plane->Dist;
        float back = Vec3.Dot(end, plane->Normal) - plane->Dist;
        int side = front < 0 ? 1 : 0;
        if ((back < 0 ? 1 : 0) == side)
            return RecursiveLightPoint(node->Children[side], start, end);
        float frac = front / (front - back);
        var mid = new Vec3(start.X + (end.X - start.X) * frac, start.Y + (end.Y - start.Y) * frac, start.Z + (end.Z - start.Z) * frac);

        // go down front side
        int r = RecursiveLightPoint(node->Children[side], start, mid);
        if (r >= 0)
            return r;   // hit something
        if ((back < 0 ? 1 : 0) == side)
            return -1;  // didn't hit anuthing

        // check for impact on this node
        MSurface* surf = world->Surfaces + node->FirstSurface;
        for (int i = 0; i < node->NumSurfaces; i++, surf++)
        {
            if ((surf->Flags & SurfFlags.DrawTiled) != 0)
                continue;   // no lightmaps
            MTexInfo* tex = surf->TexInfo;
            int s = CCast.I32(mid.X * tex->Vecs[0] + mid.Y * tex->Vecs[1] + mid.Z * tex->Vecs[2] + tex->Vecs[3]);
            int t = CCast.I32(mid.X * tex->Vecs[4] + mid.Y * tex->Vecs[5] + mid.Z * tex->Vecs[6] + tex->Vecs[7]);
            if (s < surf->TextureMins[0] || t < surf->TextureMins[1])
                continue;
            int ds = s - surf->TextureMins[0];
            int dt = t - surf->TextureMins[1];
            if (ds > surf->Extents[0] || dt > surf->Extents[1])
                continue;
            if (surf->Samples == null)
                return 0;
            ds >>= 4;
            dt >>= 4;
            byte* lightmap = surf->Samples;
            uint ur = 0;
            lightmap += dt * ((surf->Extents[0] >> 4) + 1) + ds;
            for (int maps = 0; maps < SurfFlags.MaxLightmaps && surf->Styles[maps] != 255; maps++)
            {
                uint scale = (uint)_lightStyleValue[surf->Styles[maps]];
                ur += *lightmap * scale;
                lightmap += ((surf->Extents[0] >> 4) + 1) * ((surf->Extents[1] >> 4) + 1);
            }
            r = (int)ur;
            r >>= 8;
            return r;
        }

        // go down back side
        return RecursiveLightPoint(node->Children[side ^ 1], mid, end);
    }

    /// <summary>R_LightPoint: the light below p, at least the ambient light.</summary>
    private int LightPoint(in Vec3 p)
    {
        if (_cl.WorldModel->LightData == null)
            return 255;
        var end = new Vec3(p.X, p.Y, p.Z - 2048);
        int r = RecursiveLightPoint(0, p, end);
        if (r == -1)
            r = 0;
        if (r < _ambientLight)
            r = _ambientLight;
        return r;
    }

    /// <summary>The dynamic lights' share of an entity's ambient light (R_DrawEntitiesOnList, R_DrawViewModel).</summary>
    private int AddDlights(int ambientlight, in Vec3 origin, bool viewModel)
    {
        for (int lnum = 0; lnum < MaxDLights; lnum++)
        {
            ref DLight dl = ref _cl.DLights[lnum];
            if (viewModel ? dl.Radius == 0 || dl.Die < _cl.Time : !(dl.Die >= _cl.Time))
                continue;
            Vec3 dist = Vec3.Subtract(origin, dl.Origin);
            float add = dl.Radius - Mathlib.Length(dist);
            if (add > 0)
                ambientlight = CCast.I32(ambientlight + add);
        }
        return ambientlight;
    }

    /// <summary>R_DrawEntitiesOnList's alias case: the box test, the light, the model.</summary>
    private void DrawAliasEntity(ref ClientEntity e)
    {
        _entOrigin = e.Origin;
        _modelOrg = Vec3.Subtract(_rOrigin, _entOrigin);
        // see if the bounding box lets us trivially reject, also sets trivial accept status
        if (!AliasCheckBBox(ref e))
            return;
        int j = LightPoint(e.Origin);
        int ambientlight = j, shadelight = j;
        ambientlight = AddDlights(ambientlight, e.Origin, false);
        // clamp lighting so it doesn't overbright as much
        if (ambientlight > 128)
            ambientlight = 128;
        if (ambientlight + shadelight > 192)
            shadelight = 192 - ambientlight;
        AliasDrawModel(ref e, false, ambientlight, shadelight, new Vec3(-1, 0, 0));
    }
}
