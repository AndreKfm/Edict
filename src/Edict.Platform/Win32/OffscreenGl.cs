// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using static Edict.Platform.Win32.Native;

namespace Edict.Platform.Win32;

/// <summary>
/// An OpenGL 3.3 core context on a window that is never shown, for tests and captures that render offscreen
/// (<see cref="Edict.Gl.GlRenderer.UseOffscreen"/>); null where there is no such context (not Windows, no driver).
/// </summary>
public sealed unsafe class OffscreenGl : IDisposable
{
    private readonly Win32Window _window;

    private OffscreenGl(Win32Window window) => _window = window;

    public static OffscreenGl? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var w = new Win32Window("Edict offscreen", gl: true, visible: false);
        if (!w.HasGl)
        {
            w.Dispose();
            return null;
        }
        return new OffscreenGl(w);
    }

    public void Dispose() => _window.Dispose();
}
