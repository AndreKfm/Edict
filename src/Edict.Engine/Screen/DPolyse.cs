// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The alias models' triangles (d_polyse.c): far away, recursively subdivided into points (D_DrawSubdiv); else
/// rasterized with affine s, t, light and 1/z steps from the left edge's span packages (D_DrawNonSubdiv). The skin
/// row table is C's skinstart + row * skinwidth, computed where it is read.
/// </summary>
public sealed unsafe partial class Screen
{
    private const int DpsMaxSpans = MaxHeight + 1;    // 1 extra for spanpackage that marks end
    private const int SpanEnd = -999999;

    /// <summary><c>spanpackage_t</c>.</summary>
    private struct SpanPackage
    {
        public byte* PDest;
        public short* Pz;
        public int Count;
        public byte* PTex;
        public int SFrac, TFrac, Light, Zi;
    }

    /// <summary><c>edgetable</c>: which of r_p0..r_p2 (0, 1, 2; -1 NULL) the left and right edges run through.</summary>
    private readonly struct EdgeTable(int isflattop, int numleft, int l0, int l1, int l2, int numright, int r0, int r1, int r2)
    {
        public readonly int IsFlatTop = isflattop, NumLeftEdges = numleft, L0 = l0, L1 = l1, L2 = l2,
            NumRightEdges = numright, R0 = r0, R1 = r1, R2 = r2;
    }

    private static readonly EdgeTable[] EdgeTables =
    [
        new(0, 1, 0, 2, -1, 2, 0, 1, 2),
        new(0, 2, 1, 0, 2, 1, 1, 2, -1),
        new(1, 1, 0, 2, -1, 1, 1, 2, -1),
        new(0, 1, 1, 0, -1, 2, 1, 2, 0),
        new(0, 2, 0, 2, 1, 1, 0, 1, -1),
        new(0, 1, 2, 1, -1, 1, 2, 0, -1),
        new(0, 1, 2, 1, -1, 2, 2, 0, 1),
        new(0, 2, 2, 1, 0, 1, 2, 0, -1),
        new(0, 1, 1, 0, -1, 1, 1, 2, -1),
        new(1, 1, 2, 1, -1, 1, 0, 1, -1),
        new(1, 1, 1, 0, -1, 1, 2, 0, -1),
        new(0, 1, 0, 2, -1, 1, 0, 1, -1),
    ];

    // r_affinetridesc and the skin
    private bool _drawType;
    private byte* _pskin;
    private int _skinWidth, _skinHeight, _seamFixupX16;
    private byte* _aColormap;                         // acolormap
    private byte* _colormapNative;                    // vid.colormap
    private byte* _translations;                      // cl.scores[i].translations, VID_GRADES*256 each

    private int* _rp;                                 // r_p0, r_p1, r_p2: 6 ints each
    private SpanPackage* _aSpans;                     // a_spans
    private int _edgeTableIndex;                      // pedgetable
    private byte* _dpColormap;                        // d_pcolormap
    private int _xdenom;                              // d_xdenom
    private int _aSstepxfrac, _aTstepxfrac, _rLstepx, _aSTstepxwhole;
    private int _rSstepx, _rTstepx, _rLstepy, _rSstepy, _rTstepy, _rZistepx, _rZistepy;
    private int _aSpanCount, _countExtraStep;
    private SpanPackage* _pedgeSpanPackage;
    private byte* _dPdest, _dPtex;
    private short* _dPz;
    private int _dSfrac, _dTfrac, _dLight, _dZi;
    private int _ptexExtraStep, _sfracExtraStep, _tfracExtraStep, _lightExtraStep, _pdestExtraStep;
    private int _lightBaseStep, _pdestBaseStep, _ptexBaseStep, _sfracBaseStep, _tfracBaseStep;
    private int _ziExtraStep, _ziBaseStep, _pzExtraStep, _pzBaseStep;
    private int _ubaseStep, _errorTerm, _errorAdjustUp, _errorAdjustDown;

    [NoAllocUnchecked("load path")]
    private void InitPolyset()
    {
        _rp = (int*)NativeMemory.AllocZeroed(18, sizeof(int));
        _aSpans = (SpanPackage*)NativeMemory.AllocZeroed(DpsMaxSpans + 2, (nuint)sizeof(SpanPackage));
        _colormapNative = (byte*)NativeMemory.Alloc((nuint)_colormap.Length);
        _colormap.CopyTo(new Span<byte>(_colormapNative, _colormap.Length));
        _translations = (byte*)NativeMemory.AllocZeroed(ClientState.MaxScoreboard * VidGrades * 256);
    }

    private const int TopRange = 16, BottomRange = 96;  // the player's shirt and pants colors

    /// <summary>The colormap of an entity's colormap code (1 vid.colormap, 2 + i cl.scores[i].translations).</summary>
    private byte* ColormapOf(int code) => code == 1 ? _colormapNative : _translations + (code - 2) * VidGrades * 256;

    /// <summary>Hunk_AllocName's zeroed cl.scores at the server info: the translations of the slots cleared.</summary>
    public void ClearTranslations(int maxclients) =>
        new Span<byte>(_translations, maxclients * VidGrades * 256).Clear();

    /// <summary>CL_NewTranslation: vid.colormap with the shirt and pants ranges replaced by the player's colors.</summary>
    public void NewTranslation(int slot, int colors)
    {
        byte* dest = _translations + slot * VidGrades * 256;
        byte* source = _colormapNative;
        new ReadOnlySpan<byte>(source, VidGrades * 256).CopyTo(new Span<byte>(dest, VidGrades * 256));
        int top = colors & 0xf0;
        int bottom = (colors & 15) << 4;
        for (int i = 0; i < VidGrades; i++, dest += 256, source += 256)
        {
            for (int j = 0; j < 16; j++)
            {
                // the artists made some backwards ranges.  sigh.
                dest[TopRange + j] = top < 128 ? source[top + j] : source[top + 15 - j];
                dest[BottomRange + j] = bottom < 128 ? source[bottom + j] : source[bottom + 15 - j];
            }
        }
    }

    private int* P(int i) => _rp + i * 6;

    /// <summary>D_PolysetDraw: the triangles subdivided (far) or rasterized.</summary>
    private void PolysetDraw(FinalVert* pfv, MTriangle* ptri, int numtriangles)
    {
        if (_drawType)
            DrawSubdiv(pfv, ptri, numtriangles);
        else
            DrawNonSubdiv(pfv, ptri, numtriangles);
    }

    /// <summary>The skin's texel at 16.16 s and t (skintable[t >> 16][s >> 16]).</summary>
    private byte SkinTexel(int s, int t) => _pskin[(t >> 16) * _skinWidth + (s >> 16)];

    /// <summary>D_PolysetDrawFinalVerts: each vertex as a point, z-tested.</summary>
    private void PolysetDrawFinalVerts(FinalVert* fv, int numverts)
    {
        for (int i = 0; i < numverts; i++, fv++)
        {
            // valid triangle coordinates for filling can include the bottom and right clip edges, due to the
            // fill rule; these shouldn't be drawn
            if (fv->V[0] < _vrectRight && fv->V[1] < _vrectBottom)
            {
                int z = fv->V[5] >> 16;
                short* zbuf = _zBuffer + fv->V[1] * ZWidth + fv->V[0];
                if (z >= *zbuf)
                {
                    *zbuf = (short)z;
                    int pix = SkinTexel(fv->V[2], fv->V[3]);
                    pix = _aColormap[pix + (fv->V[4] & 0xFF00)];
                    _viewBuffer[_scanTable[fv->V[1]] + fv->V[0]] = (byte)pix;
                }
            }
        }
    }

    /// <summary>D_DrawSubdiv: the front-facing triangles subdivided to points, lit flat by vertex 0.</summary>
    private void DrawSubdiv(FinalVert* pfv, MTriangle* ptri, int lnumtriangles)
    {
        for (int i = 0; i < lnumtriangles; i++)
        {
            FinalVert* index0 = pfv + ptri[i].VertIndex[0];
            FinalVert* index1 = pfv + ptri[i].VertIndex[1];
            FinalVert* index2 = pfv + ptri[i].VertIndex[2];
            if ((index0->V[1] - index1->V[1]) * (index0->V[0] - index2->V[0])
                - (index0->V[0] - index1->V[0]) * (index0->V[1] - index2->V[1]) >= 0)
                continue;
            _dpColormap = _aColormap + (index0->V[4] & 0xFF00);
            if (ptri[i].FacesFront != 0)
                PolysetRecursiveTriangle(index0->V, index1->V, index2->V);
            else
            {
                int s0 = index0->V[2], s1 = index1->V[2], s2 = index2->V[2];
                if ((index0->Flags & AliasOnSeam) != 0)
                    index0->V[2] += _seamFixupX16;
                if ((index1->Flags & AliasOnSeam) != 0)
                    index1->V[2] += _seamFixupX16;
                if ((index2->Flags & AliasOnSeam) != 0)
                    index2->V[2] += _seamFixupX16;
                PolysetRecursiveTriangle(index0->V, index1->V, index2->V);
                index0->V[2] = s0;
                index1->V[2] = s1;
                index2->V[2] = s2;
            }
        }
    }

    /// <summary>D_DrawNonSubdiv: the front-facing triangles rasterized.</summary>
    private void DrawNonSubdiv(FinalVert* pfv, MTriangle* ptri, int lnumtriangles)
    {
        for (int i = 0; i < lnumtriangles; i++, ptri++)
        {
            FinalVert* index0 = pfv + ptri->VertIndex[0];
            FinalVert* index1 = pfv + ptri->VertIndex[1];
            FinalVert* index2 = pfv + ptri->VertIndex[2];
            _xdenom = (index0->V[1] - index1->V[1]) * (index0->V[0] - index2->V[0])
                      - (index0->V[0] - index1->V[0]) * (index0->V[1] - index2->V[1]);
            if (_xdenom >= 0)
                continue;
            for (int k = 0; k < 6; k++)
            {
                P(0)[k] = index0->V[k];
                P(1)[k] = index1->V[k];
                P(2)[k] = index2->V[k];
            }
            if (ptri->FacesFront == 0)
            {
                if ((index0->Flags & AliasOnSeam) != 0)
                    P(0)[2] += _seamFixupX16;
                if ((index1->Flags & AliasOnSeam) != 0)
                    P(1)[2] += _seamFixupX16;
                if ((index2->Flags & AliasOnSeam) != 0)
                    P(2)[2] += _seamFixupX16;
            }
            PolysetSetEdgeTable();
            RasterizeAliasPolySmooth();
        }
    }

    /// <summary>D_PolysetRecursiveTriangle: split the longest-first edge until each is a pixel, drawing the midpoints.</summary>
    private void PolysetRecursiveTriangle(int* lp1, int* lp2, int* lp3)
    {
        int d = lp2[0] - lp1[0];
        bool split = d < -1 || d > 1;
        if (!split)
        {
            d = lp2[1] - lp1[1];
            split = d < -1 || d > 1;
        }
        if (!split)
        {
            d = lp3[0] - lp2[0];
            bool split2 = d < -1 || d > 1;
            if (!split2)
            {
                d = lp3[1] - lp2[1];
                split2 = d < -1 || d > 1;
            }
            if (split2)
            {
                int* temp = lp1;
                lp1 = lp2;
                lp2 = lp3;
                lp3 = temp;
            }
            else
            {
                d = lp1[0] - lp3[0];
                bool split3 = d < -1 || d > 1;
                if (!split3)
                {
                    d = lp1[1] - lp3[1];
                    split3 = d < -1 || d > 1;
                }
                if (!split3)
                    return;     // entire tri is filled
                int* temp = lp1;
                lp1 = lp3;
                lp3 = lp2;
                lp2 = temp;
            }
        }

        // split this edge
        int* n = stackalloc int[6];
        n[0] = (lp1[0] + lp2[0]) >> 1;
        n[1] = (lp1[1] + lp2[1]) >> 1;
        n[2] = (lp1[2] + lp2[2]) >> 1;
        n[3] = (lp1[3] + lp2[3]) >> 1;
        n[5] = (lp1[5] + lp2[5]) >> 1;

        // draw the point if splitting a leading edge
        if (!(lp2[1] > lp1[1]) && !(lp2[1] == lp1[1] && lp2[0] < lp1[0]))
        {
            int z = n[5] >> 16;
            short* zbuf = _zBuffer + n[1] * ZWidth + n[0];
            if (z >= *zbuf)
            {
                *zbuf = (short)z;
                int pix = _dpColormap[SkinTexel(n[2], n[3])];
                _viewBuffer[_scanTable[n[1]] + n[0]] = (byte)pix;
            }
        }
        // recursively continue
        PolysetRecursiveTriangle(lp3, lp1, n);
        PolysetRecursiveTriangle(lp3, n, lp2);
    }

    /// <summary>D_PolysetScanLeftEdge: a span package per scan of the left edge, stepped with the error term.</summary>
    private void PolysetScanLeftEdge(int height)
    {
        do
        {
            SpanPackage* p = _pedgeSpanPackage;
            p->PDest = _dPdest;
            p->Pz = _dPz;
            p->Count = _aSpanCount;
            p->PTex = _dPtex;
            p->SFrac = _dSfrac;
            p->TFrac = _dTfrac;
            // FIXME: need to clamp l, s, t, at both ends?
            p->Light = _dLight;
            p->Zi = _dZi;
            _pedgeSpanPackage++;
            _errorTerm += _errorAdjustUp;
            if (_errorTerm >= 0)
            {
                _dPdest += _pdestExtraStep;
                _dPz += _pzExtraStep;
                _aSpanCount += _countExtraStep;
                _dPtex += _ptexExtraStep;
                _dSfrac += _sfracExtraStep;
                _dPtex += _dSfrac >> 16;
                _dSfrac &= 0xFFFF;
                _dTfrac += _tfracExtraStep;
                if ((_dTfrac & 0x10000) != 0)
                {
                    _dPtex += _skinWidth;
                    _dTfrac &= 0xFFFF;
                }
                _dLight += _lightExtraStep;
                _dZi += _ziExtraStep;
                _errorTerm -= _errorAdjustDown;
            }
            else
            {
                _dPdest += _pdestBaseStep;
                _dPz += _pzBaseStep;
                _aSpanCount += _ubaseStep;
                _dPtex += _ptexBaseStep;
                _dSfrac += _sfracBaseStep;
                _dPtex += _dSfrac >> 16;
                _dSfrac &= 0xFFFF;
                _dTfrac += _tfracBaseStep;
                if ((_dTfrac & 0x10000) != 0)
                {
                    _dPtex += _skinWidth;
                    _dTfrac &= 0xFFFF;
                }
                _dLight += _lightBaseStep;
                _dZi += _ziBaseStep;
            }
        } while (--height != 0);
    }

    private static readonly SysErrorException BadDenominator = new("FloorDivMod: bad denominator");

    /// <summary>FloorDivMod (mathlib.c).</summary>
    private static void FloorDivMod(double numer, double denom, out int quotient, out int rem)
    {
        if (denom <= 0.0)
            throw BadDenominator;
        int q, r;
        if (numer >= 0.0)
        {
            double x = Math.Floor(numer / denom);
            q = CCast.I32(x);
            r = CCast.I32(Math.Floor(numer - x * denom));
        }
        else
        {
            // perform operations with positive values, and fix mod to make floor-based
            double x = Math.Floor(-numer / denom);
            q = -CCast.I32(x);
            r = CCast.I32(Math.Floor(-numer - x * denom));
            if (r != 0)
            {
                q--;
                r = CCast.I32(denom) - r;
            }
        }
        quotient = q;
        rem = r;
    }

    /// <summary>D_PolysetSetUpForLineScan: the edge's whole step and error terms (the table for short edges).</summary>
    private void PolysetSetUpForLineScan(int startvertu, int startvertv, int endvertu, int endvertv)
    {
        _errorTerm = -1;
        int tm = endvertu - startvertu;
        int tn = endvertv - startvertv;
        if (tm <= 16 && tm >= -15 && tn <= 16 && tn >= -15)
        {
            int idx = (((tm + 15) << 5) + (tn + 15)) * 2;
            _ubaseStep = Adivtab.Table[idx];
            _errorAdjustUp = Adivtab.Table[idx + 1];
            _errorAdjustDown = tn;
        }
        else
        {
            double dm = tm, dn = tn;
            FloorDivMod(dm, dn, out _ubaseStep, out _errorAdjustUp);
            _errorAdjustDown = CCast.I32(dn);
        }
    }

    /// <summary>D_PolysetCalcGradients: the affine steps of light, s, t and 1/z across the triangle.</summary>
    private void PolysetCalcGradients(int skinwidth)
    {
        int* p0 = P(0), p1 = P(1), p2 = P(2);
        float p00MinusP20 = p0[0] - p2[0];
        float p01MinusP21 = p0[1] - p2[1];
        float p10MinusP20 = p1[0] - p2[0];
        float p11MinusP21 = p1[1] - p2[1];
        float xstepdenominv = (float)(1.0 / (float)_xdenom);
        float ystepdenominv = -xstepdenominv;

        // ceil () for light so positive steps are exaggerated, negative steps diminished, pushing us away from
        // underflow toward overflow
        float t0 = p0[4] - p2[4];
        float t1 = p1[4] - p2[4];
        _rLstepx = CCast.I32(Math.Ceiling((t1 * p01MinusP21 - t0 * p11MinusP21) * xstepdenominv));
        _rLstepy = CCast.I32(Math.Ceiling((t1 * p00MinusP20 - t0 * p10MinusP20) * ystepdenominv));
        t0 = p0[2] - p2[2];
        t1 = p1[2] - p2[2];
        _rSstepx = CCast.I32((t1 * p01MinusP21 - t0 * p11MinusP21) * xstepdenominv);
        _rSstepy = CCast.I32((t1 * p00MinusP20 - t0 * p10MinusP20) * ystepdenominv);
        t0 = p0[3] - p2[3];
        t1 = p1[3] - p2[3];
        _rTstepx = CCast.I32((t1 * p01MinusP21 - t0 * p11MinusP21) * xstepdenominv);
        _rTstepy = CCast.I32((t1 * p00MinusP20 - t0 * p10MinusP20) * ystepdenominv);
        t0 = p0[5] - p2[5];
        t1 = p1[5] - p2[5];
        _rZistepx = CCast.I32((t1 * p01MinusP21 - t0 * p11MinusP21) * xstepdenominv);
        _rZistepy = CCast.I32((t1 * p00MinusP20 - t0 * p10MinusP20) * ystepdenominv);
        _aSstepxfrac = _rSstepx & 0xFFFF;
        _aTstepxfrac = _rTstepx & 0xFFFF;
        _aSTstepxwhole = skinwidth * (_rTstepx >> 16) + (_rSstepx >> 16);
    }

    /// <summary>D_PolysetDrawSpans8: each package's span to the right edge, z-tested, lit through the colormap.</summary>
    private void PolysetDrawSpans8(SpanPackage* pspanpackage)
    {
        do
        {
            int lcount = _aSpanCount - pspanpackage->Count;
            _errorTerm += _errorAdjustUp;
            if (_errorTerm >= 0)
            {
                _aSpanCount += _countExtraStep;
                _errorTerm -= _errorAdjustDown;
            }
            else
                _aSpanCount += _ubaseStep;
            if (lcount != 0)
            {
                byte* lpdest = pspanpackage->PDest;
                byte* lptex = pspanpackage->PTex;
                short* lpz = pspanpackage->Pz;
                int lsfrac = pspanpackage->SFrac;
                int ltfrac = pspanpackage->TFrac;
                int llight = pspanpackage->Light;
                int lzi = pspanpackage->Zi;
                do
                {
                    if (lzi >> 16 >= *lpz)
                    {
                        *lpdest = _aColormap[*lptex + (llight & 0xFF00)];
                        *lpz = (short)(lzi >> 16);
                    }
                    lpdest++;
                    lzi += _rZistepx;
                    lpz++;
                    llight += _rLstepx;
                    lptex += _aSTstepxwhole;
                    lsfrac += _aSstepxfrac;
                    lptex += lsfrac >> 16;
                    lsfrac &= 0xFFFF;
                    ltfrac += _aTstepxfrac;
                    if ((ltfrac & 0x10000) != 0)
                    {
                        lptex += _skinWidth;
                        ltfrac &= 0xFFFF;
                    }
                } while (--lcount != 0);
            }
            pspanpackage++;
        } while (pspanpackage->Count != SpanEnd);
    }

    /// <summary>The left edge from top to bottom into span packages (one part of D_RasterizeAliasPolySmooth).</summary>
    private void ScanLeftEdgePart(int* plefttop, int* pleftbottom, int prighttop0, bool first)
    {
        int height = pleftbottom[1] - plefttop[1];
        int ystart = plefttop[1];
        _aSpanCount = plefttop[0] - prighttop0;
        _dPtex = _pskin + (plefttop[2] >> 16) + (plefttop[3] >> 16) * _skinWidth;
        _dSfrac = first ? plefttop[2] & 0xFFFF : 0;
        _dTfrac = first ? plefttop[3] & 0xFFFF : 0;
        _dLight = plefttop[4];
        _dZi = plefttop[5];
        _dPdest = _viewBuffer + ystart * _screenWidth + plefttop[0];
        _dPz = _zBuffer + ystart * ZWidth + plefttop[0];
        if (height == 1)
        {
            SpanPackage* p = _pedgeSpanPackage;
            p->PDest = _dPdest;
            p->Pz = _dPz;
            p->Count = _aSpanCount;
            p->PTex = _dPtex;
            p->SFrac = _dSfrac;
            p->TFrac = _dTfrac;
            // FIXME: need to clamp l, s, t, at both ends?
            p->Light = _dLight;
            p->Zi = _dZi;
            _pedgeSpanPackage++;
            return;
        }
        PolysetSetUpForLineScan(plefttop[0], plefttop[1], pleftbottom[0], pleftbottom[1]);
        _pzBaseStep = ZWidth + _ubaseStep;
        _pzExtraStep = _pzBaseStep + 1;
        _pdestBaseStep = _screenWidth + _ubaseStep;
        _pdestExtraStep = _pdestBaseStep + 1;
        // for negative steps in x along left edge, bias toward overflow rather than underflow
        int workingLstepx = _ubaseStep < 0 ? _rLstepx - 1 : _rLstepx;
        _countExtraStep = _ubaseStep + 1;
        _ptexBaseStep = ((_rSstepy + _rSstepx * _ubaseStep) >> 16) + ((_rTstepy + _rTstepx * _ubaseStep) >> 16) * _skinWidth;
        _sfracBaseStep = (_rSstepy + _rSstepx * _ubaseStep) & 0xFFFF;
        _tfracBaseStep = (_rTstepy + _rTstepx * _ubaseStep) & 0xFFFF;
        _lightBaseStep = _rLstepy + workingLstepx * _ubaseStep;
        _ziBaseStep = _rZistepy + _rZistepx * _ubaseStep;
        _ptexExtraStep = ((_rSstepy + _rSstepx * _countExtraStep) >> 16)
                         + ((_rTstepy + _rTstepx * _countExtraStep) >> 16) * _skinWidth;
        _sfracExtraStep = (_rSstepy + _rSstepx * _countExtraStep) & 0xFFFF;
        _tfracExtraStep = (_rTstepy + _rTstepx * _countExtraStep) & 0xFFFF;
        _lightExtraStep = _lightBaseStep + workingLstepx;
        _ziExtraStep = _ziBaseStep + _rZistepx;
        PolysetScanLeftEdge(height);
    }

    /// <summary>D_RasterizeAliasPolySmooth: the left edge into span packages, the right edge drawing them.</summary>
    private void RasterizeAliasPolySmooth()
    {
        EdgeTable et = EdgeTables[_edgeTableIndex];
        int* plefttop = P(et.L0);
        int* prighttop = P(et.R0);
        int* pleftbottom = P(et.L1);
        int* prightbottom = P(et.R1);
        int initialrightheight = prightbottom[1] - prighttop[1];

        // set the s, t, and light gradients, which are consistent across the triangle because being a triangle,
        // things are affine
        PolysetCalcGradients(_skinWidth);

        // rasterize the polygon: scan out the top (and possibly only) part of the left edge
        _pedgeSpanPackage = _aSpans;
        ScanLeftEdgePart(plefttop, pleftbottom, prighttop[0], true);
        // scan out the bottom part of the left edge, if it exists
        if (et.NumLeftEdges == 2)
            ScanLeftEdgePart(pleftbottom, P(et.L2), prighttop[0], false);

        // scan out the top (and possibly only) part of the right edge, updating the count field
        _pedgeSpanPackage = _aSpans;
        PolysetSetUpForLineScan(prighttop[0], prighttop[1], prightbottom[0], prightbottom[1]);
        _aSpanCount = 0;
        _countExtraStep = _ubaseStep + 1;
        int originalcount = _aSpans[initialrightheight].Count;
        _aSpans[initialrightheight].Count = SpanEnd;    // mark end of the spanpackages
        PolysetDrawSpans8(_aSpans);

        // scan out the bottom part of the right edge, if it exists
        if (et.NumRightEdges == 2)
        {
            SpanPackage* pstart = _aSpans + initialrightheight;
            pstart->Count = originalcount;
            _aSpanCount = prightbottom[0] - prighttop[0];
            prighttop = prightbottom;
            prightbottom = P(et.R2);
            int height = prightbottom[1] - prighttop[1];
            PolysetSetUpForLineScan(prighttop[0], prighttop[1], prightbottom[0], prightbottom[1]);
            _countExtraStep = _ubaseStep + 1;
            _aSpans[initialrightheight + height].Count = SpanEnd;   // mark end of the spanpackages
            PolysetDrawSpans8(pstart);
        }
    }

    /// <summary>D_PolysetSetEdgeTable: which edges are left and right, and in which order to rasterize them.</summary>
    private void PolysetSetEdgeTable()
    {
        int y0 = P(0)[1], y1 = P(1)[1], y2 = P(2)[1];
        int edgetableindex = 0;     // assume the vertices are already in top to bottom order
        if (y0 >= y1)
        {
            if (y0 == y1)
            {
                _edgeTableIndex = y0 < y2 ? 2 : 5;
                return;
            }
            edgetableindex = 1;
        }
        if (y0 == y2)
        {
            _edgeTableIndex = edgetableindex != 0 ? 8 : 9;
            return;
        }
        if (y1 == y2)
        {
            _edgeTableIndex = edgetableindex != 0 ? 10 : 11;
            return;
        }
        if (y0 > y2)
            edgetableindex += 2;
        if (y1 > y2)
            edgetableindex += 4;
        _edgeTableIndex = edgetableindex;
    }
}
