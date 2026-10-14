// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 for pr_exec.c (docs/TESTING.md): one statement at a time, on inputs a game rarely produces (signed zeros,
/// NaN payloads, infinities, denormals, empty strings at nonzero offsets, random vectors), run by the reference's
/// PR_ExecuteProgram through the oracle and by the port's VM on identical memory; then all globals and edict bytes
/// are compared. Complements the capture replay, which sees only what the game produces.
/// </summary>
public sealed unsafe class VmOpcodeFuzz : IDisposable
{
    // operand slots above the system globals (GlobalVars.Count = 92)
    private const int SlotA = 100, SlotB = 103, SlotC = 106, SlotOne = 110;
    private const int NumGlobals = 128;
    private const int DefaultEdicts = 4;
    private const int EntityFields = 120;           // > EntVars.Count: OP_STATE's fields exist
    private const int NumStatements = 8;

    private static readonly nint Lib = Load();
    private static readonly delegate* unmanaged<int> HeaderBytesFn = (delegate* unmanaged<int>)NativeLibrary.GetExport(Lib, "EdictOracle_EdictHeaderBytes");
    private static readonly delegate* unmanaged<DStatement*, int, DFunction*, int, float*, int, byte*, int, void*, int, int, int, void> VmRunFn =
        (delegate* unmanaged<DStatement*, int, DFunction*, int, float*, int, byte*, int, void*, int, int, int, void>)NativeLibrary.GetExport(Lib, "EdictOracle_VmRun");

    private static nint Load()
    {
        string? path = Environment.GetEnvironmentVariable("EDICT_ORACLE");
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("EDICT_ORACLE is not set: point it to the oracle library built by ref/build.sh (docs/TESTING.md).");
        nint h = NativeLibrary.Load(path);
        var version = (delegate* unmanaged<int>)NativeLibrary.GetExport(h, "EdictOracle_Version");
        if (version() != 8)
            throw new InvalidOperationException($"Oracle version {version()} at {path}, expected 8.");
        return h;
    }

    public static int HeaderBytes => HeaderBytesFn();

    /// <summary>The VM under test, on the fuzz's memory; QcVm or a known-bad copy.</summary>
    public interface IVm
    {
        void Execute(int fnum);
    }

    // The strings block: offset 0 is "", and there are more empty strings and equal strings at other offsets.
    private static readonly byte[][] StringTable =
        [[], [], [(byte)'a'], [(byte)'a'], [(byte)'b'], "ab"u8.ToArray(), "ba"u8.ToArray(), [0xff], [0x80, (byte)'x'],
         "monster_army"u8.ToArray(), "monster_dog"u8.ToArray(), "player"u8.ToArray()];

    private readonly byte* _image;                 // a progs.dat image: header, statements, functions, strings, globals
    private readonly int _imageLength;
    private readonly int[] _stringOffsets;
    private readonly int _numStrings;

    public Progs Progs { get; }
    public int Header { get; } = HeaderBytes;
    public int Stride => Header + EntityFields * 4;

    // two copies of the VM memory: the reference's and the port's
    public float* RefGlobals { get; }
    public float* PortGlobals { get; }
    public byte* RefEdicts { get; }
    public byte* PortEdicts { get; }
    private readonly DStatement* _refStatements;
    private readonly DFunction* _refFunctions;

    public bool Active { get; set; }

    /// <param name="edicts">Edicts in each memory (the edict builtins need more than the VM statements).</param>
    public VmOpcodeFuzz(int edicts = DefaultEdicts)
    {
        NumEdicts = edicts;
        var strings = new List<byte>();
        _stringOffsets = new int[StringTable.Length];
        for (int i = 0; i < StringTable.Length; i++)
        {
            _stringOffsets[i] = strings.Count;
            strings.AddRange(StringTable[i]);
            strings.Add(0);
        }
        _numStrings = strings.Count;

        int ofsStatements = sizeof(DPrograms);
        int ofsFunctions = ofsStatements + NumStatements * sizeof(DStatement);
        int ofsStrings = ofsFunctions + 2 * sizeof(DFunction);
        int ofsGlobals = (ofsStrings + _numStrings + 3) & ~3;
        _imageLength = ofsGlobals + NumGlobals * 4;
        _image = (byte*)NativeMemory.AllocZeroed((nuint)_imageLength);
        var h = (DPrograms*)_image;
        h->VersionNumber = DPrograms.Version;
        h->Crc = DPrograms.HeaderCrc;
        h->OfsStatements = ofsStatements; h->NumStatements = NumStatements;
        h->OfsFunctions = ofsFunctions; h->NumFunctions = 2;
        h->OfsStrings = ofsStrings; h->NumStrings = _numStrings;
        h->OfsGlobals = ofsGlobals; h->NumGlobals = NumGlobals;
        h->OfsGlobalDefs = ofsGlobals; h->OfsFieldDefs = ofsGlobals;     // no defs
        h->EntityFields = EntityFields;
        for (int i = 0; i < _numStrings; i++)
            _image[ofsStrings + i] = strings[i];
        var main = (DFunction*)(_image + ofsFunctions) + 1;
        main->FirstStatement = 1;           // statement 0 is an error; function 0 is empty

        Progs = new Progs(new QFile(_image, _imageLength));
        _refStatements = (DStatement*)NativeMemory.AllocZeroed(NumStatements, (nuint)sizeof(DStatement));
        _refFunctions = (DFunction*)NativeMemory.AllocZeroed(2, (nuint)sizeof(DFunction));
        _refFunctions[1] = Progs.Functions[1];
        RefGlobals = (float*)NativeMemory.AllocZeroed(NumGlobals, 4);
        PortGlobals = (float*)NativeMemory.AllocZeroed(NumGlobals, 4);
        RefEdicts = (byte*)NativeMemory.AllocZeroed((nuint)NumEdicts, (nuint)Stride);
        PortEdicts = (byte*)NativeMemory.AllocZeroed((nuint)NumEdicts, (nuint)Stride);
    }

    public byte* Strings => Progs.Strings;

    public int StringOffset(int i) => _stringOffsets[i];

    public int StringCount => StringTable.Length;

    /// <summary>A host for programs without builtins.</summary>
    public readonly struct Host(VmOpcodeFuzz f) : IProgsHost
    {
        public void Builtin(int num) => throw new InvalidOperationException("the fuzz programs call no builtins");
        public byte* String(int ofs) => f.Strings + ofs;
        public bool Active => f.Active;
        public void Called(int fnum) { }
    }

    private sealed class PortVm(VmOpcodeFuzz f) : IVm
    {
        private readonly QcVm _vm = new(f.Progs, (int*)f.PortGlobals, f.PortEdicts, f.Header, VmReplay.NumBuiltins);
        public void Execute(int fnum)
        {
            var host = new Host(f);
            _vm.Execute(ref host, fnum);
        }
    }

    public IVm MakePortVm() => new PortVm(this);

    /// <summary>Sets statement i (1..NumStatements-1) for both sides.</summary>
    public void Statement(int i, Op op, int a = 0, int b = 0, int c = 0)
    {
        var st = new DStatement { Op = op, A = (short)a, B = (short)b, C = (short)c };
        Progs.Statements[i] = st;
        _refStatements[i] = st;
    }

    public void Global(int ofs, uint bits)
    {
        ((uint*)RefGlobals)[ofs] = bits;
        ((uint*)PortGlobals)[ofs] = bits;
    }

    public void Field(int edict, int field, uint bits)
    {
        *(uint*)(RefEdicts + edict * Stride + Header + 4 * field) = bits;
        *(uint*)(PortEdicts + edict * Stride + Header + 4 * field) = bits;
    }

    /// <summary>Fills both memories with the same pseudo-random words, so that stray writes show.</summary>
    public void Scramble(ref Rng rng)
    {
        for (int i = 0; i < NumGlobals; i++)
            Global(i, rng.Bits());
        for (int e = 0; e < NumEdicts; e++)
            for (int f = 0; f < EntityFields; f++)
                Field(e, f, rng.Bits());
    }

    /// <summary>Runs function 1 on both sides; null if memory agrees in every byte afterwards, else the report.</summary>
    /// <param name="nanClass">
    /// The statement does arithmetic: two NaN words count as equal, whatever their bits (docs/adr/D8). False for
    /// copies, which must keep every bit.
    /// </param>
    public string? Run(IVm port, bool nanClass)
    {
        VmRunFn(_refStatements, NumStatements, _refFunctions, 2, RefGlobals, NumGlobals, Strings, _numStrings,
                RefEdicts, EntityFields, Active ? 1 : 0, 1);
        port.Execute(1);
        return Compare(nanClass);
    }

    /// <summary>A float word that is NaN.</summary>
    public static bool IsNaN(uint bits) => (bits & 0x7FFFFFFF) > 0x7F800000;

    /// <summary>Equal bits, or (with <paramref name="nanClass"/>) both NaN.</summary>
    public static bool Same(uint a, uint b, bool nanClass) => a == b || (nanClass && IsNaN(a) && IsNaN(b));

    /// <summary>
    /// Null if both memories agree word for word (NaN as a class if asked), else the first difference. The edict
    /// headers must be unchanged, or with <paramref name="headers"/> (reference layout, port layout) agree in their
    /// free flag and freetime.
    /// </summary>
    public string? Compare(bool nanClass, Func<int, (int Free, uint FreeTime)>? headers = null)
    {
        for (int i = 0; i < NumGlobals; i++)
            if (!Same(((uint*)RefGlobals)[i], ((uint*)PortGlobals)[i], nanClass))
                return $"global {i}: port {Hex(((uint*)PortGlobals)[i])}, reference {Hex(((uint*)RefGlobals)[i])}";
        for (int e = 0; e < NumEdicts; e++)
        {
            if (headers is null)
            {
                var r = new ReadOnlySpan<byte>(RefEdicts + e * Stride, Header);
                if (!r.SequenceEqual(new ReadOnlySpan<byte>(PortEdicts + e * Stride, Header)))
                    return $"edict {e}: the header changed";
            }
            else
            {
                (int free, uint freetime) = headers(e);
                var port = (EdictHeader*)(PortEdicts + e * Stride);
                if (port->Free != free || BitConverter.SingleToUInt32Bits(port->FreeTime) != freetime)
                    return $"edict {e} header: port free={port->Free} freetime={Hex(BitConverter.SingleToUInt32Bits(port->FreeTime))}, " +
                           $"reference free={free} freetime={Hex(freetime)}";
            }
            for (int f = 0; f < EntityFields; f++)
            {
                uint x = *(uint*)(RefEdicts + e * Stride + Header + 4 * f), y = *(uint*)(PortEdicts + e * Stride + Header + 4 * f);
                if (!Same(x, y, nanClass))
                    return $"edict {e} field {f}: port {Hex(y)}, reference {Hex(x)}";
            }
        }
        return null;
    }

    public static string Hex(uint bits) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{bits:X8} ({BitConverter.UInt32BitsToSingle(bits):R})");

    public int EdictOffset(int e) => e * Stride;

    public int FieldPointer(int e, int field) => e * Stride + Header + 4 * field;

    public const int A = SlotA, B = SlotB, C = SlotC, One = SlotOne, Fields = EntityFields, Edicts = DefaultEdicts;

    /// <summary>Edicts in each memory.</summary>
    public int NumEdicts { get; }

    /// <summary>splitmix64 and the float inputs of the fuzz.</summary>
    /// <summary>
    /// A fuzz seed for a map that is the same in every process (string.GetHashCode is randomized per process, which
    /// made the fuzzes' inputs differ from run to run).
    /// </summary>
    public static ulong MapSeed(string map) => Edict.Harness.Xxh64.Hash(System.Text.Encoding.ASCII.GetBytes(map)) & 0xFFFF;

    public struct Rng(ulong seed)
    {
        private ulong _s = seed;

        public ulong Next()
        {
            ulong z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public uint Bits() => (uint)Next();

        public int Below(int n) => (int)((Next() >> 33) % (ulong)n);

        public static readonly uint[] Edges =
        [
            0x00000000, 0x80000000, 0x3F800000, 0xBF800000, 0x3F000000, 0xBF000000, 0x3FC00000, 0x40000000,
            0x00000001, 0x80000001, 0x00800000, 0x007FFFFF, 0x7F7FFFFF, 0xFF7FFFFF, 0x7F800000, 0xFF800000,
            0x7FC00000, 0xFFC00000, 0x7F800001, 0x7FBFFFFF, 0x7FFFFFFF, 0xFFFFFFFF, 0x4F000000, 0xCF000000,
            0x4F32D05E, 0xCF32D05E, 0x3DCCCCCD, 0x42C80000, 0x3EAAAAAB, 0x45800000, 0xC5800000, 0x7149F2CA,
        ];

        /// <summary>An edge value, any bit pattern, or a game-like coordinate.</summary>
        public uint Float()
        {
            switch (Below(4))
            {
                case 0:
                case 1:
                    return Edges[Below(Edges.Length)];
                case 2:
                    return Bits();
                default:
                    float v = (float)((Next() >> 11) * (1.0 / 9007199254740992.0) * 8192 - 4096);
                    if (Below(2) == 0)
                        v = MathF.Round(v * 8) / 8;
                    return BitConverter.SingleToUInt32Bits(v);
            }
        }
    }

    public void Dispose()
    {
        Progs.Dispose();
        NativeMemory.Free(_image);
        NativeMemory.Free(_refStatements);
        NativeMemory.Free(_refFunctions);
        NativeMemory.Free(RefGlobals);
        NativeMemory.Free(PortGlobals);
        NativeMemory.Free(RefEdicts);
        NativeMemory.Free(PortEdicts);
    }
}
