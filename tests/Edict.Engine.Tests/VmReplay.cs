// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>A difference between the port and the reference's capture.</summary>
public sealed class ReplayDivergence(string message) : Exception(message);

/// <summary>
/// Replays a VM capture (ref/src/edict_capture.c, version 3) through the port (cut 2, steps 2 and 3): the engine's
/// changes come from the capture and are applied; the port's VM runs; the builtins the port has
/// (<see cref="Builtins.Has"/>) run too, the others are replayed from the capture. At every boundary the words that
/// changed must be exactly the ones that changed in the reference: up to a builtin call and up to the return to the
/// engine (the VM's work), and from a builtin call to its return (the builtin's work, for ported builtins). For
/// ported builtins the rand seed afterwards and the text of strings they return are checked as well. Also checked:
/// the order of QuakeC calls, builtin numbers and argument counts, and that every engine string read was recorded.
/// Two NaN words count as equal (docs/adr/D8); the opcode fuzz checks that copies keep NaN bits.
/// </summary>
public sealed unsafe class VmReplay : IDisposable
{
    public const int NumBuiltins = 79;      // pr_numbuiltins: pr_builtin[] compiled without QUAKE2
    private const int Version = 3;

    private readonly byte[] _d;
    private int _p;
    private readonly string _file;
    private readonly QFileSystem _files;
    private readonly Func<Progs, nint, nint, int, IVm> _makeVm;
    private readonly Func<int, bool> _ported;
    private readonly Action<Server, int> _call;

    // per map
    private string _map = "";
    private Progs? _progs;
    private IVm? _vm;
    private Server? _sv;
    private BrushModelSet? _world;
    private int* _globals;
    private byte* _edicts;
    private byte* _stringTemp;
    private readonly List<nint> _ownStrings = [];
    private int[] _shadow = [];
    private int _numGlobals, _entityFields, _header, _stride, _numStrings;
    private readonly ReplayStrings _strings = new();
    private readonly Dictionary<int, string> _names = [];
    private readonly Stack<string> _where = new();

    public long Entries { get; private set; }
    public long BuiltinCalls { get; private set; }
    public long PortedBuiltinCalls { get; private set; }
    public long QcCalls { get; private set; }
    public long WordsChecked { get; private set; }

    /// <summary>The VM under test: <see cref="QcVm"/>, or a known-bad copy of it.</summary>
    public interface IVm
    {
        int Argc { get; }
        void Execute(VmReplay replay, int fnum);
    }

    private sealed class PortVm(QcVm vm) : IVm
    {
        public int Argc => vm.Argc;
        public void Execute(VmReplay replay, int fnum)
        {
            var host = new Host(replay);
            vm.Execute(ref host, fnum);
        }
    }

    /// <summary>Engine strings as the capture recorded them.</summary>
    private sealed class ReplayStrings : IEngineStrings
    {
        private readonly Dictionary<int, byte[]> _texts = [];
        public Func<int, Exception>? Missing;

        public void Clear() => _texts.Clear();

        public void Set(int ofs, ReadOnlySpan<byte> text)
        {
            byte[] s = GC.AllocateArray<byte>(text.Length + 1, pinned: true);
            text.CopyTo(s);
            _texts[ofs] = s;
        }

        public byte* Get(int ofs) =>
            _texts.TryGetValue(ofs, out byte[]? s) ? (byte*)Unsafe.AsPointer(ref s[0]) : throw Missing!(ofs);
    }

    /// <param name="ported">Which builtins run as the port's code; default: all the port has.</param>
    /// <param name="call">How a ported builtin runs; default <see cref="Builtins.Call"/> (known-bad variants replace it).</param>
    public VmReplay(string file, QFileSystem files, Func<Progs, nint, nint, int, IVm>? makeVm = null, Func<int, bool>? ported = null,
                    Action<Server, int>? call = null)
    {
        _call = call ?? Builtins.Call;
        _makeVm = makeVm ?? ((p, g, e, h) => new PortVm(_sv!.Vm));
        _ported = ported ?? Builtins.Has;
        _file = file;
        _d = File.ReadAllBytes(file);
        _files = files;
        if (!_d.AsSpan(0, 4).SequenceEqual("ECP1"u8) || U32At(4) != Version)
            throw new InvalidDataException($"{file}: not an ECP1 version {Version} capture (run tools/captures.sh)");
        _p = 8;
        _strings.Missing = ofs => Fail($"engine string at offset {ofs} is read, but the capture did not record it");
    }

    private uint U32At(int p) => BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p, 4));
    private byte U8() => _d[_p++];
    private ushort U16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(_p, 2)); _p += 2; return v; }
    private int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(_p, 4)); _p += 4; return v; }
    private double F64() { double v = BinaryPrimitives.ReadDoubleLittleEndian(_d.AsSpan(_p, 8)); _p += 8; return v; }

    private ReadOnlySpan<byte> Bytes()
    {
        int e = Array.IndexOf(_d, (byte)0, _p);
        var s = new ReadOnlySpan<byte>(_d, _p, e - _p);
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
        _names[fnum] = Encoding.Latin1.GetString(Bytes());
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
        _world?.Dispose();
        _world = null;
        foreach (nint p in _ownStrings)
            NativeMemory.Free((void*)p);
        _ownStrings.Clear();
        if (_globals is not null)
            NativeMemory.Free(_globals);
        if (_edicts is not null)
            NativeMemory.Free(_edicts);
        if (_stringTemp is not null)
            NativeMemory.Free(_stringTemp);
        _globals = null;
        _edicts = null;
        _stringTemp = null;
    }

    private byte* OwnString(ReadOnlySpan<byte> s)
    {
        byte* p = (byte*)NativeMemory.AllocZeroed((nuint)s.Length + 1);
        s.CopyTo(new Span<byte>(p, s.Length));
        _ownStrings.Add((nint)p);
        return p;
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
        int stringTempOffset = I32();
        int numCvars = I32();
        var cvars = new List<(byte[], byte[])>();
        for (int i = 0; i < numCvars; i++)
        {
            byte[] name = Bytes().ToArray();
            cvars.Add((name, Bytes().ToArray()));
        }

        FreeMap();
        QFile f = _files.Find("progs.dat") ?? throw new FileNotFoundException("progs.dat");
        _progs = new Progs(f);
        if (_progs.FileCrc != crc || _progs.NumGlobals != _numGlobals || _progs.EntityFields != _entityFields
            || _progs.NumStrings != _numStrings)
            throw Fail($"progs.dat differs from the capture's (crc {_progs.FileCrc} vs {crc})");

        _stride = _header + _entityFields * 4;
        _globals = (int*)NativeMemory.AllocZeroed((nuint)_numGlobals, 4);
        _edicts = (byte*)NativeMemory.AllocZeroed((nuint)maxEdicts, (nuint)_stride);
        _stringTemp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
        _shadow = new int[_numGlobals + maxEdicts * _entityFields];
        _strings.Clear();
        _names.Clear();

        var qcvm = new QcVm(_progs, _globals, _edicts, _header, NumBuiltins);
        _sv = new Server(qcvm, _progs, _globals, _edicts, _header, new Cvars(cvars), _strings, _stringTemp, stringTempOffset);

        // SV_SpawnServer's precache entries: "" first, then the world and its inline models
        string worldName = $"maps/{_map}.bsp";
        byte[] wn = Encoding.Latin1.GetBytes(worldName);
        _world = BrushLoader.Load(_files.Find(wn) ?? throw new FileNotFoundException(worldName), wn);
        var models = new nint[_world.Count];
        models[0] = (nint)OwnString(wn);
        for (int i = 1; i < _world.Count; i++)
            models[i] = (nint)OwnString(Encoding.Latin1.GetBytes($"*{i}"));
        _sv.InitPrecache(_progs.Strings, models);

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

    /// <summary>The engine's or a replayed builtin's changes: applied to memory and shadow; strings recorded.</summary>
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
        int m = I32();
        for (int i = 0; i < m; i++)
        {
            int ofs = I32();
            ReadOnlySpan<byte> text = Bytes();
            if (ofs != _sv!.StringTempOffset)
                _strings.Set(ofs, text);
            else                            // pr_string_temp, written by a replayed ftos or vtos
            {
                Span<byte> temp = _sv.StringTemp;
                text[..Math.Min(text.Length, Server.StringTempSize - 1)].CopyTo(temp);
                temp[Math.Min(text.Length, Server.StringTempSize - 1)] = 0;
            }
        }
    }

    /// <summary>
    /// The port's changes since the last boundary must be the recorded ones, word for word, and every recorded string
    /// text must be the text the port has at that offset.
    /// </summary>
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
            throw Fail($"{boundary}: the reference changed {WordName((int)U32At(_p + 8 * r))} beyond num_edicts {numEdicts}");
        WordsChecked += _numGlobals + (long)numEdicts * _entityFields;
        _p += 8 * n;
        int m = I32();
        for (int i = 0; i < m; i++)
        {
            int ofs = I32();
            ReadOnlySpan<byte> text = Bytes();
            var port = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(_sv!.String(ofs));
            if (!port.SequenceEqual(text))
                throw Fail($"{boundary}: string at offset {ofs} is \"{Encoding.Latin1.GetString(port)}\", the reference's is \"{Encoding.Latin1.GetString(text)}\"");
        }
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
            // NaN words compare as a class: which NaN arithmetic produces is not part of the state (docs/adr/D8)
            if (k == recorded)
            {
                int v = (int)U32At(_p + 8 * r + 4);
                if (!VmOpcodeFuzz.Same((uint)now[k], (uint)v, nanClass: true))
                    throw Fail($"{boundary}: {WordName(first + k)} is {Value(now[k])}, the reference wrote {Value(v)} (before: {Value(before[k])})");
                r++;
            }
            else if (!VmOpcodeFuzz.Same((uint)now[k], (uint)before[k], nanClass: true))
                throw Fail($"{boundary}: {WordName(first + k)} changed to {Value(now[k])} (before: {Value(before[k])}); the reference did not change it");
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
        uint seed = (uint)I32();
        double time = F64();
        Apply();
        Server sv = _sv!;
        sv.Rand.Srand(seed);                // the engine's draws since the last boundary
        sv.Time = time;
        bool wasActive = sv.Active;
        sv.Active = state == 1;             // ss_active
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
        sv.Active = wasActive;
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
        string name = $"builtin #{num} ({FunctionName(fnum)})";
        Check($"call of {name}");

        bool ported = _ported(num);
        if (ported)
        {
            PortedBuiltinCalls++;
            _call(_sv!, num);
        }
        while (true)
        {
            k = Next();
            if (k == (byte)'E')
            {
                if (ported)
                    throw Fail($"{name} runs QuakeC in the reference, the port's did not");
                Enter();                    // QuakeC run by a replayed builtin (touch, think)
                continue;
            }
            if (k != (byte)'R' || U16() != num)
                throw Fail($"{name}: expected its return record, found '{(char)k}'");
            _p += 12;                       // return[3]: in the diff
            uint seed = (uint)I32();
            double time = F64();
            if (!ported)
            {
                Apply();
                _sv!.Rand.Srand(seed);      // a replayed builtin's draws
                return;
            }
            if (_sv!.Rand.Seed != seed)
                throw Fail($"after {name}: rand seed 0x{_sv.Rand.Seed:X8}, the reference's 0x{seed:X8}");
            if (BitConverter.DoubleToInt64Bits(_sv.Time) != BitConverter.DoubleToInt64Bits(time))
                throw Fail($"after {name}: sv.time {_sv.Time:R}, the reference's {time:R}");
            Check($"return of {name}");
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

    public readonly struct Host(VmReplay r) : IProgsHost
    {
        public void Builtin(int num) => r.Builtin(num);
        public byte* String(int ofs) => r._sv!.String(ofs);
        public bool Active => r._sv!.Active;
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
