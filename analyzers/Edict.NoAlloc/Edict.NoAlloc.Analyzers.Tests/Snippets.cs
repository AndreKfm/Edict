// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.NoAlloc.Analyzers.Tests;

internal static class Snippets
{
    /// <summary>
    /// Wraps a statement list in a method marked <c>[NoAlloc(Constraints.All)]</c> with commonly needed parameters,
    /// next to callees with every promise (<c>Hot</c>), NoAlloc only (<c>AllocFree</c>), none (<c>Plain</c>) and an
    /// unchecked one (<c>Unchecked</c>).
    /// </summary>
    public static string InHotMethod(string body, string members = "") => $$"""
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Edict.NoAlloc;

        unsafe class C
        {
            {{members}}

            [NoAlloc(Constraints.All)]
            void M(int i, object o, string s, int[] a, List<int> list, Span<int> span)
            {
                {{body}}
            }

            static void Plain() { }
            [NoAlloc(Constraints.All)] static void Hot() { }
            [NoAlloc] static void AllocFree() { }
            [NoAllocUnchecked("setup only")] static void Unchecked() { }
        }
        """;
}
