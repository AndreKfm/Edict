// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Edict.Engine;

namespace Edict.Platform;

/// <summary>A platform's window: the screen's sink, the mouse, and the messages of the window system.</summary>
internal interface IGameWindow : IScreenSink, IMouse, IDisposable
{
    /// <summary>The screen whose key events the window feeds.</summary>
    void Attach(Screen screen);

    /// <summary>The window system's events; false once the window was closed.</summary>
    bool Pump();
}

/// <summary>
/// edict: the game (sys_win.c's WinMain loop). The game directory is -basedir's (default: the current directory,
/// else EDICT_DATA), the arguments are Quake's ("+map start" when none names a map); each turn of the loop pumps the
/// window's events and runs Host_Frame with the time since the last turn, which Host_FilterTime holds to 72 frames
/// a second.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        List<string> argv = [.. args];
        string basedir = Environment.GetEnvironmentVariable("EDICT_DATA") is { Length: > 0 } env ? env : ".";
        int b = argv.IndexOf("-basedir");
        if (b >= 0 && b + 1 < argv.Count)
            basedir = argv[b + 1];
        if (!Directory.Exists(Path.Combine(basedir, "id1")))
        {
            Console.Error.WriteLine($"edict: no id1 directory in \"{Path.GetFullPath(basedir)}\" (-basedir <dir> or EDICT_DATA)");
            return 1;
        }
        // diagnostics: -edictframes <n> ends after n host frames, -edictshot <file.bmp> then saves the last frame
        int maxFrames = int.MaxValue;
        int f = argv.IndexOf("-edictframes");
        if (f >= 0 && f + 1 < argv.Count)
        {
            maxFrames = int.Parse(argv[f + 1], System.Globalization.CultureInfo.InvariantCulture);
            argv.RemoveRange(f, 2);
        }
        string? shot = null;
        int sh = argv.IndexOf("-edictshot");
        if (sh >= 0 && sh + 1 < argv.Count)
        {
            shot = argv[sh + 1];
            argv.RemoveRange(sh, 2);
        }
        if (!argv.Contains("+map") && !argv.Contains("+playdemo") && !argv.Contains("+connect"))
            argv.AddRange(["+map", "start"]);

        IGameWindow window = OperatingSystem.IsWindows() ? new Win32.Win32Window("Edict")
            : OperatingSystem.IsLinux() ? new X11.X11Window("Edict")
            : throw new PlatformNotSupportedException("edict: this platform has no window yet");
        try
        {
            using var files = new QFileSystem(basedir);
            using Host host = Host.CreateGame(files, argv);
            Screen screen = host.Screen!;
            screen.Sink = window;
            window.Attach(screen);
            host.Cl!.Mouse = window;
            var clock = Stopwatch.StartNew();
            double oldtime = clock.Elapsed.TotalSeconds;
            while (window.Pump() && !screen.QuitRequested && host.FrameCount < maxFrames)
            {
                double newtime = clock.Elapsed.TotalSeconds;
                bool ran = host.Frame((float)(newtime - oldtime));     // Host_FilterTime keeps the time it holds back
                oldtime = newtime;
                if (!ran)
                    Thread.Sleep(1);
            }
            if (shot != null)
                WriteBmp(shot, screen.FrameBuffer, screen.Palette);
            return 0;
        }
        catch (Exception e)
        {
            string text = $"{e.GetType().Name}: {e.Message}\n\n{e.StackTrace}";
            Console.Error.WriteLine(text);
            if (OperatingSystem.IsWindows())
                Win32.Native.MessageBox(0, text, "Edict: Sys_Error", Win32.Native.MbOk | Win32.Native.MbIconError);
            return 1;
        }
        finally
        {
            window.Dispose();
        }
    }

    /// <summary>An 8-bit BMP of the frame buffer with its palette.</summary>
    private static void WriteBmp(string path, ReadOnlySpan<byte> frame, ReadOnlySpan<byte> palette)
    {
        using var w = new BinaryWriter(File.Create(path));
        int size = 14 + 40 + 1024 + frame.Length;
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(size); w.Write(0); w.Write(14 + 40 + 1024);
        w.Write(40); w.Write(Screen.Width); w.Write(-Screen.Height); w.Write((short)1); w.Write((short)8);
        w.Write(0); w.Write(frame.Length); w.Write(2835); w.Write(2835); w.Write(256); w.Write(256);
        for (int i = 0; i < 256; i++)
        {
            w.Write(palette[i * 3 + 2]); w.Write(palette[i * 3 + 1]); w.Write(palette[i * 3]); w.Write((byte)0);
        }
        w.Write(frame);
    }
}
