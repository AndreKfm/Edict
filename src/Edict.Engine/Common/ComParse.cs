// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// COM_Parse (common.c): the next token of a text into <see cref="Token"/>, as C reads it. C's <c>char</c> is signed
/// on both reference platforms, so bytes from 0x80 on compare below <c>' '</c> and count as white space. Where C
/// would write past <c>com_token[1024]</c> or read past the end of an unterminated quoted string, the port stops
/// with an error. Load path (the entity string of a map).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class ComParse
{
    public const int TokenSize = 1024;                 // com_token[1024]

    private static readonly HostErrorException TokenTooLong = new("COM_Parse: token longer than com_token");
    private static readonly HostErrorException UnterminatedString = new("COM_Parse: quoted string without its closing quote at the end of the text");

    private readonly byte[] _token;

    [NoAllocUnchecked("map load: the token buffer")]
    public ComParse() => _token = GC.AllocateArray<byte>(TokenSize, pinned: true);

    /// <summary><c>com_token</c>, zero-terminated.</summary>
    public byte* Token
    {
        get
        {
            fixed (byte* t = _token)
                return t;
        }
    }

    private void Put(int len, int c)
    {
        if (len >= TokenSize - 1)
            throw TokenTooLong;
        _token[len] = (byte)c;
    }

    private static bool Single(int c) => c == '{' || c == '}' || c == ')' || c == '(' || c == '\'' || c == ':';

    /// <summary>Parses a token out of <paramref name="data"/>; the position after it, or null at the end.</summary>
    public byte* Parse(byte* data)
    {
        int c;
        int len = 0;
        _token[0] = 0;

        if (data == null)
            return null;

        // skip whitespace
        skipwhite:
        while ((c = (sbyte)*data) <= ' ')
        {
            if (c == 0)
                return null;                // end of file
            data++;
        }

        // skip // comments
        if (c == '/' && data[1] == '/')
        {
            while (*data != 0 && *data != '\n')
                data++;
            goto skipwhite;
        }

        // handle quoted strings specially
        if (c == '"')
        {
            data++;
            while (true)
            {
                c = (sbyte)*data++;
                if (c == '"' || c == 0)
                {
                    if (c == 0)
                        throw UnterminatedString;   // C returns the position after the terminator
                    _token[len] = 0;
                    return data;
                }
                Put(len, c);
                len++;
            }
        }

        // parse single characters
        if (Single(c))
        {
            Put(len, c);
            len++;
            _token[len] = 0;
            return data + 1;
        }

        // parse a regular word
        do
        {
            Put(len, c);
            data++;
            len++;
            c = (sbyte)*data;
            if (Single(c))
                break;
        } while (c > 32);

        _token[len] = 0;
        return data;
    }
}
