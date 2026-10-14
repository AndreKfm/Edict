// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>view.c: the palette (the color shifts and gamma) and the view.</summary>
public sealed unsafe partial class Screen
{
    private const int CShiftContents = 0, CShiftDamage = 1, CShiftBonus = 2, CShiftPowerup = 3;
    private const int ItInvisibility = 1 << 19, ItInvulnerability = 1 << 20, ItSuit = 1 << 21, ItQuad = 1 << 22;

    private static readonly SysErrorException GammaNotPinned =
        new("BuildGammaTable: a gamma other than 1 needs pow, which the reference does not pin yet");

    private readonly byte[] _gammaTable;
    private float _oldGammaValue;                     // V_CheckGamma's static oldgammavalue
    private readonly byte[] _pal;                     // V_UpdatePalette's pal[768]

    /// <summary>BuildGammaTable.</summary>
    private void BuildGammaTable(float g)
    {
        if (g == 1.0f)
        {
            for (int i = 0; i < 256; i++)
                _gammaTable[i] = (byte)i;
            return;
        }
        throw GammaNotPinned;
    }

    /// <summary>V_CheckGamma.</summary>
    private bool CheckGamma()
    {
        float gamma = CvarValue("gamma\0"u8);
        if (gamma == _oldGammaValue)
            return false;
        _oldGammaValue = gamma;
        BuildGammaTable(gamma);
        _recalcRefdef = true;       // force a surface cache flush
        return true;
    }

    /// <summary>V_CalcPowerupCshift.</summary>
    private void CalcPowerupCshift()
    {
        ref ColorShift p = ref _cl.CShifts[CShiftPowerup];
        if ((_cl.Items & ItQuad) != 0)
            Set(ref p, 0, 0, 255, 30);
        else if ((_cl.Items & ItSuit) != 0)
            Set(ref p, 0, 255, 0, 20);
        else if ((_cl.Items & ItInvisibility) != 0)
            Set(ref p, 100, 100, 100, 100);
        else if ((_cl.Items & ItInvulnerability) != 0)
            Set(ref p, 255, 255, 0, 30);
        else
            p.Percent = 0;

        static void Set(ref ColorShift s, int r, int g, int b, int percent)
        {
            s.DestColor[0] = r;
            s.DestColor[1] = g;
            s.DestColor[2] = b;
            s.Percent = percent;
        }
    }

    /// <summary>V_UpdatePalette (the software renderer's): the color shifts blended into the base palette when they changed.</summary>
    private void UpdatePalette()
    {
        CalcPowerupCshift();
        bool changed = false;
        for (int i = 0; i < ClientState.NumCShifts; i++)
        {
            ref ColorShift c = ref _cl.CShifts[i];
            ref ColorShift prev = ref _cl.PrevCShifts[i];
            if (c.Percent != prev.Percent)
            {
                changed = true;
                prev.Percent = c.Percent;
            }
            for (int j = 0; j < 3; j++)
                if (c.DestColor[j] != prev.DestColor[j])
                {
                    changed = true;
                    prev.DestColor[j] = c.DestColor[j];
                }
        }

        // drop the damage value
        ref ColorShift damage = ref _cl.CShifts[CShiftDamage];
        damage.Percent = CCastInt(damage.Percent - FrameTime * 150);
        if (damage.Percent <= 0)
            damage.Percent = 0;
        // drop the bonus value
        ref ColorShift bonus = ref _cl.CShifts[CShiftBonus];
        bonus.Percent = CCastInt(bonus.Percent - FrameTime * 100);
        if (bonus.Percent <= 0)
            bonus.Percent = 0;

        bool force = CheckGamma();
        if (!changed && !force)
            return;

        for (int i = 0, b = 0; i < 256; i++, b += 3)
        {
            int r = _basePal[b], g = _basePal[b + 1], bl = _basePal[b + 2];
            for (int j = 0; j < ClientState.NumCShifts; j++)
            {
                ref ColorShift c = ref _cl.CShifts[j];
                r += (c.Percent * (c.DestColor[0] - r)) >> 8;
                g += (c.Percent * (c.DestColor[1] - g)) >> 8;
                bl += (c.Percent * (c.DestColor[2] - bl)) >> 8;
            }
            _pal[b] = _gammaTable[r];
            _pal[b + 1] = _gammaTable[g];
            _pal[b + 2] = _gammaTable[bl];
        }
        ShiftPalette(_pal);
    }

    /// <summary>V_RenderView: the view (steps 3 to 5) — with the console forced up there is none.</summary>
    private void ViewRender()
    {
        if (ForcedUp)
            return;
        throw NotPortedYet;
    }

    // ---- sbar.c (next)

#pragma warning disable CS0414     // read by Sbar_Draw (next)
    private int _sbUpdates;                           // sb_updates: if >= vid.numpages, no update needed
#pragma warning restore CS0414

    /// <summary>Sbar_Changed.</summary>
    public void SbarChanged() => _sbUpdates = 0;     // update next frame

    /// <summary>Sbar_Draw.</summary>
    private void SbarDraw()
    {
        if (_conCurrent == Height)
            return;     // console is full screen
        throw NotPortedYet;
    }

    private void IntermissionOverlay() => throw NotPortedYet;

    private void FinaleOverlay() => throw NotPortedYet;
}
