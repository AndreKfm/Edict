// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// Sprites (r_sprite.c, d_sprite.c): the frame for the entity's frame and time, the poster's axes by the sprite's
/// type, the poster clipped to the frustum in world space, projected, scanned into spans and drawn z-tested with
/// 255 transparent.
/// </summary>
public sealed unsafe partial class Screen
{
    private const int MaxWorkingVerts = 16 + 4;       // MAXWORKINGVERTS
    private const int SprVpParallelUpright = 0, SprFacingUpright = 1, SprVpParallel = 2, SprOriented = 3,
        SprVpParallelOriented = 4;
    private const int DsSpanListEnd = -128;

    /// <summary><c>emitpoint_t</c>.</summary>
    private struct EmitPoint
    {
        public float U, V, S, T, Zi;
    }

    /// <summary><c>sspan_t</c>.</summary>
    private struct SSpan
    {
        public int U, V, Count;
    }

    private float* _clipVerts;                        // clip_verts[2][MAXWORKINGVERTS] of vec5_t
    private int _clipCurrent;                         // clip_current
    private float* _clipDists;                        // R_ClipSpriteFace's dists[MAXWORKINGVERTS+1]
    private EmitPoint* _outVerts;                     // R_SetupAndDrawSprite's outverts[MAXWORKINGVERTS+1]
    private SSpan* _spriteSpans;                      // D_DrawSprite's spans[MAXHEIGHT+1]
    private SpriteFrame* _spriteFrame;                // r_spritedesc.pspriteframe
    private Vec3 _sprVpn, _sprVright, _sprVup;        // r_spritedesc's axes
    private float _sprNearZi;
    private int _sprNump;
    private int _spriteWidth, _spriteHeight;          // r_sprite.c's statics (d_sprite.c's sprite_height is the same)
    private int _sprMinIndex, _sprMaxIndex;

    [NoAllocUnchecked("load path")]
    private void InitSprites()
    {
        _clipVerts = (float*)NativeMemory.AllocZeroed(2 * MaxWorkingVerts * 5, sizeof(float));
        _clipDists = (float*)NativeMemory.AllocZeroed(MaxWorkingVerts + 1, sizeof(float));
        _outVerts = (EmitPoint*)NativeMemory.AllocZeroed(MaxWorkingVerts + 1, (nuint)sizeof(EmitPoint));
        _spriteSpans = (SSpan*)NativeMemory.AllocZeroed(MaxHeight + 1, (nuint)sizeof(SSpan));
    }

    private static readonly SysErrorException TooManySpritePoints = new("R_SetupAndDrawSprite: too many points");
    private static readonly SysErrorException BadSpriteType = new("R_DrawSprite: Bad sprite type");

    /// <summary>R_RotateSprite: a beam's sprite moved along its normal.</summary>
    private void RotateSprite(float beamlength)
    {
        if (beamlength == 0.0)
            return;
        var vec = new Vec3(_sprVpn.X * -beamlength, _sprVpn.Y * -beamlength, _sprVpn.Z * -beamlength);
        _entOrigin = Vec3.Add(_entOrigin, vec);
        _modelOrg = Vec3.Subtract(_modelOrg, vec);
    }

    /// <summary>R_ClipSpriteFace: the winding at clip_verts[clip_current] clipped by the plane, the back side out.</summary>
    private int ClipSpriteFace(int nump, int plane)
    {
        float clipdist = _clipDist[plane];
        Vec3 n = _clipNormal[plane];
        float* inp, outstep;
        if (_clipCurrent != 0)
        {
            inp = _clipVerts + MaxWorkingVerts * 5;
            outstep = _clipVerts;
            _clipCurrent = 0;
        }
        else
        {
            inp = _clipVerts;
            outstep = _clipVerts + MaxWorkingVerts * 5;
            _clipCurrent = 1;
        }
        float* instep = inp;
        for (int i = 0; i < nump; i++, instep += 5)
            _clipDists[i] = instep[0] * n.X + instep[1] * n.Y + instep[2] * n.Z - clipdist;
        // handle wraparound case
        _clipDists[nump] = _clipDists[0];
        for (int k = 0; k < 5; k++)
            instep[k] = inp[k];

        // clip the winding
        instep = inp;
        int outcount = 0;
        for (int i = 0; i < nump; i++, instep += 5)
        {
            if (_clipDists[i] >= 0)
            {
                for (int k = 0; k < 5; k++)
                    outstep[k] = instep[k];
                outstep += 5;
                outcount++;
            }
            if (_clipDists[i] == 0 || _clipDists[i + 1] == 0)
                continue;
            if (_clipDists[i] > 0 == _clipDists[i + 1] > 0)
                continue;
            // split it into a new vertex
            float frac = _clipDists[i] / (_clipDists[i] - _clipDists[i + 1]);
            float* vert2 = instep + 5;
            for (int k = 0; k < 5; k++)
                outstep[k] = instep[k] + frac * (vert2[k] - instep[k]);
            outstep += 5;
            outcount++;
        }
        return outcount;
    }

    /// <summary>R_SetupAndDrawSprite: the poster built, clipped, projected and drawn.</summary>
    private void SetupAndDrawSprite()
    {
        float dot = Vec3.Dot(_sprVpn, _modelOrg);
        // backface cull
        if (dot >= 0)
            return;
        // build the sprite poster in worldspace
        SpriteFrame* f = _spriteFrame;
        Vec3 right = new(_sprVright.X * f->Right, _sprVright.Y * f->Right, _sprVright.Z * f->Right);
        Vec3 up = new(_sprVup.X * f->Up, _sprVup.Y * f->Up, _sprVup.Z * f->Up);
        Vec3 left = new(_sprVright.X * f->Left, _sprVright.Y * f->Left, _sprVright.Z * f->Left);
        Vec3 down = new(_sprVup.X * f->Down, _sprVup.Y * f->Down, _sprVup.Z * f->Down);
        float* pv = _clipVerts;
        Corner(pv, up, left, 0, 0);
        Corner(pv + 5, up, right, _spriteWidth, 0);
        Corner(pv + 10, down, right, _spriteWidth, _spriteHeight);
        Corner(pv + 15, down, left, 0, _spriteHeight);

        // clip to the frustum in worldspace
        int nump = 4;
        _clipCurrent = 0;
        for (int i = 0; i < 4; i++)
        {
            nump = ClipSpriteFace(nump, i);
            if (nump < 3)
                return;
            if (nump >= MaxWorkingVerts)
                throw TooManySpritePoints;
        }

        // transform vertices into viewspace and project
        pv = _clipVerts + _clipCurrent * MaxWorkingVerts * 5;
        _sprNearZi = -999999;
        for (int i = 0; i < nump; i++, pv += 5)
        {
            Vec3 transformed = TransformVector(new Vec3(pv[0] - _rOrigin.X, pv[1] - _rOrigin.Y, pv[2] - _rOrigin.Z));
            if (transformed.Z < NearClip)
                transformed.Z = (float)NearClip;
            EmitPoint* pout = _outVerts + i;
            pout->Zi = (float)(1.0 / transformed.Z);
            if (pout->Zi > _sprNearZi)
                _sprNearZi = pout->Zi;
            pout->S = pv[3];
            pout->T = pv[4];
            float scale = _xscale * pout->Zi;
            pout->U = _xcenter + scale * transformed.X;
            scale = _yscale * pout->Zi;
            pout->V = _ycenter - scale * transformed.Y;
        }
        // draw it
        _sprNump = nump;
        DrawSpriteSpans();

        void Corner(float* p, in Vec3 a, in Vec3 b, float s, float t)
        {
            p[0] = _entOrigin.X + a.X + b.X;
            p[1] = _entOrigin.Y + a.Y + b.Y;
            p[2] = _entOrigin.Z + a.Z + b.Z;
            p[3] = s;
            p[4] = t;
        }
    }

    /// <summary>R_GetSpriteframe: the frame, or a group's frame for the time (the "no such frame" message at frame 0).</summary>
    private SpriteFrame* GetSpriteFrame(SpriteModel* psprite, ref ClientEntity e)
    {
        int frame = e.Frame;
        if (frame >= psprite->NumFrames || frame < 0)
        {
            Msg("R_DrawSprite: no such frame "u8).MsgInt(frame).Msg("\n"u8).EndMsg();
            frame = 0;
        }
        SpriteFrameDesc* d = psprite->Frames + frame;
        if (d->Type == 0)
            return d->Frame;
        SpriteGroup* g = d->Group;
        float* pintervals = g->Intervals;
        int numframes = g->NumFrames;
        float fullinterval = pintervals[numframes - 1];
        float time = (float)(_cl.Time + e.SyncBase);
        // when loading in Mod_LoadSpriteGroup, we guaranteed all interval values are positive, so we don't have to
        // worry about division by 0
        float targettime = time - CCast.I32(time / fullinterval) * fullinterval;
        int i;
        for (i = 0; i < numframes - 1; i++)
            if (pintervals[i] > targettime)
                break;
        return g->Frames[i];
    }

    /// <summary>R_DrawSprite.</summary>
    private void DrawSprite(ref ClientEntity e)
    {
        var psprite = (SpriteModel*)e.Model->Extra;
        _spriteFrame = GetSpriteFrame(psprite, ref e);
        _spriteWidth = _spriteFrame->Width;
        _spriteHeight = _spriteFrame->Height;
        switch (psprite->Type)
        {
            case SprFacingUpright:
            {
                // vup straight up in worldspace, vright perpendicular to modelorg; not drawn within 1 degree of
                // straight up or down
                var tvec = new Vec3(-_modelOrg.X, -_modelOrg.Y, -_modelOrg.Z);
                Mathlib.VectorNormalize(ref tvec);
                float dot = tvec.Z;
                if (dot > 0.999848 || dot < -0.999848)     // cos(1 degree) = 0.999848
                    return;
                _sprVup = new Vec3(0, 0, 1);
                _sprVright = new Vec3(tvec.Y, -tvec.X, 0);
                Mathlib.VectorNormalize(ref _sprVright);
                _sprVpn = new Vec3(-_sprVright.Y, _sprVright.X, 0);
                break;
            }
            case SprVpParallel:
                // completely parallel to the viewplane
                _sprVup = _vup;
                _sprVright = _vright;
                _sprVpn = _vpn;
                break;
            case SprVpParallelUpright:
            {
                // vup straight up in worldspace, vright parallel to the viewplane
                float dot = _vpn.Z;
                if (dot > 0.999848 || dot < -0.999848)
                    return;
                _sprVup = new Vec3(0, 0, 1);
                _sprVright = new Vec3(_vpn.Y, -_vpn.X, 0);
                Mathlib.VectorNormalize(ref _sprVright);
                _sprVpn = new Vec3(-_sprVright.Y, _sprVright.X, 0);
                break;
            }
            case SprOriented:
                // according to the sprite's world orientation
                Mathlib.AngleVectors(e.Angles, out _sprVpn, out _sprVright, out _sprVup);
                break;
            case SprVpParallelOriented:
            {
                // parallel to the viewplane, rotated in it by the entity's roll
                float angle = (float)(e.Angles.Z * (Math.PI * 2 / 360));
                float sr = (float)CMath.Sin(angle), cr = (float)CMath.Cos(angle);
                _sprVpn = _vpn;
                _sprVright = new Vec3(_vright.X * cr + _vup.X * sr, _vright.Y * cr + _vup.Y * sr, _vright.Z * cr + _vup.Z * sr);
                _sprVup = new Vec3(_vright.X * -sr + _vup.X * cr, _vright.Y * -sr + _vup.Y * cr, _vright.Z * -sr + _vup.Z * cr);
                break;
            }
            default:
                throw BadSpriteType;
        }
        RotateSprite(psprite->BeamLength);
        SetupAndDrawSprite();
    }

    /// <summary>D_SpriteScanLeftEdge: each scan's left u, from the top vertex down the decreasing indices.</summary>
    private void SpriteScanLeftEdge()
    {
        SSpan* pspan = _spriteSpans;
        int i = _sprMinIndex;
        if (i == 0)
            i = _sprNump;
        int lmaxindex = _sprMaxIndex;
        if (lmaxindex == 0)
            lmaxindex = _sprNump;
        float vtop = (float)Math.Ceiling(_outVerts[i].V);
        do
        {
            EmitPoint* pvert = _outVerts + i;
            EmitPoint* pnext = pvert - 1;
            float vbottom = (float)Math.Ceiling(pnext->V);
            if (vtop < vbottom)
            {
                float du = pnext->U - pvert->U;
                float dv = pnext->V - pvert->V;
                float slope = du / dv;
                int uStep = CCast.I32(slope * 0x10000);
                // adjust u to ceil the integer portion
                int u = CCast.I32((pvert->U + slope * (vtop - pvert->V)) * 0x10000) + (0x10000 - 1);
                int itop = CCast.I32(vtop);
                int ibottom = CCast.I32(vbottom);
                for (int v = itop; v < ibottom; v++)
                {
                    pspan->U = u >> 16;
                    pspan->V = v;
                    u += uStep;
                    pspan++;
                }
            }
            vtop = vbottom;
            i--;
            if (i == 0)
                i = _sprNump;
        } while (i != lmaxindex);
    }

    /// <summary>D_SpriteScanRightEdge: each scan's count, from the top vertex down the increasing indices, clamped to the view.</summary>
    private void SpriteScanRightEdge()
    {
        SSpan* pspan = _spriteSpans;
        int i = _sprMinIndex;
        float vvert = _outVerts[i].V;
        if (vvert < _fvrectyAdj)
            vvert = _fvrectyAdj;
        if (vvert > _fvrectbottomAdj)
            vvert = _fvrectbottomAdj;
        float vtop = (float)Math.Ceiling(vvert);
        do
        {
            EmitPoint* pvert = _outVerts + i;
            EmitPoint* pnext = pvert + 1;
            float vnext = pnext->V;
            if (vnext < _fvrectyAdj)
                vnext = _fvrectyAdj;
            if (vnext > _fvrectbottomAdj)
                vnext = _fvrectbottomAdj;
            float vbottom = (float)Math.Ceiling(vnext);
            if (vtop < vbottom)
            {
                float uvert = pvert->U;
                if (uvert < _fvrectxAdj)
                    uvert = _fvrectxAdj;
                if (uvert > _fvrectrightAdj)
                    uvert = _fvrectrightAdj;
                float unext = pnext->U;
                if (unext < _fvrectxAdj)
                    unext = _fvrectxAdj;
                if (unext > _fvrectrightAdj)
                    unext = _fvrectrightAdj;
                float du = unext - uvert;
                float dv = vnext - vvert;
                float slope = du / dv;
                int uStep = CCast.I32(slope * 0x10000);
                // adjust u to ceil the integer portion
                int u = CCast.I32((uvert + slope * (vtop - vvert)) * 0x10000) + (0x10000 - 1);
                int itop = CCast.I32(vtop);
                int ibottom = CCast.I32(vbottom);
                for (int v = itop; v < ibottom; v++)
                {
                    pspan->Count = (u >> 16) - pspan->U;
                    u += uStep;
                    pspan++;
                }
            }
            vtop = vbottom;
            vvert = vnext;
            i++;
            if (i == _sprNump)
                i = 0;
        } while (i != _sprMaxIndex);
        pspan->Count = DsSpanListEnd;   // mark the end of the span list
    }

    /// <summary>D_SpriteCalculateGradients: s/z, t/z, 1/z on the screen and the frame's offsets.</summary>
    private void SpriteCalculateGradients()
    {
        Vec3 pNormal = TransformVector(_sprVpn);
        Vec3 pSaxis = TransformVector(_sprVright);
        Vec3 pTaxis = TransformVector(_sprVup);
        pTaxis = new Vec3(-pTaxis.X, -pTaxis.Y, -pTaxis.Z);
        float distinv = (float)(1.0 / -Vec3.Dot(_modelOrg, _sprVpn));
        _dSdivzStepU = pSaxis.X * _xscaleinv;
        _dTdivzStepU = pTaxis.X * _xscaleinv;
        _dSdivzStepV = -pSaxis.Y * _yscaleinv;
        _dTdivzStepV = -pTaxis.Y * _yscaleinv;
        _dZiStepU = pNormal.X * _xscaleinv * distinv;
        _dZiStepV = -pNormal.Y * _yscaleinv * distinv;
        _dSdivzOrigin = pSaxis.Z - _xcenter * _dSdivzStepU - _ycenter * _dSdivzStepV;
        _dTdivzOrigin = pTaxis.Z - _xcenter * _dTdivzStepU - _ycenter * _dTdivzStepV;
        _dZiOrigin = pNormal.Z * distinv - _xcenter * _dZiStepU - _ycenter * _dZiStepV;
        Vec3 pTemp1 = TransformVector(_modelOrg);
        _sAdjust = CCast.I32(Vec3.Dot(pTemp1, pSaxis) * 0x10000 + 0.5) - (-(_cacheWidth >> 1) << 16);
        _tAdjust = CCast.I32(Vec3.Dot(pTemp1, pTaxis) * 0x10000 + 0.5) - (-(_spriteHeight >> 1) << 16);
        // -1 (-epsilon) so we never wander off the edge of the texture
        _bbExtentS = (_cacheWidth << 16) - 1;
        _bbExtentT = (_spriteHeight << 16) - 1;
    }

    /// <summary>D_DrawSprite: the projected poster's scans, if it crosses one.</summary>
    private void DrawSpriteSpans()
    {
        // find the top and bottom vertices, and make sure there's at least one scan to draw
        float ymin = (float)999999.9, ymax = (float)-999999.9;
        for (int i = 0; i < _sprNump; i++)
        {
            float v = _outVerts[i].V;
            if (v < ymin)
            {
                ymin = v;
                _sprMinIndex = i;
            }
            if (v > ymax)
            {
                ymax = v;
                _sprMaxIndex = i;
            }
        }
        ymin = (float)Math.Ceiling(ymin);
        ymax = (float)Math.Ceiling(ymax);
        if (ymin >= ymax)
            return;     // doesn't cross any scans at all
        _cacheWidth = _spriteFrame->Width;
        _spriteHeight = _spriteFrame->Height;
        _cacheBlock = _spriteFrame->Pixels;
        // copy the first vertex to the last vertex, so we don't have to deal with wrapping
        _outVerts[_sprNump] = _outVerts[0];
        SpriteCalculateGradients();
        SpriteScanLeftEdge();
        SpriteScanRightEdge();
        SpriteDrawSpans(_spriteSpans);
    }

    /// <summary>D_SpriteDrawSpans: the frame's texels, 255 transparent, z-tested and z-written.</summary>
    private void SpriteDrawSpans(SSpan* pspan)
    {
        int sstep = 0, tstep = 0;   // keep compiler happy
        byte* pbase = _cacheBlock;
        float sdivz8stepu = _dSdivzStepU * 8;
        float tdivz8stepu = _dTdivzStepU * 8;
        float zi8stepu = _dZiStepU * 8;
        // we count on FP exceptions being turned off to avoid range problems
        int izistep = CCast.I32(_dZiStepU * 0x8000 * 0x10000);
        do
        {
            byte* pdest = _viewBuffer + _screenWidth * pspan->V + pspan->U;
            short* pz = _zBuffer + ZWidth * pspan->V + pspan->U;
            int count = pspan->Count;
            if (count > 0)
            {
                // calculate the initial s/z, t/z, 1/z, s, and t and clamp
                float du = pspan->U;
                float dv = pspan->V;
                float sdivz = _dSdivzOrigin + dv * _dSdivzStepV + du * _dSdivzStepU;
                float tdivz = _dTdivzOrigin + dv * _dTdivzStepV + du * _dTdivzStepU;
                float zi = _dZiOrigin + dv * _dZiStepV + du * _dZiStepU;
                float z = (float)0x10000 / zi;  // prescale to 16.16 fixed-point
                // we count on FP exceptions being turned off to avoid range problems
                int izi = CCast.I32(zi * 0x8000 * 0x10000);
                int s = CCast.I32(sdivz * z) + _sAdjust;
                if (s > _bbExtentS)
                    s = _bbExtentS;
                else if (s < 0)
                    s = 0;
                int t = CCast.I32(tdivz * z) + _tAdjust;
                if (t > _bbExtentT)
                    t = _bbExtentT;
                else if (t < 0)
                    t = 0;
                do
                {
                    // calculate s and t at the far end of the span
                    int spancount = count >= 8 ? 8 : count;
                    count -= spancount;
                    int snext, tnext;
                    if (count != 0)
                    {
                        sdivz += sdivz8stepu;
                        tdivz += tdivz8stepu;
                        zi += zi8stepu;
                        z = (float)0x10000 / zi;    // prescale to 16.16 fixed-point
                        snext = CCast.I32(sdivz * z) + _sAdjust;
                        if (snext > _bbExtentS)
                            snext = _bbExtentS;
                        else if (snext < 8)
                            snext = 8;
                        tnext = CCast.I32(tdivz * z) + _tAdjust;
                        if (tnext > _bbExtentT)
                            tnext = _bbExtentT;
                        else if (tnext < 8)
                            tnext = 8;
                        sstep = (snext - s) >> 3;
                        tstep = (tnext - t) >> 3;
                    }
                    else
                    {
                        float spancountminus1 = spancount - 1;
                        sdivz += _dSdivzStepU * spancountminus1;
                        tdivz += _dTdivzStepU * spancountminus1;
                        zi += _dZiStepU * spancountminus1;
                        z = (float)0x10000 / zi;    // prescale to 16.16 fixed-point
                        snext = CCast.I32(sdivz * z) + _sAdjust;
                        if (snext > _bbExtentS)
                            snext = _bbExtentS;
                        else if (snext < 8)
                            snext = 8;
                        tnext = CCast.I32(tdivz * z) + _tAdjust;
                        if (tnext > _bbExtentT)
                            tnext = _bbExtentT;
                        else if (tnext < 8)
                            tnext = 8;
                        if (spancount > 1)
                        {
                            sstep = (snext - s) / (spancount - 1);
                            tstep = (tnext - t) / (spancount - 1);
                        }
                    }
                    do
                    {
                        byte btemp = pbase[(s >> 16) + (t >> 16) * _cacheWidth];
                        if (btemp != 255 && *pz <= izi >> 16)
                        {
                            *pz = (short)(izi >> 16);
                            *pdest = btemp;
                        }
                        izi += izistep;
                        pdest++;
                        pz++;
                        s += sstep;
                        t += tstep;
                    } while (--spancount > 0);
                    s = snext;
                    t = tnext;
                } while (count > 0);
            }
            pspan++;
        } while (pspan->Count != DsSpanListEnd);
    }
}
