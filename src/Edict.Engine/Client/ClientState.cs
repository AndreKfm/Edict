// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary><c>entity_t</c> (render.h) as the client keeps it; the renderer's fields (efrags, visframe, dlightframe and bits, trivial_accept, topnode) are left out.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ClientEntity
{
    public int ForceLink;                             // qboolean: model changed
    public int UpdateType;
    public EntityState Baseline;                      // to fill in defaults in updates
    public double MsgTime;                            // time of last update
    public Vec3 MsgOrigin0, MsgOrigin1;               // last two updates (0 is newest)
    public Vec3 Origin;
    public Vec3 MsgAngles0, MsgAngles1;               // last two updates (0 is newest)
    public Vec3 Angles;
    public Model* Model;                              // null = no model
    public int Frame;
    public float SyncBase;                            // for client-side animations
    public int Colormap;                              // 0 none (NULL), 1 vid.colormap, 2 + i scores[i]'s translation
    public int Effects;                               // light, particles, etc
    public int SkinNum;                               // for alias models
}

/// <summary><c>dlight_t</c>.</summary>
public struct DLight
{
    public Vec3 Origin;
    public float Radius, Die, Decay, MinLight;        // die: stop lighting after this time; decay: drop this each second
    public int Key;
}

/// <summary><c>beam_t</c>.</summary>
public unsafe struct Beam
{
    public int Entity;
    public Model* Model;
    public float EndTime;
    public Vec3 Start, End;
}

/// <summary><c>cshift_t</c>.</summary>
public unsafe struct ColorShift
{
    public fixed int DestColor[3];
    public int Percent;                               // 0-256
}

/// <summary><c>scoreboard_t</c> without its translation table (derived from the colors, for the renderer).</summary>
public sealed class Score
{
    public readonly byte[] Name = new byte[ClientState.MaxScoreboardName];
    public float EnterTime;
    public int Frags;
    public int Colors;                                // two 4 bit fields
}

/// <summary>A listen server as its local client sees it: sv.active, and Host_ShutdownServer, which CL_Disconnect calls.</summary>
public interface IListenServer
{
    bool Active { get; }

    void Shutdown(bool crash);
}

/// <summary>A visible entity (<c>cl_visedicts</c>) as an array and index: 0 cl_entities, 1 statics, 2 temp entities, 3 the gun model.</summary>
public struct EntityRef
{
    public byte Array;
    public int Index;
}

/// <summary>
/// The client (client.h, cl_main.c, cl_parse.c, cl_tent.c, cl_demo.c, and of view.c and r_part.c what the network
/// state needs): <c>cls</c>, <c>cl</c> and the client's arrays, as the harness runs the client — demo playback,
/// no screen (docs/PLAN.md, P5). The sounds go to the <see cref="Sound"/> of a host with sound (P6 step 8), the CD
/// player and the console are output only; the models are the <see cref="ModelCache"/>'s.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe partial class ClientState
{
    public const int MaxEdicts = 600, MaxStaticEntities = 128, MaxTempEntities = 64, MaxVisEdicts = 256,
        MaxDLights = 32, MaxBeams = 24, MaxLightstyles = 64, MaxStyleString = 64, MaxScoreboard = 16,
        MaxScoreboardName = 32, MaxClStats = 32, NumCShifts = 4, MaxModels = 256, MaxSounds = 256, Signons = 4,
        MaxMsgLen = 8000, NetMaxMessage = 8192, LevelNameSize = 40;

    // cactive_t
    public const int CaDedicated = 0, CaDisconnected = 1, CaConnected = 2;

    private static readonly SysErrorException DemoMessageTooLong = new("Demo message > MAX_MSGLEN");

    // ---- cls (client_static_t)
    public int State = CaDisconnected;
    public int Signon;
    public bool DemoPlayback;
    public IClientConnection? NetCon;                 // cls.netcon: a listen server's loopback socket

    /// <summary>The console and screen Con_Printf and SCR_CenterPrint go to; null where the screen is skipped.</summary>
    public Screen? Con;

    /// <summary>The sound of a host with sound (the sound build's), or null (snd_null's: every call does nothing).</summary>
    public Sound? Sound;

    /// <summary>The server of a listen host, which a disconnect shuts down; null for a client alone.</summary>
    public IListenServer? ListenServer;

    /// <summary>sv.active: in a listen server the client does not lerp and CL_ClearState leaves the signon alone.</summary>
    public bool ServerActive { get; set; }
    public int ForceTrack;
    public readonly SizeBuf Message;                  // cls.message: writing buffer to send to server

    // ---- cl (client_state_t)
    public int MoveMessages;
    public Vec3 CmdViewAngles;                         // cl.cmd: the last command sent
    public float CmdForwardMove, CmdSideMove, CmdUpMove;
    public readonly int[] Stats;
    public int Items;
    public readonly float[] ItemGetTime;
    public float FaceAnimTime;
    public readonly ColorShift[] CShifts;
    public readonly ColorShift[] PrevCShifts;
    public Vec3 MViewAngles0, MViewAngles1, ViewAngles;
    public Vec3 MVelocity0, MVelocity1, Velocity;
    public Vec3 PunchAngle;
    public float IdealPitch, PitchVel;
    public int NoDrift;
    public float DriftMove;
    public double LastStop;
    public float ViewHeight, Crouch;
    public int Paused, OnGround, InWater;
    public int Intermission, CompletedTime;
    public double MTime0, MTime1, Time, OldTime;
    public float LastReceivedMessage;
    public readonly nint[] ModelPrecache;              // Model*
    public readonly int[] SoundPrecache;               // sfx numbers (Sound); 0 without sound (snd_null's S_PrecacheSound gives no sfx)
    public readonly byte[] LevelName;
    public int ViewEntity, MaxClients, GameType;
    public Model* WorldModel;
    public int NumEntities, NumStatics;
    public ClientEntity ViewEnt;                       // the gun model
    public int CdTrack, LoopTrack;
    public readonly Score[] Scores;                    // cl.scores[0 .. cl.maxclients), valid once the server info came

    // ---- the client's arrays
    public readonly ClientEntity[] Entities;
    public readonly ClientEntity[] StaticEntities;
    public readonly ClientEntity[] TempEntities;
    public int NumTempEntities;
    public readonly DLight[] DLights;
    public readonly Beam[] Beams;
    public readonly int[] LightstyleLength;
    public readonly byte[][] LightstyleMap;
    public readonly EntityRef[] VisEdicts;
    public int NumVisEdicts;

    private readonly Cvars _cvars;
    private readonly ModelCache _models;

    /// <summary>The rand stream (one per process in C; without a server it is the client's alone).</summary>
    public CRand Rand;

    /// <summary>host_frametime and realtime, which the host sets before each frame.</summary>
    public double FrameTime { get; set; }
    public double RealTime { get; set; }

    /// <summary>The console commands the server stuffs in (svc_stufftext), for the host's command buffer.</summary>
    public SizeBuf CommandText { get; }

    [NoAllocUnchecked("load path: the client's buffers")]
    public ClientState(Cvars cvars, ModelCache models, uint randSeed, SizeBuf commandText)
    {
        _cvars = cvars;
        _models = models;
        Rand.Srand(randSeed);
        CommandText = commandText;
        Message = new SizeBuf(1024);                   // CL_Init: SZ_Alloc (&cls.message, 1024)
        NetMessage = new SizeBuf(NetMaxMessage);
        _reader = new MsgReader(NetMessage);
        Stats = new int[MaxClStats];
        ItemGetTime = new float[32];
        CShifts = new ColorShift[NumCShifts];
        PrevCShifts = new ColorShift[NumCShifts];
        ModelPrecache = new nint[MaxModels];
        SoundPrecache = new int[MaxSounds];
        LevelName = new byte[LevelNameSize];
        Entities = new ClientEntity[MaxEdicts];
        StaticEntities = new ClientEntity[MaxStaticEntities];
        TempEntities = new ClientEntity[MaxTempEntities];
        DLights = new DLight[MaxDLights];
        Beams = new Beam[MaxBeams];
        LightstyleLength = new int[MaxLightstyles];
        LightstyleMap = new byte[MaxLightstyles][];
        for (int i = 0; i < MaxLightstyles; i++)
            LightstyleMap[i] = new byte[MaxStyleString];
        VisEdicts = new EntityRef[MaxVisEdicts];
        Scores = new Score[MaxScoreboard];
        for (int i = 0; i < MaxScoreboard; i++)
            Scores[i] = new Score();
        _modelNames = new byte[MaxModels][];
        for (int i = 0; i < MaxModels; i++)
            _modelNames[i] = GC.AllocateArray<byte>(64, pinned: true);
        _soundNames = new byte[MaxSounds][];
        for (int i = 0; i < MaxSounds; i++)
            _soundNames[i] = new byte[64];
        InitParticles();
        _move = new SizeBuf(128);
        Buttons = new KButton[NumButtons];
        InitEfrags();
    }

    private readonly SizeBuf _move;                    // CL_SendMove's buf[128]
    private readonly byte[][] _modelNames;             // CL_ParseServerInfo's model_precache[MAX_MODELS][MAX_QPATH]
    private readonly byte[][] _soundNames;             // CL_ParseServerInfo's sound_precache[MAX_SOUNDS][MAX_QPATH]

    /// <summary><c>v[i]</c> as a reference, for component loops on fields and array elements.</summary>
    private static ref float At(ref Vec3 v, int i) => ref System.Runtime.CompilerServices.Unsafe.Add(ref v.X, i);

    /// <summary>
    /// CL_ClearState with Host_ClearMemory (no server runs): cls.signon 0, cl and the client's arrays wiped. The
    /// efrags are the renderer's. R_NewMap's particle reset comes with the server info.
    /// </summary>
    public void ClearState()
    {
        if (!ServerActive)
        {
            Signon = 0;                                // Host_ClearMemory (no server runs)
            Con?.FlushCaches();
        }
        ClearCl();

        Message.Clear();

        new Span<ClientEntity>(Entities).Clear();
        new Span<DLight>(DLights).Clear();
        for (int i = 0; i < MaxLightstyles; i++)
        {
            LightstyleLength[i] = 0;
            new Span<byte>(LightstyleMap[i]).Clear();
        }
        new Span<ClientEntity>(TempEntities).Clear();
        new Span<Beam>(Beams).Clear();
        // allocate the efrags and chain together into a free list
        ClearEfrags();
        // cl_static_entities is not cleared: a slot keeps what a statics of an earlier map left in it
    }

    /// <summary>Host_ClearMemory's part of the client, from SV_SpawnServer: cls.signon 0 and cl wiped (not the client's arrays).</summary>
    public void ClearMemory()
    {
        Con?.FlushCaches();                            // D_FlushCaches
        Signon = 0;
        ClearCl();
    }

    /// <summary>memset (&amp;cl, 0, sizeof(cl)).</summary>
    private void ClearCl()
    {
        FreeEfrag = -1;                                // cl.free_efrags
        MoveMessages = 0;
        CmdViewAngles = default;
        CmdForwardMove = CmdSideMove = CmdUpMove = 0;
        new Span<int>(Stats).Clear();
        Items = 0;
        new Span<float>(ItemGetTime).Clear();
        FaceAnimTime = 0;
        new Span<ColorShift>(CShifts).Clear();
        new Span<ColorShift>(PrevCShifts).Clear();
        MViewAngles0 = MViewAngles1 = ViewAngles = default;
        MVelocity0 = MVelocity1 = Velocity = default;
        PunchAngle = default;
        IdealPitch = PitchVel = 0;
        NoDrift = 0;
        DriftMove = 0;
        LastStop = 0;
        ViewHeight = Crouch = 0;
        Paused = OnGround = InWater = 0;
        Intermission = CompletedTime = 0;
        MTime0 = MTime1 = Time = OldTime = 0;
        LastReceivedMessage = 0;
        new Span<nint>(ModelPrecache).Clear();
        new Span<int>(SoundPrecache).Clear();
        new Span<byte>(LevelName).Clear();
        ViewEntity = MaxClients = GameType = 0;
        WorldModel = null;
        NumEntities = NumStatics = 0;
        ViewEnt = default;
        CdTrack = LoopTrack = 0;
    }

    /// <summary>
    /// CL_EstablishConnection with Host_Reconnect_f (connect local, from Host_Map_f): the connection, the signon
    /// from 0. CL_Disconnect before it finds nothing connected in a listen server's first map.
    /// </summary>
    [NoAllocUnchecked("load path: a new connection")]
    public void EstablishConnection(IClientConnection connection)
    {
        if (DemoPlayback)
            return;
        Disconnect();
        NetCon = connection;
        State = CaConnected;
        Signon = 0;                                    // need all the signon messages before playing; Host_Reconnect_f
    }

    private const int ClcDisconnect = 2;

    /// <summary>
    /// CL_Disconnect: the sounds stopped; a playback stops, a connection sends clc_disconnect, closes, and shuts down
    /// a listen server; the signon starts again.
    /// </summary>
    [NoAllocUnchecked("console command: a disconnect")]
    public void Disconnect()
    {
        // stop sounds (especially looping!)
        Sound?.StopAllSounds(true);
        // if running a local server, shut it down
        if (DemoPlayback)
            StopPlayback();
        else if (State == CaConnected)
        {
            // Con_DPrintf ("Sending clc_disconnect\n")
            Message.Clear();
            Message.WriteByte(ClcDisconnect);
            NetCon!.SendUnreliableMessage(Message);
            Message.Clear();
            NetCon.Close();
            NetCon = null;
            State = CaDisconnected;
            if (ListenServer is { Active: true } sv)
                sv.Shutdown(false);
        }
        DemoPlayback = false;                          // cls.demoplayback = cls.timedemo = false
        Signon = 0;
    }

    // ---- demo playback (cl_demo.c)

    private byte[]? _demo;
    private int _demoPos;

    /// <summary>
    /// CL_PlayDemo_f on a demo already read (the disconnect first: a playback or a connection ends): the CD track
    /// line, then the messages.
    /// </summary>
    [NoAllocUnchecked("load path: the demo")]
    public void PlayDemo(byte[] demo)
    {
        Disconnect();
        DemoPlayback = false;
        Signon = 0;
        _demo = demo;
        _demoPos = 0;
        DemoPlayback = true;
        State = CaConnected;
        ForceTrack = 0;
        bool neg = false;
        while (_demoPos < demo.Length && demo[_demoPos] != '\n')
        {
            byte c = demo[_demoPos++];
            if (c == '-')
                neg = true;
            else
                ForceTrack = ForceTrack * 10 + (c - '0');
        }
        _demoPos++;
        if (neg)
            ForceTrack = -ForceTrack;
    }

    /// <summary>CL_StopPlayback.</summary>
    public void StopPlayback()
    {
        if (!DemoPlayback)
            return;
        _demo = null;
        DemoPlayback = false;
        State = CaDisconnected;
    }

    /// <summary>
    /// CL_GetMessage in playback: once the signon is complete, a message only when cl.time has passed the last one's
    /// time. At the end of the file C reads the view angles into an uninitialized local before it stops; no record is
    /// written after that frame (the playback has ended), so the port stops without them.
    /// </summary>
    private int GetMessage()
    {
        if (!DemoPlayback)
        {
            if (NetCon is null)
                return -1;                              // NET_GetMessage (NULL)
            while (true)
            {
                int r = NetCon.GetMessage(NetMessage);
                if (r != 1 && r != 2)
                    return r;
                // discard nop keepalive message
                if (!(NetMessage.CurSize == 1 && NetMessage.Data[0] == SvcNop))
                    return r;                           // Con_Printf ("<-- server to client keepalive\n") for a nop
            }
        }
        // decide if it is time to grab the next message
        if (Signon == Signons)  // allways grab until fully connected
        {
            if (Time <= MTime0)
                return 0;       // don't need another message yet
        }

        // get the next message
        byte[] d = _demo!;
        if (_demoPos + 16 > d.Length)
        {
            StopPlayback();
            return 0;
        }
        int length = d[_demoPos] | (d[_demoPos + 1] << 8) | (d[_demoPos + 2] << 16) | (d[_demoPos + 3] << 24);
        MViewAngles1 = MViewAngles0;
        fixed (byte* p = &d[_demoPos + 4])
            MViewAngles0 = *(Vec3*)p;
        _demoPos += 16;
        if (length > MaxMsgLen)
            throw DemoMessageTooLong;
        if (length <= 0 || _demoPos + length > d.Length)
        {   // fread of the data fails (of zero bytes too: it returns 0)
            StopPlayback();
            return 0;
        }
        NetMessage.Clear();
        fixed (byte* p = &d[_demoPos])
            NetMessage.Write(p, length);
        _demoPos += length;
        return 1;
    }
}
