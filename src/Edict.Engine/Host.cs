// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The host loop of a server without a local client (host.c, host_cmd.c, cmd.c): _Host_Frame's server part — the
/// frame's rand (), Host_FilterTime with host_framerate, Cbuf_Execute, Host_ServerFrame, host_framecount — and the
/// commands a game issues: map, restart (QuakeC's localcmd after a death) and changelevel (with SV_SaveSpawnparms).
/// It owns what outlives a map (svs, the cvars, the command buffer, the rand stream, the models) and gives each spawn a
/// new <see cref="Server"/> with fresh memory, as Host_ClearMemory and the hunk do. Load path; the frame itself runs
/// in the server.
/// </summary>
[NoAllocUnchecked("the host loop: spawning allocates; the server frame is checked in Server")]
public sealed unsafe class Host : IDisposable, ISoundHost, IListenServer
{
    private readonly QFileSystem _files;
    private readonly Progs _progs;
    private readonly ModelCache _models;
    private readonly int _headerBytes;
    private readonly int _numBuiltins;
    private readonly Client[] _clients;
    private readonly INetServer _net;
    private readonly Loopback? _loop;                  // net_loop.c: a listen server's local client connects through it
    private readonly NetMain? _netMain;                // a game's net_main.c: the loopback and the datagram driver
    private readonly bool _game, _dedicated;
    private int _maxClients;                           // svs.maxclients; the array holds svs.maxclientslimit
    private ClientConsole? _console;                   // a listen server's client: its commands, its screen
    private CRand _rand;
    private int _serverflags;                          // svs.serverflags
    private double _realtime, _oldrealtime;

    private int* _globals;
    private byte* _edicts;
    private byte* _stringTemp;
    private HunkStrings? _hunk;

    /// <summary>The cvars (they outlive a map).</summary>
    public Cvars Cvars { get; }

    /// <summary>The current server, null before the first map.</summary>
    public Server? Sv { get; private set; }

    /// <summary>host_framecount.</summary>
    public int FrameCount { get; private set; }

    /// <summary>host_frametime.</summary>
    public double FrameTime { get; private set; }

    /// <summary>svs.maxclients.</summary>
    public int MaxClients
    {
        [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
        get => _maxClients;
    }

    /// <summary>A dedicated server's "quit": the platform ends.</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>A game's network (net_main.c), or null.</summary>
    public NetMain? Net => _netMain;

    /// <summary>Called at the frame-0 point of every spawn (after ED_LoadFromFile), as the reference's spawn record.</summary>
    public Action<Server>? Spawned { get; set; }

    /// <param name="cvars">The cvars as the reference had them at the first spawn.</param>
    /// <param name="maxclients">svs.maxclients.</param>
    /// <param name="randSeed">
    /// The rand state at the first spawn (after Host_Init's and the first frame's draws); for a listen server, before
    /// frame 0 (the reference's 'I' record).
    /// </param>
    /// <param name="headerBytes">The reference's edict header size (docs/adr/D7).</param>
    /// <param name="net">The tape; or a <see cref="Loopback"/>, which makes this a listen server with a local client.</param>
    public Host(QFileSystem files, Cvars cvars, int maxclients, uint randSeed, INetServer net, int headerBytes, int numBuiltins)
        : this(files, cvars, maxclients, randSeed, net, headerBytes, numBuiltins, null, null, false)
    {
    }

    /// <summary>
    /// A listen server started as the reference starts (<see cref="ClientConsole.Startup"/>): the cvars at their
    /// defaults, the console's first lines and the screen (when <paramref name="withScreen"/>), and "exec quake.rc"
    /// in the first frame, which runs the command line's "+" commands (stuffcmds).
    /// </summary>
    /// <param name="randSeed">The rand state before frame 0 (the reference's 'I' record).</param>
    /// <param name="commandLine">The arguments after the program's name.</param>
    /// <param name="cmdline">The cmdline cvar's value.</param>
    /// <param name="sound">The sound build's sound (snd_dma.c, ref/build.sh SOUND=1) instead of snd_null's.</param>
    /// <param name="sink">The screen's sink from Host_Init on.</param>
    public Host(QFileSystem files, uint randSeed, Loopback net, int headerBytes, int numBuiltins,
                IReadOnlyList<string> commandLine, string cmdline, bool withScreen, bool sound = false, IScreenSink? sink = null)
        : this(files, Cvars.FromRegistry([], SoundCvars(sound, commandLine)), 1, randSeed, net, headerBytes, numBuiltins,
               commandLine, cmdline, withScreen, sound: sound, sink: sink)
    {
    }

    /// <summary>Whether S_Init registers its cvars: with sound, unless -nosound stops it first.</summary>
    internal static bool SoundCvars(bool sound, IReadOnlyList<string> commandLine) =>
        sound && !commandLine.Take(Cmd.MaxNumArgvs - 1).Contains("-nosound");     // COM_CheckParm on com_argv

    /// <summary>offsetof (edict_t, v) of the reference on x64 (the captures' map records hold it): 108 bytes.</summary>
    public const int ReferenceEdictHeaderBytes = 108;

    /// <summary>pr_numbuiltins: pr_builtin[] compiled without QUAKE2.</summary>
    public const int ReferenceNumBuiltins = 79;

    /// <summary>
    /// A player's game, for a platform: a listen server started as the reference starts it, with the screen, the
    /// frame clock the platform's (host_framerate stays 0), the platform's cvars (in_win.c's m_filter) after the
    /// reference's, and a console command the port lacks printing that it is not ported instead of stopping.
    /// </summary>
    /// <param name="sound">The game's sound (snd_dma.c, mixed into <see cref="Sound"/>'s DMA buffer for the platform to play).</param>
    /// <param name="lan">The LAN driver (UDP) of the datagram driver; null for the loopback alone.</param>
    [NoAllocUnchecked("load path")]
    public static Host CreateGame(QFileSystem files, IReadOnlyList<string> commandLine,
                                  IReadOnlyList<(string Name, string Default)>? platformCvars = null, bool sound = false,
                                  INetLan? lan = null)
    {
        string cmdline = string.Join(' ', ["edict", .. commandLine]);
        if (cmdline.Length > 255)
            cmdline = cmdline[..255];
        var cvars = Cvars.FromRegistry([("m_filter", "0"), .. platformCvars ?? []], SoundCvars(sound, commandLine));
        // Host_FindMaxClients: -listen [n] (8 without a number)
        int maxclients = MaxClientsOf(commandLine, "-listen", 1);
        int limit = Math.Max(maxclients, 4);
        NetMain net = MakeNet(cvars, lan, limit + 1);    // net_numsockets: one more for the local client
        var host = new Host(files, cvars, maxclients, 0, net, ReferenceEdictHeaderBytes, ReferenceNumBuiltins,
            commandLine, cmdline, withScreen: true, game: true, sound: sound, maxclientsLimit: limit);
        host.StartNet(commandLine, listen: ArgIndex(commandLine, "-listen") >= 0);
        return host;
    }

    /// <summary>
    /// A dedicated server (cls.state == ca_dedicated, P6 step 9.4): no client, -dedicated [n] slots (8 without a
    /// number), the datagram driver listening, Con_Printf into <paramref name="output"/> (the platform's stdout), and
    /// quake.rc run in the first frame; startdemos starts "map start" when no map was given.
    /// </summary>
    [NoAllocUnchecked("load path")]
    public static Host CreateDedicated(QFileSystem files, IReadOnlyList<string> commandLine, INetLan lan, IConsole output)
    {
        var cvars = Cvars.FromRegistry();
        int maxclients = MaxClientsOf(commandLine, "-dedicated", 8);
        int limit = Math.Max(maxclients, 4);
        NetMain net = MakeNet(cvars, lan, limit);
        var host = new Host(files, cvars, maxclients, 0, net, ReferenceEdictHeaderBytes, ReferenceNumBuiltins,
            commandLine, null, withScreen: false, game: true, dedicated: true, maxclientsLimit: limit);
        host.Cmd.Con = output;
        host.StartNet(commandLine, listen: true);
        host.AddText("exec quake.rc\n"u8);
        return host;
    }

    private static int ArgIndex(IReadOnlyList<string> commandLine, string name)
    {
        for (int i = 0; i < commandLine.Count && i < Cmd.MaxNumArgvs - 1; i++)
            if (commandLine[i] == name)
                return i;
        return -1;
    }

    /// <summary>Host_FindMaxClients' svs.maxclients: the number after the parameter (8 without one), else the default.</summary>
    private static int MaxClientsOf(IReadOnlyList<string> commandLine, string parm, int otherwise)
    {
        int i = ArgIndex(commandLine, parm);
        if (i < 0)
            return otherwise;
        int n = i + 1 < commandLine.Count ? Edict.Exact.CAtof.QAtoi(System.Text.Encoding.Latin1.GetBytes(commandLine[i + 1])) : 8;
        if (n < 1)
            n = 8;
        else if (n > ClientState.MaxScoreboard)
            n = ClientState.MaxScoreboard;
        return n;
    }

    private static NetMain MakeNet(Cvars cvars, INetLan? lan, int numSockets)
    {
        if (lan is null)
            return new NetMain(new Loopback(), null, null);
        var hn = new HostNet(cvars);
        // a game's loopback keeps no log of the server's sends: only the gates take it (and empty it every frame)
        return new NetMain(new Loopback(), new Datagram(hn, [lan], numSockets, 1), hn) { Rules = hn };
    }

    /// <summary>NET_Init's rest once the host is there: -port, the LAN drivers (-nolan), listening; deathmatch for maxclients above 1.</summary>
    private void StartNet(IReadOnlyList<string> commandLine, bool listen)
    {
        fixed (byte* dm = "deathmatch\0"u8)
            Cvars.SetValue(dm, _maxClients > 1 ? 1 : 0);
        NetMain net = _netMain!;
        if (net.Host is HostNet hn)
            hn.Host = this;
        int p = ArgIndex(commandLine, "-port");
        if (p < 0)
            p = ArgIndex(commandLine, "-udpport");
        if (p >= 0 && p + 1 < commandLine.Count && net.Dgrm?.Lan(0) is IHostPort hp)
            hp.HostPort = Edict.Exact.CAtof.QAtoi(System.Text.Encoding.Latin1.GetBytes(commandLine[p + 1]));
        if (net.Dgrm is { } d && d.Init(ArgIndex(commandLine, "-nolan") >= 0) < 0)
            Cmd.Printf("no LAN driver: the network is the loopback alone\n"u8);
        if (listen)
            net.Listen(true);
    }

    private Host(QFileSystem files, Cvars cvars, int maxclients, uint randSeed, INetServer net, int headerBytes,
                 int numBuiltins, IReadOnlyList<string>? commandLine, string? cmdline, bool withScreen, bool game = false,
                 bool sound = false, IScreenSink? sink = null, bool dedicated = false, int maxclientsLimit = 0)
    {
        _files = files;
        Cvars = cvars;
        QFile f = files.Find("progs.dat"u8) ?? throw new FileNotFoundException("progs.dat");
        _progs = new Progs(f);
        _models = new ModelCache(files);
        _headerBytes = headerBytes;
        _numBuiltins = numBuiltins;
        _clients = new Client[Math.Max(maxclients, maxclientsLimit)];
        for (int i = 0; i < _clients.Length; i++)
            _clients[i] = new Client();
        _maxClients = maxclients;
        _net = net;
        _loop = net as Loopback ?? (net as NetMain)?.Loop;
        _netMain = net as NetMain;
        _game = game;
        _dedicated = dedicated;
        _rand.Srand(randSeed);
        _firstFrame = !game && net is not Loopback;
        Cmd = new Cmd(cvars, files) { CommandLine = commandLine ?? [], Interactive = game };
        Cmd.Add("map", Map);
        Cmd.Add("restart", Restart);
        Cmd.Add("changelevel", Changelevel);
        if (_netMain is not null)
        {
            Cmd.Add("listen", ListenCommand);
            Cmd.Add("maxplayers", MaxPlayers);
            Cmd.Add("port", Port);
        }
        if (dedicated)
        {
            Cmd.Add("startdemos", () => { if (Sv is not { Active: true }) AddText("map start\n"u8); });
            Cmd.Add("bind", () => { });         // Key_Init's commands: a dedicated server has no keys to bind
            Cmd.Add("unbindall", () => { });
            Cmd.Add("quit", () =>                           // Host_Quit_f: the server shut down, the platform ends
            {
                ShutdownServer(false);
                QuitRequested = true;
            });
        }
        if (_loop is not null && !dedicated)
        {
            Cl = new ClientState(cvars, _models, randSeed, Cmd.Text) { ListenServer = this, LoadsModels = game };
            _console = new ClientConsole(Cmd, cvars, Cl, () => Sv is { Active: true });
            Cmd.Add("playdemo", PlayDemo);
            if (SoundCvars(sound, Cmd.CommandLine))
                Cmd.UseSoundTable();
            _console.LocalStatus = () => Sv!.ConsoleStatus();
            _console.LocalKick = () => Sv!.ConsoleKick(Cmd.Argc > 1 ? [.. Cmd.Argv(0), (byte)' ', .. Cmd.Args] : Cmd.Argv(0),
                                                       CvarBytes("_cl_name"));
            if (_netMain is not null)
                Cmd.Add("connect", Connect);
            if (cmdline is not null)
                _console.Startup(files, _models, cmdline, withScreen, harness: !game, sound: sound ? this : null, sink: sink);
        }
        InitializeTypes();
    }

    /// <summary>cmd.c: the command buffer and the commands.</summary>
    public Cmd Cmd { get; }

    /// <summary>A listen server's screen, or null.</summary>
    public Screen? Screen => _console?.Screen;

    /// <summary>A listen server's local client, null for a server alone.</summary>
    public ClientState? Cl { get; }

    /// <summary>The sound of a host with sound, or null.</summary>
    public Sound? Sound => _console?.Sound;

    bool IListenServer.Active => Sv is { Active: true };

    void IListenServer.Shutdown(bool crash) => ShutdownServer(crash);

    /// <summary>
    /// Host_ShutdownServer: sv.active off, the local client disconnected (its sounds stop at once), the clients'
    /// last messages, svc_disconnect and their drops (<see cref="Server.ShutdownClients"/>), and the server gone
    /// (memset (&amp;sv)); the rand stream stays the host's.
    /// </summary>
    [NoAllocUnchecked("console command: the server shut down")]
    private void ShutdownServer(bool crash)
    {
        if (Sv is not { Active: true } sv)
            return;
        sv.Active = false;
        // stop all client sounds immediately
        if (Cl is { State: ClientState.CaConnected } cl)
            cl.Disconnect();
        if (_netMain?.Host is { } clock)
            sv.ShutdownClients(crash, clock.FloatTime);
        else
            sv.ShutdownClients(crash);
        _rand = sv.Rand;
        FreeServer();
    }

    private bool _clientReading;    // CL_ReadFromServer runs: the client's copy of the rand stream is the one

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    ref CRand ISoundHost.Rand() => ref _clientReading ? ref Cl!.Rand : ref Rand();

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    double ISoundHost.RealTime => _realtime;

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    double ISoundHost.FrameTime => FrameTime;

    /// <summary>
    /// Runs the type initializer of every engine type now, on the load path. The static readonly fields are mostly
    /// preallocated exceptions, read only when thrown, so a type's first static read can come late: Server's came in
    /// SV_SendServerinfo at the first client connect and allocated its 4,672 bytes of statics inside a frame. Tiered
    /// compilation can also run a type's initializer at the entry of a method that only names one of its statics on a
    /// throw path, when that method is recompiled: in whichever frame that happens (the client's gate saw it once).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "the engine's own types; types a trimmer removed have no type initializer left to run")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2059",
        Justification = "the engine's own types; types a trimmer removed have no type initializer left to run")]
    internal static void InitializeTypes()
    {
        foreach (Type t in typeof(Host).Assembly.GetTypes())
            if (!t.ContainsGenericParameters)
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle);
    }

    private bool _firstFrame;

    /// <summary>Cbuf_AddText.</summary>
    public void AddText(ReadOnlySpan<byte> text) => Cmd.AddText(text);

    /// <summary>
    /// _Host_Frame (time): rand () (except in the first frame, whose draw the given seed includes), the frame time,
    /// the command buffer, the server frame, and host_framecount. False when Host_FilterTime holds the frame back
    /// (less than 1/72 s since the last one): only the rand draw and the real time happened.
    /// </summary>
    public bool Frame(float time)
    {
        if (!_game)
            return HostFrame(time);
        try
        {
            if (_netMain is { } nm)
                fixed (byte* t = "net_messagetimeout\0"u8)
                    nm.MessageTimeout = Cvars.Value(t);
            return HostFrame(time);
        }
        catch (HostErrorException e)
        {
            HostError(e.Message);
        }
        catch (EndGameException e)
        {
            EndGame(e.Message);
        }
        catch (DisconnectException e)
        {
            _clientReading = false;
            Cmd.Msg(System.Text.Encoding.Latin1.GetBytes(e.Message)).Msg("\n"u8).End();
            Cl?.Disconnect();
        }
        return true;
    }

    /// <summary>Host_Error in a game: the message, the server shut down, the client disconnected, the frame abandoned.</summary>
    private void HostError(string message)
    {
        _clientReading = false;
        Cmd.Msg("Host_Error: "u8).Msg(System.Text.Encoding.Latin1.GetBytes(message)).Msg("\n"u8).End();
        ShutdownServer(false);
        if (_dedicated)
            throw new SysErrorException("Host_Error: " + message);
        Cl?.Disconnect();
        _console?.StopDemoLoop();
    }

    /// <summary>Host_EndGame in a game: the server shut down, the client disconnected (the demo loop is not ported).</summary>
    private void EndGame(string message)
    {
        _clientReading = false;
        ShutdownServer(false);
        if (_dedicated)
            throw new SysErrorException("Host_EndGame: " + message);
        Cl?.Disconnect();
    }

    private bool HostFrame(float time)
    {
        if (!_firstFrame)
            Rand().Next();          // keep the random time dependent
        _firstFrame = false;

        // decide the simulation time (Host_FilterTime)
        _realtime += time;
        if (_realtime - _oldrealtime < 1.0 / 72.0)
            return false;       // framerate is too high
        FrameTime = _realtime - _oldrealtime;
        _oldrealtime = _realtime;
        float framerate = HostFramerate();
        if (framerate > 0)
            FrameTime = framerate;
        else
        {
            if (FrameTime > 0.1)
                FrameTime = 0.1;
            if (FrameTime < 0.001)
                FrameTime = 0.001;
        }

        if (Screen is { } screen)
            screen.RealTime = _realtime;

        // process console commands
        Cmd.Execute();

        ClientState? cl = Cl;
        if (cl is not null)
        {
            cl.FrameTime = FrameTime;
            cl.RealTime = _realtime;
            cl.ServerActive = Sv is { Active: true };
            // if running the server locally, make intentions now
            if (cl.ServerActive)
                cl.SendCmd();
        }

        if (Sv is { Active: true } sv)
        {
            sv.FrameTime = FrameTime;
            sv.RealTime = _realtime;
            if (_net is { } net && net is IFrameCounted counted)
                counted.FrameCount = FrameCount;
            sv.ServerFrame();
        }

        if (cl is not null)
        {
            // if running the server remotely, send intentions now after the incoming messages have been read
            if (!cl.ServerActive)
                cl.SendCmd();
            // fetch results from server; client and server draw from one rand stream
            if (cl.State == ClientState.CaConnected)
            {
                ref CRand rand = ref Rand();
                cl.Rand = rand;
                _clientReading = true;
                cl.ReadFromServer();
                while (cl.PendingServerInfo)
                    LoadServerInfo(cl);
                _clientReading = false;
                rand = cl.Rand;
            }
            // update video (host_frametime as the frame left it: a spawn sets 0.1)
            if (Screen is { } s)
            {
                s.FrameTime = FrameTime;
                s.UpdateScreen();
            }
            // update audio; CDAudio_Update is output
            UpdateSound(_console!, cl);
            if (cl.Signon == ClientState.Signons)
                cl.DecayLights();
        }

        FrameCount++;
        return true;
    }

    /// <summary>A remote server's info: its models and sounds loaded (CL_ParseServerInfo's loads), then the read resumed.</summary>
    private void LoadServerInfo(ClientState cl)
    {
        (List<byte[]> models, List<byte[]> sounds) = cl.PendingNames();
        _models.LoadForClient(models);
        if (Sound is { } snd)
            foreach (byte[] name in sounds)
                snd.PrecacheSound(name);
        cl.ResumeRead();
    }

    private int ArgInt(int i) => Edict.Exact.CAtof.QAtoi(Cmd.Argv(i));

    /// <summary>Host_Connect_f: a demo stopped, CL_EstablishConnection to the server named, Host_Reconnect_f.</summary>
    private void Connect()
    {
        _console!.StopDemoLoop();       // stop demo loop in case this fails
        if (Cl!.DemoPlayback)
        {
            Cl.StopPlayback();
            Cl.Disconnect();
        }
        // CL_EstablishConnection
        Cl.Disconnect();
        IClientConnection? c;
        fixed (byte* h = (byte[])[.. Cmd.Argv(1), 0])
            c = _netMain!.Connect(h);
        if (c is null)
            throw new HostErrorException("CL_Connect: connect failed\n");
        Cl.EstablishConnection(c);
        _console.Reconnect();
    }

    /// <summary>NET_Listen_f.</summary>
    private void ListenCommand()
    {
        if (Cmd.Argc != 2)
        {
            Cmd.Msg("\"listen\" is \""u8).Int(_netMain!.Listening ? 1 : 0).Msg("\"\n"u8).End();
            return;
        }
        _netMain!.Listen(ArgInt(1) != 0);
    }

    /// <summary>Host_Maxplayers_f: svs.maxclients while no server runs (up to svs.maxclientslimit), listen and deathmatch with it.</summary>
    private void MaxPlayers()
    {
        if (Cmd.Argc != 2)
        {
            Cmd.Msg("\"maxplayers\" is \""u8).Int(_maxClients).Msg("\"\n"u8).End();
            return;
        }
        if (Sv is { Active: true })
        {
            Cmd.Printf("maxplayers can not be changed while a server is running.\n"u8);
            return;
        }
        int n = ArgInt(1);
        if (n < 1)
            n = 1;
        if (n > _clients.Length)
        {
            n = _clients.Length;
            Cmd.Msg("\"maxplayers\" set to \""u8).Int(n).Msg("\"\n"u8).End();
        }
        bool listening = _netMain!.Listening;
        if (n == 1 && listening)
            AddText("listen 0\n"u8);
        if (n > 1 && !listening)
            AddText("listen 1\n"u8);
        _maxClients = n;
        fixed (byte* dm = "deathmatch\0"u8)
        fixed (byte* v = n == 1 ? "0\0"u8 : "1\0"u8)
            Cvars.Set(dm, v);
    }

    /// <summary>NET_Port_f: the port the datagram driver listens on; a listening server listens again on the new one.</summary>
    private void Port()
    {
        IHostPort? hp = _netMain!.Dgrm?.Lan(0) as IHostPort;
        if (Cmd.Argc != 2)
        {
            Cmd.Msg("\"port\" is \""u8).Int(hp?.HostPort ?? Datagram.DefaultHostPort).Msg("\"\n"u8).End();
            return;
        }
        int n = ArgInt(1);
        if (n < 1 || n > 65534)
        {
            Cmd.Printf("Bad value, must be between 1 and 65534\n"u8);
            return;
        }
        if (hp is not null)
            hp.HostPort = n;
        if (_netMain.Listening)
        {
            // force a change to the new port
            AddText("listen 0\n"u8);
            AddText("listen 1\n"u8);
        }
    }

    /// <summary>A cvar's string (load path and console commands).</summary>
    [NoAllocUnchecked("console command")]
    private byte[] CvarBytes(string name)
    {
        fixed (byte* n = System.Text.Encoding.Latin1.GetBytes(name + "\0"))
        {
            byte* s = Cvars.VariableString(n);
            int len = 0;
            while (s[len] != 0)
                len++;
            return new ReadOnlySpan<byte>(s, len).ToArray();
        }
    }

    /// <summary>host_framerate's value.</summary>
    public float HostFramerate()
    {
        fixed (byte* n = "host_framerate\0"u8)
            return Cvars.Value(n);
    }

    /// <summary>_Host_Frame's S_Update: the listener as the renderer left it once the signon is complete, else at the origin.</summary>
    internal static void UpdateSound(ClientConsole console, ClientState cl)
    {
        if (console.Sound is not { } sound)
            return;
        if (cl.Signon == ClientState.Signons && console.Screen is { } s)
        {
            s.SoundListener(out Vec3 origin, out Vec3 forward, out Vec3 right, out Vec3 up);
            sound.Update(origin, forward, right, up);
        }
        else
            sound.Update(default, default, default, default);
    }

    /// <summary>The rand stream: the server's while there is one (it draws from it), the host's before.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    private ref CRand Rand()
    {
        if (Sv is not null)
            return ref Sv.Rand;
        return ref _rand;
    }

    /// <summary>Host_Map_f: a new server; a listen server's client connects to it ("connect local").</summary>
    private void Map()
    {
        if (Cmd.Source != Cmd.SrcCommand)
            return;
        _console?.StopDemoLoop();       // cls.demonum = -1: stop demo loop in case this fails
        Cl?.Disconnect();
        ShutdownServer(false);
        if (Cl is not null)
        {
            _console!.Keys.Dest = Keys.KeyGame;     // remove console or menu
            _console.BeginLoadingPlaque();          // stops the sounds, as CL_Disconnect did before it
        }
        _serverflags = 0;   // haven't completed an episode yet
        SpawnServer(Cmd.Argc > 1 ? Cmd.Argv(1) : []);
        if (Sv is not { Active: true })
            return;
        if (Cl is not null)
        {
            if (Cmd.Argc > 2)
                throw new InvalidOperationException("map with spawn parms (cls.spawnparms) is not ported");
            Cl.EstablishConnection(_loop!.Connect());     // connect local
        }
    }

    /// <summary>
    /// CL_PlayDemo_f on a listen server: the disconnect first (CL_Disconnect, which shuts the server down), then the
    /// demo by name (".dem" added if there is no extension) from the search path.
    /// </summary>
    private void PlayDemo()
    {
        if (Cmd.Source != Cmd.SrcCommand)
            return;
        if (Cmd.Argc != 2)
        {
            Cmd.Printf("play <demoname> : plays a demo\n"u8);
            return;
        }
        // disconnect from server
        Cl!.Disconnect();
        string name = System.Text.Encoding.Latin1.GetString(Cmd.Argv(1));
        int slash = name.LastIndexOf('/');
        if (name.IndexOf('.', slash + 1) < 0)
            name += ".dem";     // COM_DefaultExtension
        Cmd.Msg("Playing demo from "u8).Msg(System.Text.Encoding.Latin1.GetBytes(name)).Msg(".\n"u8).End();
        if (_files.Find(name) is not { } file)
        {
            Cmd.Printf("ERROR: couldn't open.\n"u8);
            _console!.StopDemoLoop();
            return;
        }
        byte[] demo = file.Span.ToArray();
        _models.PreloadClient(demo);
        Cl.PlayDemo(demo);
    }

    /// <summary>Host_Restart_f: the same map spawned again.</summary>
    private void Restart()
    {
        if (Cl is { DemoPlayback: true } || Sv is not { Active: true })
            return;
        if (Cmd.Source != Cmd.SrcCommand)
            return;
        SpawnServer(Sv.MapName);
    }

    /// <summary>Host_Changelevel_f: the next map, the clients' parms kept.</summary>
    private void Changelevel()
    {
        if (Cmd.Argc != 2)
        {
            Cmd.Printf("changelevel <levelname> : continue game on a new level\n"u8);
            return;
        }
        if (Sv is not { Active: true } || Cl is { DemoPlayback: true })
        {
            Cmd.Printf("Only the server may changelevel\n"u8);
            return;
        }
        SaveSpawnparms(Sv);
        SpawnServer(Cmd.Argv(1));
    }

    /// <summary>SV_SaveSpawnparms: serverflags kept, and every client's parms from SetChangeParms.</summary>
    private void SaveSpawnparms(Server sv)
    {
        _serverflags = CCast.I32(*sv.G(GlobalVars.Serverflags));
        for (int i = 0; i < _clients.Length; i++)
        {
            Client c = _clients[i];
            if (!c.Active)
                continue;
            // call the progs to get default spawn parms for the new client
            sv.Globals[GlobalVars.Self] = c.Edict * sv.EdictSize;
            sv.Hooks.ExecuteProgram(sv.Globals[GlobalVars.SetChangeParms]);
            for (int j = 0; j < Client.NumSpawnParms; j++)
                c.SpawnParms[j] = *sv.G(GlobalVars.Parm1 + j);
        }
    }

    /// <summary>
    /// SV_SpawnServer: a new server with fresh memory (Host_ClearMemory; PR_LoadProgs' globals from the file), the
    /// spawn up to its frame-0 point, then the settling frames, baselines and serverinfo.
    /// </summary>
    private void SpawnServer(ReadOnlySpan<byte> map)
    {
        byte[] name = map.ToArray();
        // tell all connected clients that we are going to a new level
        if (Sv is { Active: true } old)
        {
            old.SendReconnect();
            _console?.Reconnect();  // a listen server executes "reconnect" itself: Host_Reconnect_f
        }
        CRand rand = Rand();
        FreeServer();
        Cl?.ClearMemory();          // Host_ClearMemory: cls.signon 0, cl wiped

        int numGlobals = _progs.NumGlobals;
        int stride = _headerBytes + _progs.EntityFields * 4;
        _globals = (int*)NativeMemory.AllocZeroed((nuint)numGlobals, 4);
        new ReadOnlySpan<int>(_progs.InitialGlobals, numGlobals).CopyTo(new Span<int>(_globals, numGlobals));
        _edicts = (byte*)NativeMemory.AllocZeroed(Server.MaxEdictsDefault, (nuint)stride);
        _stringTemp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
        int tempOfs = _progs.NumStrings;
        _hunk = new HunkStrings(tempOfs + Server.StringTempSize);
        var vm = new QcVm(_progs, _globals, _edicts, _headerBytes, _numBuiltins);
        var sv = new Server(vm, _progs, _globals, _edicts, _headerBytes, Cvars, _hunk, _stringTemp, tempOfs)
        {
            MaxEdicts = Server.MaxEdictsDefault,
            MaxClients = _maxClients,
        };
        sv.Clients = _clients;
        sv.Net = _net;
        sv.CommandText = Cmd.Text;
        sv.Con = Screen ?? Cmd.Con; // a listen server prints to its client's console, a dedicated one to stdout
        sv.SysOut = _dedicated ? Cmd.Con : null;
        sv.ServerFlags = _serverflags;
        sv.Rand = rand;
        sv.Hooks = new GameHooks(sv);
        int[] names = new int[_clients.Length];
        for (int i = 0; i < names.Length; i++)
            names[i] = _hunk.Alloc(Client.NameSize, out _);    // where netname points (the port's own offsets)
        sv.SetClientNameOffsets(names);
        Sv = sv;

        if (!sv.SpawnServer(name, _models))
            throw new InvalidOperationException("Couldn't spawn server");
        if (Cl is not null)
            _models.PrepareClientLookups();     // the local client's server info finds the server's models
        Spawned?.Invoke(sv);
        sv.SettleAndFinish();
        FrameTime = sv.FrameTime;   // 0.1: the rest of this host frame runs with it
    }

    private void FreeServer()
    {
        Sv = null;
        _hunk?.Dispose();
        _hunk = null;
        if (_globals != null) NativeMemory.Free(_globals);
        if (_edicts != null) NativeMemory.Free(_edicts);
        if (_stringTemp != null) NativeMemory.Free(_stringTemp);
        _globals = null;
        _edicts = null;
        _stringTemp = null;
    }

    public void Dispose()
    {
        FreeServer();
        _models.Dispose();
        _progs.Dispose();
    }
}

/// <summary>A network driver that needs host_framecount (the tape).</summary>
public interface IFrameCounted
{
    int FrameCount { get; set; }
}
