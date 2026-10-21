// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The host loop of a client that plays a demo, as the harness runs one (edict_ref without -dedicated): _Host_Frame
/// without a server — the frame's rand (), Host_FilterTime with host_framerate, Cbuf_Execute, CL_SendCmd,
/// CL_ReadFromServer, SCR_UpdateScreen, CL_DecayLights once the signon is complete, host_framecount — and the
/// commands such a run executes.
/// <para>
/// Two ways to start. As P5 ran it (-edictclient, no screen): the cvars as the reference had them after quake.rc, and
/// quake.rc is not run. In full: Host_Init's part that the screen shows (the cvars at their defaults, the console's
/// first lines) and "exec quake.rc" in the first frame, with the screen (-edictscreen) or without.
/// </para>
/// </summary>
[NoAllocUnchecked("the host loop: commands and playdemo allocate; the client's frame is checked in ClientState")]
public sealed unsafe class ClientHost : IDisposable, ISoundHost
{
    /// <summary>EDICT_FRAMETIME (edict_sys.c): host_framerate as the harness sets it after Host_Init.</summary>
    public const string HarnessFrameRate = "0.013889";

    private readonly QFileSystem _files;
    private readonly ModelCache _models;
    private readonly ClientConsole _console;
    private double _realtime, _oldrealtime;
    private bool _played;

    public Cvars Cvars { get; }
    public ClientState Cl { get; }
    public Cmd Cmd { get; }
    public Keys Keys => _console.Keys;

    /// <summary>The screen, or null where it is skipped (-edictclient).</summary>
    public Screen? Screen => _console.Screen;

    /// <summary>host_framecount.</summary>
    public int FrameCount { get; private set; }

    /// <summary>host_frametime.</summary>
    public double FrameTime { get; private set; }

    /// <summary>The console commands executed so far (the allocation gate counts their frames apart).</summary>
    public int CommandsExecuted => Cmd.Executed;

    /// <summary>Whether the demo has ended (the harness ends its run with it).</summary>
    public bool Ended => _played && !Cl.DemoPlayback;

    /// <param name="cvars">The cvars as the reference had them after frame 0.</param>
    /// <param name="randSeed">The rand state before frame 0.</param>
    public ClientHost(QFileSystem files, Cvars cvars, uint randSeed)
    {
        _files = files;
        Cvars = cvars;
        _models = new ModelCache(files);
        Cmd = new Cmd(cvars, files);
        Cl = new ClientState(cvars, _models, randSeed, Cmd.Text);
        _console = new ClientConsole(Cmd, cvars, Cl, () => false);
        Cmd.Add("playdemo", PlayDemo);
        Host.InitializeTypes();     // every engine type's statics now, not in a frame
    }

    /// <summary>
    /// Host_Init as far as a client shows it (<see cref="ClientConsole.Startup"/>): the cvars at their defaults, the
    /// command line (cmdline and stuffcmds), the console's first lines, the screen (when <paramref name="withScreen"/>),
    /// and quake.rc for the first frame.
    /// </summary>
    /// <param name="randSeed">The rand state before frame 0.</param>
    /// <param name="commandLine">The arguments after the program's name (stuffcmds runs the "+" ones).</param>
    /// <param name="cmdline">The cmdline cvar's value (com_cmdline: the program's name too, at most 255 characters).</param>
    /// <param name="sound">The sound build's sound (snd_dma.c, ref/build.sh SOUND=1) instead of snd_null's.</param>
    /// <param name="sink">The screen's sink from Host_Init on.</param>
    public ClientHost(QFileSystem files, uint randSeed, IReadOnlyList<string> commandLine, string cmdline, bool withScreen,
                      bool sound = false, IScreenSink? sink = null)
    {
        _files = files;
        bool soundCvars = Host.SoundCvars(sound, commandLine);
        Cvars = Cvars.FromRegistry([], soundCvars);
        _models = new ModelCache(files);
        Cmd = new Cmd(Cvars, files) { CommandLine = commandLine };
        if (soundCvars)
            Cmd.UseSoundTable();
        Cl = new ClientState(Cvars, _models, randSeed, Cmd.Text);
        _console = new ClientConsole(Cmd, Cvars, Cl, () => false);
        Cmd.Add("playdemo", PlayDemo);
        _console.Startup(files, _models, cmdline, withScreen, sound: sound ? this : null, sink: sink);
        Host.InitializeTypes();
    }

    /// <summary>The sound of a host with sound, or null.</summary>
    public Sound? Sound => _console.Sound;

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    ref CRand ISoundHost.Rand() => ref Cl.Rand;

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    double ISoundHost.RealTime => _realtime;

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    double ISoundHost.FrameTime => FrameTime;

    /// <summary>Cbuf_AddText.</summary>
    public void AddText(ReadOnlySpan<byte> text) => Cmd.AddText(text);

    /// <summary>_Host_Frame (time) of the client alone.</summary>
    public void Frame(float time)
    {
        Cl.Rand.Next();         // keep the random time dependent

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
        Cl.FrameTime = FrameTime;
        Cl.RealTime = _realtime;
        if (Screen is { } s)
        {
            s.FrameTime = FrameTime;
            s.RealTime = _realtime;
        }

        // process console commands
        Cmd.Execute();

        // client operations: no server runs, so the intentions are sent after the incoming messages were read
        Cl.FrameTime = FrameTime;
        Cl.RealTime = _realtime;
        Cl.SendCmd();
        if (Cl.State == ClientState.CaConnected)
            Cl.ReadFromServer();

        // update video
        Screen?.UpdateScreen();

        // update audio; CDAudio_Update is output
        Host.UpdateSound(_console, Cl);
        if (Cl.Signon == ClientState.Signons)
            Cl.DecayLights();

        if (Cl.DemoPlayback)
            _played = true;
        FrameCount++;
    }

    /// <summary>host_framerate's value.</summary>
    public float HostFramerate()
    {
        fixed (byte* n = "host_framerate\0"u8)
            return Cvars.Value(n);
    }

    /// <summary>CL_PlayDemo_f: the demo by name (".dem" added if there is no extension), from the search path.</summary>
    private void PlayDemo()
    {
        if (Cmd.Source != Cmd.SrcCommand)
            return;
        if (Cmd.Argc != 2)
        {
            Cmd.Printf("play <demoname> : plays a demo\n"u8);
            return;
        }
        // CL_Disconnect (its S_StopAllSounds is ClientState.PlayDemo's): nothing is connected or played yet in the runs the port has
        if (Cl.DemoPlayback || Cl.State == ClientState.CaConnected)
            throw new InvalidOperationException("playdemo while connected or playing (CL_Disconnect) is not ported");
        string name = Encoding.Latin1.GetString(Cmd.Argv(1));
        int slash = name.LastIndexOf('/');
        if (name.IndexOf('.', slash + 1) < 0)
            name += ".dem";     // COM_DefaultExtension
        Cmd.Msg("Playing demo from "u8).Msg(Encoding.Latin1.GetBytes(name)).Msg(".\n"u8).End();
        QFile? f = _files.Find(name);
        if (f is not { } file)
            throw new FileNotFoundException($"playdemo: {name} not found (C prints \"ERROR: couldn't open.\" and stops the demo loop)");
        byte[] demo = file.Span.ToArray();
        _models.PreloadClient(demo);
        Cl.PlayDemo(demo);
    }

    public void Dispose() => _models.Dispose();
}
