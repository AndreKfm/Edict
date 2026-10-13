// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>A difference between the port's VM and the reference's capture.</summary>
public sealed class ReplayDivergence(string message) : Exception(message);

/// <summary>
/// Replays a VM capture (ref/src/edict_capture.c, version 2) through the port's <see cref="QcVm"/> (cut 2, step 2):
/// the engine's and the builtins' changes come from the capture and are applied; the VM runs; at every builtin call
/// and every return to the engine, the words the VM changed must be exactly the words the reference's VM changed.
/// The builtins themselves do not run. Also checked: the order of QuakeC calls, builtin numbers and argument
/// counts, and that every engine string the VM reads was recorded.
/// </summary>
public sealed unsafe class VmReplay : IDisposable
{
    public const int NumBuiltins = 79;      // pr_numbuiltins: pr_builtin[] compiled without QUAKE2

    private readonly byte[] _d;
    private int _p;
    private readonly string _file;
    private readonly QFileSystem _files;

    // per map
    private string _map = "";
    private Progs? _progs;
    private IVm? _vm;
    private readonly Func<Progs, nint, nint, int, IVm> _makeVm;
    private int* _globals;
    private byte* _edicts;
    private int[] _shadow = [];
    private int _numGlobals, _entityFields, _header, _stride, _numStrings;
    private bool _active;
    private readonly Dictionary<int, byte[]> _engineStrings = [];
    private readonly Dictionary<int, string> _names = [];
    private readonly Stack<string> _where = new();

    public long Entries { get; private set; }
    public long BuiltinCalls { get; private set; }
    public long QcCalls { get; private set; }
    public long WordsChecked { get; private set; }

    /// <summary>The VM under test: <see cref="QcVm"/>, or a known-bad copy of it.</summary>
    public interface IVm
    {
        int Argc { get; }
        void Execute(VmReplay replay, int fnum);
    }

    private sealed class PortVm(Progs progs, nint globals, nint edicts, int header) : IVm
    {
        private readonly QcVm _vm = new(progs, (int*)globals, (byte*)edicts, header, NumBuiltins);
        public int Argc => _vm.Argc;
        public void Execute(VmReplay replay, int fnum)
        {
            var host = new Host(replay);
            _vm.Execute(ref host, fnum);
        }
    }

    public VmReplay(string file, QFileSystem files, Func<Progs, nint, nint, int, IVm>? makeVm = null)
    {
        _makeVm = makeVm ?? ((p, g, e, h) => new PortVm(p, g, e, h));
        _file = file;
        _d = File.ReadAllBytes(file);
        _files = files;
        if (!_d.AsSpan(0, 4).SequenceEqual("ECP1"u8) || U32At(4) != 2)
            throw new InvalidDataException($"{file}: not an ECP1 version 2 capture");
        _p = 8;
    }

    private uint U32At(int p) => BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p, 4));
    private byte U8() => _d[_p++];
    private ushort U16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(_p, 2)); _p += 2; return v; }
    private int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(_p, 4)); _p += 4; return v; }

    private string CString()
    {
        int e = Array.IndexOf(_d, (byte)0, _p);
        string s = Encoding.Latin1.GetString(_d, _p, e - _p);
        _p = e + 1;
        return s;
    }

    private ReplayDivergence Fail(string what) =>
        new($"{_map} ({Path.GetFileName(_file)} at byte {_p}), in {string.Join(" < ", _where)}: {what}");

    /// <summary>Replays the whole file; throws <see cref="ReplayDivergence"/> on the first difference.</summary>
    public void Run()
    {
        while (_p < _d.Length)
        {
            byte k = U8();
            switch (k)
            {
                case (byte)'M': NewMap(); break;
                case (byte)'F': Name(); break;
                case (byte)'E': Enter(); break;
                default: throw Fail($"record '{(char)k}' at the top level");
            }
        }
    }

    private void Name()
    {
        int fnum = I32();
        _names[fnum] = CString();
    }

    private string FunctionName(int fnum) =>
        _names.TryGetValue(fnum, out string? n) && n.Length > 0 ? n
        : _progs is not null && fnum > 0 && fnum < _progs.NumFunctions ? Encoding.Latin1.GetString(_progs.String(_progs.Functions[fnum].SName))
        : $"#{fnum}";

    /// <summary>The next record kind, after any 'F' records.</summary>
    private byte Next()
    {
        while (true)
        {
            byte k = U8();
            if (k != (byte)'F')
                return k;
            Name();
        }
    }

    private void FreeMap()
    {
        _progs?.Dispose();
        _progs = null;
        if (_globals is not null)
            NativeMemory.Free(_globals);
        if (_edicts is not null)
            NativeMemory.Free(_edicts);
        _globals = null;
        _edicts = null;
    }

    private void NewMap()
    {
        _map = Encoding.Latin1.GetString(_d, _p, 64).TrimEnd('\0');
        _p += 64;
        int crc = I32();
        _numGlobals = I32();
        _entityFields = I32();
        _header = I32();
        int maxEdicts = I32();
        _numStrings = I32();

        FreeMap();
        QFile f = _files.Find("progs.dat") ?? throw new FileNotFoundException("progs.dat");
        _progs = new Progs(f);
        if (_progs.FileCrc != crc || _progs.NumGlobals != _numGlobals || _progs.EntityFields != _entityFields
            || _progs.NumStrings != _numStrings)
            throw Fail($"progs.dat differs from the capture's (crc {_progs.FileCrc} vs {crc})");

        _stride = _header + _entityFields * 4;
        _globals = (int*)NativeMemory.AllocZeroed((nuint)_numGlobals, 4);
        _edicts = (byte*)NativeMemory.AllocZeroed((nuint)maxEdicts, (nuint)_stride);
        _shadow = new int[_numGlobals + maxEdicts * _entityFields];
        _engineStrings.Clear();
        _names.Clear();
        _vm = _makeVm(_progs, (nint)_globals, (nint)_edicts, _header);
    }

    private ref int Word(int w)
    {
        if (w < _numGlobals)
            return ref _globals[w];
        int e = (w - _numGlobals) / _entityFields, f = (w - _numGlobals) % _entityFields;
        return ref *(int*)(_edicts + (long)e * _stride + _header + 4 * f);
    }

    private string WordName(int w)
    {
        static string Def(Progs p, DDef* defs, int n, int ofs)
        {
            for (int i = 0; i < n; i++)
            {
                int t = defs[i].Type & ~DDef.SaveGlobal, o = defs[i].Ofs;
                if (o == ofs || (t == (int)EType.Vector && ofs > o && ofs < o + 3))
                    return Encoding.Latin1.GetString(p.String(defs[i].SName)) + (o == ofs ? "" : $"[{ofs - o}]");
            }
            return $"word {ofs}";
        }
        Progs pr = _progs!;
        if (w < _numGlobals)
            return "global " + Def(pr, pr.GlobalDefs, pr.Header->NumGlobalDefs, w);
        int e = (w - _numGlobals) / _entityFields, f = (w - _numGlobals) % _entityFields;
        return $"edict {e}." + Def(pr, pr.FieldDefs, pr.Header->NumFieldDefs, f);
    }

    private static string Value(int v) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{v:X8} (int {v}, float {BitConverter.Int32BitsToSingle(v):R})");

    /// <summary>The engine's or a builtin's changes: applied to memory and shadow.</summary>
    private void Apply()
    {
        I32();                              // num_edicts: the memory is allocated for max_edicts
        int n = I32();
        for (int i = 0; i < n; i++)
        {
            int w = I32(), v = I32();
            Word(w) = v;
            _shadow[w] = v;
        }
        Strings();
    }

    private void Strings()
    {
        int m = I32();
        for (int i = 0; i < m; i++)
        {
            int ofs = I32();
            int e = Array.IndexOf(_d, (byte)0, _p);
            byte[] s = GC.AllocateArray<byte>(e - _p + 1, pinned: true);
            _d.AsSpan(_p, e - _p).CopyTo(s);
            _engineStrings[ofs] = s;
            _p = e + 1;
        }
    }

    /// <summary>The VM's changes since the last boundary must be the recorded ones, word for word.</summary>
    private void Check(string boundary)
    {
        int numEdicts = I32();
        int n = I32();
        int r = 0;                          // next recorded change
        CheckSegment(0, new Span<int>(_globals, _numGlobals), boundary, ref r, n);
        for (int e = 0; e < numEdicts; e++)
            CheckSegment(_numGlobals + e * _entityFields,
                         new Span<int>(_edicts + (long)e * _stride + _header, _entityFields), boundary, ref r, n);
        if (r != n)
            throw Fail($"{boundary}: the reference's VM changed {WordName((int)U32At(_p + 8 * r))} beyond num_edicts {numEdicts}");
        WordsChecked += _numGlobals + (long)numEdicts * _entityFields;
        _p += 8 * n;
        Strings();
    }

    // One contiguous run of words (the globals, or one edict's fields) starting at word index first. Unchanged
    // stretches are skipped with a vectorized prefix comparison against the shadow.
    private void CheckSegment(int first, Span<int> now, string boundary, ref int r, int n)
    {
        Span<int> before = _shadow.AsSpan(first, now.Length);
        int pos = 0;
        while (pos < now.Length)
        {
            int mismatch = pos + now[pos..].CommonPrefixLength(before[pos..]);
            int recorded = r < n ? (int)U32At(_p + 8 * r) - first : int.MaxValue;
            int k = Math.Min(mismatch, recorded);
            if (k >= now.Length)
                return;
            if (k == recorded)
            {
                int v = (int)U32At(_p + 8 * r + 4);
                if (now[k] != v)
                    throw Fail($"{boundary}: {WordName(first + k)} is {Value(now[k])}, the reference's VM wrote {Value(v)} (before: {Value(before[k])})");
                r++;
            }
            else
                throw Fail($"{boundary}: {WordName(first + k)} changed to {Value(now[k])} (before: {Value(before[k])}); the reference's VM did not change it");
            before[k] = now[k];
            pos = k + 1;
        }
    }

    private void Enter()
    {
        int fnum = I32();
        I32();                              // host_framecount
        byte state = U8();
        I32(); I32();                       // self, other: in the diff too
        Apply();
        bool wasActive = _active;
        _active = state == 1;               // ss_active
        Entries++;
        _where.Push($"{FunctionName(fnum)}");
        _vm!.Execute(this, fnum);
        if (Next() != (byte)'L')
            throw Fail($"the reference returned from {FunctionName(fnum)} later than the port");
        int left = I32();
        if (left != fnum)
            throw Fail($"return from {FunctionName(left)} recorded, port returned from {FunctionName(fnum)}");
        _p += 12;                           // return[3]: in the diff
        Check($"return from {FunctionName(fnum)}");
        _where.Pop();
        _active = wasActive;
    }

    private void Builtin(int num)
    {
        byte k = Next();
        if (k != (byte)'B')
            throw Fail($"port calls builtin #{num}, the reference's next record is '{(char)k}'");
        int recNum = U16();
        int fnum = I32();
        int argc = U8();
        if (recNum != num || argc != _vm!.Argc)
            throw Fail($"port calls builtin #{num} with {_vm!.Argc} arguments, the reference #{recNum} with {argc}");
        _p += 12 * argc;                    // parms: in the diff
        BuiltinCalls++;
        Check($"call of builtin #{num} ({FunctionName(fnum)})");
        while (true)
        {
            k = Next();
            if (k == (byte)'E')
            {
                Enter();                    // QuakeC run by the builtin (touch, think)
                continue;
            }
            if (k != (byte)'R' || U16() != num)
                throw Fail($"builtin #{num}: expected its return record, found '{(char)k}'");
            _p += 12;                       // return[3]: in the diff
            Apply();
            return;
        }
    }

    private void Called(int fnum)
    {
        byte k = Next();
        if (k != (byte)'C')
            throw Fail($"port calls {FunctionName(fnum)}, the reference's next record is '{(char)k}'");
        int rec = I32();
        if (rec != fnum)
            throw Fail($"port calls {FunctionName(fnum)}, the reference called {FunctionName(rec)}");
        QcCalls++;
    }

    private byte* String(int ofs)
    {
        if (ofs >= 0 && ofs < _numStrings)
            return _progs!.Strings + ofs;
        if (_engineStrings.TryGetValue(ofs, out byte[]? s))
            return (byte*)Unsafe.AsPointer(ref s[0]);
        throw Fail($"the VM reads engine string at offset {ofs}, which the capture did not record");
    }

    public readonly struct Host(VmReplay r) : IProgsHost
    {
        public void Builtin(int num) => r.Builtin(num);
        public byte* String(int ofs) => r.String(ofs);
        public bool Active => r._active;
        public void Called(int fnum) => r.Called(fnum);
    }

    public void Dispose() => FreeMap();

    /// <summary>The edict header size recorded in the first 'M' record of a capture.</summary>
    public static int HeaderBytesOf(string file)
    {
        byte[] d = File.ReadAllBytes(file);
        if (d[8] != (byte)'M')
            throw new InvalidDataException($"{file}: no map record first");
        return BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(8 + 1 + 64 + 12, 4));
    }

    /// <summary>The capture directory: EDICT_CAPTURES, default data/captures in the checkout.</summary>
    public static string Directory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_CAPTURES");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(TraceCorpus.Directory)!, "captures") : dir;
        }
    }

    public static IEnumerable<string> Maps()
    {
        string dir = Directory;
        string[] files = System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.ecp") : [];
        if (files.Length == 0)
            throw new InvalidOperationException($"no .ecp files in {dir}: run tools/captures.sh (docs/TESTING.md)");
        return files.Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal);
    }
}
