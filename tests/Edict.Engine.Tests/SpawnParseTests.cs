// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 for the entity parser of the spawn gate: COM_Parse on random texts (words, quoted strings, comments, the
/// single-character tokens, bytes from 0x80 on, which C's signed char makes white space), and ED_ParseEpair on
/// strings (ED_NewString's escapes), vectors (single spaces split, runs of spaces give empty components) and floats,
/// each against the reference's function through the oracle. Inputs on which C is undefined (a vector with fewer
/// than three components, an unterminated quoted string at the end) must make the port stop instead.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class SpawnParseTests
{
    private static readonly nint Lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("EDICT_ORACLE")
        ?? throw new InvalidOperationException("EDICT_ORACLE is not set (docs/TESTING.md)"));
    private static readonly delegate* unmanaged<byte*, byte*, int, int> ComParseFn =
        (delegate* unmanaged<byte*, byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_ComParse");
    private static readonly delegate* unmanaged<int, byte*, float*, byte*, int, int> ParseEpairFn =
        (delegate* unmanaged<int, byte*, float*, byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_ParseEpair");

    private static readonly string[] Words =
        ["classname", "light", "angle", "origin", "0", "-1.5", "1e3", "0x10", ".5", "a_b", "*1", "progs/player.mdl", "x"];
    private static readonly char[] Specials = ['{', '}', '(', ')', '\'', ':'];

    private static byte[] Text(ref VmOpcodeFuzz.Rng rng, bool closeQuotes)
    {
        var b = new List<byte>();
        int parts = 1 + rng.Below(14);
        for (int i = 0; i < parts; i++)
        {
            switch (rng.Below(10))
            {
                case 0: b.AddRange(Encoding.ASCII.GetBytes(Words[rng.Below(Words.Length)])); break;
                case 1: b.Add((byte)" \t\n\r"[rng.Below(4)]); break;
                case 2: b.Add((byte)Specials[rng.Below(Specials.Length)]); break;
                case 3:
                    b.Add((byte)'"');
                    for (int k = rng.Below(8); k > 0; k--)
                        b.Add((byte)"ab {}/\\n'\t"[rng.Below(10)]);
                    if (closeQuotes || rng.Below(4) != 0)
                        b.Add((byte)'"');
                    break;
                case 4: b.AddRange("// note\n"u8.ToArray()); break;
                case 5: b.Add((byte)(0x80 + rng.Below(128))); break;
                case 6: b.Add((byte)'/'); break;
                case 7: b.Add((byte)(0x21 + rng.Below(0x5e))); break;
                default: b.Add((byte)' '); break;
            }
        }
        b.Add(0);
        return [.. b];
    }

    [Fact]
    public void ComParse_matches_the_reference()
    {
        var rng = new VmOpcodeFuzz.Rng(11);
        var parse = new ComParse();
        byte* token = stackalloc byte[ComParse.TokenSize];
        int tokens = 0;
        for (int n = 0; n < 100_000; n++)
        {
            byte[] text = Text(ref rng, closeQuotes: n % 4 != 0);
            fixed (byte* t = text)
            {
                int len = text.Length - 1;
                byte* p = t;
                while (true)
                {
                    int r = ComParseFn(p, token, ComParse.TokenSize);
                    if (r >= 0 && p + r > t + len)
                    {   // C returned the position after the terminator (a quoted string the text ends in)
                        Assert.Throws<HostErrorException>(() => parse.Parse(p));
                        break;
                    }
                    byte* q = parse.Parse(p);
                    string where = $"text {n} \"{Encoding.Latin1.GetString(text, 0, len)}\" at {p - t}";
                    Assert.True((q == null) == (r < 0), $"{where}: port {(q == null ? "end" : "token")}, reference {(r < 0 ? "end" : "token")}");
                    var a = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(parse.Token);
                    var b = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(token);
                    Assert.True(a.SequenceEqual(b), $"{where}: port token \"{Encoding.Latin1.GetString(a)}\", reference \"{Encoding.Latin1.GetString(b)}\"");
                    if (q == null)
                        break;
                    Assert.True(q - p == r, $"{where}: port read {q - p} bytes, reference {r}");
                    tokens++;
                    p = q;
                }
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{tokens} tokens");
    }

    /// <summary>A server on the fuzz's memory with engine strings, for ED_ParseEpair.</summary>
    private sealed class Parser : IDisposable
    {
        public readonly VmOpcodeFuzz F = new(4);
        public readonly HunkStrings Hunk;
        public readonly Server Sv;
        private readonly byte* _temp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);

        public Parser()
        {
            int tempOfs = F.Progs.NumStrings;
            Hunk = new HunkStrings(tempOfs + Server.StringTempSize);
            var vm = new QcVm(F.Progs, (int*)F.PortGlobals, F.PortEdicts, F.Header, VmReplay.NumBuiltins);
            Sv = new Server(vm, F.Progs, (int*)F.PortGlobals, F.PortEdicts, F.Header, new Cvars([]), Hunk, _temp, tempOfs);
        }

        public void Dispose()
        {
            Hunk.Dispose();
            F.Dispose();
            NativeMemory.Free(_temp);
        }
    }

    private static string FloatText(ref VmOpcodeFuzz.Rng rng) => rng.Below(10) switch
    {
        0 => "",
        1 => $"{rng.Below(2000) - 1000}",
        2 => $"{(rng.Below(2) == 0 ? "-" : "")}{rng.Below(1000)}.{rng.Below(100000)}",
        3 => $"{rng.Below(10)}e{rng.Below(40) - 20}",
        4 => $"0x{rng.Bits() & 0xFFFF:x}",
        5 => new[] { "inf", "-inf", "nan", "-nan", "infinity" }[rng.Below(5)],
        6 => $".{rng.Below(1000)}",
        7 => $"{rng.Below(100)}abc",
        8 => $"  {rng.Below(100)}",
        _ => BitConverter.UInt32BitsToSingle(rng.Float()).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public void ParseEpair_matches_the_reference()
    {
        using var p = new Parser();
        var rng = new VmOpcodeFuzz.Rng(13);
        float* r = stackalloc float[3];
        float* q = stackalloc float[3];
        byte* text = stackalloc byte[512];
        DDef key = default;
        int undefined = 0;
        for (int n = 0; n < 200_000; n++)
        {
            int kind = n % 3;
            string s = kind switch
            {
                0 => FloatText(ref rng),
                1 => string.Join(new string(' ', 1 + (rng.Below(5) == 0 ? 1 : 0)),
                                 Enumerable.Range(0, 1 + rng.Below(5)).Select(_ => FloatText(ref rng)))
                     + (rng.Below(6) == 0 ? " " : ""),
                _ => new string([.. Enumerable.Range(0, rng.Below(12)).Select(_ => "ab\\n x\\\\"[rng.Below(8)])]),
            };
            EType type = kind == 0 ? EType.Float : kind == 1 ? EType.Vector : EType.String;
            if (type == EType.Vector && s.Length >= 128)
                continue;
            byte[] sb = [.. Encoding.ASCII.GetBytes(s), 0];
            key.Type = (ushort)type;
            key.Ofs = 0;
            r[0] = r[1] = r[2] = q[0] = q[1] = q[2] = 0;
            try
            {
                p.Sv.ParseEpair((int*)q, &key, sb.AsSpan(0, sb.Length - 1));
            }
            catch (SysErrorException) when (type == EType.Vector)
            {
                undefined++;        // fewer than three components: C reads past the string, the port stops
                continue;
            }
            fixed (byte* sp = sb)
                Assert.Equal(1, ParseEpairFn((int)type, sp, r, text, 512));
            if (type == EType.String)
            {
                var a = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(p.Sv.String(*(int*)q));
                var b = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text);
                Assert.True(a.SequenceEqual(b), $"ev_string \"{s}\": port \"{Encoding.Latin1.GetString(a)}\", reference \"{Encoding.Latin1.GetString(b)}\"");
                continue;
            }
            for (int i = 0; i < (type == EType.Vector ? 3 : 1); i++)
                Assert.True(((uint*)q)[i] == ((uint*)r)[i],
                    $"{type} \"{s}\" word {i}: port {VmOpcodeFuzz.Hex(((uint*)q)[i])}, reference {VmOpcodeFuzz.Hex(((uint*)r)[i])}");
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{undefined} vectors with fewer than three components");
    }

    private static readonly delegate* unmanaged<byte*, int, int> ProgsFn =
        (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_Progs");
    private static readonly delegate* unmanaged<byte*, void*, int, int, int> ParseEdictFn =
        (delegate* unmanaged<byte*, void*, int, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_ParseEdict");
    private static readonly delegate* unmanaged<int, byte*, int, int> StringFn =
        (delegate* unmanaged<int, byte*, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_String");
    private static readonly delegate* unmanaged<void> ProgsResetFn =
        (delegate* unmanaged<void>)NativeLibrary.GetExport(Lib, "EdictOracle_ProgsReset");
    private static readonly delegate* unmanaged<void*, int, int, int, int*, float*, void> HeaderFn =
        (delegate* unmanaged<void*, int, int, int, int*, float*, void>)NativeLibrary.GetExport(Lib, "EdictOracle_EdictHeader");

    /// <summary>
    /// ED_ParseEdict on random valid entities of the game's progs.dat: every field def as a key (with trailing spaces
    /// at times, which C trims), "angle" and "light" (the two hacks), keys with a leading underscore and unknown keys,
    /// values of the key's type (function and field names that exist, entity numbers in range), empty entities, and
    /// the world, which ED_ParseEdict does not clear. Fields compared word for word, strings by content, the free
    /// flag, and how far each side read.
    /// </summary>
    [Fact]
    public void ParseEdict_matches_the_reference()
    {
        const int Edicts = 8;
        using var files = new QFileSystem(Edict.Harness.TraceCorpus.DataDirectory);
        QFile file = files.Find("progs.dat") ?? throw new FileNotFoundException("progs.dat");
        using var progs = new Progs(file);
        fixed (byte* image = file.Span)
            Assert.Equal(0, ProgsFn(image, file.Length));
        int header = VmOpcodeFuzz.HeaderBytes, fields = progs.EntityFields, stride = header + 4 * fields;
        int* globals = (int*)NativeMemory.AllocZeroed((nuint)progs.NumGlobals, 4);
        byte* refEdicts = (byte*)NativeMemory.AllocZeroed(Edicts, (nuint)stride);
        byte* portEdicts = (byte*)NativeMemory.AllocZeroed(Edicts, (nuint)stride);
        byte* temp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
        var hunk = new HunkStrings(progs.NumStrings + Server.StringTempSize);
        try
        {
            new ReadOnlySpan<int>(progs.InitialGlobals, progs.NumGlobals).CopyTo(new Span<int>(globals, progs.NumGlobals));
            var vm = new QcVm(progs, globals, portEdicts, header, VmReplay.NumBuiltins);
            var sv = new Server(vm, progs, globals, portEdicts, header, new Cvars([]), hunk, temp, progs.NumStrings)
            { MaxEdicts = Edicts, NumEdicts = Edicts };
            var stringWord = new bool[fields];
            var defs = new List<(string Name, EType Type)>();
            for (int i = 0; i < progs.Header->NumFieldDefs; i++)
            {
                DDef* d = progs.FieldDefs + i;
                var t = (EType)(d->Type & ~DDef.SaveGlobal);
                defs.Add((Encoding.Latin1.GetString(progs.String(d->SName)), t));
                if (t == EType.String && d->Ofs < fields)
                    stringWord[d->Ofs] = true;
            }
            var functions = Enumerable.Range(1, progs.NumFunctions - 1)
                .Select(i => Encoding.Latin1.GetString(progs.String(progs.Functions[i].SName))).Where(n => n.Length > 0).ToArray();
            var rng = new VmOpcodeFuzz.Rng(17);
            byte* rs = stackalloc byte[1100];
            int parsed = 0, empty = 0;
            for (int n = 0; n < 20_000; n++)
            {
                var text = new StringBuilder();
                int pairs = rng.Below(12) == 0 ? 0 : 1 + rng.Below(10);
                for (int k = 0; k < pairs; k++)
                {
                    string key;
                    EType type;
                    switch (rng.Below(12))
                    {
                        case 0: key = "angle"; type = EType.Float; break;
                        case 1: key = "light"; type = EType.Float; break;
                        case 2: key = "_note"; type = EType.String; break;
                        case 3: key = "nosuchfield"; type = EType.String; break;
                        default:
                            (key, type) = defs[rng.Below(defs.Count)];
                            if (rng.Below(4) == 0)
                                key += rng.Below(2) == 0 ? " " : "  ";
                            break;
                    }
                    string value = type switch
                    {
                        EType.Float => FloatText(ref rng),
                        EType.Vector => $"{FloatText(ref rng)} {FloatText(ref rng)} {FloatText(ref rng)}",
                        EType.Entity => $"{rng.Below(Edicts)}",
                        EType.Function => functions[rng.Below(functions.Length)],
                        EType.Field => defs[rng.Below(defs.Count)].Name,
                        _ => new string([.. Enumerable.Range(0, rng.Below(16)).Select(_ => "ab\\n x/{}"[rng.Below(9)])]),
                    };
                    // C stops the process on a value that begins with '}' (quoted or not: "closing brace without data")
                    if ((key == "angle" && value.Length >= 32) || value.Contains('"') || value.StartsWith('}'))
                        value = "0";
                    text.Append(rng.Below(3) == 0 ? "\n" : " ").Append('"').Append(key).Append("\" \"").Append(value).Append('"');
                }
                text.Append("\n}\n");
                byte[] data = [.. Encoding.ASCII.GetBytes(text.ToString()), 0];
                int ent = rng.Below(4) == 0 ? 0 : 1 + rng.Below(Edicts - 1);
                int refRead;
                byte* portEnd;
                try
                {
                    fixed (byte* d = data)
                        portEnd = sv.ParseEdict(d, ent);
                }
                catch (SysErrorException e)
                {
                    throw new InvalidOperationException($"entity {n}: the port stopped: {e.Message}\n{text}");
                }
                fixed (byte* d = data)
                {
                    refRead = ParseEdictFn(d, refEdicts, Edicts, ent);
                    Assert.True(portEnd - d == refRead, $"entity {n}: port read {portEnd - d} bytes, reference {refRead}");
                }
                int free = 0;
                float freetime = 0;
                HeaderFn(refEdicts, fields, ent, 0, &free, &freetime);
                Assert.True(sv.EdictNum(ent)->Free == free, $"entity {n} ({text}): free port {sv.EdictNum(ent)->Free}, reference {free}");
                int* rw = (int*)(refEdicts + ent * stride + header), pw = (int*)sv.Fields(ent);
                for (int w = 0; w < fields; w++)
                {
                    if (stringWord[w] && (rw[w] != 0 || pw[w] != 0))
                    {
                        StringFn(rw[w], rs, 1100);
                        var a = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(sv.String(pw[w]));
                        var b = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(rs);
                        Assert.True(a.SequenceEqual(b), $"entity {n} field {w}: port \"{Encoding.Latin1.GetString(a)}\", reference \"{Encoding.Latin1.GetString(b)}\"\n{text}");
                    }
                    else
                        Assert.True(rw[w] == pw[w], $"entity {n} field {w}: port {VmOpcodeFuzz.Hex((uint)pw[w])}, reference {VmOpcodeFuzz.Hex((uint)rw[w])}\n{text}");
                }
                parsed++;
                empty += pairs == 0 ? 1 : 0;
            }
            TestContext.Current.TestOutputHelper?.WriteLine($"{parsed} entities compared, {empty} empty");
        }
        finally
        {
            ProgsResetFn();     // the strings ED_ParseEdict allocated (the world keeps its fields across entities)
            hunk.Dispose();
            NativeMemory.Free(globals);
            NativeMemory.Free(refEdicts);
            NativeMemory.Free(portEdicts);
            NativeMemory.Free(temp);
        }
    }
}
