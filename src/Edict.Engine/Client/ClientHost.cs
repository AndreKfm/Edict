// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

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
public sealed unsafe class ClientHost : IDisposable
{
    /// <summary>EDICT_FRAMETIME (edict_sys.c): host_framerate as the harness sets it after Host_Init.</summary>
    public const string HarnessFrameRate = "0.013889";

    private readonly QFileSystem _files;
    private readonly ModelCache _models;
    private double _realtime, _oldrealtime;
    private bool _played;
    private int _demoNum;                             // cls.demonum
    private readonly byte[][] _demos = new byte[MaxDemos][];

    private const int MaxDemos = 8;

    public Cvars Cvars { get; }
    public ClientState Cl { get; }
    public Cmd Cmd { get; }
    public Keys Keys { get; }

    /// <summary>The screen, or null where it is skipped (-edictclient).</summary>
    public Screen? Screen { get; }

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
        Keys = new Keys(Cmd);
        Cl = new ClientState(cvars, _models, randSeed, Cmd.Text);
        RegisterCommands();
        Host.InitializeTypes();     // every engine type's statics now, not in a frame
    }

    /// <summary>
    /// Host_Init as far as a client shows it: the cvars at their defaults, the command line (cmdline and stuffcmds),
    /// the console's first lines, the screen (when <paramref name="withScreen"/>), and quake.rc for the first frame.
    /// </summary>
    /// <param name="randSeed">The rand state before frame 0.</param>
    /// <param name="commandLine">The arguments after the program's name (stuffcmds runs the "+" ones).</param>
    /// <param name="cmdline">The cmdline cvar's value (com_cmdline: the program's name too, at most 255 characters).</param>
    public ClientHost(QFileSystem files, uint randSeed, IReadOnlyList<string> commandLine, string cmdline, bool withScreen)
    {
        _files = files;
        Cvars = Cvars.FromRegistry();
        _models = new ModelCache(files);
        Cmd = new Cmd(Cvars, files) { CommandLine = commandLine };
        Keys = new Keys(Cmd);
        Cl = new ClientState(Cvars, _models, randSeed, Cmd.Text);
        RegisterCommands();
        // COM_CheckRegistered (in COM_Init, before Con_Init: its print reaches only stdout)
        SetCvar("cmdline", cmdline);
        if (files.Registered)
            SetCvar("registered", "1");
        if (withScreen)
        {
            Screen = new Screen(files, Cvars, Cl, Keys);      // Con_Init
            Cmd.Con = Screen;
            Cl.Con = Screen;
            Screen.Printf("Exe: 00:00:00 Dec 21 1999\n"u8);    // __TIME__ __DATE__, pinned (ref/build.sh)
            Screen.Printf("32.0 megabyte heap\n"u8);           // "%4.1f megabyte heap": -mem, 32 by default
            Screen.InitVideo();
            Screen.InitScreen();
        }
        Cmd.InsertText("exec quake.rc\n"u8);
        SetCvar("host_framerate", HarnessFrameRate);           // edict_sys.c, after Host_Init
        Host.InitializeTypes();
    }

    private void SetCvar(string name, string value)
    {
        fixed (byte* n = Encoding.Latin1.GetBytes(name + "\0"))
        fixed (byte* v = Encoding.Latin1.GetBytes(value + "\0"))
            Cvars.Set(n, v);
    }

    [NoAllocUnchecked("load path")]
    private void RegisterCommands()
    {
        Cl.InitInput(Cmd);
        Cmd.Add("playdemo", PlayDemo);
        Cmd.Add("bf", Cl.BonusFlash);        // V_BonusFlash_f
        Cmd.Add("reconnect", () =>
        {
            Screen?.BeginLoadingPlaque();   // Host_Reconnect_f
            Cl.Signon = 0;                  // need new connection messages
        });
        Cmd.Add("startdemos", StartDemos);
        Cmd.Add("name", Name);
        Cmd.Add("color", Color);
    }

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

        // S_Update and CDAudio_Update are output
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
        // CL_Disconnect: nothing is connected or played yet in the runs the port has
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

    /// <summary>Host_Startdemos_f: the demo loop's names; it starts only when nothing plays and no server runs.</summary>
    private void StartDemos()
    {
        int c = Cmd.Argc - 1;
        if (c > MaxDemos)
        {
            Cmd.Msg("Max "u8).Int(MaxDemos).Msg(" demos in demoloop\n"u8).End();
            c = MaxDemos;
        }
        Cmd.Msg(""u8).Int(c).Msg(" demo(s) in loop\n"u8).End();
        for (int i = 1; i < c + 1; i++)
            _demos[i - 1] = Cmd.Argv(i);
        if (_demoNum != -1 && !Cl.DemoPlayback)
            throw new InvalidOperationException("startdemos: CL_NextDemo (the demo loop) is not ported");
        _demoNum = -1;
    }

    /// <summary>Host_Name_f from the console: the name for the next connection (_cl_name), at most 15 characters.</summary>
    private void Name()
    {
        if (Cmd.Argc == 1)
        {
            Cmd.Msg("\"name\" is \""u8).Msg(CvarString("_cl_name")).Msg("\"\n"u8).End();
            return;
        }
        byte[] newName = Cmd.Argc == 2 ? Cmd.Argv(1) : Cmd.Args;
        if (newName.Length > 15)
            newName = newName[..15];        // newName[15] = 0
        if (Cmd.Source != Cmd.SrcCommand)
            throw new InvalidOperationException("name from a client: the server's");
        if (CvarString("_cl_name").SequenceEqual(newName))
            return;
        SetCvar("_cl_name", Encoding.Latin1.GetString(newName));
        if (Cl.State == ClientState.CaConnected)
            throw new InvalidOperationException("name while connected: Cmd_ForwardToServer is not ported");
    }

    /// <summary>Host_Color_f from the console: the colors for the next connection (_cl_color).</summary>
    private void Color()
    {
        if (Cmd.Argc == 1)
            throw new InvalidOperationException("color without arguments prints the current colors: not ported");
        int top, bottom;
        if (Cmd.Argc == 2)
            top = bottom = AtoiOf(Cmd.Argv(1));
        else
        {
            top = AtoiOf(Cmd.Argv(1));
            bottom = AtoiOf(Cmd.Argv(2));
        }
        top &= 15;
        if (top > 13)
            top = 13;
        bottom &= 15;
        if (bottom > 13)
            bottom = 13;
        int playercolor = top * 16 + bottom;
        if (Cmd.Source != Cmd.SrcCommand)
            throw new InvalidOperationException("color from a client: the server's");
        fixed (byte* n = "_cl_color\0"u8)
            Cvars.SetValue(n, playercolor);
        if (Cl.State == ClientState.CaConnected)
            throw new InvalidOperationException("color while connected: Cmd_ForwardToServer is not ported");
    }

    private static int AtoiOf(byte[] s) =>
        Edict.Exact.CAtof.TryAtoi(s, out int v) ? v : throw new InvalidOperationException("atoi outside int");

    private ReadOnlySpan<byte> CvarString(string name)
    {
        fixed (byte* n = Encoding.Latin1.GetBytes(name + "\0"))
        {
            byte* s = Cvars.VariableString(n);
            int len = 0;
            while (s[len] != 0)
                len++;
            return new ReadOnlySpan<byte>(s, len);
        }
    }

    public void Dispose() => _models.Dispose();
}
