// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// cvar.c as the server's QuakeC sees it: <c>Cvar_FindVar</c>, <c>Cvar_VariableValue</c> (Q_atof of the string, on
/// every call), <c>Cvar_Set</c>. Strings live in fixed buffers, so setting a value allocates nothing; C allocates
/// any length, the port refuses values longer than <see cref="MaxString"/> bytes.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class Cvars
{
    public const int MaxString = 1023;

    private static readonly SysErrorException TooLong = new("Cvar_Set: value longer than the port's buffer");

    private sealed class Cvar(byte[] name)
    {
        public readonly byte[] Name = name;
        public readonly byte[] String = GC.AllocateArray<byte>(MaxString + 1, pinned: true);
        public float Value;
    }

    private readonly Cvar[] _vars;

    // Cvar_FindVar's answers by the address of the name asked for (QuakeC passes its string constants): a hit is
    // confirmed by comparing the name, so a string whose text changed at the same address (ftos's buffer) is looked up
    // again. Exact: cvar names are unique (Cvar_RegisterVariable refuses a second one) and the list never changes.
    private const int CacheSize = 64;
    private readonly nint[] _cacheName;
    private readonly int[] _cacheIndex;

    /// <summary>The cvars with their names and values, in the reference's list order (load path).</summary>
    [NoAllocUnchecked("load path: builds the table once")]
    public Cvars(IReadOnlyList<(byte[] Name, byte[] Value)> vars)
    {
        _vars = new Cvar[vars.Count];
        _cacheName = new nint[CacheSize];
        _cacheIndex = new int[CacheSize];
        for (int i = 0; i < vars.Count; i++)
        {
            _vars[i] = new Cvar(vars[i].Name);
            Store(_vars[i], vars[i].Value);
        }
    }

    /// <summary>The reference's cvar list with every cvar at its default, as Host_Init leaves them before quake.rc.</summary>
    [NoAllocUnchecked("load path: builds the table once")]
    public static Cvars FromRegistry() =>
        new(Registry.Cvars.Select(c => (System.Text.Encoding.Latin1.GetBytes(c.Name), System.Text.Encoding.Latin1.GetBytes(c.Default))).ToList());

    private static void Store(Cvar v, ReadOnlySpan<byte> value)
    {
        if (value.Length > MaxString)
            throw TooLong;
        value.CopyTo(v.String);
        v.String[value.Length] = 0;
        v.Value = CAtof.QAtof(value);
    }

    private static int Length(byte* s)
    {
        int n = 0;
        while (s[n] != 0)
            n++;
        return n;
    }

    /// <summary>Whether the zero-terminated <paramref name="s"/> is <paramref name="name"/>.</summary>
    private static bool Is(byte* s, byte[] name)
    {
        for (int i = 0; i < name.Length; i++)
            if (s[i] != name[i])
                return false;
        return s[name.Length] == 0;
    }

    /// <summary><c>Cvar_FindVar</c>: the first cvar with this name (Q_strcmp), or -1.</summary>
    private int Find(byte* name)
    {
        int slot = (int)(((nuint)name >> 2) % CacheSize);
        if (_cacheName[slot] == (nint)name)
        {
            int c = _cacheIndex[slot];
            if (c >= 0 && Is(name, _vars[c].Name))
                return c;
        }
        int found = -1;
        for (int i = 0; i < _vars.Length; i++)
            if (Is(name, _vars[i].Name))
            {
                found = i;
                break;
            }
        if (found >= 0)
        {
            _cacheName[slot] = (nint)name;
            _cacheIndex[slot] = found;
        }
        return found;
    }

    /// <summary>
    /// <c>Cvar_VariableValue</c>: 0 for an unknown name, otherwise Q_atof of the string. That is the value field: Store
    /// sets it as Q_atof of the same string (Cvar_Set does too), and in a dedicated server only client and renderer
    /// code writes a value directly (menu.c, r_misc.c, view.c).
    /// </summary>
    public float VariableValue(byte* name)
    {
        int i = Find(name);
        return i < 0 ? 0 : _vars[i].Value;
    }

    /// <summary><c>Cvar_VariableString</c>: the value's text, "" for an unknown name.</summary>
    public byte* VariableString(byte* name)
    {
        int i = Find(name);
        if (i < 0)
            return Empty;
        fixed (byte* s = _vars[i].String)
            return s;               // the buffers are pinned: allocated on the pinned object heap
    }

    private static readonly byte* Empty = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(1);

    /// <summary>
    /// <c>Cvar_Set</c>: unknown names are a console message in C, nothing else. The broadcast of changed server
    /// cvars to clients is message output, not ported yet.
    /// </summary>
    public void Set(byte* name, byte* value)
    {
        int i = Find(name);
        if (i < 0)
            return;
        Store(_vars[i], new ReadOnlySpan<byte>(value, Length(value)));
    }

    /// <summary>Whether a cvar has this name (Cvar_FindVar).</summary>
    public bool Exists(byte* name) => Find(name) >= 0;

    /// <summary>The value field (<c>var->value</c>) that engine code reads; 0 for an unknown name.</summary>
    public float Value(byte* name)
    {
        int i = Find(name);
        return i < 0 ? 0 : _vars[i].Value;
    }

    private static readonly SysErrorException SetValueNotIntegral =
        new("Cvar_SetValue: only integral values are ported (\"%f\" of 0..2^24 in magnitude)");

    /// <summary>
    /// <c>Cvar_SetValue</c>: <c>sprintf (val, "%f", value)</c>, then Cvar_Set. Ported for the integral values engine
    /// code sets (skill, deathmatch in SV_SpawnServer), which print exactly as the integer and ".000000"; any other
    /// value stops with an error until a general "%f" exists.
    /// </summary>
    public void SetValue(byte* name, float value)
    {
        if (value != MathF.Floor(value) || MathF.Abs(value) > 1 << 24)
            throw SetValueNotIntegral;
        byte* val = stackalloc byte[32];
        var text = new Span<byte>(val, 32);
        int n = 0;
        if (value == 0 && *(int*)&value < 0)
            text[n++] = (byte)'-';     // "%f" of -0.0 is "-0.000000"
        n += CFormat.Int((int)value, text[n..]);
        ".000000"u8.CopyTo(text[n..]);
        n += 7;
        val[n] = 0;
        Set(name, val);
    }
}
