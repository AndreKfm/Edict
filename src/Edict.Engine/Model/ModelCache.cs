// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// Mod_ForName (model.c) for the server: brush models through <see cref="BrushLoader"/>, alias models with WinQuake's
/// fixed bounds of ±16 (Mod_LoadAliasModel), sprites with ±maxwidth/2 and ±maxheight/2 in integer division
/// (Mod_LoadSpriteModel). The server reads only a model's type and bounds; the rest of the alias and sprite formats
/// comes with the client.
/// C loads a model when precache_model first names it, inside the VM. The port loads at map load instead
/// (<see cref="Preload"/>): every name that occurs as a string in progs.dat or as a value in the entity lump and
/// names a model file. <see cref="ForName"/> then only looks the name up, without allocating, from the VM path. A
/// model name QuakeC builds at run time would not be preloaded: that stops with an error, as a missing file does.
/// </summary>
public sealed unsafe class ModelCache : IModelSource, IDisposable
{
    private static readonly SysErrorException NotFound =
        new("Mod_ForName: model not found (or not among the names preloaded from progs.dat and the entity lump)");

    private readonly QFileSystem _files;
    private readonly List<BrushModelSet> _brushSets = [];
    private readonly List<nint> _own = [];
    private byte[][] _names = new byte[64][];          // zero-terminated, pinned
    private nint[] _models = new nint[64];
    private int _count;

    public ModelCache(QFileSystem files) => _files = files;

    /// <summary>A model loaded elsewhere under <paramref name="name"/> (the world, "*1", ...).</summary>
    [NoAllocUnchecked("map load")]
    public void Register(ReadOnlySpan<byte> name, Model* model)
    {
        int known = Find(name);
        if (known >= 0)
        {
            if (_replaceInline)
                _models[known] = (nint)model;     // Mod_LoadBrushModel writes the map's "*i" over the last map's
            return;
        }
        if (_count == _names.Length)
        {
            Array.Resize(ref _names, 2 * _count);
            Array.Resize(ref _models, 2 * _count);
        }
        byte[] n = GC.AllocateArray<byte>(name.Length + 1, pinned: true);
        name.CopyTo(n);
        _names[_count] = n;
        _models[_count++] = (nint)model;
    }

    /// <summary>Loads and keeps a brush model set under <paramref name="name"/> (a map: the world and its inline models).</summary>
    [NoAllocUnchecked("map load")]
    public BrushModelSet LoadBrush(ReadOnlySpan<byte> name)
    {
        string key = System.Text.Encoding.Latin1.GetString(name);
        if (_maps.TryGetValue(key, out BrushModelSet? known))
            return known;   // a map spawned again (restart): Mod_ForName finds it loaded
        QFile file = _files.Find(name) ?? throw NotFound;
        BrushModelSet set = BrushLoader.Load(file, name);
        _brushSets.Add(set);
        _maps[key] = set;
        Register(name, set.World);
        return set;
    }

    private readonly Dictionary<string, BrushModelSet> _maps = [];
    private bool _replaceInline;

    /// <summary>
    /// Mod_ForName (name, crash) as the client calls it: the model, loaded now if it is not (the client loads at
    /// serverinfo, the load path); a map (re)registers its inline models "*1", ... under their names, as
    /// Mod_LoadBrushModel does on every load. Null, if crash is false, for a name that is no model file.
    /// </summary>
    [NoAllocUnchecked("map load: the client loads models at serverinfo")]
    public Model* ClientModel(ReadOnlySpan<byte> name, bool crash)
    {
        int i = Find(name);
        if (i < 0)
        {
            TryLoad(name);
            i = Find(name);
        }
        if (i < 0)
            return crash ? throw NotFound : null;
        Model* m = (Model*)_models[i];
        if (m->Type == ModelType.Brush && WorldOf(m) is { } w)
        {
            Span<byte> sub = stackalloc byte[12];
            _replaceInline = true;
            for (int k = 1; k < w.Count; k++)
            {
                sub[0] = (byte)'*';
                int len = 1 + CFormat.Int(k, sub[1..]);
                Register(sub[..len], w.Models + k);
            }
            _replaceInline = false;
        }
        return m;
    }

    private BrushModelSet? WorldOf(Model* m)
    {
        foreach (BrushModelSet set in _brushSets)
            if (set.World == m)
                return set;
        return null;
    }

    /// <summary>
    /// Loads every model a client's playback can name with Mod_ForName: each zero-terminated name in the demo that
    /// ends in .mdl, .spr or .bsp (the server info's model lists are such names), and cl_tent.c's beam models. The
    /// parse then only looks them up (<see cref="LoadedClientModel"/>), without allocating, as the server's
    /// <see cref="Preload"/> does for QuakeC.
    /// </summary>
    [NoAllocUnchecked("load path: playdemo")]
    public void PreloadClient(ReadOnlySpan<byte> demo)
    {
        foreach (string beam in (string[])["progs/bolt.mdl", "progs/bolt2.mdl", "progs/bolt3.mdl", "progs/beam.mdl"])
            ClientModel(System.Text.Encoding.Latin1.GetBytes(beam), crash: false);
        int start = 0;
        for (int i = 0; i < demo.Length; i++)
        {
            byte c = demo[i];
            if (c > ' ' && c < 127)
                continue;
            if (c == 0 && i - start > 4)
            {
                ReadOnlySpan<byte> name = demo[start..i];
                if (name.EndsWith(".mdl"u8) || name.EndsWith(".spr"u8) || name.EndsWith(".bsp"u8))
                    ClientModel(name, crash: false);
            }
            start = i + 1;
        }
        PrepareClientLookups();
    }

    /// <summary>The loaded maps for <see cref="LoadedClientModel"/> (a listen server's client uses the server's models).</summary>
    [NoAllocUnchecked("load path")]
    public void PrepareClientLookups()
    {
        _worlds = new nint[_brushSets.Count];
        _worldModels = new nint[_brushSets.Count];
        _worldCounts = new int[_brushSets.Count];
        for (int i = 0; i < _brushSets.Count; i++)
        {
            _worlds[i] = (nint)_brushSets[i].World;
            _worldModels[i] = (nint)_brushSets[i].Models;
            _worldCounts[i] = _brushSets[i].Count;
        }
    }

    private nint[] _worlds = [], _worldModels = [];   // each loaded map's world and its model array
    private int[] _worldCounts = [];

    /// <summary>
    /// Mod_ForName (name, false) on what <see cref="PreloadClient"/> loaded: the model or null; a map's inline models
    /// "*1", ... become its own again, as Mod_LoadBrushModel registers them on every load.
    /// </summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    public Model* LoadedClientModel(byte* name)
    {
        Model* m = null;
        for (int i = 0; i < _count && m == null; i++)
            fixed (byte* n = _names[i])
                if (CString.Strcmp(n, name) == 0)
                    m = (Model*)_models[i];
        if (m == null || m->Type != ModelType.Brush)
            return m;
        byte* sub = stackalloc byte[12];
        for (int s = 0; s < _worlds.Length; s++)
        {
            if ((Model*)_worlds[s] != m)
                continue;
            for (int k = 1; k < _worldCounts[s]; k++)
            {
                sub[0] = (byte)'*';
                int len = 1 + CFormat.Int(k, new Span<byte>(sub + 1, 11));
                sub[len] = 0;
                for (int i = 0; i < _count; i++)
                    fixed (byte* n = _names[i])
                        if (CString.Strcmp(n, sub) == 0)
                            _models[i] = (nint)((Model*)_worldModels[s] + k);
            }
        }
        return m;
    }

    /// <summary>
    /// Loads every model the map can precache with a constant name: the strings of <paramref name="progs"/> and the
    /// tokens of <paramref name="entities"/> (zero-terminated, or null) that name an alias, sprite or brush model file.
    /// </summary>
    [NoAllocUnchecked("map load")]
    public void Preload(Progs progs, byte* entities)
    {
        byte* s = progs.Strings;
        for (int i = 0; i < progs.NumStrings;)
        {
            var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(s + i);
            TryLoad(name);
            i += name.Length + 1;
        }
        if (entities == null)
            return;
        var parse = new ComParse();
        for (byte* data = parse.Parse(entities); data != null; data = parse.Parse(data))
            TryLoad(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(parse.Token));
    }

    private void TryLoad(ReadOnlySpan<byte> name)
    {
        if (name.Length == 0 || name.Length >= ModelName.Length || Find(name) >= 0)
            return;
        if (_files.Find(name) is not { } file)
            return;
        ReadOnlySpan<byte> d = file.Span;
        Model* m;
        if (d.Length >= 84 && d[..4].SequenceEqual("IDPO"u8))
        {
            m = NewModel(ModelType.Alias);
            m->Mins = new Vec3(-16, -16, -16);
            m->Maxs = new Vec3(16, 16, 16);
            m->NumFrames = BinaryPrimitives.ReadInt32LittleEndian(d[68..]);    // mdl_t.numframes
            m->SyncType = BinaryPrimitives.ReadInt32LittleEndian(d[72..]);     // mdl_t.synctype
            m->Flags = BinaryPrimitives.ReadInt32LittleEndian(d[76..]);        // mdl_t.flags
            AliasLoader.LoadAlias(d, System.Text.Encoding.Latin1.GetString(name), m, _own);
        }
        else if (d.Length >= 36 && d[..4].SequenceEqual("IDSP"u8))
        {
            int width = BinaryPrimitives.ReadInt32LittleEndian(d[16..]), height = BinaryPrimitives.ReadInt32LittleEndian(d[20..]);
            m = NewModel(ModelType.Sprite);
            m->NumFrames = BinaryPrimitives.ReadInt32LittleEndian(d[24..]);    // dsprite_t.numframes
            m->SyncType = BinaryPrimitives.ReadInt32LittleEndian(d[32..]);     // dsprite_t.synctype; flags 0
            m->Mins = new Vec3(-width / 2, -width / 2, -height / 2);
            m->Maxs = new Vec3(width / 2, width / 2, height / 2);
            AliasLoader.LoadSprite(d, System.Text.Encoding.Latin1.GetString(name), m, _own);
        }
        else if (d.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(d) == Bsp.Version)
        {
            BrushModelSet set = BrushLoader.Load(file, name);
            _brushSets.Add(set);
            m = set.World;
        }
        else
            return;     // a sound or any other file: C's Mod_ForName would try it as a brush model, but nothing precaches it
        m->Name.Set(name);
        Register(name, m);
    }

    private Model* NewModel(ModelType type)
    {
        var m = (Model*)NativeMemory.AllocZeroed((nuint)sizeof(Model));
        _own.Add((nint)m);
        m->Type = type;
        return m;
    }

    private int Find(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < _count; i++)
            if (_names[i].AsSpan(0, _names[i].Length - 1).SequenceEqual(name))
                return i;
        return -1;
    }

    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    public Model* ForName(byte* name)
    {
        for (int i = 0; i < _count; i++)
            fixed (byte* n = _names[i])
                if (CString.Strcmp(n, name) == 0)
                    return (Model*)_models[i];
        throw NotFound;
    }

    public void Dispose()
    {
        foreach (BrushModelSet s in _brushSets)
            s.Dispose();
        foreach (nint m in _own)
            NativeMemory.Free((void*)m);
        _brushSets.Clear();
        _own.Clear();
        _count = 0;
    }
}
