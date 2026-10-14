// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace Edict.Engine;

/// <summary>
/// What both hosts of a client share: Host_Init's part that a client shows (the cvars its init code sets, the console's
/// first lines, the screen) and the client's console commands (cl_input.c's buttons, V_BonusFlash_f, Host_Reconnect_f,
/// Host_Startdemos_f, the console side of Host_Name_f and Host_Color_f, +showscores).
/// </summary>
[NoAllocUnchecked("console commands and the load path")]
public sealed unsafe class ClientConsole
{
    private const int MaxDemos = 8;

    private readonly Cmd _cmd;
    private readonly Cvars _cvars;
    private readonly ClientState _cl;
    private readonly Func<bool> _serverActive;
    private int _demoNum;                             // cls.demonum
    private readonly byte[][] _demos = new byte[MaxDemos][];

    /// <summary>The screen, or null where it is skipped (-edictclient).</summary>
    public Screen? Screen { get; private set; }

    /// <summary>keys.c: the bindings, key_dest and the console's input lines.</summary>
    public Keys Keys { get; }

    /// <summary>The sound (the sound build's snd_dma.c), or null for snd_null's.</summary>
    public Sound? Sound { get; private set; }

    public ClientConsole(Cmd cmd, Cvars cvars, ClientState cl, Func<bool> serverActive)
    {
        _cmd = cmd;
        _cvars = cvars;
        _cl = cl;
        _serverActive = serverActive;
        Keys = new Keys(cmd);       // Key_Init
        cl.InitInput(cmd);
        cmd.Add("bf", cl.BonusFlash);        // V_BonusFlash_f
        cmd.Add("reconnect", Reconnect);
        cmd.Add("startdemos", StartDemos);
        cmd.Add("name", Name);
        cmd.Add("color", Color);
        cmd.Add("+showscores", () => Screen?.ShowScores(true));
        cmd.Add("-showscores", () => Screen?.ShowScores(false));
    }

    /// <summary>
    /// Host_Init's part that a client shows, with the cvars at their defaults (<see cref="Cvars.FromRegistry"/>):
    /// COM_CheckRegistered, Host_FindMaxClients, Con_Init and the console's first lines, VID_Init, Draw_Init, SCR_Init
    /// (when <paramref name="withScreen"/>), R_Init's cvars, S_Init (with <paramref name="sound"/>), Sbar_Init (with the
    /// screen), CL_Init's CL_InitTEnts, and "exec quake.rc" for the first frame.
    /// </summary>
    /// <param name="cmdline">The cmdline cvar's value (com_cmdline: the program's name too, at most 255 characters).</param>
    /// <param name="harness">host_framerate as the reference's harness sets it (edict_sys.c); a game keeps it 0.</param>
    /// <param name="sound">The host for a sound of the sound build's; null: snd_null's.</param>
    /// <param name="sink">Where the screen's frames go from Con_Init on (S_Init's console lines present the screen).</param>
    public void Startup(QFileSystem files, ModelCache models, string cmdline, bool withScreen, bool harness = true,
                        ISoundHost? sound = null, IScreenSink? sink = null)
    {
        // COM_CheckRegistered (in COM_Init, before Con_Init: its print reaches only stdout)
        if (files.Registered)
        {
            SetCvar("cmdline", cmdline);
            SetCvar("registered", "1");
        }
        // Host_InitLocal's Host_FindMaxClients: one client, no deathmatch
        fixed (byte* n = "deathmatch\0"u8)
            _cvars.SetValue(n, 0);
        if (withScreen)
        {
            Screen s = new(files, _cvars, _cl, Keys) { Sink = sink };     // Con_Init
            Screen = s;
            _cmd.Con = s;
            _cl.Con = s;
            s.Printf("Exe: 00:00:00 Dec 21 1999\n"u8);    // __TIME__ __DATE__, pinned (ref/build.sh)
            s.Printf("32.0 megabyte heap\n"u8);           // "%4.1f megabyte heap": -mem, 32 by default
            s.InitVideo();
            s.InitScreen();
        }
        // R_Init: the surface and edge counts of the stack-based renderer
        fixed (byte* n = "r_maxedges\0"u8)
            _cvars.SetValue(n, 2400);    // NUMSTACKEDGES
        fixed (byte* n = "r_maxsurfs\0"u8)
            _cvars.SetValue(n, 800);     // NUMSTACKSURFACES
        if (sound is not null)
        {
            Sound snd = new(files, _cmd, _cvars, _cl, sound);
            Sound = snd;
            _cl.Sound = snd;
            if (Screen is { } sc)
                sc.Sound = snd;
            snd.Init(_cmd.CommandLine);     // S_Init: its console lines are the screen's first presents
        }
        if (Screen is { } screen)
        {
            screen.InitSbar();
            screen.AddConsoleCommands(_cmd);
        }
        _cl.InitTEnts();                    // CL_Init
        _cmd.InsertText("exec quake.rc\n"u8);
        if (harness)
            SetCvar("host_framerate", ClientHost.HarnessFrameRate);     // edict_sys.c, after Host_Init
    }

    private void SetCvar(string name, string value)
    {
        fixed (byte* n = Encoding.Latin1.GetBytes(name + "\0"))
        fixed (byte* v = Encoding.Latin1.GetBytes(value + "\0"))
            _cvars.Set(n, v);
    }

    /// <summary>Host_Reconnect_f: the loading plaque, and new connection messages.</summary>
    public void Reconnect()
    {
        BeginLoadingPlaque();
        _cl.Signon = 0;
    }

    /// <summary>SCR_BeginLoadingPlaque: the sounds stopped, and the plaque where there is a screen.</summary>
    public void BeginLoadingPlaque()
    {
        if (Screen is { } s)
            s.BeginLoadingPlaque();
        else
            Sound?.StopAllSounds(true);
    }

    /// <summary>Host_Startdemos_f: the demo loop's names; it starts only when nothing plays and no server runs.</summary>
    private void StartDemos()
    {
        int c = _cmd.Argc - 1;
        if (c > MaxDemos)
        {
            _cmd.Msg("Max "u8).Int(MaxDemos).Msg(" demos in demoloop\n"u8).End();
            c = MaxDemos;
        }
        _cmd.Msg(""u8).Int(c).Msg(" demo(s) in loop\n"u8).End();
        for (int i = 1; i < c + 1; i++)
            _demos[i - 1] = _cmd.Argv(i);
        if (!_serverActive() && _demoNum != -1 && !_cl.DemoPlayback)
            throw new InvalidOperationException("startdemos: CL_NextDemo (the demo loop) is not ported");
        _demoNum = -1;
    }

    /// <summary>cls.demonum = -1: playdemo and map stop the demo loop.</summary>
    public void StopDemoLoop() => _demoNum = -1;

    /// <summary>Host_Name_f from the console: the name for the next connection (_cl_name), at most 15 characters.</summary>
    private void Name()
    {
        if (_cmd.Argc == 1)
        {
            _cmd.Msg("\"name\" is \""u8).Msg(CvarString("_cl_name")).Msg("\"\n"u8).End();
            return;
        }
        byte[] newName = _cmd.Argc == 2 ? _cmd.Argv(1) : _cmd.Args;
        if (newName.Length > 15)
            newName = newName[..15];        // newName[15] = 0
        if (_cmd.Source != Cmd.SrcCommand)
            throw new InvalidOperationException("name from a client: the server's");
        if (CvarString("_cl_name").SequenceEqual(newName))
            return;
        SetCvar("_cl_name", Encoding.Latin1.GetString(newName));
        if (_cl.State == ClientState.CaConnected)
            throw new InvalidOperationException("name while connected: Cmd_ForwardToServer is not ported");
    }

    /// <summary>Host_Color_f from the console: the colors for the next connection (_cl_color).</summary>
    private void Color()
    {
        if (_cmd.Argc == 1)
            throw new InvalidOperationException("color without arguments prints the current colors: not ported");
        int top, bottom;
        if (_cmd.Argc == 2)
            top = bottom = AtoiOf(_cmd.Argv(1));
        else
        {
            top = AtoiOf(_cmd.Argv(1));
            bottom = AtoiOf(_cmd.Argv(2));
        }
        top &= 15;
        if (top > 13)
            top = 13;
        bottom &= 15;
        if (bottom > 13)
            bottom = 13;
        int playercolor = top * 16 + bottom;
        if (_cmd.Source != Cmd.SrcCommand)
            throw new InvalidOperationException("color from a client: the server's");
        fixed (byte* n = "_cl_color\0"u8)
            _cvars.SetValue(n, playercolor);
        if (_cl.State == ClientState.CaConnected)
            throw new InvalidOperationException("color while connected: Cmd_ForwardToServer is not ported");
    }

    private static int AtoiOf(byte[] s) =>
        Edict.Exact.CAtof.TryAtoi(s, out int v) ? v : throw new InvalidOperationException("atoi outside int");

    private ReadOnlySpan<byte> CvarString(string name)
    {
        fixed (byte* n = Encoding.Latin1.GetBytes(name + "\0"))
        {
            byte* s = _cvars.VariableString(n);
            int len = 0;
            while (s[len] != 0)
                len++;
            return new ReadOnlySpan<byte>(s, len);
        }
    }
}
