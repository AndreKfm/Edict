// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.MemoryMappedFiles;

namespace Edict.Engine;

/// <summary>A whole file mapped read-only; the pointer stays valid until disposal. Load path only.</summary>
public sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;

    public string Path { get; }

    public byte* Data { get; }

    public long Length { get; }

    private MappedFile(string path, MemoryMappedFile? map, MemoryMappedViewAccessor? view, byte* data, long length)
    {
        Path = path;
        _map = map;
        _view = view;
        Data = data;
        Length = length;
    }

    /// <summary>Maps <paramref name="path"/>; null if it does not exist.</summary>
    public static MappedFile? Open(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        if (info.Length == 0)                         // an empty file cannot be mapped
            return new MappedFile(path, null, null, null, 0);
        var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            return new MappedFile(path, map, view, p + view.PointerOffset, info.Length);
        }
        catch
        {
            view?.Dispose();
            map.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _map?.Dispose();
    }
}
