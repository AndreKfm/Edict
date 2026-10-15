// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

using Edict.Engine;

namespace Edict.Harness;

/// <summary>
/// A hull trace corpus (.etr) written by the reference (ref/src/edict_traces.c, format in its header comment):
/// the loader dump of one map and the traces with inputs and reference results. Files come from tools/traces.sh;
/// the directory is EDICT_TRACES, default data/traces in the checkout.
/// </summary>
public sealed class TraceCorpus
{
    public const int RecordSize = 136;

    public required string Map { get; init; }
    public required ulong Seed { get; init; }
    public required byte[] Planes { get; init; }              // 18 bytes each
    public required int NumPlanes { get; init; }
    public required byte[] Nodes { get; init; }               // 28 bytes each
    public required int NumNodes { get; init; }
    public required byte[] Leafs { get; init; }               // 24 bytes each
    public required int NumLeafs { get; init; }
    public required byte[] ClipNodes { get; init; }           // 8 bytes each
    public required int NumClipNodes { get; init; }
    public required byte[] Hull0 { get; init; }               // 8 bytes each
    public required int NumHull0 { get; init; }
    public required ModelRecord[] Models { get; init; }
    public required TraceRecord[] Traces { get; init; }

    public sealed record ModelRecord(int Index, string Name, Vec3 Mins, Vec3 Maxs, float Radius, int NumLeafs,
                                     int FirstModelSurface, int NumModelSurfaces, HullRecord[] Hulls);

    public readonly record struct HullRecord(int FirstClipNode, int LastClipNode, Vec3 ClipMins, Vec3 ClipMaxs);

    public struct TraceRecord
    {
        // inputs
        public byte Kind;                     // 0 world, 1 brush model, 2 box
        public ushort ModelIndex;
        public Vec3 EntOrigin, EntMins, EntMaxs;
        public Vec3 Start, Mins, Maxs, End;
        // reference results
        public Trace Trace;
        public sbyte Ent;
        public ContentsArray ContentsStart, ContentsEnd;
    }

    [InlineArray(3)]
    public struct ContentsArray
    {
        private short _element0;
    }

    public static string Directory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_TRACES");
            return string.IsNullOrEmpty(dir) ? Path.Combine(RepoRoot, "data", "traces") : dir;
        }
    }

    /// <summary>The base directory with id1/pak*.pak: EDICT_DATA, default data/libreq-v0.09-beta in the checkout.</summary>
    public static string DataDirectory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_DATA");
            return string.IsNullOrEmpty(dir) ? Path.Combine(RepoRoot, "data", "libreq-v0.09-beta") : dir;
        }
    }

    /// <summary>The checkout above the test binary; with the build output outside it, set EDICT_TRACES and EDICT_DATA.</summary>
    private static string RepoRoot
    {
        get
        {
            for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, "Edict.slnx")))
                    return d;
            throw new InvalidOperationException(
                $"Edict.slnx not found above {AppContext.BaseDirectory}: set EDICT_TRACES and EDICT_DATA (docs/TESTING.md)");
        }
    }

    /// <summary>The maps with a corpus file; a missing corpus is a failure, never a skip (docs/TESTING.md R10).</summary>
    public static IEnumerable<string> Maps()
    {
        string dir = Directory;
        string[] files = System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.etr") : [];
        if (files.Length == 0)
            throw new InvalidOperationException($"no .etr files in {dir}: run tools/traces.sh (docs/TESTING.md)");
        return files.Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, TraceCorpus> Cache = [];

    public static TraceCorpus Load(string map)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(map, out TraceCorpus? c))
                Cache[map] = c = Read(Path.Combine(Directory, map + ".etr"));
            return c;
        }
    }

    private static TraceCorpus Read(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        var r = new Reader(d);
        if (r.Bytes(4).SequenceEqual("ETR1"u8) is false || r.U32() != 1)
            throw new InvalidDataException($"{path}: not an ETR1 version 1 file");
        ulong seed = r.U64();
        int count = (int)r.U32();
        string map = r.Name();

        static (byte[] bytes, int n) Section(ref Reader r, int size)
        {
            int n = (int)r.U32();
            return (r.Bytes(n * size).ToArray(), n);
        }

        (byte[] planes, int numPlanes) = Section(ref r, 18);
        (byte[] nodes, int numNodes) = Section(ref r, 28);
        (byte[] leafs, int numLeafs) = Section(ref r, 24);
        (byte[] clip, int numClip) = Section(ref r, 8);
        (byte[] hull0, int numHull0) = Section(ref r, 8);

        var models = new ModelRecord[r.U32()];
        for (int i = 0; i < models.Length; i++)
        {
            int index = (int)r.U32();
            string name = r.Name();
            Vec3 mins = r.Vec(), maxs = r.Vec();
            float radius = r.F32();
            int numLeafsM = r.I32(), firstSurf = r.I32(), numSurf = r.I32();
            var hulls = new HullRecord[3];
            for (int h = 0; h < 3; h++)
                hulls[h] = new HullRecord(r.I32(), r.I32(), r.Vec(), r.Vec());
            models[i] = new ModelRecord(index, name, mins, maxs, radius, numLeafsM, firstSurf, numSurf, hulls);
        }

        if (d.Length - r.Position != (long)count * RecordSize)
            throw new InvalidDataException($"{path}: {d.Length - r.Position} bytes of traces, expected {count} x {RecordSize}");
        var traces = new TraceRecord[count];
        for (int i = 0; i < count; i++)
        {
            ref TraceRecord t = ref traces[i];
            t.Kind = r.U8();
            t.ModelIndex = r.U16();
            t.EntOrigin = r.Vec();
            t.EntMins = r.Vec();
            t.EntMaxs = r.Vec();
            t.Start = r.Vec();
            t.Mins = r.Vec();
            t.Maxs = r.Vec();
            t.End = r.Vec();
            t.Trace.AllSolid = r.Bool();
            t.Trace.StartSolid = r.Bool();
            t.Trace.InOpen = r.Bool();
            t.Trace.InWater = r.Bool();
            t.Trace.Fraction = r.F32();
            t.Trace.EndPos = r.Vec();
            t.Trace.Plane.Normal = r.Vec();
            t.Trace.Plane.Dist = r.F32();
            t.Ent = (sbyte)r.U8();
            t.Trace.Ent = t.Ent;
            for (int h = 0; h < 3; h++)
                t.ContentsStart[h] = (short)r.U16();
            for (int h = 0; h < 3; h++)
                t.ContentsEnd[h] = (short)r.U16();
        }

        return new TraceCorpus
        {
            Map = map, Seed = seed,
            Planes = planes, NumPlanes = numPlanes, Nodes = nodes, NumNodes = numNodes, Leafs = leafs, NumLeafs = numLeafs,
            ClipNodes = clip, NumClipNodes = numClip, Hull0 = hull0, NumHull0 = numHull0,
            Models = models, Traces = traces,
        };
    }

    private ref struct Reader(byte[] data)
    {
        private readonly byte[] _data = data;
        public int Position;

        public ReadOnlySpan<byte> Bytes(int n)
        {
            var s = new ReadOnlySpan<byte>(_data, Position, n);
            Position += n;
            return s;
        }

        public byte U8() => _data[Position++];
        public bool Bool() => U8() switch { 0 => false, 1 => true, var b => throw new InvalidDataException($"qboolean {b}") };
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Bytes(4));
        public Vec3 Vec() => new(F32(), F32(), F32());

        public string Name()
        {
            ReadOnlySpan<byte> s = Bytes(64);
            int n = s.IndexOf((byte)0);
            return Encoding.Latin1.GetString(n < 0 ? s : s[..n]);
        }
    }
}
