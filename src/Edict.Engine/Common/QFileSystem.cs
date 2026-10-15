// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace Edict.Engine;

/// <summary>
/// Quake's search path (common.c): <c>COM_AddGameDirectory</c> puts the game directory at the head of the path,
/// then pak0.pak, pak1.pak, ... each in front of the previous one, so later paks override earlier ones and every
/// pak overrides loose files. <c>COM_FindFile</c> walks the path in that order. Load path only.
/// </summary>
public sealed class QFileSystem : IDisposable
{
    private abstract record SearchPath;
    private sealed record PackPath(PakFile Pack) : SearchPath;
    private sealed record DirectoryPath(string Directory) : SearchPath;

    private readonly List<SearchPath> _path = [];       // [0] is searched first
    private readonly Dictionary<string, MappedFile> _loose = [];

    /// <summary>
    /// <c>static_registered</c>: gfx/pop.lmp was found and matches. Without it, loose files are only looked up in
    /// the top directory of a game directory (no '/' or '\\' in the name).
    /// </summary>
    public bool Registered { get; private set; }

    /// <summary>COM_InitFilesystem without -basedir/-cachedir/-game: <c>basedir/id1</c>, then COM_CheckRegistered.</summary>
    public QFileSystem(string basedir)
    {
        AddGameDirectory(System.IO.Path.Combine(basedir, "id1"));
        CheckRegistered();
    }

    private void AddGameDirectory(string dir)
    {
        _path.Insert(0, new DirectoryPath(dir));
        for (int i = 0; ; i++)
        {
            PakFile? pak = PakFile.Open(System.IO.Path.Combine(dir, $"pak{i}.pak"));
            if (pak is null)
                break;
            _path.Insert(0, new PackPath(pak));
        }
    }

    /// <summary>COM_FindFile: the first hit on the search path, or null.</summary>
    public QFile? Find(ReadOnlySpan<byte> name)
    {
        foreach (SearchPath s in _path)
        {
            switch (s)
            {
                case PackPath p:
                    if (p.Pack.TryFind(name, out QFile f))
                        return f;
                    break;
                case DirectoryPath d:
                    if (!Registered && (name.IndexOf((byte)'/') >= 0 || name.IndexOf((byte)'\\') >= 0))
                        continue;
                    if (FindLoose(System.IO.Path.Combine(d.Directory, Encoding.Latin1.GetString(name))) is { } loose)
                        return loose;
                    break;
            }
        }
        return null;
    }

    public QFile? Find(string name) => Find(Encoding.Latin1.GetBytes(name));

    /// <summary>
    /// The names on the search path that start with <paramref name="prefix"/> and end with <paramref name="suffix"/>,
    /// each once: the paks' entries, and the files under the game directories (relative, with '/'), for a lookup
    /// table built on the load path (<see cref="Find(ReadOnlySpan{byte})"/> decides which one a name means).
    /// </summary>
    public List<byte[]> Names(string prefix, string suffix)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SearchPath s in _path)
        {
            switch (s)
            {
                case PackPath p:
                    for (int i = 0; i < p.Pack.NumFiles; i++)
                        names.Add(Encoding.Latin1.GetString(p.Pack.Name(i)));
                    break;
                case DirectoryPath d when System.IO.Directory.Exists(d.Directory):
                    foreach (string f in System.IO.Directory.EnumerateFiles(d.Directory, "*", SearchOption.AllDirectories))
                        names.Add(System.IO.Path.GetRelativePath(d.Directory, f).Replace('\\', '/'));
                    break;
            }
        }
        return names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(suffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(n => Encoding.Latin1.GetBytes(n)).ToList();
    }

    private unsafe QFile? FindLoose(string path)
    {
        if (!_loose.TryGetValue(path, out MappedFile? file))
        {
            file = MappedFile.Open(path);
            if (file is null)
                return null;
            if (file.Length > int.MaxValue)
            {
                file.Dispose();
                throw new InvalidDataException($"{path}: too large");
            }
            _loose.Add(path, file);
        }
        return new QFile(file.Data, (int)file.Length);
    }

    // this graphic needs to be in the pak file to use registered features (common.c)
    private static ReadOnlySpan<ushort> Pop =>
    [
        0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x6600, 0x0000, 0x0000, 0x0000, 0x6600, 0x0000,
        0x0000, 0x0066, 0x0000, 0x0000, 0x0000, 0x0000, 0x0067, 0x0000, 0x0000, 0x6665, 0x0000, 0x0000, 0x0000, 0x0000, 0x0065, 0x6600,
        0x0063, 0x6561, 0x0000, 0x0000, 0x0000, 0x0000, 0x0061, 0x6563, 0x0064, 0x6561, 0x0000, 0x0000, 0x0000, 0x0000, 0x0061, 0x6564,
        0x0064, 0x6564, 0x0000, 0x6469, 0x6969, 0x6400, 0x0064, 0x6564, 0x0063, 0x6568, 0x6200, 0x0064, 0x6864, 0x0000, 0x6268, 0x6563,
        0x0000, 0x6567, 0x6963, 0x0064, 0x6764, 0x0063, 0x6967, 0x6500, 0x0000, 0x6266, 0x6769, 0x6a68, 0x6768, 0x6a69, 0x6766, 0x6200,
        0x0000, 0x0062, 0x6566, 0x6666, 0x6666, 0x6666, 0x6562, 0x0000, 0x0000, 0x0000, 0x0062, 0x6364, 0x6664, 0x6362, 0x0000, 0x0000,
        0x0000, 0x0000, 0x0000, 0x0062, 0x6662, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0061, 0x6661, 0x0000, 0x0000, 0x0000,
        0x0000, 0x0000, 0x0000, 0x0000, 0x6500, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x6400, 0x0000, 0x0000, 0x0000,
    ];

    /// <summary>
    /// COM_CheckRegistered: registered if gfx/pop.lmp is found (looked up while still unregistered) and its first
    /// 256 bytes, read as big-endian shorts, equal the table; found but different is "Corrupted data file.". The
    /// Windows dedicated reference (WINDED) refuses to run unregistered; this port does not.
    /// </summary>
    private void CheckRegistered()
    {
        Registered = false;
        if (Find("gfx/pop.lmp") is not { } f)
            return;
        ReadOnlySpan<byte> data = f.Span;
        for (int i = 0; i < Pop.Length; i++)
        {
            int v = 2 * i + 1 < data.Length ? (data[2 * i] << 8) | data[2 * i + 1] : -1;
            if (v != Pop[i])
                throw new InvalidDataException("Corrupted data file.");
        }
        Registered = true;
    }

    public void Dispose()
    {
        foreach (SearchPath s in _path)
            if (s is PackPath p)
                p.Pack.Dispose();
        foreach (MappedFile f in _loose.Values)
            f.Dispose();
        _path.Clear();
        _loose.Clear();
    }
}
