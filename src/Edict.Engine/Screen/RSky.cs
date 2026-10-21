// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The sky (r_sky.c, d_sky.c): R_InitSky takes a loaded sky texture's two halves apart, R_MakeSky lays the moving
/// lower layer under the upper one into newsky when the shift has changed since the last time it did, and
/// D_DrawSkyScans8 maps each sky span through the view direction into it. newsky's left half and R_MakeSky's last
/// shift outlive a map, as C's statics do.
/// </summary>
public sealed unsafe partial class Screen
{
    private const int SkyShift = 7, SkySize = 1 << SkyShift, SkyMask = SkySize - 1;
    private const int ISkySpeed = 8, ISkySpeed2 = 2;
    private const int SkySpanShift = 5, SkySpanMax = 1 << SkySpanShift;
    private const int SkySMask = 0x007F0000, SkyTMask = 0x007F0000;     // R_SKY_SMASK, R_SKY_TMASK

    private byte* _bottomSky, _bottomMask, _newSky;   // bottomsky[128*131], bottommask[128*131], newsky[128*256]
    private byte* _skySource;                         // r_skysource (NULL until a sky texture is loaded)
    private bool _skyMade;                            // r_skymade
    private float _skySpeed, _skyTime;                // skyspeed, skytime
    private int _skyXLast = -1, _skyYLast = -1;       // R_MakeSky's statics

    [NoAllocUnchecked("load path")]
    private void InitSkyBuffers()
    {
        _bottomSky = (byte*)NativeMemory.AllocZeroed(128 * 131);
        _bottomMask = (byte*)NativeMemory.AllocZeroed(128 * 131);
        _newSky = (byte*)NativeMemory.AllocZeroed(128 * 256);
    }

    /// <summary>
    /// R_InitSky, which Mod_LoadTextures calls for each texture named "sky*" of a brush model it loads: the right
    /// half is the upper layer, the left half (black transparent) the lower one, with a mask.
    /// </summary>
    public void InitSky(Texture* mt)
    {
        byte* src = mt->Pixels + mt->MipOffsets[0];
        for (int i = 0; i < 128; i++)
            for (int j = 0; j < 128; j++)
                _newSky[i * 256 + j + 128] = src[i * 256 + j + 128];
        for (int i = 0; i < 128; i++)
            for (int j = 0; j < 131; j++)
            {
                byte b = src[i * 256 + (j & 0x7F)];
                if (b != 0)
                {
                    _bottomSky[i * 131 + j] = b;
                    _bottomMask[i * 131 + j] = 0;
                }
                else
                {
                    _bottomSky[i * 131 + j] = 0;
                    _bottomMask[i * 131 + j] = 0xff;
                }
            }
        _skySource = _newSky;
    }

    /// <summary>R_SetSkyFrame: the sky's time within its period; newsky to be made again if it is drawn.</summary>
    private void SetSkyFrame()
    {
        _skySpeed = ISkySpeed;
        // GreatestCommonDivisor (8, 2) = 2: s1 4, s2 1
        const int g = 2, s1 = ISkySpeed / g, s2 = ISkySpeed2 / g;
        float temp = SkySize * s1 * s2;
        _skyTime = (float)(_cl.Time - CCast.I32(_cl.Time / temp) * temp);
        _skyMade = false;
    }

    /// <summary>R_MakeSky: the lower layer, shifted, under the upper one (the dword path writes the same bytes).</summary>
    private void MakeSky()
    {
        int xshift = CCast.I32(_skyTime * _skySpeed);
        int yshift = CCast.I32(_skyTime * _skySpeed);
        if (xshift == _skyXLast && yshift == _skyYLast)
            return;
        _skyXLast = xshift;
        _skyYLast = yshift;
        byte* pnewsky = _newSky;
        for (int y = 0; y < SkySize; y++)
        {
            int baseofs = ((y + yshift) & SkyMask) * 131;
            for (int x = 0; x < SkySize; x += 4)
            {
                int ofs = baseofs + ((x + xshift) & SkyMask);
                for (int k = 0; k < 4; k++)
                    pnewsky[k] = (byte)((pnewsky[k + 128] & _bottomMask[ofs + k]) | _bottomSky[ofs + k]);
                pnewsky += 4;
            }
            pnewsky += 128;
        }
        _skyMade = true;
    }

    /// <summary>D_Sky_uv_To_st: the sky's s and t (16.16) for a screen pixel.</summary>
    private void SkyUvToSt(int u, int v, out int s, out int t)
    {
        float temp = RefdefVrect.Width >= RefdefVrect.Height ? RefdefVrect.Width : RefdefVrect.Height;
        float wu = (float)(8192.0 * (float)(u - (Width >> 1)) / temp);
        float wv = (float)(8192.0 * (float)((Height >> 1) - v) / temp);
        var end = new Vec3(
            4096 * _vpn.X + wu * _vright.X + wv * _vup.X,
            4096 * _vpn.Y + wu * _vright.Y + wv * _vup.Y,
            4096 * _vpn.Z + wu * _vright.Z + wv * _vup.Z);
        end.Z *= 3;
        Mathlib.VectorNormalize(ref end);
        temp = _skyTime * _skySpeed;    // TODO: add D_SetupFrame & set this there
        s = CCast.I32((temp + 6 * (SkySize / 2 - 1) * end.X) * 0x10000);
        t = CCast.I32((temp + 6 * (SkySize / 2 - 1) * end.Y) * 0x10000);
    }

    /// <summary>D_DrawSkyScans8: the sky spans, s and t exact every 32 pixels.</summary>
    private void DrawSkyScans8(int pspan)
    {
        int sstep = 0, tstep = 0;   // keep compiler happy
        do
        {
            ref ESpan span = ref _spans[pspan];
            byte* pdest = _viewBuffer + _screenWidth * span.V + span.U;
            int count = span.Count;
            // calculate the initial s & t
            int u = span.U;
            int v = span.V;
            SkyUvToSt(u, v, out int s, out int t);
            do
            {
                int spancount = count >= SkySpanMax ? SkySpanMax : count;
                count -= spancount;
                int snext = 0, tnext = 0;  // C leaves them unset on a one-pixel tail, where they are not used
                if (count != 0)
                {
                    u += spancount;
                    // calculate s and t at far end of span, calculate s and t steps across span by shifting
                    SkyUvToSt(u, v, out snext, out tnext);
                    sstep = (snext - s) >> SkySpanShift;
                    tstep = (tnext - t) >> SkySpanShift;
                }
                else
                {
                    // calculate s and t at last pixel in span, calculate s and t steps across span by division
                    int spancountminus1 = spancount - 1;
                    if (spancountminus1 > 0)
                    {
                        u += spancountminus1;
                        SkyUvToSt(u, v, out snext, out tnext);
                        sstep = (snext - s) / spancountminus1;
                        tstep = (tnext - t) / spancountminus1;
                    }
                }
                do
                {
                    *pdest++ = _skySource[((t & SkyTMask) >> 8) + ((s & SkySMask) >> 16)];
                    s += sstep;
                    t += tstep;
                } while (--spancount > 0);
                s = snext;
                t = tnext;
            } while (count > 0);
        } while ((pspan = _spans[pspan].Next) >= 0);
    }
}
