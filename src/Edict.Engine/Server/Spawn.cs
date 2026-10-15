// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// Spawning a map: SV_SpawnServer (sv_main.c) up to its frame-0 point, and the entity parser of pr_edict.c
/// (ED_FindField, ED_FindFunction, ED_NewString, ED_ParseEpair, ED_ParseEdict, ED_LoadFromFile). Console output
/// (Con_Printf, ED_Print) is not ported. Where C has undefined behaviour on malformed text (strcpy into fixed buffers,
/// a vector with fewer than three components read past its end, atoi out of range), the port stops with an error.
/// Load path.
/// </summary>
public sealed unsafe partial class Server
{
    public const int SpawnflagNotEasy = 256, SpawnflagNotMedium = 512, SpawnflagNotHard = 1024, SpawnflagNotDeathmatch = 2048;
    private const int SolidBspValue = 4, MovetypePushValue = 7;

    /// <summary>current_skill: skill rounded and clamped to 0..3 by SV_SpawnServer.</summary>
    public int CurrentSkill { get; private set; }

    /// <summary>sv.name, as a copy (Host_Restart_f spawns it again).</summary>
    public byte[] MapName { get; private set; } = null!;

    /// <summary>sv.name, an engine string (pr_global_struct->mapname points to it).</summary>
    public int NameOffset { get; private set; }

    private ComParse? _parse;

    [NoAllocUnchecked("map load")]
    private HunkStrings Hunk => EngineStrings as HunkStrings
        ?? throw new InvalidOperationException("spawning needs the engine strings of a HunkStrings");

    /// <summary>ED_FindField: the field def named <paramref name="name"/>, or null.</summary>
    [NoAllocUnchecked("map load")]
    public DDef* FindField(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < Progs.Header->NumFieldDefs; i++)
            if (Progs.String(Progs.FieldDefs[i].SName).SequenceEqual(name))
                return Progs.FieldDefs + i;
        return null;
    }

    /// <summary>ED_FindFunction: the index of the function named <paramref name="name"/>, or -1.</summary>
    [NoAllocUnchecked("map load")]
    public int FindFunction(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < Progs.NumFunctions; i++)
            if (Progs.String(Progs.Functions[i].SName).SequenceEqual(name))
                return i;
        return -1;
    }

    /// <summary>
    /// ED_NewString: a copy on the hunk with "\n" turned into a newline and a backslash before any other character
    /// kept as a backslash in place of both (C's loop: <c>if (string[i] == '\\' &amp;&amp; i &lt; l-1)</c>).
    /// </summary>
    [NoAllocUnchecked("map load")]
    public int NewString(ReadOnlySpan<byte> s)
    {
        int l = s.Length + 1;
        int ofs = Hunk.Alloc(l, out byte* p);    // zeroed, as Hunk_Alloc's memory
        byte* q = p;
        for (int i = 0; i < l; i++)
        {
            byte c = i < s.Length ? s[i] : (byte)0;
            if (c == '\\' && i < l - 1)
            {
                i++;
                *q++ = i < s.Length && s[i] == 'n' ? (byte)'\n' : (byte)'\\';
            }
            else
                *q++ = c;
        }
        return ofs;
    }

    private static readonly HostErrorException ParseError = new("ED_ParseEdict: parse error");
    private static readonly SysErrorException VectorTooLong = new("ED_ParseEpair: vector value longer than C's string[128]");
    private static readonly SysErrorException VectorTooShort =
        new("ED_ParseEpair: vector value with fewer than three components (C reads past its end: undefined)");
    private static readonly SysErrorException AtoiRange = new("ED_ParseEpair: entity number outside int (atoi differs between C libraries)");
    private static readonly SysErrorException BadEdictNum = new("EDICT_NUM: bad number");

    /// <summary>
    /// ED_ParseEpair: <paramref name="s"/> parsed into the word(s) of <paramref name="key"/> in <paramref name="base"/>
    /// (an edict's fields or the globals); false where C prints "Can't find field/function".
    /// </summary>
    [NoAllocUnchecked("map load")]
    public bool ParseEpair(int* @base, DDef* key, ReadOnlySpan<byte> s)
    {
        int* d = @base + key->Ofs;
        switch ((EType)(key->Type & ~DDef.SaveGlobal))
        {
            case EType.String:
                *d = NewString(s);
                break;

            case EType.Float:
                *(float*)d = (float)CAtof.Atof(s);
                break;

            case EType.Vector:
            {
                // strcpy (string, s), then three words split at single spaces, each read with atof
                if (s.Length >= 128)
                    throw VectorTooLong;
                byte* str = stackalloc byte[128];
                s.CopyTo(new Span<byte>(str, s.Length));
                str[s.Length] = 0;
                int v = 0, w = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (w > s.Length)
                        throw VectorTooShort;
                    while (str[v] != 0 && str[v] != ' ')
                        v++;
                    str[v] = 0;
                    ((float*)d)[i] = (float)CAtof.Atof(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(str + w));
                    w = v = v + 1;
                }
                break;
            }

            case EType.Entity:
            {
                if (!CAtof.TryAtoi(s, out int n))
                    throw AtoiRange;
                if (n < 0 || n >= MaxEdicts)
                    throw BadEdictNum;
                *d = n * _edictSize;
                break;
            }

            case EType.Field:
            {
                DDef* def = FindField(s);
                if (def == null)
                {
                    Con?.Msg("Can't find field "u8).Msg(s).Msg("\n"u8).EndMsg();
                    return false;
                }
                *d = Globals[def->Ofs];
                break;
            }

            case EType.Function:
            {
                int func = FindFunction(s);
                if (func < 0)
                {
                    Con?.Msg("Can't find function "u8).Msg(s).Msg("\n"u8).EndMsg();
                    return false;
                }
                *d = func;
                break;
            }
        }
        return true;
    }

    private static readonly SysErrorException EofInEntity = new("ED_ParseEntity: EOF without closing brace");
    private static readonly SysErrorException BraceWithoutData = new("ED_ParseEntity: closing brace without data");
    private static readonly SysErrorException KeyTooLong = new("ED_ParseEdict: key longer than C's keyname[256]");
    private static readonly SysErrorException EdPrintNotPorted =
        new("ED_LoadFromFile: an entity without classname or spawn function, which a listen server's console shows with ED_Print (not ported)");
    private static readonly SysErrorException AngleTooLong = new("ED_ParseEdict: angle longer than C's temp[32]");

    /// <summary>ED_ParseEdict: the key/value pairs of one entity into edict <paramref name="ent"/>.</summary>
    [NoAllocUnchecked("map load")]
    public byte* ParseEdict(byte* data, int ent)
    {
        ComParse parse = _parse ??= new ComParse();
        bool init = false;
        Span<byte> keyname = stackalloc byte[256];
        Span<byte> value = stackalloc byte[ComParse.TokenSize + 8];

        // clear it
        if (ent != 0)   // hack
            new Span<byte>(Fields(ent), _entityFields * 4).Clear();

        // go through all the dictionary pairs
        while (true)
        {
            // parse key
            data = parse.Parse(data);
            if (parse.Token[0] == '}')
                break;
            if (data == null)
                throw EofInEntity;

            ReadOnlySpan<byte> token = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(parse.Token);
            // anglehack is to allow QuakeEd to write single scalar angles and allow them to be turned into vectors
            bool anglehack = token.SequenceEqual("angle"u8);
            if (anglehack)
                token = "angles"u8;
            // FIXME: change light to _light to get rid of this hack
            if (token.SequenceEqual("light"u8))
                token = "light_lev"u8;      // hack for single light def

            if (token.Length >= keyname.Length)
                throw KeyTooLong;
            token.CopyTo(keyname);
            int n = token.Length;
            // another hack to fix keynames with trailing spaces
            while (n != 0 && keyname[n - 1] == ' ')
                n--;
            ReadOnlySpan<byte> key = keyname[..n];

            // parse value
            data = parse.Parse(data);
            if (data == null)
                throw EofInEntity;
            if (parse.Token[0] == '}')
                throw BraceWithoutData;

            init = true;

            // keynames with a leading underscore are used for utility comments, and are immediately discarded
            if (n > 0 && key[0] == '_')
                continue;

            DDef* def = FindField(key);
            if (def == null)
            {
                Con?.Msg("'"u8).Msg(key).Msg("' is not a field\n"u8).EndMsg();
                continue;
            }

            scoped ReadOnlySpan<byte> v = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(parse.Token);
            if (anglehack)
            {   // strcpy (temp, com_token); sprintf (com_token, "0 %s 0", temp);
                if (v.Length >= 32)
                    throw AngleTooLong;
                "0 "u8.CopyTo(value);
                v.CopyTo(value[2..]);
                " 0"u8.CopyTo(value[(2 + v.Length)..]);
                v = value[..(v.Length + 4)];
            }

            if (!ParseEpair((int*)Fields(ent), def, v))
                throw ParseError;
        }

        if (!init)
            EdictNum(ent)->Free = 1;

        return data;
    }

    private static readonly SysErrorException ExpectingBrace = new("ED_LoadFromFile: found a token when expecting {");

    /// <summary>
    /// ED_LoadFromFile: every entity of <paramref name="data"/> parsed into an edict (the first into the world) and
    /// its spawn function run, unless its spawnflags exclude it for the skill or deathmatch, or it has no classname
    /// or no spawn function. Returns the number of entities inhibited.
    /// </summary>
    [NoAllocUnchecked("map load")]
    public int LoadFromFile(byte* data)
    {
        ComParse parse = _parse ??= new ComParse();
        int ent = -1;
        int inhibit = 0;
        *G(GlobalVars.Time) = (float)Time;
        float deathmatch = CvarValue("deathmatch"u8);

        // parse ents
        while (true)
        {
            // parse the opening brace
            data = parse.Parse(data);
            if (data == null)
                break;
            if (parse.Token[0] != '{')
                throw ExpectingBrace;

            ent = ent < 0 ? 0 : Alloc();
            data = ParseEdict(data, ent);

            // remove things from different skill levels or deathmatch
            int spawnflags = CCast.I32(Fields(ent)[EntVars.Spawnflags]);
            if (deathmatch != 0)
            {
                if ((spawnflags & SpawnflagNotDeathmatch) != 0)
                {
                    Free(ent);
                    inhibit++;
                    continue;
                }
            }
            else if ((CurrentSkill == 0 && (spawnflags & SpawnflagNotEasy) != 0)
                     || (CurrentSkill == 1 && (spawnflags & SpawnflagNotMedium) != 0)
                     || (CurrentSkill >= 2 && (spawnflags & SpawnflagNotHard) != 0))
            {
                Free(ent);
                inhibit++;
                continue;
            }

            // immediately call spawn function
            int classname = ((int*)Fields(ent))[EntVars.Classname];
            if (classname == 0)
            {
                if (Con != null)
                    throw EdPrintNotPorted;     // Con_Printf ("No classname for:\n"); ED_Print (ent)
                Free(ent);
                continue;
            }

            // look for the spawn function
            int func = FindFunction(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(String(classname)));
            if (func < 0)
            {
                if (Con != null)
                    throw EdPrintNotPorted;     // Con_Printf ("No spawn function for:\n"); ED_Print (ent)
                Free(ent);
                continue;
            }

            Globals[GlobalVars.Self] = ent * _edictSize;
            Hooks.ExecuteProgram(func);
        }

        return inhibit;     // Con_DPrintf ("%i entities inhibited\n", inhibit)
    }

    [NoAllocUnchecked("map load")]
    private float CvarValue(ReadOnlySpan<byte> name)
    {
        Span<byte> z = stackalloc byte[name.Length + 1];
        name.CopyTo(z);
        z[name.Length] = 0;
        fixed (byte* p = z)
            return Cvars.Value(p);
    }

    [NoAllocUnchecked("map load")]
    private void CvarSetValue(ReadOnlySpan<byte> name, float value)
    {
        Span<byte> z = stackalloc byte[name.Length + 1];
        name.CopyTo(z);
        z[name.Length] = 0;
        fixed (byte* p = z)
            Cvars.SetValue(p, value);
    }

    /// <summary>
    /// SV_SpawnServer up to its frame-0 point (after ED_LoadFromFile, before the two settling SV_Physics frames,
    /// SV_CreateBaseline and the serverinfo messages, which are cut 3 and messages): skill and deathmatch made
    /// consistent, the server state as after <c>memset (&amp;sv, 0, ...)</c> with sv.time 1, the world model loaded
    /// into <paramref name="models"/> with its inline models as precache entries 1.., the world edict, the spawn
    /// globals, then every entity. The server must be new (Host_ClearMemory): its progs loaded, edicts zeroed,
    /// <see cref="MaxEdicts"/> and <see cref="MaxClients"/> set. False if the map does not exist ("Couldn't spawn
    /// server").
    /// </summary>
    [NoAllocUnchecked("map load")]
    public bool SpawnServer(ReadOnlySpan<byte> map, ModelCache models)
    {
        // let's not have any servers with no name
        fixed (byte* hn = "hostname\0"u8)
        {
            byte* hostname = Cvars.VariableString(hn);
            if (hostname != null && hostname[0] == 0)
                fixed (byte* un = "UNNAMED\0"u8)
                    Cvars.Set(hn, un);
        }

        // make cvars consistant
        if (CvarValue("coop"u8) != 0)
            CvarSetValue("deathmatch"u8, 0);
        CurrentSkill = CCast.I32(CvarValue("skill"u8) + 0.5);
        if (CurrentSkill < 0)
            CurrentSkill = 0;
        if (CurrentSkill > 3)
            CurrentSkill = 3;
        CvarSetValue("skill"u8, CurrentSkill);

        // set up the new server
        NumEdicts = MaxClients + 1;
        Active = false;             // ss_loading
        Paused = false;
        Time = 1.0;
        NameOffset = Hunk.Add(map);
        MapName = map.ToArray();

        Span<byte> modelname = stackalloc byte[map.Length + 9];
        "maps/"u8.CopyTo(modelname);
        map.CopyTo(modelname[5..]);
        ".bsp"u8.CopyTo(modelname[(5 + map.Length)..]);
        BrushModelSet world;
        try
        {
            world = models.LoadBrush(modelname);
        }
        catch (SysErrorException)
        {
            return false;           // Con_Printf ("Couldn't spawn server %s\n", sv.modelname)
        }
        int worldName = Hunk.Add(modelname);
        models.Preload(Progs, world.World->Entities);  // the port loads at map load what precache_model may name

        // the precache lists: "" first, then the world and its inline models (localmodels "*1", ...)
        var names = new nint[world.Count];
        var inline = new nint[world.Count];
        names[0] = (nint)String(worldName);
        inline[0] = (nint)world.World;
        Span<byte> sub = stackalloc byte[12];
        for (int i = 1; i < world.Count; i++)
        {
            sub[0] = (byte)'*';
            int len = 1 + CFormat.Int(i, sub[1..]);
            names[i] = (nint)String(Hunk.Add(sub[..len]));
            inline[i] = (nint)(world.Models + i);
            models.Register(sub[..len], world.Models + i);
        }
        InitPrecache(Progs.Strings, names, inline);
        ModelSource = models;

        // clear world interaction links
        ClearWorld(world.World);

        // load the rest of the entities
        float* w = Fields(0);
        new Span<byte>(w, _entityFields * 4).Clear();
        EdictNum(0)->Free = 0;
        ((int*)w)[EntVars.Model] = worldName;
        w[EntVars.Modelindex] = 1;      // world model
        w[EntVars.Solid] = SolidBspValue;
        w[EntVars.Movetype] = MovetypePushValue;

        float coop = CvarValue("coop"u8);
        if (coop != 0)
            *G(GlobalVars.Coop) = coop;
        else
            *G(GlobalVars.Deathmatch) = CvarValue("deathmatch"u8);

        Globals[GlobalVars.Mapname] = NameOffset;

        // serverflags are for cross level information (sigils)
        *G(GlobalVars.Serverflags) = ServerFlags;

        LoadFromFile(world.World->Entities);
        return true;
    }

    /// <summary>
    /// SV_SpawnServer after the frame-0 point: the server is active, two settling frames with host_frametime 0.1
    /// (which stays the host's frame time for the rest of the host frame), the baselines and serverinfo to the
    /// connected clients.
    /// </summary>
    [NoAllocUnchecked("map load")]
    public void SettleAndFinish()
    {
        Active = true;              // sv.active, sv.state = ss_active
        FrameTime = 0.1;
        Physics();
        Physics();
        FinishSpawn();
    }
}
