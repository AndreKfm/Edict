// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// sbar.c for id1 (the mission packs' hipnotic and rogue code is not reached): the status bar, the inventory, the
/// scoreboards and the intermission's overlays. M_DrawPic is the reference's menu stub, which draws nothing.
/// </summary>
public sealed unsafe partial class Screen
{
    private const int SbarHeight = 24, StatMinus = 10, GameDeathmatch = 1, MaxScoreboard = 16;
    private const int ItShotgun = 1, ItShells = 256, ItNails = 512, ItRockets = 1024, ItCells = 2048,
        ItArmor1 = 8192, ItArmor2 = 16384, ItArmor3 = 32768;
    private const int StatAmmo = 3, StatArmor = 4, StatShells = 6, StatActiveWeapon = 10, StatTotalSecrets = 11,
        StatTotalMonsters = 12, StatSecrets = 13, StatMonsters = 14;

    private QPic[][] _sbNums = null!;                 // sb_nums[2][11]
    private QPic _sbColon, _sbSlash, _sbIbar, _sbSbar, _sbScorebar, _disc;
    private QPic[][] _sbWeapons = null!;              // sb_weapons[7][8]: 0 is active, 1 is owned, 2-5 are flashes
    private QPic[] _sbAmmo = null!, _sbSigil = null!, _sbArmor = null!, _sbItems = null!;
    private QPic[][] _sbFaces = null!;                // sb_faces[7][2]: 0 is gibbed, 1 is dead, 2-6 are alive; 0 static, 1 animation
    private QPic _faceInvis, _faceQuad, _faceInvuln, _faceInvisInvuln;

    /// <summary>sb_showscores (+showscores, -showscores).</summary>
    private bool _showScores;

    private int[] _fragSort = null!;                  // fragsort[MAX_SCOREBOARD]
    private int _scoreboardLines;
    private byte[] _num = null!;                      // a sprintf buffer, char num[12]

    /// <summary>Sbar_Init: the status bar's pictures from gfx.wad.</summary>
    [NoAllocUnchecked("load path")]
    public void InitSbar()
    {
        QPic W(string name) => PicFromWad(System.Text.Encoding.Latin1.GetBytes(name));
        _sbNums = [new QPic[11], new QPic[11]];
        for (int i = 0; i < 10; i++)
        {
            _sbNums[0][i] = W($"num_{i}");
            _sbNums[1][i] = W($"anum_{i}");
        }
        _sbNums[0][10] = W("num_minus");
        _sbNums[1][10] = W("anum_minus");
        _sbColon = W("num_colon");
        _sbSlash = W("num_slash");

        string[] weapons = ["shotgun", "sshotgun", "nailgun", "snailgun", "rlaunch", "srlaunch", "lightng"];
        _sbWeapons = new QPic[7][];
        for (int f = 0; f < 7; f++)
            _sbWeapons[f] = new QPic[8];
        for (int i = 0; i < 7; i++)
        {
            _sbWeapons[0][i] = W("inv_" + weapons[i]);
            _sbWeapons[1][i] = W("inv2_" + weapons[i]);
            for (int f = 0; f < 5; f++)
                _sbWeapons[2 + f][i] = W($"inva{f + 1}_" + weapons[i]);
        }
        _sbAmmo = [W("sb_shells"), W("sb_nails"), W("sb_rocket"), W("sb_cells")];
        _sbArmor = [W("sb_armor1"), W("sb_armor2"), W("sb_armor3")];
        _sbItems = new QPic[32];
        _sbItems[0] = W("sb_key1");
        _sbItems[1] = W("sb_key2");
        _sbItems[2] = W("sb_invis");
        _sbItems[3] = W("sb_invuln");
        _sbItems[4] = W("sb_suit");
        _sbItems[5] = W("sb_quad");
        _sbSigil = [W("sb_sigil1"), W("sb_sigil2"), W("sb_sigil3"), W("sb_sigil4")];
        _sbFaces = new QPic[7][];
        for (int f = 0; f < 7; f++)
            _sbFaces[f] = new QPic[2];
        for (int f = 0; f < 5; f++)
        {
            _sbFaces[4 - f][0] = W($"face{f + 1}");
            _sbFaces[4 - f][1] = W($"face_p{f + 1}");
        }
        _faceInvis = W("face_invis");
        _faceInvuln = W("face_invul2");
        _faceInvisInvuln = W("face_inv2");
        _faceQuad = W("face_quad");
        _sbSbar = W("sbar");
        _sbIbar = W("ibar");
        _sbScorebar = W("scorebar");
        _disc = W("disc");
        _fragSort = new int[MaxScoreboard];
        _num = new byte[12];
    }

    /// <summary>Sbar_ShowScores, Sbar_DontShowScores (the Tab key).</summary>
    public void ShowScores(bool show)
    {
        if (show && _showScores)
            return;
        _showScores = show;
        _sbUpdates = 0;
    }

    /// <summary>sprintf (buf, "%{width}i", v): right-aligned in spaces; the length.</summary>
    private static int FormatInt(int v, int width, Span<byte> dest)
    {
        Span<byte> t = stackalloc byte[12];
        int n = Edict.Exact.CFormat.Int(v, t);
        int pad = width > n ? width - n : 0;
        dest[..pad].Fill((byte)' ');
        t[..n].CopyTo(dest[pad..]);
        dest[pad + n] = 0;
        return pad + n;
    }

    private int SbarX(int x) => _cl.GameType == GameDeathmatch ? x : x + ((Width - 320) >> 1);

    private void SbarPic(int x, int y, QPic pic) => Pic(SbarX(x), y + (Height - SbarHeight), pic);

    private void SbarTransPic(int x, int y, QPic pic) => TransPic(SbarX(x), y + (Height - SbarHeight), pic);

    /// <summary>Sbar_DrawCharacter: draws one solid graphics character.</summary>
    private void SbarCharacter(int x, int y, int num) => Character(SbarX(x) + 4, y + Height - SbarHeight, num);

    private void SbarString(int x, int y, ReadOnlySpan<byte> str) => String(SbarX(x), y + Height - SbarHeight, str);

    /// <summary>Sbar_itoa.</summary>
    private static int Itoa(int num, Span<byte> buf)
    {
        int s = 0;
        if (num < 0)
        {
            buf[s++] = (byte)'-';
            num = -num;
        }
        int pow10;
        for (pow10 = 10; num >= pow10; pow10 *= 10)
        {
        }
        do
        {
            pow10 /= 10;
            int dig = num / pow10;
            buf[s++] = (byte)('0' + dig);
            num -= dig * pow10;
        } while (pow10 != 1);
        buf[s] = 0;
        return s;
    }

    /// <summary>Sbar_DrawNum, or Sbar_IntermissionNumber when not in the status bar.</summary>
    private void DrawNum(int x, int y, int num, int digits, int color, bool inBar)
    {
        Span<byte> str = stackalloc byte[12];
        int l = Itoa(num, str);
        int ptr = 0;
        if (l > digits)
            ptr += l - digits;
        if (l < digits)
            x += (digits - l) * 24;
        for (; str[ptr] != 0; ptr++, x += 24)
        {
            int frame = str[ptr] == '-' ? StatMinus : str[ptr] - '0';
            if (inBar)
                SbarTransPic(x, y, _sbNums[color][frame]);
            else
                TransPic(x, y, _sbNums[color][frame]);
        }
    }

    /// <summary>Sbar_SortFrags: the scoreboard's lines by frags.</summary>
    private void SortFrags()
    {
        _scoreboardLines = 0;
        for (int i = 0; i < _cl.MaxClients; i++)
            if (_cl.Scores[i].Name[0] != 0)
                _fragSort[_scoreboardLines++] = i;
        for (int i = 0; i < _scoreboardLines; i++)
            for (int j = 0; j < _scoreboardLines - 1 - i; j++)
                if (_cl.Scores[_fragSort[j]].Frags < _cl.Scores[_fragSort[j + 1]].Frags)
                    (_fragSort[j], _fragSort[j + 1]) = (_fragSort[j + 1], _fragSort[j]);
    }

    private static int ColorForMap(int m) => m + 8;      // m < 128 ? m + 8 : m + 8

    /// <summary>Sbar_SoloScoreboard.</summary>
    private void SoloScoreboard()
    {
        Span<byte> str = stackalloc byte[80];
        int n = 0;
        "Monsters:"u8.CopyTo(str);
        n += 9;
        n += FormatInt(_cl.Stats[StatMonsters], 3, str[n..]);
        " /"u8.CopyTo(str[n..]);
        n += 2;
        n += FormatInt(_cl.Stats[StatTotalMonsters], 3, str[n..]);
        SbarString(8, 4, str[..n]);

        n = 0;
        "Secrets :"u8.CopyTo(str);
        n += 9;
        n += FormatInt(_cl.Stats[StatSecrets], 3, str[n..]);
        " /"u8.CopyTo(str[n..]);
        n += 2;
        n += FormatInt(_cl.Stats[StatTotalSecrets], 3, str[n..]);
        SbarString(8, 12, str[..n]);

        // time
        int minutes = CCastInt(_cl.Time / 60);
        int seconds = CCastInt(_cl.Time - 60 * minutes);
        int tens = seconds / 10;
        int units = seconds - 10 * tens;
        n = 0;
        "Time :"u8.CopyTo(str);
        n += 6;
        n += FormatInt(minutes, 3, str[n..]);
        str[n++] = (byte)':';
        n += Edict.Exact.CFormat.Int(tens, str[n..]);
        n += Edict.Exact.CFormat.Int(units, str[n..]);
        SbarString(184, 4, str[..n]);

        // draw level name
        int l = 0;
        while (l < _cl.LevelName.Length && _cl.LevelName[l] != 0)
            l++;
        SbarString(232 - l * 4, 12, new ReadOnlySpan<byte>(_cl.LevelName, 0, l));
    }

    /// <summary>Sbar_DrawScoreboard.</summary>
    private void DrawScoreboard()
    {
        SoloScoreboard();
        if (_cl.GameType == GameDeathmatch)
            DeathmatchOverlay();
    }

    /// <summary>Sbar_DrawInventory: the weapons, the ammo counts, the items and the sigils.</summary>
    private void DrawInventory()
    {
        SbarPic(0, -24, _sbIbar);

        // weapons
        for (int i = 0; i < 7; i++)
        {
            if ((_cl.Items & (ItShotgun << i)) == 0)
                continue;
            float time = _cl.ItemGetTime[i];
            int flashon = CCastInt((_cl.Time - time) * 10);
            if (flashon >= 10)
                flashon = _cl.Stats[StatActiveWeapon] == (ItShotgun << i) ? 1 : 0;
            else
                flashon = (flashon % 5) + 2;
            SbarPic(i * 24, -16, _sbWeapons[flashon][i]);
            if (flashon > 1)
                _sbUpdates = 0;     // force update to remove flash
        }

        // ammo counts
        Span<byte> num = _num;
        for (int i = 0; i < 4; i++)
        {
            FormatInt(_cl.Stats[StatShells + i], 3, num);
            if (num[0] != ' ')
                SbarCharacter((6 * i + 1) * 8 - 2, -24, 18 + num[0] - '0');
            if (num[1] != ' ')
                SbarCharacter((6 * i + 2) * 8 - 2, -24, 18 + num[1] - '0');
            if (num[2] != ' ')
                SbarCharacter((6 * i + 3) * 8 - 2, -24, 18 + num[2] - '0');
        }

        // items (flashon is 0 here: C's flash frames never show)
        for (int i = 0; i < 6; i++)
        {
            if ((_cl.Items & (1 << (17 + i))) == 0)
                continue;
            float time = _cl.ItemGetTime[17 + i];
            SbarPic(192 + i * 16, -16, _sbItems[i]);
            if (time != 0 && time > _cl.Time - 2)
                _sbUpdates = 0;
        }
        // sigils
        for (int i = 0; i < 4; i++)
        {
            if ((_cl.Items & (1 << (28 + i))) == 0)
                continue;
            float time = _cl.ItemGetTime[28 + i];
            SbarPic(320 - 32 + i * 8, -16, _sbSigil[i]);
            if (time != 0 && time > _cl.Time - 2)
                _sbUpdates = 0;
        }
    }

    /// <summary>Sbar_DrawFrags: the four best frag counts over the inventory.</summary>
    private void DrawFrags()
    {
        SortFrags();
        int l = _scoreboardLines <= 4 ? _scoreboardLines : 4;
        int x = 23;
        int xofs = _cl.GameType == GameDeathmatch ? 0 : (Width - 320) >> 1;
        int y = Height - SbarHeight - 23;
        Span<byte> num = _num;
        for (int i = 0; i < l; i++)
        {
            int k = _fragSort[i];
            Score s = _cl.Scores[k];
            if (s.Name[0] == 0)
                continue;
            // draw background
            Fill(xofs + x * 8 + 10, y, 28, 4, ColorForMap(s.Colors & 0xf0));
            Fill(xofs + x * 8 + 10, y + 4, 28, 3, ColorForMap((s.Colors & 15) << 4));
            // draw number
            FormatInt(s.Frags, 3, num);
            SbarCharacter((x + 1) * 8, -24, num[0]);
            SbarCharacter((x + 2) * 8, -24, num[1]);
            SbarCharacter((x + 3) * 8, -24, num[2]);
            if (k == _cl.ViewEntity - 1)
            {
                SbarCharacter(x * 8 + 2, -24, 16);
                SbarCharacter((x + 4) * 8 - 4, -24, 17);
            }
            x += 4;
        }
    }

    /// <summary>Sbar_DrawFace.</summary>
    private void DrawFace()
    {
        if ((_cl.Items & (ItInvisibility | ItInvulnerability)) == (ItInvisibility | ItInvulnerability))
        {
            SbarPic(112, 0, _faceInvisInvuln);
            return;
        }
        if ((_cl.Items & ItQuad) != 0)
        {
            SbarPic(112, 0, _faceQuad);
            return;
        }
        if ((_cl.Items & ItInvisibility) != 0)
        {
            SbarPic(112, 0, _faceInvis);
            return;
        }
        if ((_cl.Items & ItInvulnerability) != 0)
        {
            SbarPic(112, 0, _faceInvuln);
            return;
        }
        int f = _cl.Stats[StatHealth] >= 100 ? 4 : _cl.Stats[StatHealth] / 20;
        int anim;
        if (_cl.Time <= _cl.FaceAnimTime)
        {
            anim = 1;
            _sbUpdates = 0;     // make sure the anim gets drawn over
        }
        else
            anim = 0;
        SbarPic(112, 0, _sbFaces[f][anim]);
    }

    /// <summary>Sbar_Draw.</summary>
    private void SbarDraw()
    {
        if (_conCurrent == Height)
            return;     // console is full screen
        if (_sbUpdates >= NumPages)
            return;
        _copyEverything = true;
        _sbUpdates++;

        if (_sbLines > 24)
        {
            DrawInventory();
            if (_cl.MaxClients != 1)
                DrawFrags();
        }

        if (_showScores || _cl.Stats[StatHealth] <= 0)
        {
            SbarPic(0, 0, _sbScorebar);
            DrawScoreboard();
            _sbUpdates = 0;
        }
        else if (_sbLines != 0)
        {
            SbarPic(0, 0, _sbSbar);
            // armor
            if ((_cl.Items & ItInvulnerability) != 0)
            {
                DrawNum(24, 0, 666, 3, 1, true);
                SbarPic(0, 0, _disc);
            }
            else
            {
                DrawNum(24, 0, _cl.Stats[StatArmor], 3, _cl.Stats[StatArmor] <= 25 ? 1 : 0, true);
                if ((_cl.Items & ItArmor3) != 0)
                    SbarPic(0, 0, _sbArmor[2]);
                else if ((_cl.Items & ItArmor2) != 0)
                    SbarPic(0, 0, _sbArmor[1]);
                else if ((_cl.Items & ItArmor1) != 0)
                    SbarPic(0, 0, _sbArmor[0]);
            }
            // face
            DrawFace();
            // health
            DrawNum(136, 0, _cl.Stats[StatHealth], 3, _cl.Stats[StatHealth] <= 25 ? 1 : 0, true);
            // ammo icon
            if ((_cl.Items & ItShells) != 0)
                SbarPic(224, 0, _sbAmmo[0]);
            else if ((_cl.Items & ItNails) != 0)
                SbarPic(224, 0, _sbAmmo[1]);
            else if ((_cl.Items & ItRockets) != 0)
                SbarPic(224, 0, _sbAmmo[2]);
            else if ((_cl.Items & ItCells) != 0)
                SbarPic(224, 0, _sbAmmo[3]);
            DrawNum(248, 0, _cl.Stats[StatAmmo], 3, _cl.Stats[StatAmmo] <= 10 ? 1 : 0, true);
        }
        // vid.width > 320 only: Sbar_MiniDeathmatchOverlay
    }

    /// <summary>Sbar_DeathmatchOverlay (the ranking's picture goes through M_DrawPic: nothing in the reference).</summary>
    private void DeathmatchOverlay()
    {
        _copyEverything = true;
        _fullUpdate = 0;
        CachePic("gfx/ranking.lmp"u8);
        // scores
        SortFrags();
        int l = _scoreboardLines;
        int x = 80 + ((Width - 320) >> 1);
        int y = 40;
        Span<byte> num = _num;
        for (int i = 0; i < l; i++)
        {
            int k = _fragSort[i];
            Score s = _cl.Scores[k];
            if (s.Name[0] == 0)
                continue;
            // draw background
            Fill(x, y, 40, 4, ColorForMap(s.Colors & 0xf0));
            Fill(x, y + 4, 40, 4, ColorForMap((s.Colors & 15) << 4));
            // draw number
            FormatInt(s.Frags, 3, num);
            Character(x + 8, y, num[0]);
            Character(x + 16, y, num[1]);
            Character(x + 24, y, num[2]);
            if (k == _cl.ViewEntity - 1)
                Character(x - 8, y, 12);
            // draw name
            String(x + 64, y, s.Name);
            y += 10;
        }
    }

    /// <summary>Sbar_IntermissionOverlay.</summary>
    private void IntermissionOverlay()
    {
        _copyEverything = true;
        _fullUpdate = 0;
        if (_cl.GameType == GameDeathmatch)
        {
            DeathmatchOverlay();
            return;
        }
        Pic(64, 24, CachePic("gfx/complete.lmp"u8));
        TransPic(0, 56, CachePic("gfx/inter.lmp"u8));
        // time
        int dig = _cl.CompletedTime / 60;
        DrawNum(160, 64, dig, 3, 0, false);
        int num = _cl.CompletedTime - dig * 60;
        TransPic(234, 64, _sbColon);
        TransPic(246, 64, _sbNums[0][num / 10]);
        TransPic(266, 64, _sbNums[0][num % 10]);
        DrawNum(160, 104, _cl.Stats[StatSecrets], 3, 0, false);
        TransPic(232, 104, _sbSlash);
        DrawNum(240, 104, _cl.Stats[StatTotalSecrets], 3, 0, false);
        DrawNum(160, 144, _cl.Stats[StatMonsters], 3, 0, false);
        TransPic(232, 144, _sbSlash);
        DrawNum(240, 144, _cl.Stats[StatTotalMonsters], 3, 0, false);
    }

    /// <summary>Sbar_FinaleOverlay.</summary>
    private void FinaleOverlay()
    {
        _copyEverything = true;
        QPic pic = CachePic("gfx/finale.lmp"u8);
        TransPic((Width - pic.Width) / 2, 16, pic);
    }
}
