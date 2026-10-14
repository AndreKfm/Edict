// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// <c>sizebuf_t</c> and SZ_* (common.c): a message buffer of fixed capacity. A buffer that allows overflow is
/// cleared and marked overflowed when a write does not fit (the server drops a client whose message overflowed);
/// one that does not is a Sys_Error. The byte contents are what the clients receive; no lane hashes them yet, but
/// their sizes decide overflows, which are state.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class SizeBuf
{
    private static readonly SysErrorException OverflowNotAllowed = new("SZ_GetSpace: overflow without allowoverflow set");
    private static readonly SysErrorException LongerThanBuffer = new("SZ_GetSpace: a write longer than the full buffer");

    private readonly byte[] _data;

    public int MaxSize { get; }
    public int CurSize { get; set; }
    public bool AllowOverflow { get; set; }
    public bool Overflowed { get; set; }

    [NoAllocUnchecked("load path: the buffer")]
    public SizeBuf(int maxSize, bool allowOverflow = false)
    {
        _data = GC.AllocateArray<byte>(maxSize, pinned: true);
        MaxSize = maxSize;
        AllowOverflow = allowOverflow;
    }

    /// <summary>The buffer (pinned): <c>data</c>.</summary>
    public byte* Data
    {
        get
        {
            fixed (byte* d = _data)
                return d;
        }
    }

    /// <summary>SZ_Clear.</summary>
    public void Clear() => CurSize = 0;

    /// <summary>SZ_GetSpace: room for <paramref name="length"/> bytes at the end.</summary>
    public byte* GetSpace(int length)
    {
        if (CurSize + length > MaxSize)
        {
            if (!AllowOverflow)
                throw OverflowNotAllowed;
            if (length > MaxSize)
                throw LongerThanBuffer;
            Overflowed = true;          // Con_Printf ("SZ_GetSpace: overflow")
            Clear();
        }
        byte* d = Data + CurSize;
        CurSize += length;
        return d;
    }

    /// <summary>SZ_Print: the text and its zero, over the zero a previous print left at the end.</summary>
    public void Print(ReadOnlySpan<byte> text)
    {
        int len = text.Length + 1;
        byte* d = Data[CurSize - 1] != 0 ? GetSpace(len) : GetSpace(len - 1) - 1;    // write over trailing 0
        for (int i = 0; i < text.Length; i++)
            d[i] = text[i];
        d[text.Length] = 0;
    }

    /// <summary>SZ_Write.</summary>
    public void Write(byte* data, int length)
    {
        byte* d = GetSpace(length);
        for (int i = 0; i < length; i++)
            d[i] = data[i];
    }

    // MSG_Write* (common.c, without PARANOID: no range checks). Callers that pass a float convert it with C's cast.

    public void WriteChar(int c) => *GetSpace(1) = (byte)c;

    public void WriteByte(int c) => *GetSpace(1) = (byte)c;

    public void WriteShort(int c)
    {
        byte* b = GetSpace(2);
        b[0] = (byte)(c & 0xff);
        b[1] = (byte)(c >> 8);
    }

    public void WriteLong(int c)
    {
        byte* b = GetSpace(4);
        b[0] = (byte)(c & 0xff);
        b[1] = (byte)((c >> 8) & 0xff);
        b[2] = (byte)((c >> 16) & 0xff);
        b[3] = (byte)(c >> 24);
    }

    public void WriteFloat(float f)
    {
        int l = *(int*)&f;
        Write((byte*)&l, 4);
    }

    /// <summary>MSG_WriteString: the string and its terminator; "" for null.</summary>
    /// <summary>MSG_WriteString of a text in a span: up to its first zero (or its end), and the zero.</summary>
    public void WriteString(ReadOnlySpan<byte> s)
    {
        int n = 0;
        while (n < s.Length && s[n] != 0)
            n++;
        byte* d = GetSpace(n + 1);
        for (int i = 0; i < n; i++)
            d[i] = s[i];
        d[n] = 0;
    }

    public void WriteString(byte* s)
    {
        if (s == null)
        {
            WriteByte(0);
            return;
        }
        int n = 0;
        while (s[n] != 0)
            n++;
        Write(s, n + 1);
    }

    /// <summary>MSG_WriteCoord: <c>(int)(f*8)</c> as a short.</summary>
    public void WriteCoord(float f) => WriteShort(CCast.I32(f * 8));

    /// <summary>MSG_WriteAngle: <c>((int)f*256/360) &amp; 255</c>.</summary>
    public void WriteAngle(float f) => WriteByte((CCast.I32(f) * 256 / 360) & 255);
}

/// <summary>
/// MSG_BeginReading and MSG_Read* (common.c) on <c>net_message</c>: reads past the end set
/// <see cref="BadRead"/> and return -1, except MSG_ReadFloat, which C does not check (it reads what the buffer holds).
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class MsgReader
{
    private static readonly SysErrorException FloatPastBuffer = new("MSG_ReadFloat past the end of net_message's buffer");

    private readonly byte[] _string;                // MSG_ReadString's static buffer

    [NoAllocUnchecked("load path: the string buffer")]
    public MsgReader(SizeBuf message)
    {
        Message = message;
        _string = GC.AllocateArray<byte>(2048, pinned: true);
    }

    public SizeBuf Message { get; }
    public int ReadCount { get; private set; }
    public bool BadRead { get; private set; }

    public void BeginReading()
    {
        ReadCount = 0;
        BadRead = false;
    }

    public int ReadChar()
    {
        if (ReadCount + 1 > Message.CurSize)
        {
            BadRead = true;
            return -1;
        }
        return (sbyte)Message.Data[ReadCount++];
    }

    public int ReadByte()
    {
        if (ReadCount + 1 > Message.CurSize)
        {
            BadRead = true;
            return -1;
        }
        return Message.Data[ReadCount++];
    }

    public int ReadShort()
    {
        if (ReadCount + 2 > Message.CurSize)
        {
            BadRead = true;
            return -1;
        }
        byte* d = Message.Data + ReadCount;
        ReadCount += 2;
        return (short)(d[0] + (d[1] << 8));
    }

    public int ReadLong()
    {
        if (ReadCount + 4 > Message.CurSize)
        {
            BadRead = true;
            return -1;
        }
        byte* d = Message.Data + ReadCount;
        ReadCount += 4;
        return d[0] + (d[1] << 8) + (d[2] << 16) + (d[3] << 24);
    }

    public float ReadFloat()
    {
        if (ReadCount + 4 > Message.MaxSize)
            throw FloatPastBuffer;
        byte* d = Message.Data + ReadCount;
        ReadCount += 4;
        int l = d[0] | (d[1] << 8) | (d[2] << 16) | (d[3] << 24);
        return *(float*)&l;
    }

    /// <summary>MSG_ReadString: up to 2047 characters into a buffer the next call overwrites.</summary>
    public byte* ReadString()
    {
        int l = 0;
        do
        {
            int c = ReadChar();
            if (c == -1 || c == 0)
                break;
            _string[l] = (byte)c;
            l++;
        } while (l < _string.Length - 1);
        _string[l] = 0;
        fixed (byte* s = _string)
            return s;
    }

    public float ReadCoord() => (float)(ReadShort() * (1.0 / 8));

    public float ReadAngle() => (float)(ReadChar() * (360.0 / 256));
}
