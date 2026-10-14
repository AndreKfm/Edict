// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;
using Edict.Harness;
using Edict.Net;

namespace Edict.Engine.Tests;

/// <summary>
/// P6 step 9.4, outside the proof: the port and the reference on one network. The reference's network build
/// (ref/build.sh NET=1: net_bsd.c's drivers over net_udp.c, the wall clock; Linux) is EDICT_NETREF; tools/interop.sh
/// builds it and runs these tests in WSL. The port's client plays on the reference's dedicated server, the
/// reference's client on the port's: each signs on, says a line the server prints and echoes, and leaves, which the
/// server reports ("Client ... removed").
/// </summary>
public sealed class InteropTests
{
    private static string? Reference => Environment.GetEnvironmentVariable("EDICT_NETREF") is { Length: > 0 } r ? r : null;

    /// <summary>The reference running with its stdout collected.</summary>
    private sealed class ReferenceProcess : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();

        public ReferenceProcess(params string[] args)
        {
            var psi = new ProcessStartInfo(Reference!)
            {
                WorkingDirectory = TraceCorpus.DataDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            _process = Process.Start(psi)!;
            _process.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
            _process.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public string Output
        {
            get
            {
                lock (_output)
                    return _output.ToString();
            }
        }

        public bool HasExited => _process.HasExited;

        public void Dispose()
        {
            if (!_process.HasExited)
                _process.Kill();
            _process.WaitForExit(5000);
            _process.Dispose();
        }
    }

    /// <summary>Whether a server answers CCREQ_SERVER_INFO on the port of 127.0.0.1.</summary>
    private static bool Answers(int port)
    {
        using var u = new System.Net.Sockets.UdpClient();
        u.Client.ReceiveTimeout = 500;
        byte[] pkt = [0x80, 0, 0, 12, 0x02, (byte)'Q', (byte)'U', (byte)'A', (byte)'K', (byte)'E', 0, 3];
        u.Send(pkt, pkt.Length, "127.0.0.1", port);
        try
        {
            var from = new System.Net.IPEndPoint(0, 0);
            return u.Receive(ref from).Length > 5;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    [Fact]
    public void The_ports_client_plays_on_the_references_server()
    {
        Assert.SkipWhen(Reference is null, "EDICT_NETREF: the reference's network build (tools/interop.sh)");
        int port = UdpGameTests.FreePort();
        using var reference = new ReferenceProcess("-dedicated", "4", "-port", port.ToString(), "-edictframes", "2400", "+map", "start");
        Assert.True(SpinWait.SpinUntil(() => Answers(port) || reference.HasExited, 30000),
            "the reference's server did not answer: " + reference.Output);

        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using Host client = Host.CreateGame(files, ["+connect", $"\"127.0.0.1:{port}\""], lan: new UdpLan());
        ClientState cl = client.Cl!;
        Assert.True(UdpGameTests.RunUntil(client, null, () => cl.Signon == ClientState.Signons, 20),
            $"the port's client did not sign on (signon {cl.Signon}): " + UdpGameTests.ClientConsole(client));

        client.AddText("say the port to the reference\n"u8);
        Assert.True(UdpGameTests.RunUntil(client, null, () => reference.Output.Contains("the port to the reference"), 5),
            "the reference's server did not print the say: " + reference.Output);
        Assert.True(UdpGameTests.RunUntil(client, null, () => UdpGameTests.ClientConsole(client).Contains("the port to the reference"), 5),
            "the say did not come back: " + UdpGameTests.ClientConsole(client));

        client.AddText("disconnect\n"u8);
        Assert.True(UdpGameTests.RunUntil(client, null, () => reference.Output.Contains(" removed"), 10),
            "the reference's server did not drop the client: " + reference.Output);
    }

    [Fact]
    public void The_references_client_plays_on_the_ports_server()
    {
        Assert.SkipWhen(Reference is null, "EDICT_NETREF: the reference's network build (tools/interop.sh)");
        int port = UdpGameTests.FreePort();
        using var server = new UdpGameTests.ServerThread(port, "start");
        Assert.True(SpinWait.SpinUntil(() => server.ActiveConnections == 0 || server.Failure is not null, 30000),
            "the port's server did not start: " + server.Failure);

        // the reference's client: connect (to net_hostport, -port), a say after its signon, a disconnect
        string cfg = Path.Combine(TraceCorpus.DataDirectory, "id1", "edict", "interop.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllText(cfg, string.Concat(Enumerable.Repeat("wait\n", 300)) + "say the reference to the port\n"
                               + string.Concat(Enumerable.Repeat("wait\n", 200)) + "disconnect\n");
        using var reference = new ReferenceProcess("-port", port.ToString(), "-edictframes", "1500", "+connect", "127.0.0.1",
            "+exec", "edict/interop.cfg");
        Assert.True(SpinWait.SpinUntil(() => server.ActiveConnections == 1 || reference.HasExited || server.Failure is not null, 30000),
            "the reference's client did not connect: " + reference.Output + " SERVER: " + server.Console);
        Assert.True(SpinWait.SpinUntil(() => server.Console.Contains("the reference to the port") || reference.HasExited, 30000),
            "the port's server did not print the say: " + server.Console);
        Assert.True(SpinWait.SpinUntil(() => server.Console.Contains(" removed") || reference.HasExited, 30000),
            "the port's server did not drop the reference's client: " + server.Console);
        Assert.True(SpinWait.SpinUntil(() => reference.Output.Contains("the reference to the port"), 5000),
            "the say did not come back to the reference: " + reference.Output);
        Assert.Null(server.Failure);
    }
}
