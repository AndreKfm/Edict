// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using Edict.Engine;

namespace Edict.Harness;

/// <summary>
/// The state hashes of ref/src/edict_hash.c (.edh, docs/PLAN.md section E.1) over the port's server: the same
/// canonical little-endian streams, hashed with XXH64. Nothing layout-dependent enters the gated lanes: strings by
/// content, entity references by edict index, NaN as one value (docs/adr/D8), no pointers.
/// </summary>
public sealed unsafe class StateHash
{
    private const byte WtRaw = 0, WtFloat = 1, WtString = 2, WtEntity = 3, WtFunction = 4, WtField = 5, WtPointer = 6;
    public const int MaxLightstyles = 64;              // MAX_LIGHTSTYLES

    private readonly byte[] _fieldTypes, _globalTypes;
    private readonly List<byte> _s = new(1 << 16);

    /// <summary>The three lanes and one hash per edict (a spawn or frame record's contents).</summary>
    public readonly record struct Lanes(ulong Edicts, ulong GlobalsTyped, ulong GlobalsRaw, ulong[] Entities);

    public StateHash(Progs progs)
    {
        _fieldTypes = BuildTypes(progs.EntityFields, progs.FieldDefs, progs.Header->NumFieldDefs);
        _globalTypes = BuildTypes(progs.NumGlobals, progs.GlobalDefs, progs.Header->NumGlobalDefs);
    }

    private static byte[] BuildTypes(int count, DDef* defs, int numdefs)
    {
        var types = new byte[count];
        for (int i = 0; i < numdefs; i++)
        {
            int t = defs[i].Type & ~DDef.SaveGlobal, ofs = defs[i].Ofs;
            if (t == (int)EType.Vector)
            {
                if (ofs + 3 <= count)
                    types[ofs] = types[ofs + 1] = types[ofs + 2] = WtFloat;
                continue;
            }
            if (ofs >= count)
                continue;
            switch ((EType)t)
            {
                case EType.Float: types[ofs] = WtFloat; break;
                case EType.String: types[ofs] = WtString; break;
                case EType.Entity: types[ofs] = WtEntity; break;
                case EType.Function: types[ofs] = WtFunction; break;
                case EType.Field: types[ofs] = WtField; break;
                case EType.Pointer: types[ofs] = WtPointer; break;
            }
        }
        return types;
    }

    private void Put8(int v) => _s.Add((byte)v);

    private void Put32(uint v)
    {
        Put8((int)v); Put8((int)(v >> 8)); Put8((int)(v >> 16)); Put8((int)(v >> 24));
    }

    private void Put64(ulong v)
    {
        Put32((uint)v); Put32((uint)(v >> 32));
    }

    private void PutFloat(float f) => Put32(float.IsNaN(f) ? 0x7fc00000u : BitConverter.SingleToUInt32Bits(f));

    private void PutDouble(double d) => Put64(double.IsNaN(d) ? 0x7ff8000000000000ul : BitConverter.DoubleToUInt64Bits(d));

    private void PutString(byte* s)
    {
        while (*s != 0)
            Put8(*s++);
        Put8(0);
    }

    private void PutWord(Server sv, byte type, int raw)
    {
        switch (type)
        {
            case WtFloat:
                PutFloat(BitConverter.Int32BitsToSingle(raw));
                break;
            case WtString:
                PutString(sv.String(raw));
                break;
            case WtEntity:
                if (raw % sv.EdictSize != 0)
                    throw new InvalidDataException($"StateHash: entity value {raw} is not a multiple of {sv.EdictSize}");
                Put32((uint)(raw / sv.EdictSize));
                break;
            case WtPointer:
                break;
            default:        // function index, field offset (in words), untyped
                Put32((uint)raw);
                break;
        }
    }

    private ulong Hash()
    {
        ulong h = Xxh64.Hash(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_s));
        _s.Clear();
        return h;
    }

    private ulong Entity(Server sv, int index)
    {
        EdictHeader* e = sv.EdictNum(index);
        int* words = (int*)sv.Fields(index);
        Put32((uint)index);
        Put8(e->Free != 0 ? 1 : 0);
        PutFloat(e->FreeTime);
        Put32((uint)e->NumLeafs);
        for (int i = 0; i < e->NumLeafs && i < EdictHeader.MaxEntLeafs; i++)
            Put32((ushort)e->LeafNums[i]);
        for (int i = 0; i < 6; i++)
            PutFloat(0);    // baseline origin, angles: SV_CreateBaseline runs after the spawn point (cut 3)
        for (int i = 0; i < 5; i++)
            Put32(0);       // baseline modelindex, frame, colormap, skin, effects
        for (int i = 0; i < _fieldTypes.Length; i++)
            PutWord(sv, _fieldTypes[i], words[i]);
        return Hash();
    }

    private ulong Globals(Server sv, bool typed)
    {
        for (int i = 0; i < _globalTypes.Length; i++)
        {
            if (!typed)
                Put32((uint)sv.Globals[i]);
            else if (_globalTypes[i] != WtRaw && _globalTypes[i] != WtPointer)
            {
                Put32((uint)i);
                PutWord(sv, _globalTypes[i], sv.Globals[i]);
            }
        }
        return Hash();
    }

    /// <summary>EdictHash's StateRecord: the lanes of the server's current state.</summary>
    public Lanes Of(Server sv)
    {
        int n = sv.NumEdicts;
        var entities = new ulong[n];
        for (int i = 0; i < n; i++)
            entities[i] = Entity(sv, i);

        PutDouble(sv.Time);
        Put32((uint)n);
        Put32(sv.Active ? 1u : 0u);     // sv.state: ss_loading 0, ss_active 1
        Put32(sv.Paused ? 1u : 0u);
        Put32((uint)sv.ServerFlags);
        for (int i = 0; i < MaxLightstyles; i++)
        {
            byte* style = sv.Lightstyle(i);
            if (style != null)
            {
                Put8(1);
                PutString(style);
            }
            else
                Put8(0);
        }
        for (int i = 0; i < n; i++)
        {
            Put32((uint)i);
            Put64(entities[i]);
        }
        ulong edicts = Hash();
        return new Lanes(edicts, Globals(sv, typed: true), Globals(sv, typed: false), entities);
    }
}

/// <summary>XXH64 with seed 0, from the xxHash specification (the reference's ref/src/edict_xxh64.c).</summary>
public static class Xxh64
{
    private const ulong P1 = 11400714785074694791, P2 = 14029467366897019727, P3 = 1609587929392839161,
        P4 = 9650029242287828579, P5 = 2870177450012600261;

    private static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(acc + input * P2, 31) * P1;

    private static ulong Merge(ulong acc, ulong val) => (acc ^ Round(0, val)) * P1 + P4;

    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        int p = 0, len = data.Length;
        ulong h;
        if (len >= 32)
        {
            ulong v1 = unchecked(P1 + P2), v2 = P2, v3 = 0, v4 = unchecked(0 - P1);
            for (; p + 32 <= len; p += 32)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[p..]));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[(p + 8)..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[(p + 16)..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[(p + 24)..]));
            }
            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12)
                + BitOperations.RotateLeft(v4, 18);
            h = Merge(h, v1);
            h = Merge(h, v2);
            h = Merge(h, v3);
            h = Merge(h, v4);
        }
        else
            h = P5;
        h += (ulong)len;
        for (; p + 8 <= len; p += 8)
            h = BitOperations.RotateLeft(h ^ Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data[p..])), 27) * P1 + P4;
        if (p + 4 <= len)
        {
            h = BitOperations.RotateLeft(h ^ BinaryPrimitives.ReadUInt32LittleEndian(data[p..]) * P1, 23) * P2 + P3;
            p += 4;
        }
        for (; p < len; p++)
            h = BitOperations.RotateLeft(h ^ data[p] * P5, 11) * P1;
        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }
}
