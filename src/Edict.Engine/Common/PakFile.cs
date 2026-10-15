// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>A file found by <see cref="QFileSystem"/>: bytes in mapped memory, valid while the file system lives.</summary>
public readonly unsafe struct QFile(byte* data, int length)
{
    public byte* Data { get; } = data;
    public int Length { get; } = length;

    public ReadOnlySpan<byte> Span => new(Data, Length);
}

/// <summary>
/// A PAK archive (common.c, <c>COM_LoadPackFile</c>), memory-mapped: the directory and the files are read in place,
/// nothing is copied. Lookups follow <c>COM_FindFile</c>: entries in directory order, exact byte comparison
/// (<c>strcmp</c>), the first match wins. Unlike C, entries that point outside the file are an error instead of a
/// short read.
/// </summary>
public sealed unsafe class PakFile : IDisposable
{
    public const int MaxFilesInPack = 2048;

    [StructLayout(LayoutKind.Sequential)]
    private struct DPackHeader
    {
        public uint Id;                               // "PACK"
        public int DirOfs;
        public int DirLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPackFile
    {
        public fixed byte Name[56];
        public int FilePos, FileLen;
    }

    private readonly MappedFile _file;
    private readonly DPackFile* _files;

    public string Path => _file.Path;

    public int NumFiles { get; }

    private PakFile(MappedFile file)
    {
        _file = file;
        if (file.Length < sizeof(DPackHeader))
            throw new InvalidDataException($"{Path} is not a packfile");
        DPackHeader header = *(DPackHeader*)file.Data;
        if (header.Id != 0x4B434150)                 // 'P' 'A' 'C' 'K'
            throw new InvalidDataException($"{Path} is not a packfile");
        NumFiles = header.DirLen / sizeof(DPackFile);
        if (NumFiles > MaxFilesInPack)
            throw new InvalidDataException($"{Path} has {NumFiles} files");
        if (header.DirOfs < 0 || NumFiles < 0 || header.DirOfs + (long)NumFiles * sizeof(DPackFile) > file.Length)
            throw new InvalidDataException($"{Path}: directory lies outside the file");
        _files = (DPackFile*)(file.Data + header.DirOfs);
    }

    /// <summary>Opens and maps <paramref name="path"/>; null if it does not exist (C: Sys_FileOpenRead fails).</summary>
    public static PakFile? Open(string path)
    {
        MappedFile? file = MappedFile.Open(path);
        if (file is null)
            return null;
        try
        {
            return new PakFile(file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The name of entry <paramref name="i"/>: up to its zero byte, at most the 56 bytes of the entry (C's strcpy
    /// would run on into filepos for a name without a zero byte).
    /// </summary>
    public ReadOnlySpan<byte> Name(int i)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, NumFiles);
        var name = new ReadOnlySpan<byte>(_files[i].Name, 56);
        int n = name.IndexOf((byte)0);
        return n < 0 ? name : name[..n];
    }

    /// <summary>The first entry named <paramref name="name"/>, as COM_FindFile searches a pack.</summary>
    public bool TryFind(ReadOnlySpan<byte> name, out QFile file)
    {
        for (int i = 0; i < NumFiles; i++)
        {
            if (!Name(i).SequenceEqual(name))
                continue;
            int pos = _files[i].FilePos, len = _files[i].FileLen;
            if (pos < 0 || len < 0 || pos + (long)len > _file.Length)
                throw new InvalidDataException($"{Path}: entry {i} lies outside the file");
            file = new QFile(_file.Data + pos, len);
            return true;
        }
        file = default;
        return false;
    }

    public void Dispose() => _file.Dispose();
}
