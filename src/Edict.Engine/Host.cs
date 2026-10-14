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
public sealed unsafe class Host : IDisposable
{
    private readonly QFileSystem _files;
    private readonly Progs _progs;
    private readonly ModelCache _models;
    private readonly int _headerBytes;
    private readonly int _numBuiltins;
    private readonly Client[] _clients;
    private readonly INetServer _net;
    private readonly SizeBuf _cmdText = new(8192);     // cmd_text
    private readonly ComParse _parse = new();
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
    {
        _files = files;
        Cvars = cvars;
        QFile f = files.Find("progs.dat"u8) ?? throw new FileNotFoundException("progs.dat");
        _progs = new Progs(f);
        _models = new ModelCache(files);
        _headerBytes = headerBytes;
        _numBuiltins = numBuiltins;
        _clients = new Client[maxclients];
        for (int i = 0; i < maxclients; i++)
            _clients[i] = new Client();
        _net = net;
        _rand.Srand(randSeed);
        _firstFrame = net is not Loopback;
        if (net is Loopback)
            Cl = new ClientState(cvars, _models, randSeed, _cmdText);
        InitializeTypes();
    }

    /// <summary>A listen server's local client, null for a server alone.</summary>
    public ClientState? Cl { get; }

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
    private bool _cmdWait;                             // cmd_wait

    /// <summary>Cbuf_AddText.</summary>
    public void AddText(ReadOnlySpan<byte> text)
    {
        if (_cmdText.CurSize + text.Length >= _cmdText.MaxSize)
            return;     // Con_Printf ("Cbuf_AddText: overflow\n")
        fixed (byte* t = text)
            _cmdText.Write(t, text.Length);
    }

    /// <summary>Cbuf_InsertText: the text ahead of what the buffer holds (a script's lines run before the rest).</summary>
    private void InsertText(ReadOnlySpan<byte> text)
    {
        byte[] rest = new ReadOnlySpan<byte>(_cmdText.Data, _cmdText.CurSize).ToArray();
        _cmdText.Clear();
        AddText(text);
        if (_cmdText.CurSize + rest.Length > _cmdText.MaxSize)
            throw new InvalidOperationException("SZ_GetSpace: overflow without allowoverflow set (cmd_text)");
        fixed (byte* r = rest)
            _cmdText.Write(r, rest.Length);
    }

    /// <summary>
    /// _Host_Frame (time): rand () (except in the first frame, whose draw the given seed includes), the frame time,
    /// the command buffer, the server frame, and host_framecount.
    /// </summary>
    public void Frame(float time)
    {
        if (!_firstFrame)
            Rand().Next();          // keep the random time dependent
        _firstFrame = false;

        // decide the simulation time (Host_FilterTime)
        _realtime += time;
        if (_realtime - _oldrealtime < 1.0 / 72.0)
            throw new InvalidOperationException("Host_FilterTime: the frame would not run (the reference stops there too)");
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

        // process console commands
        ExecuteCommands();

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
                cl.ReadFromServer();
                rand = cl.Rand;
            }
            // SCR_UpdateScreen: no screen; S_Update and CDAudio_Update are output
            if (cl.Signon == ClientState.Signons)
                cl.DecayLights();
        }

        FrameCount++;
    }

    /// <summary>host_framerate's value.</summary>
    public float HostFramerate()
    {
        fixed (byte* n = "host_framerate\0"u8)
            return Cvars.Value(n);
    }

    /// <summary>The rand stream: the server's while there is one (it draws from it), the host's before.</summary>
    private ref CRand Rand()
    {
        if (Sv is not null)
            return ref Sv.Rand;
        return ref _rand;
    }

    /// <summary>Cbuf_Execute: every line (to a newline, or a ; outside quotes) of the command buffer.</summary>
    private void ExecuteCommands()
    {
        byte* line = stackalloc byte[1024];
        while (_cmdText.CurSize != 0)
        {
            // find a \n or ; line break
            byte* text = _cmdText.Data;
            int quotes = 0, i;
            for (i = 0; i < _cmdText.CurSize; i++)
            {
                if (text[i] == '"')
                    quotes++;
                if ((quotes & 1) == 0 && text[i] == ';')
                    break;  // don't break if inside a quoted string
                if (text[i] == '\n')
                    break;
            }
            if (i >= 1024)
                throw new InvalidOperationException("Cbuf_Execute: a line longer than C's line[1024]");
            for (int k = 0; k < i; k++)
                line[k] = text[k];
            line[i] = 0;

            // delete the text from the command buffer and move remaining commands down
            if (i == _cmdText.CurSize)
                _cmdText.CurSize = 0;
            else
            {
                i++;
                _cmdText.CurSize -= i;
                for (int k = 0; k < _cmdText.CurSize; k++)
                    text[k] = text[k + i];
            }

            // execute the command line
            Execute(line);

            if (_cmdWait)
            {
                // skip out while text still remains in buffer, leaving it for next frame
                _cmdWait = false;
                break;
            }
        }
    }

    /// <summary>Cmd_ExecuteString (line, src_command) for the commands a game issues.</summary>
    private void Execute(byte* line)
    {
        // Cmd_TokenizeString: up to the first newline; the words as COM_Parse reads them
        var argv = new List<byte[]>();
        byte* text = line;
        while (true)
        {
            while (*text != 0 && (sbyte)*text <= ' ' && *text != '\n')
                text++;
            if (*text == '\n' || *text == 0)
                break;
            text = _parse.Parse(text);
            if (text == null)
                break;
            argv.Add(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(_parse.Token).ToArray());
        }
        if (argv.Count == 0)
            return;     // no tokens

        string cmd = System.Text.Encoding.Latin1.GetString(argv[0]).ToLowerInvariant();
        switch (cmd)
        {
            case "map":
                // Host_Map_f (a server without a local client: no connect afterwards)
                if (Sv is { Active: true })
                    throw new InvalidOperationException("map while a server runs (Host_ShutdownServer) is not ported");
                _serverflags = 0;   // haven't completed an episode yet
                SpawnServer(argv.Count > 1 ? argv[1] : []);
                if (Cl is not null && Sv is { Active: true })
                    Cl.EstablishConnection(((Loopback)_net).Connect());     // connect local (cls.spawnparms "")
                break;
            case "restart":
                // Host_Restart_f
                if (Sv is not { Active: true })
                    return;
                SpawnServer(Sv.MapName);
                break;
            case "wait":
                // Cmd_Wait_f: the rest of the buffer waits for the next frame
                _cmdWait = true;
                break;
            case "exec":
                // Cmd_Exec_f: the script's text (to its first NUL, as COM_LoadHunkFile's terminator ends it) inserted
                if (argv.Count != 2)
                    return;     // Con_Printf ("exec <filename> : execute a script file\n")
                if (_files.Find(argv[1]) is not { } script)
                    return;     // Con_Printf ("couldn't exec %s\n")
                ReadOnlySpan<byte> f = script.Span;
                int nul = f.IndexOf((byte)0);
                InsertText(nul < 0 ? f : f[..nul]);
                break;
            case "reconnect":
                // Host_Reconnect_f: SCR_BeginLoadingPlaque (the screen and sound: none), and new connection messages
                if (Cl is not null)
                    Cl.Signon = 0;
                break;
            case "changelevel":
                // Host_Changelevel_f
                if (argv.Count != 2)
                    return;     // Con_Printf ("changelevel <levelname> : continue game on a new level\n")
                if (Sv is not { Active: true })
                    return;     // Con_Printf ("Only the server may changelevel\n")
                SaveSpawnparms(Sv);
                SpawnServer(argv[1]);
                break;
            default:
                throw new InvalidOperationException($"the console command \"{cmd}\" is not ported");
        }
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
            if (Cl is not null)
                Cl.Signon = 0;      // a listen server executes "reconnect" itself: Host_Reconnect_f
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
            MaxClients = _clients.Length,
        };
        sv.Clients = _clients;
        sv.Net = _net;
        sv.CommandText = _cmdText;
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
