// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Exact;

/// <summary>
/// The reference's rand (ref/src/edict_libc.c): the MSVC linear congruential generator, seed 1 until
/// <see cref="Srand"/>. One instance is one C library stream; the engine owns it as part of its state.
/// </summary>
[NoAlloc(Constraints.All)]
public struct CRand
{
    // The seed minus one, so that default(CRand) starts with seed 1 like the C library.
    private uint _seedMinusOne;

    /// <summary>C's <c>srand</c>.</summary>
    public void Srand(uint seed) => _seedMinusOne = seed - 1;

    /// <summary>C's <c>rand</c>: 0..0x7fff.</summary>
    public int Next()
    {
        uint seed = (_seedMinusOne + 1) * 214013u + 2531011u;
        _seedMinusOne = seed - 1;
        return (int)((seed >> 16) & 0x7fff);
    }
}
