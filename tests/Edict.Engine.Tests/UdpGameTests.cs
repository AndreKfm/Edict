// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;
using Edict.Harness;
using Edict.Net;

namespace Edict.Engine.Tests;

/// <summary>
/// P6 step 9.4, outside the proof: a game over real UDP on 127.0.0.1. A dedicated server (its own thread, as a
/// datagram connect waits for the server's answer) and a game's client connect, sign on (the client loads the
/// server's models itself), talk, follow a changelevel, and part: a disconnect drops the client on the server, a
/// server that quits ends the client's game (svc_disconnect, Host_EndGame).
/// </summary>
public sealed class UdpGameTests
{
    private const float FrameTime = 1f / 72;

    /// <summary>A dedicated server running on a thread of its own until stopped.</summary>
    internal sealed class ServerThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _commands = new();
        private readonly StringBuilder _console = new();
        private volatile bool _stop;
        public volatile int ActiveConnections = -1;
        public volatile Exception? Failure;

        public ServerThread(int port, string map)
        {
            _thread = new Thread(() => Run(port, map)) { IsBackground = true, Name = "dedicated server" };
            _thread.Start();
        }

        public string Console
        {
            get
            {
                lock (_console)
                    return _console.ToString();
            }
        }

        public void Command(string text) => _commands.Enqueue(text);

        private void Run(int port, string map)
        {
            try
            {
                using var files = new QFileSystem(TraceCorpus.DataDirectory);
                var output = new ConsoleBuffer();
                using Host host = Host.CreateDedicated(files, ["-dedicated", "4", "-port", port.ToString(), "+map", map],
                    new UdpLan(), output);
                using var text = new MemoryStream();
                var clock = Stopwatch.StartNew();
                double oldtime = 0;
                while (!_stop && !host.QuitRequested)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    if (now - oldtime < 0.02)
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    while (_commands.TryDequeue(out string? c))
                        host.AddText(Encoding.Latin1.GetBytes(c + "\n"));
                    host.Frame((float)(now - oldtime));
                    oldtime = now;
                    ActiveConnections = host.Sv is { Active: true } sv ? sv.ActiveConnections : -1;
                    output.Drain(text);
                    lock (_console)
                        _console.Append(Encoding.Latin1.GetString(text.ToArray()));
                    text.SetLength(0);
                }
            }
            catch (Exception e)
            {
                Failure = e;
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(10000);
        }
    }

    internal static int FreePort()
    {
        using var s = new System.Net.Sockets.UdpClient(0);
        return ((System.Net.IPEndPoint)s.Client.LocalEndPoint!).Port;
    }

    /// <summary>Runs the client's frames (with the wall clock's time) until the condition holds or the seconds pass.</summary>
    internal static bool RunUntil(Host client, ServerThread? server, Func<bool> done, double seconds)
    {
        var clock = Stopwatch.StartNew();
        double oldtime = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (server?.Failure is { } f)
                throw new InvalidOperationException("the server failed", f);
            if (done())
                return true;
            double now = clock.Elapsed.TotalSeconds;
            if (now - oldtime < FrameTime)
            {
                Thread.Sleep(1);
                continue;
            }
            client.Frame((float)(now - oldtime));
            oldtime = now;
        }
        return done();
    }

    internal static string ClientConsole(Host client) => Encoding.Latin1.GetString(client.Screen!.ConsoleText());

    private static unsafe string WorldName(ClientState cl)
    {
        if (cl.WorldModel == null)
            return "";
        byte* n = (byte*)&cl.WorldModel->Name;
        int len = 0;
        while (n[len] != 0)
            len++;
        return Encoding.Latin1.GetString(n, len);
    }

    [Fact]
    public void A_client_signs_on_talks_and_leaves_over_udp()
    {
        int port = FreePort();
        using var server = new ServerThread(port, "start");
        Assert.True(SpinWait.SpinUntil(() => server.ActiveConnections == 0 || server.Failure is not null, 30000),
            "the server did not start: " + server.Failure);
        Assert.Null(server.Failure);

        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using Host client = Host.CreateGame(files, ["+connect", $"\"127.0.0.1:{port}\""], lan: new UdpLan());
        ClientState cl = client.Cl!;
        Assert.True(RunUntil(client, server, () => cl.Signon == ClientState.Signons, 20),
            $"the client did not sign on (signon {cl.Signon}, state {cl.State}): " + ClientConsole(client) + " SERVER: " + server.Console);
        Assert.True(RunUntil(client, server, () => server.ActiveConnections == 1, 5), "the server has no client");

        client.AddText("say over the wire\n"u8);
        Assert.True(RunUntil(client, server, () => server.Console.Contains("over the wire"), 5),
            "the server did not get the say: " + server.Console);
        Assert.True(RunUntil(client, server, () => ClientConsole(client).Contains("over the wire"), 5),
            "the say did not come back: " + ClientConsole(client));

        client.AddText("disconnect\n"u8);
        Assert.True(RunUntil(client, server, () => server.ActiveConnections == 0, 10),
            "the server did not drop the client: " + server.Console);
        Assert.Equal(ClientState.CaDisconnected, cl.State);
    }

    [Fact]
    public void A_server_that_shuts_down_ends_the_clients_game()
    {
        int port = FreePort();
        using var server = new ServerThread(port, "start");
        Assert.True(SpinWait.SpinUntil(() => server.ActiveConnections == 0 || server.Failure is not null, 30000),
            "the server did not start: " + server.Failure);

        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using Host client = Host.CreateGame(files, ["+connect", $"\"127.0.0.1:{port}\""], lan: new UdpLan());
        ClientState cl = client.Cl!;
        Assert.True(RunUntil(client, server, () => cl.Signon == ClientState.Signons, 20),
            $"the client did not sign on (signon {cl.Signon}): " + ClientConsole(client));

        // the next level: the client follows (reconnect, a new server info, its models loaded)
        server.Command("changelevel lq_e0m1");
        Assert.True(RunUntil(client, server, () => cl.Signon == ClientState.Signons && WorldName(cl) == "maps/lq_e0m1.bsp", 20),
            $"the client did not follow the map change (signon {cl.Signon}, world {WorldName(cl)}): " + ClientConsole(client));

        // a quit while the next level's signon is on its way: NET_SendToAll waits for its ack before svc_disconnect
        server.Command("changelevel lq_e0m2\nquit");
        Assert.True(RunUntil(client, server, () => cl.State == ClientState.CaDisconnected, 10),
            "the client is still connected: " + ClientConsole(client));
        Assert.DoesNotContain("Host_Error", ClientConsole(client));     // the game ended by svc_disconnect
        // Host_ShutdownServer dropped the remote client (SV_DropClient's Sys_Printf on a dedicated server)
        Assert.True(SpinWait.SpinUntil(() => server.Console.Contains(" removed"), 5000),
            "the server did not drop its client at the quit: " + server.Console);
    }
}
