// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>
/// A bump allocator over one aligned native block, the role of id's hunk for data loaded with a map. Memory is
/// zeroed, as <c>Hunk_AllocName</c> zeroes it. Every allocation starts on a 64-byte boundary. Load path only.
/// </summary>
public sealed unsafe class NativeArena : IDisposable
{
    private const int Alignment = 64;

    private byte* _base;
    private readonly nuint _size;
    private nuint _used;

    public NativeArena(nuint size)
    {
        _size = Align(Math.Max(size, 1));
        _base = (byte*)NativeMemory.AlignedAlloc(_size, Alignment);
        NativeMemory.Clear(_base, _size);
    }

    /// <summary>Bytes an allocation of <paramref name="count"/> values of <typeparamref name="T"/> takes here.</summary>
    public static nuint SizeOf<T>(int count) where T : unmanaged => Align((nuint)count * (nuint)sizeof(T));

    /// <summary>Zeroed space for <paramref name="count"/> values of <typeparamref name="T"/>.</summary>
    public T* Alloc<T>(int count) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_base is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        nuint bytes = SizeOf<T>(count);
        if (bytes > _size - _used)
            throw new InvalidOperationException($"NativeArena: {bytes} bytes requested, {_size - _used} of {_size} left");
        T* p = (T*)(_base + _used);
        _used += bytes;
        return p;
    }

    public void Dispose()
    {
        if (_base is not null)
        {
            NativeMemory.AlignedFree(_base);
            _base = null;
        }
    }

    private static nuint Align(nuint n) => (n + Alignment - 1) & ~(nuint)(Alignment - 1);
}
