// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// P6, step 6: a player's game as a platform runs it (<see cref="Host.CreateGame"/>), its input through the screen's
/// Key_Event and a mouse: a held key moves the player (CL_KeyState, CL_BaseMove), the mouse turns the view
/// (IN_MouseMove), the console and the chat line take the keys, and quit ends the game. Not a gate against the
/// reference (no corpus records input); what C does is kept by the port of keys.c, cl_input.c and in_win.c.
/// </summary>
public sealed unsafe class GameInputTests
{
    private sealed class FakeMouse : IMouse
    {
        public int Mx, My;
        public bool Active => true;

        public void TakeMotion(out int mx, out int my)
        {
            mx = Mx;
            my = My;
        }
    }

    private static unsafe float FovOf(Host host)
    {
        fixed (byte* n = "fov\0"u8)
            return host.Cvars.VariableValue(n);
    }

    private static void Run(Host host, int frames)
    {
        for (int i = 0; i < frames; i++)
            Assert.True(host.Frame(0.014f));
    }

    private static Host Start(QFileSystem files)
    {
        Host host = Host.CreateGame(files, ["+map", "start"]);
        Run(host, 150);     // the map spawned, the client signed on
        Assert.Equal(ClientState.Signons, host.Cl!.Signon);
        return host;
    }

    [Fact]
    public void A_held_key_moves_the_player()
    {
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using Host host = Start(files);
        ClientState cl = host.Cl!;
        Vec3 before = cl.Entities[cl.ViewEntity].Origin;
        host.Screen!.KeyEvent('w', true);       // bind w +forward (default.cfg)
        Run(host, 72);
        host.Screen.KeyEvent('w', false);
        Run(host, 10);
        Vec3 after = cl.Entities[cl.ViewEntity].Origin;
        float moved = MathF.Sqrt((after.X - before.X) * (after.X - before.X) + (after.Y - before.Y) * (after.Y - before.Y));
        Assert.True(moved > 50, $"the player moved {moved} units in a second of +forward");
        Assert.Equal(0, cl.Buttons[ClientState.InForward].State);
    }

    [Fact]
    public void The_mouse_turns_the_view()
    {
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using Host host = Start(files);
        ClientState cl = host.Cl!;
        var mouse = new FakeMouse { Mx = 10 };
        cl.Mouse = mouse;
        float yaw = cl.ViewAngles.Y;
        Run(host, 10);
        // m_yaw 0.022, sensitivity 3 (LibreQuake's config may change it): the view turns right, by a few degrees
        Assert.NotEqual(yaw, cl.ViewAngles.Y);
        mouse.Mx = 0;
        mouse.My = 20;
        host.Cmd.ExecuteString("+mlook");
        float pitch = cl.ViewAngles.X;
        Run(host, 5);
        Assert.True(cl.ViewAngles.X > pitch, $"pitch {pitch} -> {cl.ViewAngles.X} with the mouse pulled back under +mlook");
    }

    [Fact]
    public void The_console_takes_the_keys_and_quit_ends_the_game()
    {
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using Host host = Start(files);
        Screen s = host.Screen!;
        Assert.Equal(Keys.KeyGame, s.KeyDest);
        s.KeyEvent('`', true);                  // bind ` toggleconsole
        s.KeyEvent('`', false);
        Run(host, 1);
        Assert.Equal(Keys.KeyConsole, s.KeyDest);
        foreach (char c in "fov 100")
        {
            s.KeyEvent(c, true);
            s.KeyEvent(c, false);
        }
        s.KeyEvent(Screen.KEnter, true);
        s.KeyEvent(Screen.KEnter, false);
        Run(host, 1);
        Assert.Equal(100f, FovOf(host));
        // the chat line: t opens it from the game, escape closes it
        s.KeyEvent('`', true);
        s.KeyEvent('`', false);
        Run(host, 1);
        Assert.Equal(Keys.KeyGame, s.KeyDest);
        s.KeyEvent('t', true);
        s.KeyEvent('t', false);
        Run(host, 1);
        Assert.Equal(Keys.KeyMessage, s.KeyDest);
        s.KeyEvent(Screen.KEscape, true);
        s.KeyEvent(Screen.KEscape, false);
        Assert.Equal(Keys.KeyGame, s.KeyDest);
        // quit from the console
        s.KeyEvent('`', true);
        s.KeyEvent('`', false);
        Run(host, 1);
        foreach (char c in "quit")
        {
            s.KeyEvent(c, true);
            s.KeyEvent(c, false);
        }
        s.KeyEvent(Screen.KEnter, true);
        Run(host, 1);
        Assert.True(s.QuitRequested);
    }
}
