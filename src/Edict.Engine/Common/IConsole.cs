// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// Con_Printf's target: a listen server's and a client's <see cref="Screen"/> (the console), or a dedicated server's
/// <see cref="ConsoleBuffer"/> (Sys_Printf: stdout).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public unsafe interface IConsole
{
    /// <summary>Appends to the message Con_Printf formats.</summary>
    IConsole Msg(ReadOnlySpan<byte> text);

    /// <summary>Appends a zero-terminated string ("%s").</summary>
    IConsole Msg(byte* s);

    /// <summary>Appends "%i".</summary>
    IConsole MsgInt(int value);

    /// <summary>Con_Printf of the message built.</summary>
    void EndMsg();

    /// <summary>Con_Printf of a text without conversions.</summary>
    void Printf(ReadOnlySpan<byte> text);
}

/// <summary>
/// A dedicated server's console output (P6 step 9.4, outside the proof): Con_Printf's text kept in a buffer, which the
/// platform writes to stdout between frames (<see cref="Drain"/>); what does not fit until then is dropped.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class ConsoleBuffer : IConsole
{
    private readonly byte[] _text;
    private int _length;

    [NoAllocUnchecked("load path")]
    public ConsoleBuffer(int size = 1 << 16) => _text = new byte[size];

    public IConsole Msg(ReadOnlySpan<byte> text)
    {
        int n = Math.Min(text.Length, _text.Length - _length);
        text[..n].CopyTo(new Span<byte>(_text, _length, n));
        _length += n;
        return this;
    }

    public IConsole Msg(byte* s)
    {
        int n = 0;
        while (s[n] != 0)
            n++;
        return Msg(new ReadOnlySpan<byte>(s, n));
    }

    public IConsole MsgInt(int value)
    {
        Span<byte> t = stackalloc byte[12];
        int n = Edict.Exact.CFormat.Int(value, t);
        return Msg(t[..n]);
    }

    public void EndMsg()
    {
    }

    public void Printf(ReadOnlySpan<byte> text) => Msg(text);

    /// <summary>The text printed since the last drain, written to <paramref name="output"/>; the buffer emptied.</summary>
    [NoAllocUnchecked("the platform's output, between frames")]
    public void Drain(Stream output)
    {
        if (_length == 0)
            return;
        output.Write(_text, 0, _length);
        output.Flush();
        _length = 0;
    }
}
