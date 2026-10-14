// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// cl_parse.c: CL_ParseServerMessage and what it calls, V_ParseDamage's state (the color shift and the face time;
/// the view kick is the renderer's) and CL_SignonReply. Sounds, the CD player and the console print nothing here.
/// Messages the corpus never sends and that C would act on outside the client (svc_disconnect, svc_sellscreen) stop
/// with an error.
/// </summary>
public sealed unsafe partial class ClientState
{
    private const int SvcNop = 1, SvcDisconnect = 2, SvcUpdateStat = 3, SvcVersion = 4, SvcSetView = 5, SvcSound = 6,
        SvcTime = 7, SvcPrint = 8, SvcStuffText = 9, SvcSetAngle = 10, SvcServerInfo = 11, SvcLightStyle = 12,
        SvcUpdateName = 13, SvcUpdateFrags = 14, SvcClientData = 15, SvcStopSound = 16, SvcUpdateColors = 17,
        SvcParticle = 18, SvcDamage = 19, SvcSpawnStatic = 20, SvcSpawnBaseline = 22, SvcTempEntity = 23,
        SvcSetPause = 24, SvcSignonNum = 25, SvcCenterPrint = 26, SvcKilledMonster = 27, SvcFoundSecret = 28,
        SvcSpawnStaticSound = 29, SvcIntermission = 30, SvcFinale = 31, SvcCdTrack = 32, SvcSellScreen = 33,
        SvcCutScene = 34;
    private const int UMoreBits = 1 << 0, UOrigin1 = 1 << 1, UOrigin2 = 1 << 2, UOrigin3 = 1 << 3, UAngle2 = 1 << 4,
        UNoLerp = 1 << 5, UFrame = 1 << 6, UAngle1 = 1 << 8, UAngle3 = 1 << 9, UModel = 1 << 10, UColormap = 1 << 11,
        USkin = 1 << 12, UEffects = 1 << 13, ULongEntity = 1 << 14;
    private const int SuViewHeight = 1 << 0, SuIdealPitch = 1 << 1, SuPunch1 = 1 << 2, SuVelocity1 = 1 << 5,
        SuOnGround = 1 << 10, SuInWater = 1 << 11, SuWeaponFrame = 1 << 12, SuArmor = 1 << 13, SuWeapon = 1 << 14;
    private const int StatHealth = 0, StatWeapon = 2, StatAmmo = 3, StatArmor = 4, StatWeaponFrame = 5, StatShells = 6,
        StatActiveWeapon = 10, StatSecrets = 13, StatMonsters = 14;
    private const int DefaultViewHeight = 22, ProtocolVersion = 15, StRand = 1, CShiftDamage = 1, CShiftBonus = 2;
    private const int ClcStringCmd = 4;

    private static readonly HostErrorException BadServerMessage = new("CL_ParseServerMessage: Bad server message");
    private static readonly HostErrorException IllegibleMessage = new("CL_ParseServerMessage: Illegible server message");
    private static readonly HostErrorException NotPorted = new("CL_ParseServerMessage: a message the port does not have yet (disconnect, sellscreen)");
    private static readonly HostErrorException WrongProtocol = new("CL_ParseServerMessage: Server is not protocol 15");
    private static readonly HostErrorException BadEntityNum = new("CL_EntityNum: an invalid number");
    private static readonly HostErrorException BadModNum = new("CL_ParseModel: bad modnum");
    private static readonly HostErrorException BadSoundEnt = new("CL_ParseStartSoundPacket: ent > MAX_EDICTS");
    private static readonly HostErrorException BadScoreboard = new("CL_ParseServerMessage: svc_update* > MAX_SCOREBOARD");
    private static readonly HostErrorException BadSignon = new("Received signon at or below the current one");
    private static readonly HostErrorException TooManyStatics = new("Too many static entities");
    private static readonly SysErrorException BadLightstyle = new("svc_lightstyle > MAX_LIGHTSTYLES");
    private static readonly SysErrorException LightstyleTooLong = new("svc_lightstyle: longer than cl_lightstyle's map (C writes past it)");
    private static readonly SysErrorException NameTooLong = new("svc_updatename: longer than the scoreboard's name (C writes past it)");
    private static readonly SysErrorException BadStat = new("svc_updatestat: an invalid stat");
    private static readonly SysErrorException BadColormap = new("CL_ParseUpdate: colormap above cl.maxclients");

    /// <summary>net_message and its reader.</summary>
    public SizeBuf NetMessage { get; }
    private readonly MsgReader _reader;

    /// <summary>CL_ParseServerMessage: one message, command by command.</summary>
    private void ParseServerMessage()
    {
        MsgReader r = _reader;
        OnGround = 0;               // unless the server says otherwise
        r.BeginReading();
        while (true)
        {
            if (r.BadRead)
                throw BadServerMessage;
            int cmd = r.ReadByte();
            if (cmd == -1)
                return;             // end of message

            // if the high bit of the command byte is set, it is a fast update
            if ((cmd & 128) != 0)
            {
                ParseUpdate(cmd & 127);
                continue;
            }

            switch (cmd)
            {
                case SvcNop:
                    break;
                case SvcTime:
                    MTime1 = MTime0;
                    MTime0 = r.ReadFloat();
                    break;
                case SvcClientData:
                    ParseClientdata(r.ReadShort());
                    break;
                case SvcVersion:
                    if (r.ReadLong() != ProtocolVersion)
                        throw WrongProtocol;
                    break;
                case SvcPrint:
                case SvcCenterPrint:
                    r.ReadString();         // Con_Printf, SCR_CenterPrint: the screen's
                    break;
                case SvcStuffText:
                    AddText(r.ReadString());
                    break;
                case SvcDamage:
                    ParseDamage();
                    break;
                case SvcServerInfo:
                    ParseServerInfo();
                    break;
                case SvcSetAngle:
                    ViewAngles.X = r.ReadAngle();
                    ViewAngles.Y = r.ReadAngle();
                    ViewAngles.Z = r.ReadAngle();
                    break;
                case SvcSetView:
                    ViewEntity = r.ReadShort();
                    break;
                case SvcLightStyle:
                {
                    int i = r.ReadByte();
                    if (i >= MaxLightstyles)
                        throw BadLightstyle;
                    byte* s = r.ReadString();
                    int n = 0;
                    while (s[n] != 0)
                        n++;
                    if (n >= MaxStyleString)
                        throw LightstyleTooLong;
                    new ReadOnlySpan<byte>(s, n + 1).CopyTo(LightstyleMap[i]);
                    LightstyleLength[i] = n;
                    break;
                }
                case SvcSound:
                    ParseStartSoundPacket();
                    break;
                case SvcStopSound:
                    r.ReadShort();          // S_StopSound
                    break;
                case SvcUpdateName:
                {
                    int i = r.ReadByte();
                    if (i >= MaxClients)
                        throw BadScoreboard;
                    byte* s = r.ReadString();
                    int n = 0;
                    while (s[n] != 0)
                        n++;
                    if (n >= MaxScoreboardName)
                        throw NameTooLong;
                    new ReadOnlySpan<byte>(s, n + 1).CopyTo(Scores[i].Name);
                    break;
                }
                case SvcUpdateFrags:
                {
                    int i = r.ReadByte();
                    if (i >= MaxClients)
                        throw BadScoreboard;
                    Scores[i].Frags = r.ReadShort();
                    break;
                }
                case SvcUpdateColors:
                {
                    int i = r.ReadByte();
                    if (i >= MaxClients)
                        throw BadScoreboard;
                    Scores[i].Colors = r.ReadByte();
                    break;                  // CL_NewTranslation: the translation table, the renderer's
                }
                case SvcParticle:
                    ParseParticleEffect();
                    break;
                case SvcSpawnBaseline:
                {
                    int i = r.ReadShort();
                    // must use CL_EntityNum() to force cl.num_entities up
                    ParseBaseline(ref Entities[EntityNum(i)]);
                    break;
                }
                case SvcSpawnStatic:
                    ParseStatic();
                    break;
                case SvcTempEntity:
                    ParseTEnt();
                    break;
                case SvcSetPause:
                    Paused = r.ReadByte();  // CDAudio_Pause and _Resume, VID_HandlePause: output
                    break;
                case SvcSignonNum:
                {
                    int i = r.ReadByte();
                    if (i <= Signon)
                        throw BadSignon;
                    Signon = i;
                    SignonReply();
                    break;
                }
                case SvcKilledMonster:
                    Stats[StatMonsters]++;
                    break;
                case SvcFoundSecret:
                    Stats[StatSecrets]++;
                    break;
                case SvcUpdateStat:
                {
                    int i = r.ReadByte();
                    if (i < 0 || i >= MaxClStats)
                        throw BadStat;
                    Stats[i] = r.ReadLong();
                    break;
                }
                case SvcSpawnStaticSound:
                    for (int i = 0; i < 3; i++)
                        r.ReadCoord();
                    r.ReadByte();
                    r.ReadByte();
                    r.ReadByte();           // S_StaticSound
                    break;
                case SvcCdTrack:
                    CdTrack = r.ReadByte();
                    LoopTrack = r.ReadByte();   // CDAudio_Play
                    break;
                case SvcIntermission:
                    Intermission = 1;
                    CompletedTime = CCast.I32(Time);
                    break;
                case SvcFinale:
                    Intermission = 2;
                    CompletedTime = CCast.I32(Time);
                    r.ReadString();         // SCR_CenterPrint
                    break;
                case SvcCutScene:
                    Intermission = 3;
                    CompletedTime = CCast.I32(Time);
                    r.ReadString();
                    break;
                case SvcDisconnect:
                case SvcSellScreen:
                    throw NotPorted;
                default:
                    throw IllegibleMessage;
            }
        }
    }

    /// <summary>Cbuf_AddText: the stuffed text for the host's command buffer (dropped when it would overflow, as C does).</summary>
    private void AddText(byte* s)
    {
        int n = 0;
        while (s[n] != 0)
            n++;
        if (CommandText.CurSize + n >= CommandText.MaxSize)
            return;                 // Con_Printf ("Cbuf_AddText: overflow\n")
        CommandText.Write(s, n);
    }

    /// <summary>CL_EntityNum: the slot, cl.num_entities grown to it (new slots get vid.colormap).</summary>
    private int EntityNum(int num)
    {
        if (num >= NumEntities)
        {
            if (num >= MaxEdicts)
                throw BadEntityNum;
            while (NumEntities <= num)
            {
                Entities[NumEntities].Colormap = 1;     // vid.colormap
                NumEntities++;
            }
        }
        return num;
    }

    /// <summary>CL_ParseStartSoundPacket: S_StartSound is output; the packet is read and its entity checked.</summary>
    private void ParseStartSoundPacket()
    {
        MsgReader r = _reader;
        int fieldMask = r.ReadByte();
        if ((fieldMask & 1) != 0)       // SND_VOLUME
            r.ReadByte();
        if ((fieldMask & 2) != 0)       // SND_ATTENUATION
            r.ReadByte();
        int channel = r.ReadShort();
        r.ReadByte();                   // sound_num
        int ent = channel >> 3;
        if (ent > MaxEdicts)
            throw BadSoundEnt;
        for (int i = 0; i < 3; i++)
            r.ReadCoord();
    }

    /// <summary>
    /// CL_ParseServerInfo: the state wiped (CL_ClearState), the protocol, maxclients, the game type, the level name,
    /// the model list (each loaded: Mod_ForName with crash false) and the sound list (snd_null precaches nothing),
    /// R_NewMap's particle reset. A wrong protocol or maxclients, or a model not found, is a console message that ends
    /// the parse of the server info, as in C. The models were loaded when the demo was opened
    /// (<see cref="ModelCache.PreloadClient"/>): here they are only looked up.
    /// </summary>
    private void ParseServerInfo()
    {
        MsgReader r = _reader;
        // wipe the client_state_t struct
        ClearState();

        // parse protocol version number
        if (r.ReadLong() != ProtocolVersion)
            return;                     // Con_Printf ("Server returned version %i, not %i")

        // parse maxclients
        MaxClients = r.ReadByte();
        if (MaxClients < 1 || MaxClients > MaxScoreboard)
            return;                     // Con_Printf ("Bad maxclients (%u) from server\n")
        for (int i = 0; i < MaxClients; i++)
        {   // Hunk_AllocName: zeroed
            Score s = Scores[i];
            new Span<byte>(s.Name).Clear();
            s.EnterTime = 0;
            s.Frags = s.Colors = 0;
        }

        // parse gametype
        GameType = r.ReadByte();

        // parse signon message: strncpy (cl.levelname, str, sizeof(cl.levelname)-1)
        byte* str = r.ReadString();
        for (int i = 0; i < LevelNameSize - 1 && str[i] != 0; i++)
            LevelName[i] = str[i];

        // precache models
        int nummodels;
        for (nummodels = 1; ; nummodels++)
        {
            str = r.ReadString();
            if (str[0] == 0)
                break;
            if (nummodels == MaxModels)
                return;                 // Con_Printf ("Server sent too many model precaches\n")
            byte[] name = _modelNames[nummodels];
            int n = 0;
            for (; str[n] != 0 && n < name.Length - 1; n++)
                name[n] = str[n];
            name[n] = 0;                // strcpy into model_precache[MAX_QPATH]
        }

        // precache sounds
        for (int numsounds = 1; ; numsounds++)
        {
            str = r.ReadString();
            if (str[0] == 0)
                break;
            if (numsounds == MaxSounds)
                return;                 // Con_Printf ("Server sent too many sound precaches\n")
        }

        // now we try to load everything else until a cache allocation fails
        for (int i = 1; i < nummodels; i++)
        {
            Model* m;
            fixed (byte* name = _modelNames[i])
                m = _models.LoadedClientModel(name);
            ModelPrecache[i] = (nint)m;
            if (m == null)
                return;                 // Con_Printf ("Model %s not found\n")
        }
        // S_PrecacheSound: snd_null's gives no sfx, so sound_precache stays empty

        // local state
        WorldModel = (Model*)ModelPrecache[1];
        Entities[0].Model = WorldModel;
        ClearParticles();               // R_NewMap
    }

    /// <summary>CL_ParseUpdate: an entity update; its first one completes the signon.</summary>
    private void ParseUpdate(int bits)
    {
        MsgReader r = _reader;
        if (Signon == Signons - 1)
        {   // first update is the final signon stage
            Signon = Signons;
            SignonReply();
        }

        if ((bits & UMoreBits) != 0)
            bits |= r.ReadByte() << 8;

        int num = (bits & ULongEntity) != 0 ? r.ReadShort() : r.ReadByte();
        ref ClientEntity ent = ref Entities[EntityNum(num)];

        bool forcelink = ent.MsgTime != MTime1;     // no previous frame to lerp from
        ent.MsgTime = MTime0;

        int modnum;
        if ((bits & UModel) != 0)
        {
            modnum = r.ReadByte();
            if (modnum >= MaxModels)
                throw BadModNum;
        }
        else
            modnum = ent.Baseline.ModelIndex;

        Model* model = (Model*)ModelPrecache[modnum];
        if (model != ent.Model)
        {
            ent.Model = model;
            // automatic animation (torches, etc) can be either all together or randomized
            if (model != null)
                ent.SyncBase = model->SyncType == StRand ? (float)(Rand.Next() & 0x7fff) / 0x7fff : 0.0f;
            else
                forcelink = true;   // hack to make null model players work
        }

        ent.Frame = (bits & UFrame) != 0 ? r.ReadByte() : ent.Baseline.Frame;

        int c = (bits & UColormap) != 0 ? r.ReadByte() : ent.Baseline.Colormap;
        if (c == 0)
            ent.Colormap = 1;       // vid.colormap
        else
        {
            if (c > MaxClients)
                throw BadColormap;
            ent.Colormap = 1 + c;   // cl.scores[c-1].translations
        }

        ent.SkinNum = (bits & USkin) != 0 ? r.ReadByte() : ent.Baseline.Skin;
        ent.Effects = (bits & UEffects) != 0 ? r.ReadByte() : ent.Baseline.Effects;

        // shift the known values for interpolation
        ent.MsgOrigin1 = ent.MsgOrigin0;
        ent.MsgAngles1 = ent.MsgAngles0;

        ent.MsgOrigin0.X = (bits & UOrigin1) != 0 ? r.ReadCoord() : ent.Baseline.Origin.X;
        ent.MsgAngles0.X = (bits & UAngle1) != 0 ? r.ReadAngle() : ent.Baseline.Angles.X;
        ent.MsgOrigin0.Y = (bits & UOrigin2) != 0 ? r.ReadCoord() : ent.Baseline.Origin.Y;
        ent.MsgAngles0.Y = (bits & UAngle2) != 0 ? r.ReadAngle() : ent.Baseline.Angles.Y;
        ent.MsgOrigin0.Z = (bits & UOrigin3) != 0 ? r.ReadCoord() : ent.Baseline.Origin.Z;
        ent.MsgAngles0.Z = (bits & UAngle3) != 0 ? r.ReadAngle() : ent.Baseline.Angles.Z;

        if ((bits & UNoLerp) != 0)
            ent.ForceLink = 1;

        if (forcelink)
        {   // didn't have an update last message
            ent.MsgOrigin1 = ent.MsgOrigin0;
            ent.Origin = ent.MsgOrigin0;
            ent.MsgAngles1 = ent.MsgAngles0;
            ent.Angles = ent.MsgAngles0;
            ent.ForceLink = 1;
        }
    }

    /// <summary>CL_ParseBaseline.</summary>
    private void ParseBaseline(ref ClientEntity ent)
    {
        MsgReader r = _reader;
        ent.Baseline.ModelIndex = r.ReadByte();
        ent.Baseline.Frame = r.ReadByte();
        ent.Baseline.Colormap = r.ReadByte();
        ent.Baseline.Skin = r.ReadByte();
        ent.Baseline.Origin.X = r.ReadCoord();
        ent.Baseline.Angles.X = r.ReadAngle();
        ent.Baseline.Origin.Y = r.ReadCoord();
        ent.Baseline.Angles.Y = r.ReadAngle();
        ent.Baseline.Origin.Z = r.ReadCoord();
        ent.Baseline.Angles.Z = r.ReadAngle();
    }

    /// <summary>CL_ParseClientdata: server information pertaining to this client only.</summary>
    private void ParseClientdata(int bits)
    {
        MsgReader r = _reader;
        ViewHeight = (bits & SuViewHeight) != 0 ? r.ReadChar() : DefaultViewHeight;
        IdealPitch = (bits & SuIdealPitch) != 0 ? r.ReadChar() : 0;

        MVelocity1 = MVelocity0;
        for (int i = 0; i < 3; i++)
        {
            At(ref PunchAngle, i) = (bits & (SuPunch1 << i)) != 0 ? r.ReadChar() : 0;
            At(ref MVelocity0, i) = (bits & (SuVelocity1 << i)) != 0 ? r.ReadChar() * 16 : 0;
        }

        // [always sent]	if (bits & SU_ITEMS)
        int items = r.ReadLong();
        if (Items != items)
        {   // set flash times
            for (int j = 0; j < 32; j++)
                if ((items & (1 << j)) != 0 && (Items & (1 << j)) == 0)
                    ItemGetTime[j] = (float)Time;
            Items = items;
        }

        OnGround = (bits & SuOnGround) != 0 ? 1 : 0;
        InWater = (bits & SuInWater) != 0 ? 1 : 0;

        Stats[StatWeaponFrame] = (bits & SuWeaponFrame) != 0 ? r.ReadByte() : 0;
        Stats[StatArmor] = (bits & SuArmor) != 0 ? r.ReadByte() : 0;
        Stats[StatWeapon] = (bits & SuWeapon) != 0 ? r.ReadByte() : 0;
        Stats[StatHealth] = r.ReadShort();
        Stats[StatAmmo] = r.ReadByte();
        for (int i = 0; i < 4; i++)
            Stats[StatShells + i] = r.ReadByte();
        Stats[StatActiveWeapon] = r.ReadByte();     // standard_quake
    }

    /// <summary>CL_ParseStatic: a static entity into the next slot, which keeps what it held before in other fields.</summary>
    private void ParseStatic()
    {
        int i = NumStatics;
        if (i >= MaxStaticEntities)
            throw TooManyStatics;
        ref ClientEntity ent = ref StaticEntities[i];
        NumStatics++;
        ParseBaseline(ref ent);

        // copy it to the current state
        ent.Model = (Model*)ModelPrecache[ent.Baseline.ModelIndex];
        ent.Frame = ent.Baseline.Frame;
        ent.Colormap = 1;           // vid.colormap
        ent.SkinNum = ent.Baseline.Skin;
        ent.Effects = ent.Baseline.Effects;
        ent.Origin = ent.Baseline.Origin;
        ent.Angles = ent.Baseline.Angles;   // R_AddEfrags: the renderer's
    }

    /// <summary>V_ParseDamage as far as it is state: the face's pain time and the damage color shift.</summary>
    private void ParseDamage()
    {
        MsgReader r = _reader;
        int armor = r.ReadByte();
        int blood = r.ReadByte();
        for (int i = 0; i < 3; i++)
            r.ReadCoord();

        float count = (float)(blood * 0.5 + armor * 0.5);
        if (count < 10)
            count = 10;

        FaceAnimTime = (float)(Time + 0.2);        // but sbar face into pain frame
        ref ColorShift cs = ref CShifts[CShiftDamage];
        cs.Percent = CCast.I32(cs.Percent + 3 * count);
        if (cs.Percent < 0)
            cs.Percent = 0;
        if (cs.Percent > 150)
            cs.Percent = 150;

        if (armor > blood)
        {
            cs.DestColor[0] = 200;
            cs.DestColor[1] = 100;
            cs.DestColor[2] = 100;
        }
        else if (armor != 0)
        {
            cs.DestColor[0] = 220;
            cs.DestColor[1] = 50;
            cs.DestColor[2] = 50;
        }
        else
        {
            cs.DestColor[0] = 255;
            cs.DestColor[1] = 0;
            cs.DestColor[2] = 0;
        }
        // the view angle kicks (v_dmg_roll, v_dmg_pitch, v_dmg_time) are the renderer's
    }

    /// <summary>V_BonusFlash_f (the "bf" command the server stuffs in).</summary>
    public void BonusFlash()
    {
        ref ColorShift cs = ref CShifts[CShiftBonus];
        cs.DestColor[0] = 215;
        cs.DestColor[1] = 186;
        cs.DestColor[2] = 69;
        cs.Percent = 50;
    }

    /// <summary>CL_SignonReply: the reply to the server's signon stage, into cls.message (cleared in a playback).</summary>
    private void SignonReply()
    {
        SizeBuf m = Message;
        switch (Signon)
        {
            case 1:
                m.WriteByte(ClcStringCmd);
                fixed (byte* s = "prespawn\0"u8)
                    m.WriteString(s);
                break;
            case 2:
            {
                byte* text = stackalloc byte[128];
                var t = new Span<byte>(text, 128);
                m.WriteByte(ClcStringCmd);
                int n = Concat(t, "name \""u8, Cvar("_cl_name\0"u8), "\"\n"u8);     // cl_name is "_cl_name"
                text[n] = 0;
                m.WriteString(text);

                int color = CCast.I32(CvarValue("_cl_color\0"u8));          // cl_color is "_cl_color"
                m.WriteByte(ClcStringCmd);
                n = Concat(t, "color "u8, default, default);
                n += CFormat.Int(color >> 4, t[n..]);
                t[n++] = (byte)' ';
                n += CFormat.Int(color & 15, t[n..]);
                t[n++] = (byte)'\n';
                text[n] = 0;
                m.WriteString(text);

                m.WriteByte(ClcStringCmd);
                fixed (byte* s = "spawn \0"u8)    // "spawn %s", cls.spawnparms: empty without a saved game
                    m.WriteString(s);
                break;
            }
            case 3:
                m.WriteByte(ClcStringCmd);
                fixed (byte* s = "begin\0"u8)
                    m.WriteString(s);
                break;
            case 4:
                break;      // SCR_EndLoadingPlaque
        }
    }

    private ReadOnlySpan<byte> Cvar(ReadOnlySpan<byte> name)
    {
        fixed (byte* n = name)
        {
            byte* s = _cvars.VariableString(n);
            int len = 0;
            while (s[len] != 0)
                len++;
            return new ReadOnlySpan<byte>(s, len);
        }
    }

    private float CvarValue(ReadOnlySpan<byte> name)
    {
        fixed (byte* n = name)
            return _cvars.Value(n);
    }

    private static int Concat(Span<byte> dest, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        a.CopyTo(dest);
        b.CopyTo(dest[a.Length..]);
        c.CopyTo(dest[(a.Length + b.Length)..]);
        return a.Length + b.Length + c.Length;
    }
}
