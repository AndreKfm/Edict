// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>Bitwise comparison and first-divergence reports (docs/TESTING.md R1, R5).</summary>
internal static class Bits
{
    public static uint Of(float f) => BitConverter.SingleToUInt32Bits(f);

    public static string Hex(float f) => string.Create(CultureInfo.InvariantCulture, $"{f:R} (0x{Of(f):X8})");

    public static string Hex(in Vec3 v) => $"({Hex(v.X)}, {Hex(v.Y)}, {Hex(v.Z)})";

    public static bool Same(in Vec3 a, in Vec3 b) => Of(a.X) == Of(b.X) && Of(a.Y) == Of(b.Y) && Of(a.Z) == Of(b.Z);

    /// <summary>The first field in which two traces differ, with both values; null if they agree in every bit.</summary>
    public static string? FirstDifference(in Trace port, in Trace reference)
    {
        if (port.AllSolid != reference.AllSolid)
            return $"allsolid: port {port.AllSolid}, reference {reference.AllSolid}";
        if (port.StartSolid != reference.StartSolid)
            return $"startsolid: port {port.StartSolid}, reference {reference.StartSolid}";
        if (port.InOpen != reference.InOpen)
            return $"inopen: port {port.InOpen}, reference {reference.InOpen}";
        if (port.InWater != reference.InWater)
            return $"inwater: port {port.InWater}, reference {reference.InWater}";
        if (Of(port.Fraction) != Of(reference.Fraction))
            return $"fraction: port {Hex(port.Fraction)}, reference {Hex(reference.Fraction)}";
        if (!Same(port.EndPos, reference.EndPos))
            return $"endpos: port {Hex(port.EndPos)}, reference {Hex(reference.EndPos)}";
        if (!Same(port.Plane.Normal, reference.Plane.Normal))
            return $"plane.normal: port {Hex(port.Plane.Normal)}, reference {Hex(reference.Plane.Normal)}";
        if (Of(port.Plane.Dist) != Of(reference.Plane.Dist))
            return $"plane.dist: port {Hex(port.Plane.Dist)}, reference {Hex(reference.Plane.Dist)}";
        if (port.Ent != reference.Ent)
            return $"ent: port {port.Ent}, reference {reference.Ent}";
        return null;
    }

    public static string Inputs(in TraceCorpus.TraceRecord t)
    {
        var s = new StringBuilder();
        s.Append(t.Kind switch { 0 => "world", 1 => $"brush model {t.ModelIndex}", _ => "box" });
        s.Append($" at {Hex(t.EntOrigin)}");
        if (t.Kind == 2)
            s.Append($" size {Hex(t.EntMins)}..{Hex(t.EntMaxs)}");
        s.Append($"; start {Hex(t.Start)}, mins {Hex(t.Mins)}, maxs {Hex(t.Maxs)}, end {Hex(t.End)}");
        return s.ToString();
    }
}
