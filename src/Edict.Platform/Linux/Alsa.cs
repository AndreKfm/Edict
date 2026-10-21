// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Platform.Linux;

/// <summary>
/// The sound through ALSA (libasound's "default" device, as snd_linux.c's OSS device would take it): 16-bit stereo
/// at 11025 Hz, written without blocking; what the device's queue does not take is dropped, an underrun recovered.
/// </summary>
internal sealed unsafe partial class Alsa : AudioOutput
{
    private const string Lib = "libasound.so.2";
    private const int StreamPlayback = 0, NonBlock = 1, FormatS16Le = 2, AccessRwInterleaved = 3, EAgain = -11;

    private const string FrameCall = "a libasound call with blittable arguments: nothing managed is allocated, a non-blocking device does not wait";

    [LibraryImport(Lib)]
    private static partial int snd_pcm_open(out nint pcm, byte* name, int stream, int mode);

    [LibraryImport(Lib)]
    private static partial int snd_pcm_set_params(nint pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport(Lib)]
    private static partial nint snd_pcm_writei(nint pcm, byte* buffer, nuint frames);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport(Lib)]
    private static partial int snd_pcm_recover(nint pcm, int err, int silent);

    [LibraryImport(Lib)]
    private static partial int snd_pcm_close(nint pcm);

    private readonly nint _pcm;

    private Alsa(nint pcm) => _pcm = pcm;

    /// <summary>The default device for 16-bit stereo at 11025 Hz (a fifth of a second of queue), or null when there is none.</summary>
    public static Alsa? Create()
    {
        nint pcm;
        fixed (byte* name = "default\0"u8)
            if (snd_pcm_open(out pcm, name, StreamPlayback, NonBlock) < 0)
                return null;
        if (snd_pcm_set_params(pcm, FormatS16Le, AccessRwInterleaved, 2, 11025, 1, 200_000) < 0)
        {
            snd_pcm_close(pcm);
            return null;
        }
        return new Alsa(pcm);
    }

    protected override void Write(ReadOnlySpan<byte> pairs)
    {
        fixed (byte* p = pairs)
        {
            int frames = pairs.Length / 4, done = 0;
            for (int tries = 0; done < frames && tries < 2; tries++)
            {
                nint n = snd_pcm_writei(_pcm, p + done * 4, (nuint)(frames - done));
                if (n >= 0)
                    done += (int)n;
                else if ((int)n == EAgain || snd_pcm_recover(_pcm, (int)n, 1) < 0)
                    return;     // the queue is full, or the device is gone: drop
            }
        }
    }

    public override void Dispose() => snd_pcm_close(_pcm);
}
