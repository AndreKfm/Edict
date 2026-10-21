// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Platform.Win32;

/// <summary>
/// The sound through winmm's waveOut (as snd_win.c's wave path): 16-bit stereo at 11025 Hz, a ring of headers whose
/// buffers are written in order; a header the device has not played yet means the queue is full, and the samples
/// are dropped.
/// </summary>
internal sealed unsafe partial class WaveOut : AudioOutput
{
    private const int Headers = 32, BufferBytes = 8192;
    private const uint WaveMapper = 0xFFFFFFFF, CallbackNull = 0, WhdrDone = 1, WhdrPrepared = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public byte* Data;
        public uint BufferLength, BytesRecorded;
        public nint User;
        public uint Flags, Loops;
        public nint Next, Reserved;
    }

    private const string FrameCall = "a winmm call with blittable arguments and no callback: nothing managed is allocated, nothing waits";

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutOpen(out nint handle, uint device, WaveFormatEx* format, nint callback, nint instance, uint flags);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutPrepareHeader(nint handle, WaveHdr* header, uint size);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutUnprepareHeader(nint handle, WaveHdr* header, uint size);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutWrite(nint handle, WaveHdr* header, uint size);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutReset(nint handle);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutClose(nint handle);

    private readonly nint _handle;
    private readonly WaveHdr* _headers;
    private readonly byte* _buffers;
    private int _next;

    private WaveOut(nint handle)
    {
        _handle = handle;
        _headers = (WaveHdr*)NativeMemory.AllocZeroed(Headers, (nuint)sizeof(WaveHdr));
        _buffers = (byte*)NativeMemory.AllocZeroed(Headers, BufferBytes);
        for (int i = 0; i < Headers; i++)
            _headers[i].Data = _buffers + i * BufferBytes;
    }

    /// <summary>The wave mapper's device for 16-bit stereo at 11025 Hz, or null when there is none.</summary>
    public static WaveOut? Create()
    {
        var format = new WaveFormatEx
        {
            FormatTag = 1,      // WAVE_FORMAT_PCM
            Channels = 2,
            SamplesPerSec = 11025,
            BitsPerSample = 16,
            BlockAlign = 4,
            AvgBytesPerSec = 11025 * 4,
        };
        return waveOutOpen(out nint h, WaveMapper, &format, 0, 0, CallbackNull) == 0 ? new WaveOut(h) : null;
    }

    protected override void Write(ReadOnlySpan<byte> pairs)
    {
        while (pairs.Length > 0)
        {
            WaveHdr* h = _headers + _next;
            if ((h->Flags & WhdrPrepared) != 0)
            {
                if ((h->Flags & WhdrDone) == 0)
                    return;     // the queue is full: the device is behind, drop the rest
                waveOutUnprepareHeader(_handle, h, (uint)sizeof(WaveHdr));
            }
            int n = Math.Min(pairs.Length, BufferBytes);
            pairs[..n].CopyTo(new Span<byte>(h->Data, n));
            h->BufferLength = (uint)n;
            h->Flags = 0;
            if (waveOutPrepareHeader(_handle, h, (uint)sizeof(WaveHdr)) != 0)
                return;
            waveOutWrite(_handle, h, (uint)sizeof(WaveHdr));
            _next = (_next + 1) % Headers;
            pairs = pairs[n..];
        }
    }

    public override void Dispose()
    {
        waveOutReset(_handle);
        for (int i = 0; i < Headers; i++)
            if ((_headers[i].Flags & WhdrPrepared) != 0)
                waveOutUnprepareHeader(_handle, _headers + i, (uint)sizeof(WaveHdr));
        waveOutClose(_handle);
        NativeMemory.Free(_headers);
        NativeMemory.Free(_buffers);
    }
}
