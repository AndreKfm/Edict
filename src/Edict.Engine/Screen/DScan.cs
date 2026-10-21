// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The span drawers (d_edge.c, d_scan.c): each surface's spans textured from its cache block (D_DrawSpans8, with
/// a perspective-correct s and t every 8 pixels), turbulent for water (Turbulent8), the sky (RSky.cs), the
/// background in r_clearcolor; the z-buffer written for every span (D_DrawZSpans); the water warp of the finished
/// view (D_WarpScreen).
/// </summary>
public sealed unsafe partial class Screen
{
    private int _miplevel;                            // d_edge.c's miplevel
    private Vec3 _transformedModelOrg;                // transformed_modelorg
    private float _dSdivzStepU, _dTdivzStepU, _dSdivzStepV, _dTdivzStepV, _dSdivzOrigin, _dTdivzOrigin;
    private float _dZiStepU, _dZiStepV, _dZiOrigin;
    private int _sAdjust, _tAdjust, _bbExtentS, _bbExtentT;
    private byte* _cacheBlock;                        // cacheblock
    private int _cacheWidth;                          // cachewidth

    /// <summary>D_MipLevelForScale.</summary>
    private int MipLevelForScale(float scale)
    {
        int lmiplevel;
        if (scale >= _scaleMip[0])
            lmiplevel = 0;
        else if (scale >= _scaleMip[1])
            lmiplevel = 1;
        else if (scale >= _scaleMip[2])
            lmiplevel = 2;
        else
            lmiplevel = 3;
        if (lmiplevel < _minMip)
            lmiplevel = _minMip;
        return lmiplevel;
    }

    /// <summary>D_DrawSolidSurface: the spans in one color (the byte stores and the int stores write the same).</summary>
    private void DrawSolidSurface(int spans, int color)
    {
        for (int sp = spans; sp >= 0; sp = _spans[sp].Next)
        {
            ref ESpan span = ref _spans[sp];
            new Span<byte>(_viewBuffer + _screenWidth * span.V + span.U, span.Count).Fill((byte)color);
        }
    }

    /// <summary>D_CalcGradients: s/z, t/z on the screen and the texture's offsets for the face at the mip level.</summary>
    private void CalcGradients(MSurface* pface)
    {
        float mipscale = (float)(1.0 / (float)(1 << _miplevel));
        MTexInfo* ti = pface->TexInfo;
        Vec3 pSaxis = TransformVector(new Vec3(ti->Vecs[0], ti->Vecs[1], ti->Vecs[2]));
        Vec3 pTaxis = TransformVector(new Vec3(ti->Vecs[4], ti->Vecs[5], ti->Vecs[6]));

        float t = _xscaleinv * mipscale;
        _dSdivzStepU = pSaxis.X * t;
        _dTdivzStepU = pTaxis.X * t;
        t = _yscaleinv * mipscale;
        _dSdivzStepV = -pSaxis.Y * t;
        _dTdivzStepV = -pTaxis.Y * t;
        _dSdivzOrigin = pSaxis.Z * mipscale - _xcenter * _dSdivzStepU - _ycenter * _dSdivzStepV;
        _dTdivzOrigin = pTaxis.Z * mipscale - _xcenter * _dTdivzStepU - _ycenter * _dTdivzStepV;

        var pTemp1 = new Vec3(_transformedModelOrg.X * mipscale, _transformedModelOrg.Y * mipscale,
            _transformedModelOrg.Z * mipscale);
        t = 0x10000 * mipscale;
        _sAdjust = CCast.I32((float)(CCast.I32(Vec3.Dot(pTemp1, pSaxis) * 0x10000 + 0.5)
            - ((pface->TextureMins[0] << 16) >> _miplevel)) + ti->Vecs[3] * t);
        _tAdjust = CCast.I32((float)(CCast.I32(Vec3.Dot(pTemp1, pTaxis) * 0x10000 + 0.5)
            - ((pface->TextureMins[1] << 16) >> _miplevel)) + ti->Vecs[7] * t);
        // -1 (-epsilon) so we never wander off the edge of the texture
        _bbExtentS = ((pface->Extents[0] << 16) >> _miplevel) - 1;
        _bbExtentT = ((pface->Extents[1] << 16) >> _miplevel) - 1;
    }

    /// <summary>D_DrawSurfaces: every surface with spans, by its kind.</summary>
    private void DrawSurfaces()
    {
        _currentEntity = default;   // currententity = &cl_entities[0]
        _transformedModelOrg = TransformVector(_modelOrg);
        Vec3 worldTransformedModelOrg = _transformedModelOrg;
        if (CvarValue("r_drawflat\0"u8) != 0)
            throw NotPortedYet;     // the color is the low byte of the surface's address
        for (int si = 1; si < _surfaceP; si++)
        {
            ref RSurf s = ref _surfaces[si];
            if (s.Spans < 0)
                continue;
            _drawnPolyCount++;
            if (!s.InSubmodel && s.Data != null)
                _drawnSurface[(int)(s.Data - _cl.WorldModel->Surfaces)] = _frameCount;
            _dZiStepU = s.DZiStepU;
            _dZiStepV = s.DZiStepV;
            _dZiOrigin = s.DZiOrigin;
            if ((s.Flags & SurfFlags.DrawSky) != 0)
            {
                if (!_skyMade)
                    MakeSky();
                DrawSkyScans8(s.Spans);
                DrawZSpans(s.Spans);
            }
            else if ((s.Flags & SurfFlags.DrawBackground) != 0)
            {
                // set up a gradient for the background surface that places it effectively at infinity distance
                // from the viewpoint
                _dZiStepU = 0;
                _dZiStepV = 0;
                _dZiOrigin = (float)-0.9;
                DrawSolidSurface(s.Spans, CCast.I32(CvarValue("r_clearcolor\0"u8)) & 0xFF);
                DrawZSpans(s.Spans);
            }
            else if ((s.Flags & SurfFlags.DrawTurb) != 0)
            {
                MSurface* pface = s.Data;
                _miplevel = 0;
                Texture* tx = pface->TexInfo->Texture;
                _cacheBlock = tx->Pixels + tx->MipOffsets[0];
                _cacheWidth = 64;
                if (s.InSubmodel)
                    EnterBmodelSurface(s.Entity);
                CalcGradients(pface);
                Turbulent8(s.Spans);
                DrawZSpans(s.Spans);
                if (s.InSubmodel)
                    LeaveBmodelSurface(worldTransformedModelOrg);
            }
            else
            {
                if (s.InSubmodel)
                    EnterBmodelSurface(s.Entity);
                MSurface* pface = s.Data;
                _miplevel = MipLevelForScale(s.NearZi * _scaleForMip * pface->TexInfo->MipAdjust);
                // FIXME: make this passed in to D_CacheSurface
                SurfCache* pcurrentcache = CacheSurface(pface, _miplevel, Entity(_currentEntity).Frame);
                _cacheBlock = pcurrentcache->Data;
                _cacheWidth = (int)pcurrentcache->Width;
                CalcGradients(pface);
                DrawSpans8(s.Spans);
                DrawZSpans(s.Spans);
                if (s.InSubmodel)
                    LeaveBmodelSurface(worldTransformedModelOrg);
            }
        }
    }

    /// <summary>A brush entity's surface: its origin relative to the view, the view rotated into it (D_DrawSurfaces).</summary>
    private void EnterBmodelSurface(EntityRef entity)
    {
        _currentEntity = entity;
        Vec3 localModelOrg = Vec3.Subtract(_rOrigin, Entity(entity).Origin);
        _transformedModelOrg = TransformVector(localModelOrg);
        RotateBmodel();     // FIXME: don't mess with the frustum, make entity passed in
    }

    /// <summary>Back to the world's drawing state after a brush entity's surface.</summary>
    private void LeaveBmodelSurface(in Vec3 worldTransformedModelOrg)
    {
        _currentEntity = default;
        _transformedModelOrg = worldTransformedModelOrg;
        RestoreWorldView(_baseModelOrg);
    }

    /// <summary>D_DrawSpans8: s and t exact every 8 pixels, linear between.</summary>
    private void DrawSpans8(int pspan)
    {
        int sstep = 0, tstep = 0;   // keep compiler happy
        byte* pbase = _cacheBlock;
        float sdivz8stepu = _dSdivzStepU * 8;
        float tdivz8stepu = _dTdivzStepU * 8;
        float zi8stepu = _dZiStepU * 8;
        do
        {
            ref ESpan span = ref _spans[pspan];
            byte* pdest = _viewBuffer + _screenWidth * span.V + span.U;
            int count = span.Count;
            // calculate the initial s/z, t/z, 1/z, s, and t and clamp
            float du = span.U;
            float dv = span.V;
            float sdivz = _dSdivzOrigin + dv * _dSdivzStepV + du * _dSdivzStepU;
            float tdivz = _dTdivzOrigin + dv * _dTdivzStepV + du * _dTdivzStepU;
            float zi = _dZiOrigin + dv * _dZiStepV + du * _dZiStepU;
            float z = (float)0x10000 / zi;  // prescale to 16.16 fixed-point
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
                    // calculate s/z, t/z, zi->fixed s and t at far end of span, calculate s and t steps across span
                    // by shifting
                    sdivz += sdivz8stepu;
                    tdivz += tdivz8stepu;
                    zi += zi8stepu;
                    z = (float)0x10000 / zi;    // prescale to 16.16 fixed-point
                    snext = CCast.I32(sdivz * z) + _sAdjust;
                    if (snext > _bbExtentS)
                        snext = _bbExtentS;
                    else if (snext < 8)
                        snext = 8;  // prevent round-off error on <0 steps from causing overstepping
                    tnext = CCast.I32(tdivz * z) + _tAdjust;
                    if (tnext > _bbExtentT)
                        tnext = _bbExtentT;
                    else if (tnext < 8)
                        tnext = 8;  // guard against round-off error on <0 steps
                    sstep = (snext - s) >> 3;
                    tstep = (tnext - t) >> 3;
                }
                else
                {
                    // calculate s/z, t/z, zi->fixed s and t at last pixel in span (so can't step off polygon),
                    // clamp, calculate s and t steps across span by division, biasing steps low so we don't run
                    // off the texture
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
                    *pdest++ = pbase[(s >> 16) + (t >> 16) * _cacheWidth];
                    s += sstep;
                    t += tstep;
                } while (--spancount > 0);
                s = snext;
                t = tnext;
            } while (count > 0);
        } while ((pspan = _spans[pspan].Next) >= 0);
    }

    /// <summary>Turbulent8: the 64x64 texture warped by the sine table, s and t exact every 16 pixels.</summary>
    private void Turbulent8(int pspan)
    {
        fixed (int* sintable = _sinTable)
        {
            int* turb = sintable + (CCast.I32(_cl.Time * Speed) & (Cycle - 1));
            int sstep = 0, tstep = 0;   // keep compiler happy
            byte* pbase = _cacheBlock;
            float sdivz16stepu = _dSdivzStepU * 16;
            float tdivz16stepu = _dTdivzStepU * 16;
            float zi16stepu = _dZiStepU * 16;
            do
            {
                ref ESpan span = ref _spans[pspan];
                byte* pdest = _viewBuffer + _screenWidth * span.V + span.U;
                int count = span.Count;
                // calculate the initial s/z, t/z, 1/z, s, and t and clamp
                float du = span.U;
                float dv = span.V;
                float sdivz = _dSdivzOrigin + dv * _dSdivzStepV + du * _dSdivzStepU;
                float tdivz = _dTdivzOrigin + dv * _dTdivzStepV + du * _dTdivzStepU;
                float zi = _dZiOrigin + dv * _dZiStepV + du * _dZiStepU;
                float z = (float)0x10000 / zi;  // prescale to 16.16 fixed-point
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
                    int spancount = count >= 16 ? 16 : count;
                    count -= spancount;
                    int snext, tnext;
                    if (count != 0)
                    {
                        sdivz += sdivz16stepu;
                        tdivz += tdivz16stepu;
                        zi += zi16stepu;
                        z = (float)0x10000 / zi;    // prescale to 16.16 fixed-point
                        snext = CCast.I32(sdivz * z) + _sAdjust;
                        if (snext > _bbExtentS)
                            snext = _bbExtentS;
                        else if (snext < 16)
                            snext = 16;
                        tnext = CCast.I32(tdivz * z) + _tAdjust;
                        if (tnext > _bbExtentT)
                            tnext = _bbExtentT;
                        else if (tnext < 16)
                            tnext = 16;
                        sstep = (snext - s) >> 4;
                        tstep = (tnext - t) >> 4;
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
                        else if (snext < 16)
                            snext = 16;
                        tnext = CCast.I32(tdivz * z) + _tAdjust;
                        if (tnext > _bbExtentT)
                            tnext = _bbExtentT;
                        else if (tnext < 16)
                            tnext = 16;
                        if (spancount > 1)
                        {
                            sstep = (snext - s) / (spancount - 1);
                            tstep = (tnext - t) / (spancount - 1);
                        }
                    }
                    s &= (Cycle << 16) - 1;
                    t &= (Cycle << 16) - 1;
                    // D_DrawTurbulent8Span
                    do
                    {
                        int sturb = ((s + turb[(t >> 16) & (Cycle - 1)]) >> 16) & 63;
                        int tturb = ((t + turb[(s >> 16) & (Cycle - 1)]) >> 16) & 63;
                        *pdest++ = pbase[(tturb << 6) + sturb];
                        s += sstep;
                        t += tstep;
                    } while (--spancount > 0);
                    s = snext;
                    t = tnext;
                } while (count > 0);
            } while ((pspan = _spans[pspan].Next) >= 0);
        }
    }

    /// <summary>
    /// D_DrawZSpans: 1/z as 16 bits per pixel. C writes pixel pairs as one int when the address allows (the
    /// z-buffer is 4-aligned, so on odd pixels first); the pair's second half is ORed onto the first's
    /// sign-extended upper bits, as there.
    /// </summary>
    private void DrawZSpans(int pspan)
    {
        // we count on FP exceptions being turned off to avoid range problems
        int izistep = CCast.I32(_dZiStepU * 0x8000 * 0x10000);
        do
        {
            ref ESpan span = ref _spans[pspan];
            int index = ZWidth * span.V + span.U;
            short* pdest = _zBuffer + index;
            int count = span.Count;
            // calculate the initial 1/z
            float du = span.U;
            float dv = span.V;
            double zi = _dZiOrigin + dv * _dZiStepV + du * _dZiStepU;
            int izi = CCast.I32(zi * 0x8000 * 0x10000);
            if ((index & 1) != 0)
            {
                *pdest++ = (short)(izi >> 16);
                izi += izistep;
                count--;
            }
            int doublecount = count >> 1;
            if (doublecount > 0)
            {
                do
                {
                    uint ltemp = (uint)(izi >> 16);
                    izi += izistep;
                    ltemp |= (uint)izi & 0xFFFF0000;
                    izi += izistep;
                    *(uint*)pdest = ltemp;
                    pdest += 2;
                } while (--doublecount > 0);
            }
            if ((count & 1) != 0)
                *pdest = (short)(izi >> 16);
        } while ((pspan = _spans[pspan].Next) >= 0);
    }

    /// <summary>D_WarpScreen: the view from the warp buffer onto the screen, sine-warped and slightly compressed.</summary>
    private void WarpScreen()
    {
        int w = RefdefVrect.Width;
        int h = RefdefVrect.Height;
        float wratio = w / (float)_scrVrect.Width;
        float hratio = h / (float)_scrVrect.Height;
        byte** rowptr = stackalloc byte*[MaxHeight + Amp2 * 2];
        int* column = stackalloc int[MaxWidth + Amp2 * 2];
        for (int v = 0; v < _scrVrect.Height + Amp2 * 2; v++)
            rowptr[v] = _viewBuffer + RefdefVrect.Y * _screenWidth
                + _screenWidth * CCast.I32((float)v * hratio * h / (h + Amp2 * 2));
        for (int u = 0; u < _scrVrect.Width + Amp2 * 2; u++)
            column[u] = RefdefVrect.X + CCast.I32((float)u * wratio * w / (w + Amp2 * 2));
        fixed (int* intsintable = _intSinTable)
        {
            int* turb = intsintable + (CCast.I32(_cl.Time * Speed) & (Cycle - 1));
            byte* dest = _buffer + _scrVrect.Y * Width + _scrVrect.X;
            for (int v = 0; v < _scrVrect.Height; v++, dest += Width)
            {
                int* col = column + turb[v];
                byte** row = rowptr + v;
                for (int u = 0; u < _scrVrect.Width; u++)
                    dest[u] = row[turb[u]][col[u]];
            }
        }
    }
}
