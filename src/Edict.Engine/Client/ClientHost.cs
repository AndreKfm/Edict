// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>
/// The host loop of a client that plays a demo, as the harness runs one (edict_ref -edictclient, no -dedicated):
/// _Host_Frame without a server and without the screen — the frame's rand (), Host_FilterTime with host_framerate,
/// Cbuf_Execute, CL_SendCmd, CL_ReadFromServer, CL_DecayLights once the signon is complete, host_framecount — and the
/// commands such a run executes: playdemo, and what the server stuffs in, bf and reconnect. quake.rc's configs are
/// not run: their effect is the cvars, which the port takes from the reference.
/// </summary>
[NoAllocUnchecked("the host loop: commands and playdemo allocate; the client's frame is checked in ClientState")]
public sealed unsafe class ClientHost : IDisposable
{
    private readonly QFileSystem _files;
    private readonly ModelCache _models;
    private readonly SizeBuf _cmdText = new(8192);     // cmd_text
    private readonly ComParse _parse = new();
    private double _realtime, _oldrealtime;
    private bool _played;

    public Cvars Cvars { get; }
    public ClientState Cl { get; }

    /// <summary>host_framecount.</summary>
    public int FrameCount { get; private set; }

    /// <summary>host_frametime.</summary>
    public double FrameTime { get; private set; }

    /// <summary>Whether the demo has ended (the harness ends its run with it).</summary>
    public bool Ended => _played && !Cl.DemoPlayback;

    /// <param name="cvars">The cvars as the reference had them after frame 0.</param>
    /// <param name="randSeed">The rand state before frame 0.</param>
    public ClientHost(QFileSystem files, Cvars cvars, uint randSeed)
    {
        _files = files;
        Cvars = cvars;
        _models = new ModelCache(files);
        Cl = new ClientState(cvars, _models, randSeed, _cmdText);
    }

    /// <summary>Cbuf_AddText.</summary>
    public void AddText(ReadOnlySpan<byte> text)
    {
        if (_cmdText.CurSize + text.Length >= _cmdText.MaxSize)
            return;     // Con_Printf ("Cbuf_AddText: overflow\n")
        fixed (byte* t = text)
            _cmdText.Write(t, text.Length);
    }

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

        // process console commands
        ExecuteCommands();

        // client operations: no server runs, so the intentions are sent after the incoming messages were read
        Cl.FrameTime = FrameTime;
        Cl.RealTime = _realtime;
        Cl.SendCmd();
        if (Cl.State == ClientState.CaConnected)
            Cl.ReadFromServer();
        // SCR_UpdateScreen: no screen; S_Update and CDAudio_Update are output
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

    /// <summary>Cbuf_Execute: every line (to a newline, or a ; outside quotes) of the command buffer.</summary>
    private void ExecuteCommands()
    {
        byte* line = stackalloc byte[1024];
        while (_cmdText.CurSize != 0)
        {
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

            Execute(line);
        }
    }

    /// <summary>Cmd_ExecuteString (line, src_command) for the commands a demo playback issues.</summary>
    private void Execute(byte* line)
    {
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
            return;

        string cmd = System.Text.Encoding.Latin1.GetString(argv[0]).ToLowerInvariant();
        switch (cmd)
        {
            case "playdemo":
                PlayDemo(argv);
                break;
            case "bf":
                Cl.BonusFlash();    // V_BonusFlash_f
                break;
            case "reconnect":
                Cl.Signon = 0;      // Host_Reconnect_f: SCR_BeginLoadingPlaque (the screen's), need new connection messages
                break;
            default:
                throw new InvalidOperationException($"the console command \"{cmd}\" is not ported for the client");
        }
    }

    /// <summary>CL_PlayDemo_f: the demo by name (".dem" added if there is no extension), from the search path.</summary>
    private void PlayDemo(List<byte[]> argv)
    {
        if (argv.Count != 2)
            return;     // Con_Printf ("play <demoname> : plays a demo\n")
        string name = System.Text.Encoding.Latin1.GetString(argv[1]);
        if (Path.GetExtension(name).Length == 0)
            name += ".dem";     // COM_DefaultExtension
        QFile? f = _files.Find(name);
        if (f is not { } file)
            throw new FileNotFoundException($"playdemo: {name} not found (C prints \"ERROR: couldn't open.\" and stops the demo loop)");
        byte[] demo = file.Span.ToArray();
        _models.PreloadClient(demo);
        Cl.PlayDemo(demo);
    }

    public void Dispose() => _models.Dispose();
}
