// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// host_cmd.c's commands as a client sends them (Cmd_ExecuteString with src_client; P6 step 9): status, god,
/// notarget, fly, noclip, ping, say, say_team, tell, kill, pause, kick, give; and SV_DropClient (host.c) with
/// SV_ClientPrintf. A name SV_ReadClientMessage lets through that is no command (ban without the datagram driver)
/// only prints "Unknown command" on the server's console.
/// </summary>
public sealed unsafe partial class Server
{
    private const int SvcDisconnect = 2, SvcSetPause = 24, FlGodMode = 64, ItShotgun = 1;
    private const int PrintSize = 1024;                  // SV_ClientPrintf's string[1024]

    /// <summary>noclip_anglehack's other reader: a listen server's local client (cl_input.c), or null.</summary>
    public ClientState? LocalClient { get; set; }

    /// <summary>A text being formatted as vsprintf writes it (the conversions the commands use).</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    private struct Text(byte* p)
    {
        public readonly byte* P = p;
        public int N;

        public void Add(ReadOnlySpan<byte> s)
        {
            for (int i = 0; i < s.Length; i++)
                P[N++] = s[i];
        }

        public void Add(byte* s)
        {
            for (int i = 0; s[i] != 0; i++)
                P[N++] = s[i];
        }

        /// <summary>%-w.ps: at most <paramref name="precision"/> characters, padded right to <paramref name="width"/>.</summary>
        public void Left(byte* s, int width, int precision)
        {
            int n = 0;
            for (; n < precision && s[n] != 0; n++)
                P[N++] = s[n];
            for (; n < width; n++)
                P[N++] = (byte)' ';
        }

        /// <summary>%i with a width, zero-padded (%02i) or left-aligned (%-2u).</summary>
        public void Int(int v, int width = 0, bool zero = false, bool left = false)
        {
            Span<byte> d = stackalloc byte[12];
            int n = CFormat.Int(v, d);
            int pad = width - n;
            if (!left)
                for (; pad > 0; pad--)
                    P[N++] = zero ? (byte)'0' : (byte)' ';
            for (int i = 0; i < n; i++)
                P[N++] = d[i];
            for (; pad > 0; pad--)
                P[N++] = (byte)' ';
        }

        public void End() => P[N] = 0;
    }

    /// <summary>SV_ClientPrintf: svc_print and the text to host_client.</summary>
    private void ClientPrint(byte* s)
    {
        SizeBuf m = Clients[_hostClient].Message;
        m.WriteByte(SvcPrint);
        m.WriteString(s);
    }

    /// <summary>Host_Status_f's print: Con_Printf from the console, else SV_ClientPrintf.</summary>
    private void Print(byte* s, bool console)
    {
        if (console)
            Con?.Printf(new ReadOnlySpan<byte>(s, Strlen(s)));
        else
            ClientPrint(s);
    }

    private void Print(ReadOnlySpan<byte> s, bool console)
    {
        if (console)
            Con?.Printf(s);
        else
            ClientPrint(s);
    }

    private void ClientPrint(ReadOnlySpan<byte> s)
    {
        byte* b = stackalloc byte[PrintSize];
        var t = new Text(b);
        t.Add(s);
        t.End();
        ClientPrint(b);
    }

    private float* Player => Fields(_svPlayer);

    /// <summary>pr_global_struct->deathmatch &amp;&amp; !host_client->privileged: deathmatch refuses a client's cheats.</summary>
    private bool CheatsRefused => *G(GlobalVars.Deathmatch) != 0 && !Clients[_hostClient].Privileged;

    /// <summary>
    /// SV_DropClient: unless <paramref name="crash"/>, svc_disconnect (if the connection can send) and the progs'
    /// ClientDisconnect for a spawned client; then the connection closed, the slot freed (the body stays around) and
    /// every active client told the slot is empty.
    /// </summary>
    public void DropClient(bool crash)
    {
        Client host = Clients[_hostClient];
        if (!crash)
        {
            // send any final messages (don't check for errors)
            if (host.NetConnection!.CanSendMessage())
            {
                host.Message.WriteByte(SvcDisconnect);
                host.NetConnection.SendMessage(host.Message);
            }
            if (host.Edict != 0 && host.Spawned)
            {
                // call the prog function for removing a client: this will set the body to a dead frame, among
                // other things
                int saveSelf = Globals[GlobalVars.Self];
                Globals[GlobalVars.Self] = host.Edict * _edictSize;
                Hooks.ExecuteProgram(Globals[GlobalVars.ClientDisconnect]);
                Globals[GlobalVars.Self] = saveSelf;
            }
            // Sys_Printf ("Client %s removed\n", host_client->name)
        }

        // break the net connection
        host.NetConnection!.Close();
        host.NetConnection = null;

        // free the client (the body stays around)
        host.Active = false;
        host.Name[0] = 0;
        host.OldFrags = -999999;
        // net_activeconnections-- (ActiveConnections counts the active clients)

        // send notification to all clients
        for (int i = 0; i < MaxClients; i++)
        {
            Client c = Clients[i];
            if (!c.Active)
                continue;
            c.Message.WriteByte(SvcUpdateName);
            c.Message.WriteByte(_hostClient);
            c.Message.WriteByte(0);      // ""
            c.Message.WriteByte(SvcUpdateFrags);
            c.Message.WriteByte(_hostClient);
            c.Message.WriteShort(0);
            c.Message.WriteByte(SvcUpdateColors);
            c.Message.WriteByte(_hostClient);
            c.Message.WriteByte(0);
        }
    }

    /// <summary>net_activeconnections: SV_CheckForNewClients counts a connection up, SV_DropClient down.</summary>
    public int ActiveConnections
    {
        get
        {
            int n = 0;
            for (int i = 0; i < MaxClients; i++)
                if (Clients[i].Active)
                    n++;
            return n;
        }
    }

    /// <summary>The value of the cvar whose zero-terminated name is given.</summary>
    private float CvarValueZ(ReadOnlySpan<byte> name)
    {
        fixed (byte* n = name)
            return Cvars.Value(n);
    }

    /// <summary>Host_Status_f from a listen or dedicated server's console (the server active): print = Con_Printf.</summary>
    public void ConsoleStatus() => HostStatus(true);

    /// <summary>
    /// Host_Status_f: the host, the map, and each active client's slot, name, frags and time connected; to the
    /// client, or with <paramref name="console"/> to the console.
    /// </summary>
    private void HostStatus(bool console = false)
    {
        byte* b = stackalloc byte[PrintSize];
        var t = new Text(b);
        t.Add("host:    "u8);
        fixed (byte* hostname = "hostname\0"u8)
            t.Add(Cvars.VariableString(hostname));
        t.Add("\n"u8);
        t.End();
        Print(b, console);
        Print("version: 1.09\n"u8, console);       // "%4.2f", VERSION; no tcp/ip or ipx driver
        t = new Text(b);
        t.Add("map:     "u8);
        t.Add(MapName);
        t.Add("\n"u8);
        t.End();
        Print(b, console);
        t = new Text(b);
        t.Add("players: "u8);
        t.Int(ActiveConnections);
        t.Add(" active ("u8);
        t.Int(MaxClients);
        t.Add(" max)\n\n"u8);
        t.End();
        Print(b, console);
        for (int j = 0; j < MaxClients; j++)
        {
            Client client = Clients[j];
            if (!client.Active)
                continue;
            int seconds = CCast.I32(RealTime - client.ConnectTime);      // net_time - connecttime
            int minutes = seconds / 60, hours = 0;
            if (minutes != 0)
            {
                seconds -= minutes * 60;
                hours = minutes / 60;
                if (hours != 0)
                    minutes -= hours * 60;
            }
            t = new Text(b);
            t.Add("#"u8);
            t.Int(j + 1, 2, left: true);
            t.Add(" "u8);
            t.Left(client.NamePtr, 16, 16);
            t.Add("  "u8);
            t.Int(CCast.I32(Fields(client.Edict)[EntVars.Frags]), 3);
            t.Add("  "u8);
            t.Int(hours, 2);
            t.Add(":"u8);
            t.Int(minutes, 2, zero: true);
            t.Add(":"u8);
            t.Int(seconds, 2, zero: true);
            t.Add("\n"u8);
            t.End();
            Print(b, console);
            t = new Text(b);
            t.Add("   "u8);
            t.Add(client.NetConnection!.Address);
            t.Add("\n"u8);
            t.End();
            Print(b, console);
        }
    }

    /// <summary>Host_God_f, Host_Notarget_f: a flag toggled and told.</summary>
    private void HostToggleFlag(int flag, ReadOnlySpan<byte> on, ReadOnlySpan<byte> off)
    {
        if (CheatsRefused)
            return;
        float* p = Player;
        p[EntVars.Flags] = CCast.I32(p[EntVars.Flags]) ^ flag;
        ClientPrint((CCast.I32(p[EntVars.Flags]) & flag) == 0 ? off : on);
    }

    /// <summary>Host_Fly_f, Host_Noclip_f: the movetype toggled with walking, and told.</summary>
    private void HostToggleMovetype(int movetype, ReadOnlySpan<byte> on, ReadOnlySpan<byte> off)
    {
        if (CheatsRefused)
            return;
        float* p = Player;
        bool set = p[EntVars.Movetype] != movetype;
        if (movetype == MovetypeNoclip && LocalClient is { } cl)
            cl.NoclipAngleHack = set;
        p[EntVars.Movetype] = set ? movetype : MovetypeWalk;
        ClientPrint(set ? on : off);
    }

    /// <summary>Host_Ping_f: every active client's average ping in milliseconds.</summary>
    private void HostPing()
    {
        ClientPrint("Client ping times:\n"u8);
        byte* b = stackalloc byte[PrintSize];
        for (int i = 0; i < MaxClients; i++)
        {
            Client client = Clients[i];
            if (!client.Active)
                continue;
            float total = 0;
            for (int j = 0; j < Client.NumPingTimes; j++)
                total += client.PingTimes[j];
            total /= Client.NumPingTimes;
            var t = new Text(b);
            t.Int(CCast.I32(total * 1000), 4);
            t.Add(" "u8);
            t.Add(client.NamePtr);
            t.Add("\n"u8);
            t.End();
            ClientPrint(b);
        }
    }

    private static int Strlen(byte* s)
    {
        int n = 0;
        while (s[n] != 0)
            n++;
        return n;
    }

    /// <summary>Cmd_Args with its quotes removed, as Host_Say and Host_Tell take it (in place).</summary>
    private byte* UnquotedArgs()
    {
        byte* p = _args;
        if (*p == '"')
        {
            p++;
            p[Strlen(p) - 1] = 0;
        }
        return p;
    }

    /// <summary>
    /// Host_Say from a client: color set 1, the name and the text (cut to fit text[64]) to every spawned client, with
    /// <paramref name="teamonly"/> under teamplay to those of the same team.
    /// </summary>
    private void HostSay(bool teamonly)
    {
        if (_argc < 2)
            return;
        Client save = Clients[_hostClient];
        int saveIndex = _hostClient;
        byte* p = UnquotedArgs();
        byte* text = stackalloc byte[64];
        var t = new Text(text);
        t.Add([1]);             // turn on color set 1
        t.Add(save.NamePtr);
        t.Add(": "u8);
        int j = 64 - 2 - t.N;   // -2 for /n and null terminator
        if (Strlen(p) > j)
            p[j] = 0;
        t.Add(p);
        t.Add("\n"u8);
        t.End();
        bool teamplay = CvarValueZ("teamplay\0"u8) != 0;
        for (int i = 0; i < MaxClients; i++)
        {
            Client client = Clients[i];
            if (!client.Active || !client.Spawned)
                continue;
            if (teamplay && teamonly && Fields(client.Edict)[EntVars.Team] != Fields(save.Edict)[EntVars.Team])
                continue;
            _hostClient = i;
            ClientPrint(text);
        }
        _hostClient = saveIndex;
        // Sys_Printf ("%s", &text[1])
    }

    /// <summary>Host_Tell_f: the name and the text to the first spawned client of that name.</summary>
    private void HostTell()
    {
        if (_argc < 3)
            return;
        byte* text = stackalloc byte[64];
        var t = new Text(text);
        t.Add(Clients[_hostClient].NamePtr);
        t.Add(": "u8);
        byte* p = UnquotedArgs();
        // check length & truncate if necessary
        int j = 64 - 2 - t.N;   // -2 for /n and null terminator
        if (Strlen(p) > j)
            p[j] = 0;
        t.Add(p);
        t.Add("\n"u8);
        t.End();
        int save = _hostClient;
        for (int i = 0; i < MaxClients; i++)
        {
            Client client = Clients[i];
            if (!client.Active || !client.Spawned)
                continue;
            if (!StrCaseEquals(client.NamePtr, Argv(1)))
                continue;
            _hostClient = i;
            ClientPrint(text);
            break;
        }
        _hostClient = save;
    }

    /// <summary>Q_strcasecmp (a, b) == 0.</summary>
    private static bool StrCaseEquals(byte* a, byte* b)
    {
        for (int i = 0; ; i++)
        {
            int c1 = a[i], c2 = b[i];
            if (c1 != c2)
            {
                if (c1 >= 'a' && c1 <= 'z') c1 -= 'a' - 'A';
                if (c2 >= 'a' && c2 <= 'z') c2 -= 'a' - 'A';
                if (c1 != c2)
                    return false;
            }
            if (c1 == 0)
                return true;
        }
    }

    /// <summary>Host_Kill_f: the progs' ClientKill, unless the player is dead already.</summary>
    private void HostKill()
    {
        if (Player[EntVars.Health] <= 0)
        {
            ClientPrint("Can't suicide -- allready dead!\n"u8);
            return;
        }
        *G(GlobalVars.Time) = (float)Time;
        Globals[GlobalVars.Self] = _svPlayer * _edictSize;
        Hooks.ExecuteProgram(Globals[GlobalVars.ClientKill]);
    }

    /// <summary>Host_Pause_f: the pause toggled, told to everyone, and svc_setpause on the reliable datagram.</summary>
    private void HostPause()
    {
        if (CvarValueZ("pausable\0"u8) == 0)
        {
            ClientPrint("Pause not allowed.\n"u8);
            return;
        }
        Paused = !Paused;
        byte* b = stackalloc byte[PrintSize];
        var t = new Text(b);
        t.Add(String(((int*)Player)[EntVars.Netname]));
        t.Add(Paused ? " paused the game\n"u8 : " unpaused the game\n"u8);
        t.End();
        BroadcastPrint(b);
        // send notification to all clients
        ReliableDatagram.WriteByte(SvcSetPause);
        ReliableDatagram.WriteByte(Paused ? 1 : 0);
    }

    /// <summary>
    /// Host_Kick_f from the console of a running server: the command line tokenized as Cmd_TokenizeString did,
    /// "who" the player's name (cl_name; null: a dedicated server's "Console"), and host_client past the clients,
    /// where the last frame's loops left it, so that even the listen server's own client can be kicked.
    /// </summary>
    public void ConsoleKick(ReadOnlySpan<byte> line, ReadOnlySpan<byte> who)
    {
        byte* text = stackalloc byte[line.Length + 1];
        line.CopyTo(new Span<byte>(text, line.Length));
        text[line.Length] = 0;
        byte* w = stackalloc byte[who.Length + 1];
        who.CopyTo(new Span<byte>(w, who.Length));
        w[who.Length] = 0;
        Tokenize(text);
        _hostClient = MaxClients;   // host_client = svs.clients + svs.maxclients
        HostKick(w);
    }

    /// <summary>
    /// Host_Kick_f from a client (refused in deathmatch unless privileged), or from the console (<paramref name="consoleWho"/>):
    /// the client by name or "# slot" is told who kicked it, with the rest of the line, and dropped; nobody kicks himself.
    /// </summary>
    private void HostKick(byte* consoleWho = null)
    {
        if (consoleWho == null && CheatsRefused)
            return;
        int save = _hostClient;
        bool byNumber = false;
        int i;
        if (_argc > 2 && Argv(1)[0] == '#' && Argv(1)[1] == 0)
        {
            i = CCast.I32(CAtof.QAtof(new ReadOnlySpan<byte>(Argv(2), Strlen(Argv(2)))) - 1);
            if (i < 0 || i >= MaxClients)
                return;
            if (!Clients[i].Active)
                return;
            _hostClient = i;
            byNumber = true;
        }
        else
        {
            for (i = 0; i < MaxClients; i++)
            {
                _hostClient = i;
                if (!Clients[i].Active)
                    continue;
                if (StrCaseEquals(Clients[i].NamePtr, Argv(1)))
                    break;
            }
        }
        if (i < MaxClients)
        {
            byte* who = consoleWho != null ? consoleWho : Clients[save].NamePtr;
            // can't kick yourself!
            if (_hostClient == save)
                return;
            byte* message = null;
            if (_argc > 2)
            {
                message = _cmdParse.Parse(_args);
                if (byNumber)
                {
                    message++;                          // skip the #
                    while (*message == ' ')             // skip white space
                        message++;
                    message += Strlen(Argv(2));         // skip the number
                }
                while (*message != 0 && *message == ' ')
                    message++;
            }
            byte* b = stackalloc byte[PrintSize];
            var t = new Text(b);
            t.Add("Kicked by "u8);
            t.Add(who);
            if (message != null)
            {
                t.Add(": "u8);
                t.Add(message);
            }
            t.Add("\n"u8);
            t.End();
            ClientPrint(b);
            DropClient(false);
        }
        _hostClient = save;
    }

    /// <summary>Host_Give_f (not hipnotic, not rogue): a weapon by its number, or shells, nails, rockets, cells, health.</summary>
    private void HostGive()
    {
        if (CheatsRefused)
            return;
        byte* t = Argv(1);
        int v = Atoi(Argv(2));
        float* p = Player;
        switch (t[0])
        {
            case >= (byte)'0' and <= (byte)'9':
                if (t[0] >= '2')
                    p[EntVars.Items] = CCast.I32(p[EntVars.Items]) | (ItShotgun << (t[0] - '2'));
                break;
            case (byte)'s':
                p[EntVars.AmmoShells] = v;
                break;
            case (byte)'n':
                p[EntVars.AmmoNails] = v;
                break;
            case (byte)'r':
                p[EntVars.AmmoRockets] = v;
                break;
            case (byte)'h':
                p[EntVars.Health] = v;
                break;
            case (byte)'c':
                p[EntVars.AmmoCells] = v;
                break;
        }
    }
}
