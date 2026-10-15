// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// The port's brush loader against the reference's loader dump: planes, nodes, leafs, clip nodes, hull 0 and the
/// hulls and bounds of every brush model, record by record, in the dump's byte layout.
/// </summary>
public sealed unsafe class LoaderTests
{
    public static IEnumerable<TheoryDataRow<string>> MapNames => TraceCorpus.Maps().Select(m => new TheoryDataRow<string>(m));

    private sealed class Writer
    {
        private readonly List<byte> _b = [];

        public byte[] ToArray() => [.. _b];
        public void U8(int v) => _b.Add((byte)v);
        public void U16(int v) { U8(v); U8(v >> 8); }
        public void U32(int v) { U16(v); U16(v >> 16); }
        public void F32(float f) => U32((int)Bits.Of(f));
        public void Vec(in Vec3 v) { F32(v.X); F32(v.Y); F32(v.Z); }
    }

    /// <summary>Compares two sections of fixed-size records; reports the first differing record.</summary>
    private static void Section(string map, string name, int size, byte[] reference, int referenceCount, byte[] port, int portCount)
    {
        Assert.True(referenceCount == portCount, $"{map}: {name}: port has {portCount} records, reference {referenceCount}");
        for (int i = 0; i < referenceCount; i++)
        {
            ReadOnlySpan<byte> a = port.AsSpan(i * size, size), b = reference.AsSpan(i * size, size);
            if (!a.SequenceEqual(b))
                Assert.Fail($"{map}: {name} {i} differs: port {Convert.ToHexString(a)}, reference {Convert.ToHexString(b)}");
        }
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Collision_data_matches_the_reference(string map)
    {
        using var f = new TraceMap(map);
        TraceCorpus c = f.Corpus;
        Model* m = f.Set.World;

        var w = new Writer();
        for (int i = 0; i < m->NumPlanes; i++)
        {
            MPlane* p = &m->Planes[i];
            w.Vec(p->Normal);
            w.F32(p->Dist);
            w.U8(p->Type);
            w.U8(p->SignBits);
        }
        Section(map, "plane", 18, c.Planes, c.NumPlanes, w.ToArray(), m->NumPlanes);

        w = new Writer();
        for (int i = 0; i < m->NumNodes; i++)
        {
            MNode* n = &m->Nodes[i];
            w.U32(n->PlaneNum);
            w.U32(n->Children[0]);
            w.U32(n->Children[1]);
            for (int j = 0; j < 6; j++)
                w.U16(n->MinMaxs[j]);
            w.U16(n->FirstSurface);
            w.U16(n->NumSurfaces);
        }
        Section(map, "node", 28, c.Nodes, c.NumNodes, w.ToArray(), m->NumNodes);

        w = new Writer();
        for (int i = 0; i < m->LeafCount; i++)
        {
            MLeaf* l = &m->Leafs[i];
            w.U32(l->Contents);
            for (int j = 0; j < 6; j++)
                w.U16(l->MinMaxs[j]);
            w.U32(l->VisOfs);
            for (int j = 0; j < 4; j++)
                w.U8(l->AmbientSoundLevel[j]);
        }
        Section(map, "leaf", 24, c.Leafs, c.NumLeafs, w.ToArray(), m->LeafCount);

        static byte[] Clip(DClipNode* nodes, int count)
        {
            var w = new Writer();
            for (int i = 0; i < count; i++)
            {
                w.U32(nodes[i].PlaneNum);
                w.U16(nodes[i].Children[0]);
                w.U16(nodes[i].Children[1]);
            }
            return w.ToArray();
        }

        Section(map, "clipnode", 8, c.ClipNodes, c.NumClipNodes, Clip(m->ClipNodes, m->NumClipNodes), m->NumClipNodes);
        Section(map, "hull 0 clipnode", 8, c.Hull0, c.NumHull0, Clip(m->Hulls[0].ClipNodes, m->Hulls[0].LastClipNode + 1),
                m->Hulls[0].LastClipNode + 1);
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Brush_models_match_the_reference(string map)
    {
        using var f = new TraceMap(map);
        foreach (TraceCorpus.ModelRecord r in f.Corpus.Models)
        {
            Model* m = f.World.Models[r.Index];
            string where = $"{map}: model {r.Index} ({r.Name})";
            Assert.True(System.Text.Encoding.Latin1.GetString(m->Name.Span) == r.Name, $"{where}: name {System.Text.Encoding.Latin1.GetString(m->Name.Span)}");
            Assert.True(Bits.Same(m->Mins, r.Mins), $"{where}: mins port {Bits.Hex(m->Mins)}, reference {Bits.Hex(r.Mins)}");
            Assert.True(Bits.Same(m->Maxs, r.Maxs), $"{where}: maxs port {Bits.Hex(m->Maxs)}, reference {Bits.Hex(r.Maxs)}");
            Assert.True(Bits.Of(m->Radius) == Bits.Of(r.Radius), $"{where}: radius port {Bits.Hex(m->Radius)}, reference {Bits.Hex(r.Radius)}");
            Assert.True(m->NumLeafs == r.NumLeafs, $"{where}: numleafs port {m->NumLeafs}, reference {r.NumLeafs}");
            Assert.True(m->FirstModelSurface == r.FirstModelSurface && m->NumModelSurfaces == r.NumModelSurfaces,
                        $"{where}: surfaces port {m->FirstModelSurface}+{m->NumModelSurfaces}, reference {r.FirstModelSurface}+{r.NumModelSurfaces}");
            for (int h = 0; h < 3; h++)
            {
                ref Hull hull = ref m->Hulls[h];
                TraceCorpus.HullRecord e = r.Hulls[h];
                Assert.True(hull.FirstClipNode == e.FirstClipNode && hull.LastClipNode == e.LastClipNode,
                            $"{where}: hull {h} nodes port {hull.FirstClipNode}..{hull.LastClipNode}, reference {e.FirstClipNode}..{e.LastClipNode}");
                Assert.True(Bits.Same(hull.ClipMins, e.ClipMins) && Bits.Same(hull.ClipMaxs, e.ClipMaxs),
                            $"{where}: hull {h} bounds port {Bits.Hex(hull.ClipMins)}..{Bits.Hex(hull.ClipMaxs)}, reference {Bits.Hex(e.ClipMins)}..{Bits.Hex(e.ClipMaxs)}");
            }
        }
    }

    [Fact]
    public void Search_path_finds_maps_in_the_paks()
    {
        using var fs = new QFileSystem(TraceCorpus.DataDirectory);
        QFile? start = fs.Find("maps/start.bsp");
        Assert.NotNull(start);
        Assert.Equal(Bsp.Version, BinaryPrimitives.ReadInt32LittleEndian(start.Value.Span));
        Assert.Null(fs.Find("maps/START.bsp"));       // strcmp: case matters
        Assert.Null(fs.Find("maps/nonexistent.bsp"));
    }
}
