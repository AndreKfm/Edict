// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

using Edict.Engine;

namespace Edict.Harness;

/// <summary>
/// One map as the port loads it: the file system over the LibreQuake paks, the BSPs from the search path, and a
/// <see cref="ClipWorld"/> whose model table has the brush models at the indices the reference had them in
/// <c>sv.models</c> (from the corpus): the world at 1, its inline models "*k", and brush models the QuakeC
/// precaches from their own files (the item boxes maps/b_*.bsp).
/// </summary>
public sealed unsafe class TraceMap : IDisposable
{
    public TraceCorpus Corpus { get; }
    public QFileSystem Files { get; }
    public BrushModelSet Set { get; }
    private readonly List<BrushModelSet> _others = [];
    public ClipWorld World { get; }

    public TraceMap(string map)
    {
        Corpus = TraceCorpus.Load(map);
        Files = new QFileSystem(TraceCorpus.DataDirectory);
        try
        {
            Set = Load(Corpus.Models.Single(m => m.Index == 1).Name);
            World = new ClipWorld();
            foreach (TraceCorpus.ModelRecord m in Corpus.Models)
            {
                if (m.Index == 1)
                    World.Models[m.Index] = Set.World;
                else if (m.Name.StartsWith('*'))
                    World.Models[m.Index] = Set.Models + int.Parse(m.Name.AsSpan(1));
                else
                {
                    BrushModelSet other = Load(m.Name);
                    _others.Add(other);
                    World.Models[m.Index] = other.World;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private BrushModelSet Load(string modelName)
    {
        byte[] name = Encoding.Latin1.GetBytes(modelName);
        QFile file = Files.Find(name) ?? throw new FileNotFoundException(modelName);
        return BrushLoader.Load(file, name);
    }

    /// <summary>The entity a corpus record traces against, with the fields SV_SpawnServer gives the world.</summary>
    public static ClipEntity Entity(in TraceCorpus.TraceRecord t) => t.Kind switch
    {
        0 => new ClipEntity { Index = 0, Solid = Solid.Bsp, MoveType = MoveType.Push, ModelIndex = 1, Origin = t.EntOrigin },
        1 => new ClipEntity { Index = 1, Solid = Solid.Bsp, MoveType = MoveType.Push, ModelIndex = t.ModelIndex, Origin = t.EntOrigin },
        2 => new ClipEntity { Index = 1, Solid = Solid.BBox, MoveType = MoveType.Step, Origin = t.EntOrigin, Mins = t.EntMins, Maxs = t.EntMaxs },
        _ => throw new InvalidDataException($"trace kind {t.Kind}"),
    };

    public void Dispose()
    {
        World?.Dispose();
        Set?.Dispose();
        foreach (BrushModelSet s in _others)
            s.Dispose();
        Files.Dispose();
    }
}
