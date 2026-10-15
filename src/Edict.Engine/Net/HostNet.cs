// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// What the network asks of a game's host (P6 step 9.4, outside the proof): Sys_FloatTime (the wall clock), svs's
/// limits, the hostname and map for the server info, Con_Printf, and the answers to the player and rule queries
/// (svs.clients; the server cvars, walked in the port's list order).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class HostNet : INetHost, IServerRules
{
    private Host _host = null!;
    private readonly bool[] _server;    // per cvar of the list: a server cvar (cvar_t.server)
    private readonly int _hostname;
    private readonly long _start = Stopwatch.GetTimestamp();

    [NoAllocUnchecked("load path")]
    public HostNet(Cvars cv)
    {
        _server = new bool[cv.Count];
        var server = new HashSet<string>(Registry.Cvars.Where(c => c.Server).Select(c => c.Name));
        for (int i = 0; i < cv.Count; i++)
            _server[i] = server.Contains(System.Text.Encoding.Latin1.GetString(cv.NameAt(i)));
        fixed (byte* n = "hostname\0"u8)
            _hostname = cv.IndexOf(n);
    }

    /// <summary>The host it answers for (set once the host is made: the host is made with the network).</summary>
    public Host Host
    {
        get => _host;
        [NoAllocUnchecked("load path")]
        set => _host = value;
    }

    public double FloatTime() => (double)(Stopwatch.GetTimestamp() - _start) / Stopwatch.Frequency;

    public int MaxClients => _host.MaxClients;

    public int ActiveConnections => _host.Sv is { Active: true } sv ? sv.ActiveConnections : 0;

    public ReadOnlySpan<byte> Hostname => _hostname >= 0 ? _host.Cvars.StringAt(_hostname) : "UNNAMED"u8;

    public ReadOnlySpan<byte> MapName => _host.Sv is { Active: true } sv ? sv.MapName : default;

    public void Print(ReadOnlySpan<byte> text) => _host.Cmd.Con?.Printf(text);

    public void UpdateScreen()
    {
    }

    public bool Player(int playerNumber, out ReadOnlySpan<byte> name, out int colors, out int frags, out double connectTime,
                       out ReadOnlySpan<byte> address)
    {
        name = address = default;
        colors = frags = 0;
        connectTime = 0;
        if (_host.Sv is not { Active: true } sv)
            return false;
        int active = -1;
        for (int i = 0; i < sv.MaxClients; i++)
        {
            Client c = sv.Clients[i];
            if (!c.Active || ++active != playerNumber)
                continue;
            byte* n = c.NamePtr;
            int len = 0;
            while (n[len] != 0)
                len++;
            name = new ReadOnlySpan<byte>(n, len);
            colors = c.Colors;
            frags = CCast.I32(sv.Fields(c.Edict)[EntVars.Frags]);
            connectTime = c.NetConnection is DatagramConnection d ? d.Socket.ConnectTime : c.ConnectTime;
            address = c.NetConnection is { } conn ? conn.Address : default;
            return true;
        }
        return false;
    }

    public CvarSearch NextServerCvar(byte* previous, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        name = value = default;
        Cvars cv = _host.Cvars;
        int i = 0;
        if (previous[0] != 0)
        {
            i = cv.IndexOf(previous);
            if (i < 0)
                return CvarSearch.Unknown;
            i++;
        }
        while (i < cv.Count && !_server[i])
            i++;
        if (i >= cv.Count)
            return CvarSearch.End;
        name = cv.NameAt(i);
        value = cv.StringAt(i);
        return CvarSearch.Found;
    }
}
