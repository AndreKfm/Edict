// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Engine;

namespace Edict.Platform;

/// <summary>
/// A platform's sound device: what the mixer painted into the DMA buffer (<see cref="Sound.Buffer"/>, 16-bit stereo
/// at 11025 Hz, a ring of 16384 sample pairs) goes to it after each host frame. The engine's DMA position is realtime
/// in samples, so the device, playing at the same rate, follows the frame clock; the mixer paints _snd_mixahead
/// (0.1 s) ahead of it, which is the queue the device is fed with, as a DMA buffer's hardware would read it.
/// </summary>
internal abstract class AudioOutput : IDisposable
{
    private const int RingPairs = Sound.DmaSamples / 2;

    private int _written = int.MinValue;    // the sample pairs handed to the device, in paintedtime's count

    /// <summary>The device, or one that plays nothing when the system has none (or -nosound).</summary>
    public static AudioOutput Open()
    {
        try
        {
            AudioOutput? device = OperatingSystem.IsWindows() ? Win32.WaveOut.Create()
                : OperatingSystem.IsLinux() ? Linux.Alsa.Create()
                : null;
            return device ?? new Silent();
        }
        catch (DllNotFoundException)
        {
            return new Silent();        // no libasound
        }
    }

    /// <summary>After a host frame: the part of the ring painted since the last call (from soundtime on, after a stop or a lag).</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = "copies the painted samples into the device's preallocated buffers; the device's calls do not wait")]
    public void Feed(Sound? sound)
    {
        if (sound is not { Started: true })
            return;
        int painted = sound.PaintedTime, soundtime = sound.SoundTime;
        if (_written < soundtime || _written > painted)
            _written = soundtime;       // behind the DMA position, or the time was chopped (GetSoundtime)
        ReadOnlySpan<byte> ring = sound.Buffer;
        while (_written < painted)
        {
            int pos = _written & (RingPairs - 1);
            int pairs = Math.Min(painted - _written, RingPairs - pos);
            Write(ring.Slice(pos * 4, pairs * 4));
            _written += pairs;
        }
    }

    /// <summary>Interleaved 16-bit stereo pairs for the device; what does not fit its queue is dropped.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = "copies into the device's preallocated buffers; the device's calls do not wait")]
    protected abstract void Write(ReadOnlySpan<byte> pairs);

    public abstract void Dispose();

    /// <summary>Diagnostics (-edictwav): the samples into a .wav file instead of a device, its first ten minutes.</summary>
    public sealed class WavFile(string path) : AudioOutput
    {
        private readonly byte[] _data = new byte[11025 * 4 * 600];
        private int _length;

        protected override void Write(ReadOnlySpan<byte> pairs)
        {
            int n = Math.Min(pairs.Length, _data.Length - _length);
            pairs[..n].CopyTo(new Span<byte>(_data, _length, n));
            _length += n;
        }

        [NoAllocUnchecked("diagnostics: the file at the end")]
        public override void Dispose()
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write("RIFF"u8); w.Write(36 + _length); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(11025); w.Write(11025 * 4);
            w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(_length);
            w.Write(_data, 0, _length);
        }
    }

    private sealed class Silent : AudioOutput
    {
        protected override void Write(ReadOnlySpan<byte> pairs)
        {
        }

        public override void Dispose()
        {
        }
    }
}
