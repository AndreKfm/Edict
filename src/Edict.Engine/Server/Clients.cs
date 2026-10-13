// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>The server's side of one client connection (net.h's qsocket_t as SV_ReadClientMessage uses it).</summary>
public interface IClientConnection
{
    /// <summary>NET_GetMessage: -1 error, 0 nothing pending, 1 a reliable and 2 an unreliable message in <paramref name="netMessage"/>.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    int GetMessage(SizeBuf netMessage);
}

/// <summary>The network as the server sees it: NET_CheckNewConnections.</summary>
public interface INetServer
{
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    IClientConnection? CheckNewConnections();
}

/// <summary><c>usercmd_t</c>: the movement a client asks for.</summary>
public struct UserCmd
{
    public Vec3 ViewAngles;
    public float ForwardMove, SideMove, UpMove;
}

/// <summary>
/// <c>client_t</c> (server.h). Clients outlive a map (svs.clients): SV_SpawnServer only points them at the new edicts.
/// The name buffer is engine string memory: <c>netname</c> holds its offset (<see cref="Server.ClientNameOffset"/>).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class Client
{
    public const int MaxMsgLen = 8000, NumPingTimes = 16, NumSpawnParms = 16, NameSize = 32;

    public bool Active, Spawned, DropAsap, Privileged, SendSignon;
    public double LastMessage;
    public IClientConnection? NetConnection;
    public UserCmd Cmd;
    public Vec3 WishDir;
    public readonly SizeBuf Message;
    public int Edict;
    public readonly byte[] Name;
    public int Colors;
    public readonly float[] PingTimes;
    public int NumPings;
    public readonly float[] SpawnParms;
    public int OldFrags;

    [NoAllocUnchecked("load path: the client's buffers")]
    public Client()
    {
        Message = new SizeBuf(MaxMsgLen);
        Name = GC.AllocateArray<byte>(NameSize, pinned: true);
        PingTimes = new float[NumPingTimes];
        SpawnParms = new float[NumSpawnParms];
    }

    public byte* NamePtr
    {
        get
        {
            fixed (byte* n = Name)
                return n;
        }
    }

    /// <summary><c>memset (client, 0, sizeof(*client))</c>, keeping the buffers.</summary>
    public void Clear()
    {
        Active = Spawned = DropAsap = Privileged = SendSignon = false;
        LastMessage = 0;
        NetConnection = null;
        Cmd = default;
        WishDir = default;
        Message.Clear();
        Message.AllowOverflow = false;
        Message.Overflowed = false;
        Edict = 0;
        for (int i = 0; i < Name.Length; i++)
            Name[i] = 0;
        Colors = 0;
        for (int i = 0; i < PingTimes.Length; i++)
            PingTimes[i] = 0;
        NumPings = 0;
        for (int i = 0; i < SpawnParms.Length; i++)
            SpawnParms[i] = 0;
        OldFrags = 0;
    }
}

/// <summary>
/// The server's clients (sv_main.c, sv_user.c, host_cmd.c): SV_CheckForNewClients, SV_ConnectClient,
/// SV_SendServerinfo, SV_RunClients, SV_ReadClientMessage, SV_ReadClientMove, the console command parser as client
/// commands use it (Cmd_TokenizeString, Cmd_ExecuteString with src_client), and the host commands a client sends
/// on connecting: prespawn, name, color, spawn, begin. Any other command, a dropped client and loading a saved game
/// are not ported yet and stop with an error. Messages to clients are written in full (their sizes decide overflows).
/// </summary>
public sealed unsafe partial class Server
{
    public const int FlWaterJump = 2048;
    private const int SvcPrint = 8, SvcServerInfo = 11, SvcSetView = 5, SvcSignonNum = 25, SvcCdTrack = 32, SvcTime = 7,
        SvcUpdateName = 13, SvcUpdateFrags = 14, SvcUpdateColors = 17, SvcLightStyle = 12, SvcUpdateStat = 3,
        SvcSetAngle = 10, SvcDamage = 19, SvcClientData = 15;
    private const int ClcBad = 0, ClcNop = 1, ClcDisconnect = 2, ClcMove = 3, ClcStringCmd = 4;
    private const int StatTotalSecrets = 11, StatTotalMonsters = 12, StatSecrets = 13, StatMonsters = 14;
    private const int ProtocolVersion = 15, GameCoop = 0, GameDeathmatch = 1;
    private const int MaxDatagram = 1024, NetMaxMessage = 8192;

    private static readonly SysErrorException NoFreeClients = new("Host_CheckForNewClients: no free clients");
    private static readonly HostErrorException DropNotPorted = new("SV_DropClient: dropping a client is not ported yet");
    private static readonly HostErrorException CommandNotPorted = new("Cmd_ExecuteString: a client command the port does not have yet");

    /// <summary>svs.clients; shared by the servers of one session (they outlive a map).</summary>
    public Client[] Clients { get; set; }

    /// <summary>The network the server reads new clients and their messages from.</summary>
    public INetServer? Net { get; set; }

    /// <summary>host_frametime (double).</summary>
    public double FrameTime { get; set; }

    /// <summary>sv.datagram, sv.reliable_datagram, sv.signon.</summary>
    public SizeBuf Datagram { get; private set; }
    public SizeBuf ReliableDatagram { get; private set; }
    public SizeBuf Signon { get; private set; }

    /// <summary>net_message and its reader.</summary>
    public SizeBuf NetMessage { get; private set; }
    private MsgReader _reader;
    private MsgReader Reader => _reader;

    private int _hostClient = -1;               // host_client, an index into Clients
    private int _svPlayer;                       // sv_player, an edict number

    /// <summary>sv_player (for the level-1 tests, which run SV_Physics without SV_RunClients).</summary>
    internal int SvPlayer { get => _svPlayer; set => _svPlayer = value; }
    private int[] _clientNameOffsets;

    /// <summary>The client state's part of the constructor (load path).</summary>
    [NoAllocUnchecked("load path")]
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Clients), nameof(Datagram), nameof(ReliableDatagram),
        nameof(Signon), nameof(NetMessage), nameof(_reader), nameof(_cmdParse), nameof(_argv), nameof(_clientNameOffsets))]
    private void InitClients()
    {
        Clients = [];
        Datagram = new SizeBuf(MaxDatagram);
        ReliableDatagram = new SizeBuf(MaxDatagram);
        Signon = new SizeBuf(NetMaxMessage);
        NetMessage = new SizeBuf(NetMaxMessage);
        _reader = new MsgReader(NetMessage);
        _cmdParse = new ComParse();
        _argv = new byte[MaxArgs][];
        _clientNameOffsets = [];
        for (int i = 0; i < MaxArgs; i++)
            _argv[i] = GC.AllocateArray<byte>(ComParse.TokenSize, pinned: true);
        DDef* d = FindField("items2"u8);
        _items2 = d == null ? -1 : d->Ofs;
    }

    /// <summary>
    /// The offset (relative to pr_strings) of client <paramref name="i"/>'s name; netname holds it. The replay sets the
    /// reference's offsets (from the capture's map record), so that copies of netname compare word for word.
    /// </summary>
    public int ClientNameOffset(int i) => _clientNameOffsets[i];

    [NoAllocUnchecked("map load")]
    public void SetClientNameOffsets(ReadOnlySpan<int> offsets) => _clientNameOffsets = offsets.ToArray();

    /// <summary>The client whose name is at <paramref name="ofs"/>, or -1.</summary>
    private int ClientNameAt(int ofs)
    {
        for (int i = 0; i < _clientNameOffsets.Length && i < Clients.Length; i++)
            if (_clientNameOffsets[i] == ofs)
                return i;
        return -1;
    }

    private float CvarValue(byte* name) => Cvars.Value(name);

    // ---- connecting

    /// <summary>SV_CheckForNewClients: every new connection gets the first free client slot.</summary>
    public void CheckForNewClients()
    {
        while (true)
        {
            IClientConnection? ret = Net?.CheckNewConnections();
            if (ret is null)
                break;
            int i;
            for (i = 0; i < MaxClients; i++)
                if (!Clients[i].Active)
                    break;
            if (i == MaxClients)
                throw NoFreeClients;
            Clients[i].NetConnection = ret;
            ConnectClient(i);
        }
    }

    /// <summary>SV_ConnectClient (not from a saved game): SetNewParms gives the client's spawn parms.</summary>
    public void ConnectClient(int clientnum)
    {
        Client client = Clients[clientnum];
        int edictnum = clientnum + 1;
        IClientConnection? netconnection = client.NetConnection;
        client.Clear();
        client.NetConnection = netconnection;
        "unconnected\0"u8.CopyTo(client.Name);
        client.Active = true;
        client.Spawned = false;
        client.Edict = edictnum;
        client.Message.AllowOverflow = true;    // we can catch it
        client.Privileged = false;

        // call the progs to get default spawn parms for the new client
        Hooks.ExecuteProgram(Globals[GlobalVars.SetNewParms]);
        for (int i = 0; i < Client.NumSpawnParms; i++)
            client.SpawnParms[i] = *G(GlobalVars.Parm1 + i);

        SendServerinfo(client);
    }

    private static readonly HostErrorException LevelNameFormat = new("SV_SendServerinfo: a % in the level's message (C formats it)");

    private static readonly byte[] VersionText = "\u0002\nVERSION 1.09 SERVER ("u8.ToArray();

    /// <summary>SV_SendServerinfo: the welcome, the precache lists and signon 1.</summary>
    public void SendServerinfo(Client client)
    {
        SizeBuf m = client.Message;
        m.WriteByte(SvcPrint);
        byte* message = stackalloc byte[64];
        int n = 0;
        foreach (byte b in VersionText)
            message[n++] = b;
        n += CFormat.Int(Progs.FileCrc, new Span<byte>(message + n, 16));
        foreach (byte b in " CRC)"u8)
            message[n++] = b;
        message[n] = 0;
        m.WriteString(message);

        m.WriteByte(SvcServerInfo);
        m.WriteLong(ProtocolVersion);
        m.WriteByte(MaxClients);
        fixed (byte* coop = "coop\0"u8, dm = "deathmatch\0"u8)
            m.WriteByte(CvarValue(coop) == 0 && CvarValue(dm) != 0 ? GameDeathmatch : GameCoop);

        byte* level = String(((int*)Fields(0))[EntVars.Message]);
        for (int i = 0; level[i] != 0; i++)
            if (level[i] == '%')
                throw LevelNameFormat;  // sprintf (message, pr_strings + message) would read it as a format
        m.WriteString(level);
        for (int i = 1; i < MaxModels && _modelPrecache[i] != null; i++)
            m.WriteString(_modelPrecache[i]);
        m.WriteByte(0);
        for (int i = 1; i < MaxSounds && _soundPrecache[i] != null; i++)
            m.WriteString(_soundPrecache[i]);
        m.WriteByte(0);

        // send music
        m.WriteByte(SvcCdTrack);
        m.WriteByte(CCast.I32(Fields(0)[EntVars.Sounds]));
        m.WriteByte(CCast.I32(Fields(0)[EntVars.Sounds]));

        // set view
        m.WriteByte(SvcSetView);
        m.WriteShort(client.Edict);

        m.WriteByte(SvcSignonNum);
        m.WriteByte(1);

        client.SendSignon = true;
        client.Spawned = false;     // need prespawn, spawn, etc
    }

    // ---- reading clients

    /// <summary>
    /// SV_RunClients: every active client's messages, then, once spawned, its movement (SV_ClientThink). C also pauses
    /// in single player while the console or a menu is up (<c>key_dest != key_game</c>); a server without a local
    /// client never is.
    /// </summary>
    public void RunClients()
    {
        for (int i = 0; i < MaxClients; i++)
        {
            _hostClient = i;
            Client host = Clients[i];
            if (!host.Active)
                continue;

            _svPlayer = host.Edict;

            if (!ReadClientMessage())
                throw DropNotPorted;    // SV_DropClient (false): client misbehaved

            if (!host.Spawned)
            {   // clear client movement until a new packet is received
                host.Cmd = default;
                continue;
            }

            if (!Paused)
                ClientThink();
        }
    }

    private static bool StrNCaseCmp(byte* s, ReadOnlySpan<byte> word)
    {
        for (int i = 0; i < word.Length; i++)
        {
            int a = s[i], b = word[i];
            if (a >= 'a' && a <= 'z') a -= 'a' - 'A';
            if (b >= 'a' && b <= 'z') b -= 'a' - 'A';
            if (a != b)
                return false;
            if (a == 0)
                return true;
        }
        return true;
    }

    private static readonly byte[][] HostCommandPrefixes =
    [
        "status"u8.ToArray(), "god"u8.ToArray(), "notarget"u8.ToArray(), "fly"u8.ToArray(), "name"u8.ToArray(),
        "noclip"u8.ToArray(), "say"u8.ToArray(), "say_team"u8.ToArray(), "tell"u8.ToArray(), "color"u8.ToArray(),
        "kill"u8.ToArray(), "pause"u8.ToArray(), "spawn"u8.ToArray(), "begin"u8.ToArray(), "prespawn"u8.ToArray(),
        "kick"u8.ToArray(), "ping"u8.ToArray(), "give"u8.ToArray(), "ban"u8.ToArray(),
    ];

    /// <summary>
    /// SV_ReadClientMessage: every pending reliable message, then one unreliable one; false if the client must be
    /// dropped (read error, bad command, disconnect).
    /// </summary>
    public bool ReadClientMessage()
    {
        Client host = Clients[_hostClient];
        MsgReader r = Reader;
        // C's do { ... } while (ret == 1) never tests its condition: each message ends with goto nextmsg, so every
        // pending message is read, until NET_GetMessage has none
        while (true)
        {
            nextmsg:
            int ret = host.NetConnection!.GetMessage(NetMessage);
            if (ret == -1)
                return false;           // Sys_Printf ("SV_ReadClientMessage: NET_GetMessage failed\n")
            if (ret == 0)
                return true;

            r.BeginReading();

            while (true)
            {
                if (!host.Active)
                    return false;       // a command caused an error
                if (r.BadRead)
                    return false;       // Sys_Printf ("SV_ReadClientMessage: badread\n")

                int cmd = r.ReadChar();
                switch (cmd)
                {
                    case -1:
                        goto nextmsg;   // end of message

                    default:
                        return false;   // Sys_Printf ("SV_ReadClientMessage: unknown command char\n")

                    case ClcNop:
                        break;

                    case ClcStringCmd:
                    {
                        byte* s = r.ReadString();
                        int allowed = host.Privileged ? 2 : 0;
                        foreach (byte[] prefix in HostCommandPrefixes)
                            if (StrNCaseCmp(s, prefix))
                            {
                                allowed = 1;
                                break;
                            }
                        if (allowed == 2)
                            throw CommandNotPorted;     // Cbuf_InsertText (s)
                        if (allowed == 1)
                            ExecuteClientCommand(s);
                        // else Con_DPrintf ("%s tried to %s\n", host_client->name, s)
                        break;
                    }

                    case ClcDisconnect:
                        return false;

                    case ClcMove:
                        ReadClientMove(ref host.Cmd);
                        break;
                }
            }
        }
    }

    /// <summary>SV_ReadClientMove: ping time, view angles, movement, buttons, impulse.</summary>
    private void ReadClientMove(ref UserCmd move)
    {
        Client host = Clients[_hostClient];
        MsgReader r = Reader;
        float* v = Fields(host.Edict);

        // read ping time
        host.PingTimes[host.NumPings % Client.NumPingTimes] = (float)(Time - r.ReadFloat());
        host.NumPings++;

        // read current angles
        Vec3 angle;
        angle.X = r.ReadAngle();
        angle.Y = r.ReadAngle();
        angle.Z = r.ReadAngle();
        *(Vec3*)(v + EntVars.VAngle) = angle;

        // read movement
        move.ForwardMove = r.ReadShort();
        move.SideMove = r.ReadShort();
        move.UpMove = r.ReadShort();

        // read buttons
        int bits = r.ReadByte();
        v[EntVars.Button0] = bits & 1;
        v[EntVars.Button2] = (bits & 2) >> 1;

        int i = r.ReadByte();
        if (i != 0)
            v[EntVars.Impulse] = i;
    }

    // ---- client commands

    private const int MaxArgs = 80;
    private ComParse _cmdParse;
    private byte[][] _argv;
    private int _argc;
    private byte* _args;

    /// <summary>
    /// Cmd_TokenizeString: the words of one command (up to a newline), each as COM_Parse reads it; Cmd_Args is the rest
    /// of the line after the first word.
    /// </summary>
    private void Tokenize(byte* text)
    {
        ComParse parse = _cmdParse;
        _argc = 0;
        _args = null;
        while (true)
        {
            // skip whitespace up to a /n
            while (*text != 0 && (sbyte)*text <= ' ' && *text != '\n')
                text++;
            if (*text == '\n')
            {   // a newline seperates commands in the buffer
                text++;
                break;
            }
            if (*text == 0)
                return;
            if (_argc == 1)
                _args = text;
            text = parse.Parse(text);
            if (text == null)
                return;
            if (_argc < MaxArgs)
            {
                byte* t = parse.Token;
                int n = 0;
                while (t[n] != 0)
                    n++;
                new ReadOnlySpan<byte>(t, n + 1).CopyTo(_argv[_argc]);  // a token is shorter than com_token
                _argc++;
            }
        }
    }

    private byte* Argv(int i)
    {
        if ((uint)i >= (uint)_argc)
            fixed (byte* e = "\0"u8)
                return e;
        fixed (byte* a = _argv[i])
            return a;
    }

    private static bool CaseEquals(byte* a, ReadOnlySpan<byte> b)
    {
        int i;
        for (i = 0; i < b.Length; i++)
        {
            int x = a[i], y = b[i];
            if (x >= 'a' && x <= 'z') x -= 'a' - 'A';
            if (y >= 'a' && y <= 'z') y -= 'a' - 'A';
            if (x != y)
                return false;
        }
        return a[i] == 0;
    }

    /// <summary>Cmd_ExecuteString (text, src_client) for the commands a connecting client sends.</summary>
    private void ExecuteClientCommand(byte* text)
    {
        Tokenize(text);
        if (_argc == 0)
            return;     // no tokens
        byte* name = Argv(0);
        if (CaseEquals(name, "prespawn"u8))
            HostPreSpawn();
        else if (CaseEquals(name, "name"u8))
            HostName();
        else if (CaseEquals(name, "color"u8))
            HostColor();
        else if (CaseEquals(name, "spawn"u8))
            HostSpawn();
        else if (CaseEquals(name, "begin"u8))
            Clients[_hostClient].Spawned = true;    // Host_Begin_f
        else
            throw CommandNotPorted;
    }

    /// <summary>Host_Name_f from a client: the name (at most 15 characters), netname pointing at it, and the notice.</summary>
    private void HostName()
    {
        if (_argc == 1)
            return;     // Con_Printf ("\"name\" is \"%s\"\n", cl_name.string)
        byte* newName = _argc == 2 ? Argv(1) : _args;
        int n = 0;
        while (newName[n] != 0 && n < 15)
            n++;        // newName[15] = 0 (C writes it whether or not the name is that long)
        Client host = Clients[_hostClient];
        // Con_Printf ("%s renamed to %s\n", ...) if the client had a name
        new ReadOnlySpan<byte>(newName, n).CopyTo(host.Name);
        host.Name[n] = 0;
        ((int*)Fields(host.Edict))[EntVars.Netname] = ClientNameOffset(_hostClient);
        // send notification to all clients
        ReliableDatagram.WriteByte(SvcUpdateName);
        ReliableDatagram.WriteByte(_hostClient);
        ReliableDatagram.WriteString(host.NamePtr);
    }

    /// <summary>Host_Color_f from a client: top and bottom color (0..13), team = bottom + 1.</summary>
    private void HostColor()
    {
        if (_argc == 1)
            return;     // Con_Printf the current color
        int top, bottom;
        if (_argc == 2)
            top = bottom = Atoi(Argv(1));
        else
        {
            top = Atoi(Argv(1));
            bottom = Atoi(Argv(2));
        }
        top &= 15;
        if (top > 13)
            top = 13;
        bottom &= 15;
        if (bottom > 13)
            bottom = 13;
        int playercolor = top * 16 + bottom;
        Client host = Clients[_hostClient];
        host.Colors = playercolor;
        Fields(host.Edict)[EntVars.Team] = bottom + 1;
        // send notification to all clients
        ReliableDatagram.WriteByte(SvcUpdateColors);
        ReliableDatagram.WriteByte(_hostClient);
        ReliableDatagram.WriteByte(host.Colors);
    }

    private static readonly SysErrorException AtoiOutOfRange = new("atoi: a value outside int (the C libraries differ there)");

    private static int Atoi(byte* s)
    {
        int n = 0;
        while (s[n] != 0)
            n++;
        if (!CAtof.TryAtoi(new ReadOnlySpan<byte>(s, n), out int v))
            throw AtoiOutOfRange;
        return v;
    }

    /// <summary>Host_PreSpawn_f: the signon message and signon 2.</summary>
    private void HostPreSpawn()
    {
        Client host = Clients[_hostClient];
        if (host.Spawned)
            return;     // Con_Printf ("prespawn not valid -- allready spawned\n")
        host.Message.Write(Signon.Data, Signon.CurSize);
        host.Message.WriteByte(SvcSignonNum);
        host.Message.WriteByte(2);
        host.SendSignon = true;
    }

    /// <summary>
    /// Host_Spawn_f (not from a saved game): the client's edict cleared and set up, its spawn parms in parm1..16,
    /// ClientConnect and PutClientInServer; then the names, frags, colors, light styles, stats, the view angle and
    /// the client data (whose state effects matter: SV_WriteClientdataToMessage) and signon 3.
    /// </summary>
    private void HostSpawn()
    {
        Client host = Clients[_hostClient];
        if (host.Spawned)
            return;     // Con_Printf ("Spawn not valid -- allready spawned\n")

        // set up the edict
        int ent = host.Edict;
        float* v = Fields(ent);
        new Span<byte>(v, _entityFields * 4).Clear();
        v[EntVars.Colormap] = ent;
        v[EntVars.Team] = (host.Colors & 15) + 1;
        ((int*)v)[EntVars.Netname] = ClientNameOffset(_hostClient);
        // copy spawn parms out of the client_t
        for (int i = 0; i < Client.NumSpawnParms; i++)
            *G(GlobalVars.Parm1 + i) = host.SpawnParms[i];
        // call the spawn function
        *G(GlobalVars.Time) = (float)Time;
        Globals[GlobalVars.Self] = _svPlayer * _edictSize;
        Hooks.ExecuteProgram(Globals[GlobalVars.ClientConnect]);
        // Sys_Printf ("%s entered the game\n") if the connection is not older than the server
        Hooks.ExecuteProgram(Globals[GlobalVars.PutClientInServer]);

        // send all current names, colors, and frag counts
        SizeBuf m = host.Message;
        m.Clear();
        // send time of update
        m.WriteByte(SvcTime);
        m.WriteFloat((float)Time);
        for (int i = 0; i < MaxClients; i++)
        {
            Client c = Clients[i];
            m.WriteByte(SvcUpdateName);
            m.WriteByte(i);
            m.WriteString(c.NamePtr);
            m.WriteByte(SvcUpdateFrags);
            m.WriteByte(i);
            m.WriteShort(c.OldFrags);
            m.WriteByte(SvcUpdateColors);
            m.WriteByte(i);
            m.WriteByte(c.Colors);
        }
        // send all current light styles
        for (int i = 0; i < MaxLightstyles; i++)
        {
            m.WriteByte(SvcLightStyle);
            m.WriteByte(i);
            m.WriteString(_lightstyles[i]);
        }
        // send some stats
        m.WriteByte(SvcUpdateStat);
        m.WriteByte(StatTotalSecrets);
        m.WriteLong(CCast.I32(*G(GlobalVars.TotalSecrets)));
        m.WriteByte(SvcUpdateStat);
        m.WriteByte(StatTotalMonsters);
        m.WriteLong(CCast.I32(*G(GlobalVars.TotalMonsters)));
        m.WriteByte(SvcUpdateStat);
        m.WriteByte(StatSecrets);
        m.WriteLong(CCast.I32(*G(GlobalVars.FoundSecrets)));
        m.WriteByte(SvcUpdateStat);
        m.WriteByte(StatMonsters);
        m.WriteLong(CCast.I32(*G(GlobalVars.KilledMonsters)));

        // send a fixangle; never a roll angle (savegames would leave a permanent head tilt)
        float* e = Fields(1 + _hostClient);
        m.WriteByte(SvcSetAngle);
        m.WriteAngle(e[EntVars.Angles]);
        m.WriteAngle(e[EntVars.Angles + 1]);
        m.WriteAngle(0);

        WriteClientdataToMessage(_svPlayer, m);

        m.WriteByte(SvcSignonNum);
        m.WriteByte(3);
        host.SendSignon = true;
    }

    // ---- client data

    private const int SuViewHeight = 1 << 0, SuIdealPitch = 1 << 1, SuPunch1 = 1 << 2, SuVelocity1 = 1 << 5,
        SuItems = 1 << 9, SuOnGround = 1 << 10, SuInWater = 1 << 11, SuWeaponFrame = 1 << 12, SuArmor = 1 << 13,
        SuWeapon = 1 << 14;
    private const int DefaultViewHeight = 22;
    private int _items2;                        // GetEdictFieldValue (ent, "items2"): the field's offset, -1 none

    /// <summary>
    /// SV_WriteClientdataToMessage: the damage, ideal pitch, fixangle and the stats of <paramref name="ent"/>. State:
    /// dmg_take and dmg_save are cleared once sent, SV_SetIdealPitch sets idealpitch, a fixangle is cleared.
    /// </summary>
    public void WriteClientdataToMessage(int ent, SizeBuf msg)
    {
        float* v = Fields(ent);

        // send a damage message
        if (v[EntVars.DmgTake] != 0 || v[EntVars.DmgSave] != 0)
        {
            float* other = Fields(((int*)v)[EntVars.DmgInflictor] / _edictSize);
            msg.WriteByte(SvcDamage);
            msg.WriteByte(CCast.I32(v[EntVars.DmgSave]));
            msg.WriteByte(CCast.I32(v[EntVars.DmgTake]));
            for (int i = 0; i < 3; i++)
                msg.WriteCoord((float)(other[EntVars.Origin + i] + 0.5 * (other[EntVars.Mins + i] + other[EntVars.Maxs + i])));
            v[EntVars.DmgTake] = 0;
            v[EntVars.DmgSave] = 0;
        }

        // send the current viewpos offset from the view entity
        SetIdealPitch();    // how much to look up / down ideally

        // a fixangle might get lost in a dropped packet.  Oh well.
        if (v[EntVars.Fixangle] != 0)
        {
            msg.WriteByte(SvcSetAngle);
            for (int i = 0; i < 3; i++)
                msg.WriteAngle(v[EntVars.Angles + i]);
            v[EntVars.Fixangle] = 0;
        }

        int bits = 0;
        if (v[EntVars.ViewOfs + 2] != DefaultViewHeight)
            bits |= SuViewHeight;
        if (v[EntVars.Idealpitch] != 0)
            bits |= SuIdealPitch;

        // stuff the sigil bits into the high bits of items for sbar, or else mix in items2
        int items = _items2 >= 0
            ? CCast.I32(v[EntVars.Items]) | (CCast.I32(v[_items2]) << 23)
            : CCast.I32(v[EntVars.Items]) | (CCast.I32(*G(GlobalVars.Serverflags)) << 28);

        bits |= SuItems;
        if ((CCast.I32(v[EntVars.Flags]) & FlOnGround) != 0)
            bits |= SuOnGround;
        if (v[EntVars.Waterlevel] >= 2)
            bits |= SuInWater;
        for (int i = 0; i < 3; i++)
        {
            if (v[EntVars.Punchangle + i] != 0)
                bits |= SuPunch1 << i;
            if (v[EntVars.Velocity + i] != 0)
                bits |= SuVelocity1 << i;
        }
        if (v[EntVars.Weaponframe] != 0)
            bits |= SuWeaponFrame;
        if (v[EntVars.Armorvalue] != 0)
            bits |= SuArmor;
        bits |= SuWeapon;

        // send the data
        msg.WriteByte(SvcClientData);
        msg.WriteShort(bits);
        if ((bits & SuViewHeight) != 0)
            msg.WriteChar(CCast.I32(v[EntVars.ViewOfs + 2]));
        if ((bits & SuIdealPitch) != 0)
            msg.WriteChar(CCast.I32(v[EntVars.Idealpitch]));
        for (int i = 0; i < 3; i++)
        {
            if ((bits & (SuPunch1 << i)) != 0)
                msg.WriteChar(CCast.I32(v[EntVars.Punchangle + i]));
            if ((bits & (SuVelocity1 << i)) != 0)
                msg.WriteChar(CCast.I32(v[EntVars.Velocity + i] / 16));
        }
        msg.WriteLong(items);
        if ((bits & SuWeaponFrame) != 0)
            msg.WriteByte(CCast.I32(v[EntVars.Weaponframe]));
        if ((bits & SuArmor) != 0)
            msg.WriteByte(CCast.I32(v[EntVars.Armorvalue]));
        msg.WriteByte(SvModelIndex(String(((int*)v)[EntVars.Weaponmodel])));
        msg.WriteShort(CCast.I32(v[EntVars.Health]));
        msg.WriteByte(CCast.I32(v[EntVars.Currentammo]));
        msg.WriteByte(CCast.I32(v[EntVars.AmmoShells]));
        msg.WriteByte(CCast.I32(v[EntVars.AmmoNails]));
        msg.WriteByte(CCast.I32(v[EntVars.AmmoRockets]));
        msg.WriteByte(CCast.I32(v[EntVars.AmmoCells]));
        msg.WriteByte(CCast.I32(v[EntVars.Weapon]));    // standard_quake
    }

    private static readonly SysErrorException ModelNotPrecached = new("SV_ModelIndex: model not precached");

    /// <summary>SV_ModelIndex: 0 for no name, else the precache index; Sys_Error if not precached.</summary>
    public int SvModelIndex(byte* name)
    {
        if (name == null || name[0] == 0)
            return 0;
        int i;
        for (i = 0; i < MaxModels && _modelPrecache[i] != null; i++)
            if (Same(_modelPrecache[i], name))
                return i;
        throw ModelNotPrecached;
    }

    private const int MaxForward = 6;
    private const double OnEpsilon = 0.1;

    /// <summary>
    /// SV_SetIdealPitch: on the ground, six traces down in front of sv_player; a steady slope sets idealpitch
    /// (step heights truncated to int as in C).
    /// </summary>
    private void SetIdealPitch()
    {
        float* p = Fields(_svPlayer);
        if ((CCast.I32(p[EntVars.Flags]) & FlOnGround) == 0)
            return;

        float angleval = (float)(p[EntVars.Angles + 1] * Math.PI * 2 / 360);
        float sinval = (float)CMath.Sin(angleval);
        float cosval = (float)CMath.Cos(angleval);
        float* z = stackalloc float[MaxForward];
        int i;
        for (i = 0; i < MaxForward; i++)
        {
            Vec3 top, bottom;
            top.X = p[EntVars.Origin] + cosval * (i + 3) * 12;
            top.Y = p[EntVars.Origin + 1] + sinval * (i + 3) * 12;
            top.Z = p[EntVars.Origin + 2] + p[EntVars.ViewOfs + 2];
            bottom.X = top.X;
            bottom.Y = top.Y;
            bottom.Z = top.Z - 160;
            Trace tr = Move(top, default, default, bottom, 1, _svPlayer);
            if (tr.AllSolid)
                return;     // looking at a wall, leave ideal the way is was
            if (tr.Fraction == 1)
                return;     // near a dropoff
            z[i] = top.Z + tr.Fraction * (bottom.Z - top.Z);
        }

        int dir = 0;
        int steps = 0;
        for (int j = 1; j < i; j++)
        {
            int step = CCast.I32(z[j] - z[j - 1]);
            if (step > -OnEpsilon && step < OnEpsilon)
                continue;
            if (dir != 0 && (step - dir > OnEpsilon || step - dir < -OnEpsilon))
                return;     // mixed changes
            steps++;
            dir = step;
        }

        if (dir == 0)
        {
            p[EntVars.Idealpitch] = 0;
            return;
        }
        if (steps < 2)
            return;
        fixed (byte* n = "sv_idealpitchscale\0"u8)
            p[EntVars.Idealpitch] = -dir * Cvars.Value(n);
    }

    /// <summary>
    /// Host_ServerFrame up to the physics: the frametime global, SV_ClearDatagram, new clients, the clients' messages
    /// and movement. Cut 3, step 2 (SV_Physics and SV_SendClientMessages follow).
    /// </summary>
    public void ServerFrameStart()
    {
        *G(GlobalVars.Frametime) = (float)FrameTime;
        Datagram.Clear();
        CheckForNewClients();
        RunClients();
    }
}
