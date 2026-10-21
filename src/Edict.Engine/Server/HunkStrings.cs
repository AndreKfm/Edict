// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>
/// Engine strings (ED_NewString's Hunk_Alloc, and names the server keeps outside the progs: the map name, the world
/// model's name) at offsets above the progs strings and the string temp. C's offsets are hunk addresses minus
/// <c>pr_strings</c>; the port's differ, which no gated lane sees (strings are hashed by content, docs/PLAN.md A.3).
/// Native chunks of <see cref="ChunkSize"/> bytes that never move; allocation is load path.
/// </summary>
public sealed unsafe class HunkStrings : IEngineStrings, IDisposable
{
    public const int ChunkSize = 1 << 16;

    private static readonly SysErrorException BadOffset = new("engine string at an offset no string was allocated at");

    private readonly int _start;
    private nint[] _chunks = new nint[4];
    private int _numChunks, _used = ChunkSize;     // bytes used in the last chunk; full: the first Alloc opens one

    /// <param name="start">The first offset; above the progs strings and the string temp.</param>
    public HunkStrings(int start) => _start = start;

    /// <summary>Room for <paramref name="length"/> bytes; returns their offset (relative to pr_strings).</summary>
    [NoAllocUnchecked("load path: the hunk grows by chunks")]
    public int Alloc(int length, out byte* p)
    {
        if (length > ChunkSize)
            throw new InvalidOperationException($"engine string of {length} bytes, more than a chunk");
        if (_used + length > ChunkSize)
        {
            if (_numChunks == _chunks.Length)
                Array.Resize(ref _chunks, 2 * _numChunks);
            _chunks[_numChunks++] = (nint)NativeMemory.AllocZeroed(ChunkSize);
            _used = 0;
        }
        int ofs = _start + (_numChunks - 1) * ChunkSize + _used;
        p = (byte*)_chunks[_numChunks - 1] + _used;
        _used += length;
        return ofs;
    }

    /// <summary>A copy of <paramref name="s"/>, zero-terminated; returns its offset.</summary>
    [NoAllocUnchecked("load path")]
    public int Add(ReadOnlySpan<byte> s)
    {
        int ofs = Alloc(s.Length + 1, out byte* p);
        s.CopyTo(new Span<byte>(p, s.Length));
        p[s.Length] = 0;
        return ofs;
    }

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    public byte* Get(int ofs)
    {
        int rel = ofs - _start;
        int chunk = rel >> 16;
        if (rel < 0 || chunk >= _numChunks || (chunk == _numChunks - 1 && (rel & (ChunkSize - 1)) >= _used))
            throw BadOffset;
        return (byte*)_chunks[chunk] + (rel & (ChunkSize - 1));
    }

    public void Dispose()
    {
        for (int i = 0; i < _numChunks; i++)
            NativeMemory.Free((void*)_chunks[i]);
        _numChunks = 0;
    }
}
