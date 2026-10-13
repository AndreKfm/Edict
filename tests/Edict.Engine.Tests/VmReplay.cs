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
/// Replays a VM capture (ref/src/edict_capture.c, version 6) through the port (cut 2, steps 2 and 3): the engine's
/// changes come from the capture and are applied; the port's VM runs; the builtins the port has
/// (<see cref="Builtins.Has"/>) run too, the others are replayed from the capture. At every boundary the words that
/// changed must be exactly the ones that changed in the reference: up to a builtin call and up to the return to the
/// engine (the VM's work), and from a builtin call to its return (the builtin's work, for ported builtins). For
/// ported builtins the rand seed afterwards and the text of strings they return are checked as well. Also checked:
/// the order of QuakeC calls, builtin numbers and argument counts, and that every engine string read was recorded.
/// Two NaN words count as equal (docs/adr/D8); the opcode fuzz checks that copies keep NaN bits.
/// Linking (step 3c): a link the reference made from code the port does not have yet is made by the port at the same
/// point ('K'), and what follows from it is checked like the port's own work: the touch functions it runs, the leafs
/// and the area node ('k'), the fields it sets. Links the port's own code makes must come in the reference's order.
/// </summary>
public sealed unsafe class VmReplay : IDisposable
{
    public const int NumBuiltins = 79;      // pr_numbuiltins: pr_builtin[] compiled without QUAKE2
    private const int Version = 8;

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
    private uint[] _shadowHeader = [];             // free, freetime bits per edict
    private int _numGlobals, _entityFields, _header, _stride, _numStrings;
    private readonly ReplayStrings _strings = new();
    private readonly Dictionary<int, string> _names = [];
    private readonly Stack<string> _where = new();
    private readonly List<BrushModelSet> _otherModels = [];
    private readonly Dictionary<string, nint> _modelsByName = [];
    private readonly List<nint> _ownModels = [];               // alias and sprite models, allocated here
    private bool _drivingLink, _drivingUnlink;  // the next Link/Unlink hook is the one the replay itself started
    private int _drivenDepth;                   // inside a link the replay made for code the port does not have
    public long Links { get; private set; }

    // the reference's hashes of the same run (.edh next to the capture): the spawn records are checked at 'S'
    private readonly EdhFile _edh;
    private int _spawns;
    private StateHash? _hash;
    private readonly List<byte[]> _styles = [];

    /// <summary>Spawn points whose state hashed as the reference's (edicts and globals lanes, every entity).</summary>
    public int SpawnsHashed { get; private set; }

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
        _edh = new EdhFile(Path.ChangeExtension(file, ".edh"));
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
                case (byte)'K': DriveLink(); break;
                case (byte)'U': DriveUnlink(); break;
                case (byte)'S': SpawnPoint(); break;
                case (byte)'H': ServerFrame(); break;
                default: throw Fail($"record '{(char)k}' at the top level");
            }
        }
    }

    /// <summary>
    /// The frame-0 point after ED_LoadFromFile: the engine's last changes are applied, then the state must hash as the
    /// reference's spawn record (lanes and every entity). Checks the port's state hash (Edict.Harness) on the
    /// reference's state, before the port spawns a map itself.
    /// </summary>
    private void SpawnPoint()
    {
        Server sv = _sv!;
        uint seed = (uint)I32();
        double time = F64();
        int serverflags = I32();
        _styles.Clear();
        for (int i = 0; i < Server.MaxLightstyles; i++)
        {
            if (U8() == 0)
            {
                sv.SetLightstyle(i, null);
                continue;
            }
            ReadOnlySpan<byte> text = Bytes();
            byte[] s = GC.AllocateArray<byte>(text.Length + 1, pinned: true);
            text.CopyTo(s);
            _styles.Add(s);
            sv.SetLightstyle(i, (byte*)Unsafe.AsPointer(ref s[0]));
        }
        Apply();
        sv.Rand.Srand(seed);
        sv.Time = time;
        sv.ServerFlags = serverflags;

        if (_spawns >= _edh.Spawns.Count)
            throw Fail("a spawn point the .edh has no spawn record for");
        EdhFile.Spawn r = _edh.Spawns[_spawns++];
        if (r.Map != _map)
            throw Fail($"spawn record {_spawns - 1} of the .edh is for {r.Map}");
        _hash ??= new StateHash(_progs!);
        bool active = sv.Active;
        sv.Active = false;                  // ss_loading: the point is before sv.state = ss_active
        StateHash.Lanes l = _hash.Of(sv);
        sv.Active = active;
        if (sv.NumEdicts != r.NumEdicts)
            throw Fail($"spawn point: num_edicts {sv.NumEdicts}, the reference's {r.NumEdicts}");
        for (int e = 0; e < r.NumEdicts; e++)
            if (l.Entities[e] != r.Entities[e])
                throw Fail($"spawn point: edict {e} hashes as {l.Entities[e]:x16}, the reference's as {r.Entities[e]:x16}");
        if (l.GlobalsTyped != r.GlobalsTyped)
            throw Fail($"spawn point: typed globals lane {l.GlobalsTyped:x16}, the reference's {r.GlobalsTyped:x16}");
        if (l.GlobalsRaw != r.GlobalsRaw)
            throw Fail($"spawn point: raw globals lane {l.GlobalsRaw:x16}, the reference's {r.GlobalsRaw:x16}");
        if (l.Edicts != r.Edicts)
            throw Fail($"spawn point: edicts lane {l.Edicts:x16}, the reference's {r.Edicts:x16} (every entity matches)");
        SpawnsHashed++;
    }

    /// <summary>host_frametime of the current server frame (from its 'H' record).</summary>
    public double FrameTime { get; private set; }

    /// <summary>Server frames the capture recorded ('H').</summary>
    public int ServerFrames { get; private set; }

    /// <summary>
    /// Host_ServerFrame begins: the engine's changes since the last record (commands, SV_SpawnServer's own frames)
    /// are applied, as at an engine entry.
    /// </summary>
    private void ServerFrame()
    {
        I32();                              // host_framecount
        FrameTime = F64();
        uint seed = (uint)I32();
        double time = F64();
        Apply();
        _sv!.Rand.Srand(seed);
        _sv.Time = time;
        ServerFrames++;
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

    /// <summary>What a map's first spawn starts from, as the capture recorded it.</summary>
    public sealed record SpawnInputs(string Map, int HeaderBytes, int MaxClients, List<(byte[] Name, byte[] Value)> Cvars,
                                     uint RandSeed);

    /// <summary>
    /// The first map record of <paramref name="file"/> (header size, maxclients, cvars) and the rand seed at its first
    /// engine entry (the world's spawn function: nothing draws from rand before it in SV_SpawnServer).
    /// </summary>
    public static SpawnInputs FirstSpawn(string file)
    {
        byte[] d = File.ReadAllBytes(file);
        int p = 8;
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at, 4));
        byte[] Z()
        {
            int e = Array.IndexOf(d, (byte)0, p);
            byte[] s = d[p..e];
            p = e + 1;
            return s;
        }
        if (d[p++] != (byte)'M')
            throw new InvalidDataException($"{file}: no map record first");
        string map = Encoding.Latin1.GetString(d, p, 64).TrimEnd('\0');
        p += 64;
        int header = I(p + 12), maxClients = I(p + 28), numCvars = I(p + 32);
        p += 36;
        var cvars = new List<(byte[], byte[])>();
        for (int i = 0; i < numCvars; i++)
            cvars.Add((Z(), Z()));
        while (d[p] == (byte)'F')
        {
            p += 5;
            Z();
        }
        if (d[p] != (byte)'E')
            throw new InvalidDataException($"{file}: record '{(char)d[p]}' before the first engine entry");
        uint seed = (uint)I(p + 1 + 17);   // fnum, framecount, state, self, other, then rand_seed
        return new SpawnInputs(map, header, maxClients, cvars, seed);
    }

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
        foreach (BrushModelSet m in _otherModels)
            m.Dispose();
        _otherModels.Clear();
        foreach (nint m in _ownModels)
            NativeMemory.Free((void*)m);
        _ownModels.Clear();
        _modelsByName.Clear();
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
        int maxClients = I32();
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
        _hash = null;
        if (_progs.FileCrc != crc || _progs.NumGlobals != _numGlobals || _progs.EntityFields != _entityFields
            || _progs.NumStrings != _numStrings)
            throw Fail($"progs.dat differs from the capture's (crc {_progs.FileCrc} vs {crc})");

        _stride = _header + _entityFields * 4;
        _globals = (int*)NativeMemory.AllocZeroed((nuint)_numGlobals, 4);
        _edicts = (byte*)NativeMemory.AllocZeroed((nuint)maxEdicts, (nuint)_stride);
        _stringTemp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
        _shadow = new int[_numGlobals + maxEdicts * _entityFields];
        _shadowHeader = new uint[2 * maxEdicts];
        _strings.Clear();
        _names.Clear();

        var qcvm = new QcVm(_progs, _globals, _edicts, _header, NumBuiltins);
        _sv = new Server(qcvm, _progs, _globals, _edicts, _header, new Cvars(cvars), _strings, _stringTemp, stringTempOffset)
        {
            MaxEdicts = maxEdicts,
            MaxClients = maxClients,
        };

        // SV_SpawnServer's precache entries: "" first, then the world and its inline models
        string worldName = $"maps/{_map}.bsp";
        byte[] wn = Encoding.Latin1.GetBytes(worldName);
        _world = BrushLoader.Load(_files.Find(wn) ?? throw new FileNotFoundException(worldName), wn);
        var names = new nint[_world.Count];
        var models = new nint[_world.Count];
        names[0] = (nint)OwnString(wn);
        models[0] = (nint)_world.World;
        _modelsByName[worldName] = (nint)_world.World;
        for (int i = 1; i < _world.Count; i++)
        {
            names[i] = (nint)OwnString(Encoding.Latin1.GetBytes($"*{i}"));
            models[i] = (nint)(_world.Models + i);
            _modelsByName[$"*{i}"] = models[i];
        }
        _sv.InitPrecache(_progs.Strings, names, models);
        _sv.ModelSource = new Models(this);
        _sv.Hooks = new Hooks(this);
        _sv.ClearWorld(_world.World);

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
        _sv!.NumEdicts = I32();
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
        int h = I32();
        for (int i = 0; i < h; i++)
        {
            int e = I32();
            uint free = (uint)I32(), freetime = (uint)I32();
            EdictHeader* ed = _sv.EdictNum(e);
            ed->Free = (int)free;
            ed->FreeTime = BitConverter.UInt32BitsToSingle(freetime);
            _shadowHeader[2 * e] = free;
            _shadowHeader[2 * e + 1] = freetime;
        }
    }

    /// <summary>
    /// The port's changes since the last boundary must be the recorded ones, word for word, and every recorded string
    /// text must be the text the port has at that offset.
    /// </summary>
    private void Check(string boundary)
    {
        int numEdicts = I32();
        if (numEdicts != _sv!.NumEdicts)
            throw Fail($"{boundary}: num_edicts is {_sv.NumEdicts}, the reference's {numEdicts}");
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
        CheckHeaders(boundary, numEdicts);
    }

    /// <summary>The free flags and freetimes the port changed must be the ones the reference changed.</summary>
    private void CheckHeaders(string boundary, int numEdicts)
    {
        int h = I32(), r = 0;
        for (int e = 0; e < numEdicts; e++)
        {
            EdictHeader* ed = _sv!.EdictNum(e);
            uint free = (uint)ed->Free, freetime = BitConverter.SingleToUInt32Bits(ed->FreeTime);
            bool changed = free != _shadowHeader[2 * e] || freetime != _shadowHeader[2 * e + 1];
            bool recorded = r < h && (int)U32At(_p + 12 * r) == e;
            if (recorded)
            {
                uint rf = U32At(_p + 12 * r + 4), rt = U32At(_p + 12 * r + 8);
                if (rf != free || rt != freetime)
                    throw Fail($"{boundary}: edict {e} is free={free} freetime={BitConverter.UInt32BitsToSingle(freetime):R}, the reference's free={rf} freetime={BitConverter.UInt32BitsToSingle(rt):R}");
                r++;
            }
            else if (changed)
                throw Fail($"{boundary}: edict {e} became free={free} freetime={BitConverter.UInt32BitsToSingle(freetime):R}; the reference did not change it");
            _shadowHeader[2 * e] = free;
            _shadowHeader[2 * e + 1] = freetime;
        }
        if (r != h)
            throw Fail($"{boundary}: the reference changed the header of edict {U32At(_p + 12 * r)}, the port did not");
        _p += 12 * h;
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

    /// <summary>
    /// An entry into QuakeC. From the engine (<paramref name="portRun"/> false): its changes since the last boundary
    /// are applied. From the port's own engine code (a touch function the port's SV_LinkEdict runs): checked.
    /// </summary>
    private void Enter(bool portRun = false, int portFnum = -1)
    {
        int fnum = I32();
        I32();                              // host_framecount
        byte state = U8();
        I32(); I32();                       // self, other: in the diff too
        uint seed = (uint)I32();
        double time = F64();
        Server sv = _sv!;
        if (portRun)
        {
            if (fnum != portFnum)
                throw Fail($"the port's engine runs {FunctionName(portFnum)}, the reference ran {FunctionName(fnum)}");
            // Inside a link the replay made for unported code, that code may have drawn from rand and moved the clock
            // before linking (SV_NewChaseDir, _Host_Frame): the capture's values hold. In the port's own code, the
            // port's must be the reference's.
            if (_drivenDepth > 0)
            {
                sv.Rand.Srand(seed);
                sv.Time = time;
            }
            else if (sv.Rand.Seed != seed)
                throw Fail($"entering {FunctionName(fnum)}: rand seed 0x{sv.Rand.Seed:X8}, the reference's 0x{seed:X8}");
            Check($"the port's engine entering {FunctionName(fnum)}");
        }
        else
        {
            Apply();
            sv.Rand.Srand(seed);            // the engine's draws since the last boundary
            sv.Time = time;
        }
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
            if (k is (byte)'K' or (byte)'U')
            {
                if (ported)
                    throw Fail($"{name} links in the reference (record '{(char)k}'), the port's did not");
                if (k == (byte)'K')
                    DriveLink();
                else
                    DriveUnlink();
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

    /// <summary>A link made by code the port does not have: the port makes it now ('K'; the hooks check the rest).</summary>
    private void DriveLink()
    {
        int e = I32();
        bool touch = U8() != 0;
        Apply();
        _drivingLink = true;
        _drivenDepth++;
        try
        {
            _sv!.LinkEdict(e, touch);
        }
        finally
        {
            _drivenDepth--;
        }
        if (_drivingLink)
            throw Fail($"the port's SV_LinkEdict did not reach its capture point for edict {e}");
    }

    private void DriveUnlink()
    {
        int e = I32();
        _drivingUnlink = true;
        _sv!.UnlinkEdict(e);
    }

    private void HookLink(int e, bool touch)
    {
        Links++;
        if (_drivingLink)
        {
            _drivingLink = false;
            return;
        }
        byte k = Next();
        if (k != (byte)'K')
            throw Fail($"the port links edict {e}, the reference's next record is '{(char)k}'");
        int re = I32();
        bool rt = U8() != 0;
        if (re != e || rt != touch)
            throw Fail($"the port links edict {e} (touch {touch}), the reference linked edict {re} (touch {rt})");
        Check($"linking edict {e}");
    }

    private void HookLinked(int e)
    {
        byte k = Next();
        if (k != (byte)'k')
            throw Fail($"the port's SV_LinkEdict of edict {e} returns, the reference's next record is '{(char)k}'");
        int re = I32();
        if (re != e)
            throw Fail($"the port's SV_LinkEdict of edict {e} returns, the reference's of edict {re}");
        EdictHeader* h = _sv!.EdictNum(e);
        int n = I32();
        if (n != h->NumLeafs)
            throw Fail($"edict {e} touches {h->NumLeafs} leafs, in the reference {n}");
        for (int i = 0; i < n; i++)
        {
            short leaf = (short)U16();
            if (leaf != h->LeafNums[i])
                throw Fail($"edict {e} leaf {i} is {h->LeafNums[i]}, in the reference {leaf}");
        }
        int node = I32();
        var list = (AreaList)U8();
        int prev = I32();
        (int Node, AreaList List) port = _sv.AreaOf(e);
        if (port.Node != node || port.List != list)
            throw Fail($"edict {e} is linked in area node {port.Node} ({port.List}), in the reference {node} ({list})");
        int portPrev = _sv.PreviousInArea(e);
        if (portPrev != prev)
            throw Fail($"edict {e} follows edict {portPrev} in its area list (-1: the head), in the reference edict {prev}");
        Check($"edict {e} linked");
    }

    private void HookUnlink(int e)
    {
        if (_drivingUnlink)
        {
            _drivingUnlink = false;
            return;
        }
        byte k = Next();
        if (k != (byte)'U')
            throw Fail($"the port unlinks edict {e}, the reference's next record is '{(char)k}'");
        int re = I32();
        if (re != e)
            throw Fail($"the port unlinks edict {e}, the reference unlinked edict {re}");
    }

    private void HookExecuteProgram(int fnum)
    {
        byte k = Next();
        if (k != (byte)'E')
            throw Fail($"the port's engine runs {FunctionName(fnum)}, the reference's next record is '{(char)k}'");
        Enter(portRun: true, portFnum: fnum);
    }

    private sealed class Hooks(VmReplay r) : IServerHooks
    {
        public void ExecuteProgram(int fnum) => r.HookExecuteProgram(fnum);
        public void Link(int e, bool touchTriggers) => r.HookLink(e, touchTriggers);
        public void Linked(int e) => r.HookLinked(e);
        public void Unlink(int e) => r.HookUnlink(e);
    }

    /// <summary>
    /// Mod_ForName for the replay: brush models through BrushLoader, alias models with WinQuake's fixed bounds of
    /// +-16, sprites with +-maxwidth/2 and +-maxheight/2 (integer division) from the sprite header.
    /// </summary>
    private sealed class Models(VmReplay r) : IModelSource
    {
        public Model* ForName(byte* name)
        {
            string n = Encoding.Latin1.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name));
            if (r._modelsByName.TryGetValue(n, out nint known))
                return (Model*)known;
            byte[] nb = Encoding.Latin1.GetBytes(n);
            QFile file = r._files.Find(nb) ?? throw r.Fail($"Mod_NumForName: {n} not found");
            Model* m;
            ReadOnlySpan<byte> d = file.Span;
            if (d[..4].SequenceEqual("IDPO"u8))
            {
                m = (Model*)NativeMemory.AllocZeroed((nuint)sizeof(Model));
                m->Type = ModelType.Alias;
                r._ownModels.Add((nint)m);
                m->Mins = new Vec3(-16, -16, -16);
                m->Maxs = new Vec3(16, 16, 16);
            }
            else if (d[..4].SequenceEqual("IDSP"u8))
            {
                int width = BinaryPrimitives.ReadInt32LittleEndian(d[16..]), height = BinaryPrimitives.ReadInt32LittleEndian(d[20..]);
                m = (Model*)NativeMemory.AllocZeroed((nuint)sizeof(Model));
                r._ownModels.Add((nint)m);
                m->Type = ModelType.Sprite;
                m->Mins = new Vec3(-width / 2, -width / 2, -height / 2);
                m->Maxs = new Vec3(width / 2, width / 2, height / 2);
            }
            else
            {
                BrushModelSet set = BrushLoader.Load(file, nb);
                r._otherModels.Add(set);
                m = set.World;
            }
            m->Name.Set(nb);
            r._modelsByName[n] = (nint)m;
            return m;
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
