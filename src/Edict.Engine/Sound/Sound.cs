// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>What the sound needs from its host: the rand stream it draws from, realtime (the DMA clock) and host_frametime.</summary>
public interface ISoundHost
{
    /// <summary>The rand stream as C's rand () sees it at this moment (a listen server's client reads with the server's).</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    ref CRand Rand();

    double RealTime
    {
        [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
        get;
    }

    double FrameTime
    {
        [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
        get;
    }
}

/// <summary>
/// The sound of the reference's sound build (ref/build.sh SOUND=1, docs/PLAN.md P6 step 8): snd_dma.c, snd_mix.c and
/// snd_mem.c on their portable path, with the harness's DMA driver (ref/src/edict_snd.c): 32768 16-bit samples,
/// stereo, 11025 Hz, and the "DMA position" is realtime in samples. A sound is an sfx number (index + 1 into
/// known_sfx, 0 for C's NULL). Kept as in C: SND_PaintChannelFrom8/16 always paint from paintbuffer[0] (a channel
/// that loops within a paint chunk paints its restart over its start), S_StopSound looks at channels 0 to 7 (the
/// ambients' and half the dynamic ones), S_Update's "j == total_channels" is never true. The cache never evicts:
/// with the reference's 32 MB the sounds are never flushed, and a reload would give the same data.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe class Sound
{
    public const int MaxChannels = 128, MaxDynamicChannels = 8, NumAmbients = 4, MaxSfx = 512, PaintBufferSize = 512,
        MaxQPath = 64, AmbientWater = 0, AmbientSky = 1;

    /// <summary>edict_snd.c's buffer: 32768 16-bit samples (both channels).</summary>
    public const int DmaSamples = 32768;

    private const float NominalClipDist = 1000.0f;     // sound_nominal_clip_dist

    /// <summary>The cvars S_Init registers, in the cvar list's order (newest first); they come before R_Init's last, d_mipscale.</summary>
    public static readonly Registry.CvarDef[] CvarBlock =
    [
        new("_snd_mixahead", "0.1", true, false),
        new("snd_show", "0", false, false),
        new("snd_noextraupdate", "0", false, false),
        new("ambient_fade", "100", false, false),
        new("ambient_level", "0.3", false, false),
        new("bgmbuffer", "4096", false, false),
        new("bgmvolume", "1", true, false),
        new("loadas8bit", "0", false, false),
        new("precache", "1", false, false),
        new("volume", "0.7", true, false),
        new("nosound", "0", false, false),
    ];

    /// <summary>The cvar the sound block follows in the list: R_Init's last registration.</summary>
    public const string CvarBlockBefore = "d_mipscale";

    /// <summary>The commands S_Init registers, in registration order.</summary>
    public static readonly string[] Commands = ["play", "playvol", "stopsound", "soundlist", "soundinfo"];

    /// <summary>The reference's cvar list of the sound build: S_Init's block before d_mipscale.</summary>
    [NoAllocUnchecked("load path")]
    public static IEnumerable<Registry.CvarDef> RegistryCvars()
    {
        foreach (Registry.CvarDef c in Registry.Cvars)
        {
            if (c.Name == CvarBlockBefore)
                foreach (Registry.CvarDef s in CvarBlock)
                    yield return s;
            yield return c;
        }
    }

    /// <summary>channel_t.</summary>
    private struct Channel
    {
        public int Sfx;                                 // sfx number, 0: none
        public int LeftVol, RightVol;                   // 0-255 volume
        public int End;                                 // end time in global paintsamples
        public int Pos;                                 // sample position in sfx
        public int Looping;                             // where to loop, -1 = no looping
        public int EntNum, EntChannel;                  // to allow overriding a specific sound
        public Vec3 Origin;                             // origin of sound effect
        public float DistMult;                          // distance multiplier (attenuation/clipK)
        public int MasterVol;                           // 0-255 master volume
    }

    /// <summary>sfxcache_t: the sound resampled to the DMA's speed, Length samples of Width bytes at Offset in the pool.</summary>
    private struct SfxCache
    {
        public bool Loaded;                             // Cache_Check finds it
        public int Length, LoopStart, Speed, Width, Stereo;
        public int Offset;
    }

    /// <summary>wavinfo_t.</summary>
    private struct WavInfo
    {
        public int Rate, Width, Channels, LoopStart, Samples, DataOfs;
    }

    /// <summary>A .wav of the search path ("sound/..."), and the pool's bytes its resampled data takes once loaded.</summary>
    private struct WavFile
    {
        public byte[] Name;
        public QFile File;
        public int Offset, Size;
    }

    private readonly QFileSystem _files;
    private readonly Cmd _cmd;
    private readonly Cvars _cvars;
    private readonly ClientState _cl;
    private readonly ISoundHost _host;

    private readonly Channel[] _channels;
    private int _totalChannels;
    private readonly byte[][] _sfxNames;                // known_sfx[].name, zero-terminated
    private readonly SfxCache[] _sfxCache;              // known_sfx[].cache
    private int _numSfx;
    private readonly int[] _ambientSfx;
    private readonly int[] _paintBuffer;                // portable_samplepair_t[PAINTBUFFER_SIZE]: left, right
    private readonly int[] _scaleTable;                 // snd_scaletable[32][256]

    // the cache's memory: every .wav of the search path, found and sized when the sound starts, so that S_LoadSound's
    // COM_LoadStackFile and Cache_Alloc only look up and take their part, allocating nothing in a frame
    private WavFile[] _wavs;
    private byte[] _pool;

    private bool _initialized, _started, _fakeDma;      // snd_initialized, sound_started, fakedma
    private bool _ambient = true;                       // snd_ambient
    private readonly int _blocked = 0;                  // snd_blocked: only Win32's window activation blocks

    // shm (dma_t)
    private int _shmChannels, _shmSamples, _shmSamplePos, _shmSampleBits, _shmSpeed;
    private byte[]? _shmBuffer;
    private readonly byte[] _dma;                       // edict_snd.c's buffer, the one the harness hashes

    private Vec3 _listenerOrigin, _listenerForward, _listenerRight, _listenerUp;
    private int _soundTime, _paintedTime;               // sample PAIRS
    private int _buffers, _oldSamplePos;                // GetSoundtime's statics
    private int _playHash = 345, _playVolHash = 543;    // S_Play's and S_PlayVol's statics

    [NoAllocUnchecked("load path: the sound's tables")]
    public Sound(QFileSystem files, Cmd cmd, Cvars cvars, ClientState cl, ISoundHost host)
    {
        _files = files;
        _cmd = cmd;
        _cvars = cvars;
        _cl = cl;
        _host = host;
        _channels = new Channel[MaxChannels];
        _sfxNames = new byte[MaxSfx][];
        for (int i = 0; i < MaxSfx; i++)
            _sfxNames[i] = new byte[MaxQPath];
        _sfxCache = new SfxCache[MaxSfx];
        _ambientSfx = new int[NumAmbients];
        _paintBuffer = new int[PaintBufferSize * 2];
        _scaleTable = new int[32 * 256];
        _dma = GC.AllocateArray<byte>(DmaSamples * 2, pinned: true);
        _wavs = [];
        _pool = [];
    }

    /// <summary>sound_started.</summary>
    public bool Started => _started;

    /// <summary>paintedtime.</summary>
    public int PaintedTime => _paintedTime;

    /// <summary>soundtime.</summary>
    public int SoundTime => _soundTime;

    /// <summary>The DMA buffer of the harness's driver (edict_snd.c), as the 'A' record hashes it: 32768 16-bit samples, left and right.</summary>
    public ReadOnlySpan<byte> Buffer => _dma;

    /// <summary>The DMA's playback position in samples of both channels (shm->samplepos), for an output device.</summary>
    public int SamplePos => _shmSamplePos;

    private Screen? Con => _cl.Con;

    private float CvarValue(ReadOnlySpan<byte> name)
    {
        fixed (byte* n = name)
            return _cvars.Value(n);
    }

    // =======================================================================
    // init (snd_dma.c)
    // =======================================================================

    /// <summary>
    /// S_Init: the console line, then with neither -nosound nor -simsound in <paramref name="args"/> the commands, the
    /// DMA (S_Startup, SNDDMA_Init), the scale table, the sampling rate's line, the ambient sounds and
    /// S_StopAllSounds. The cvars are in the list already (<see cref="RegistryCvars"/>); a host with -nosound
    /// leaves them out, as C does not register them.
    /// </summary>
    [NoAllocUnchecked("load path")]
    public void Init(IReadOnlyList<string> args)
    {
        Con?.Printf("\nSound Initialization\n"u8);
        if (args.Contains("-nosound"))
            return;
        if (args.Contains("-simsound"))
            _fakeDma = true;
        _cmd.Add("play", Play);
        _cmd.Add("playvol", PlayVol);
        _cmd.Add("stopsound", () => StopAllSounds(true));
        _cmd.Add("soundlist", SoundList);
        _cmd.Add("soundinfo", SoundInfo);
        // host_parms.memsize is the reference's 32 MB: loadas8bit is not forced
        _initialized = true;
        Startup();
        InitScaleTable();
        _numSfx = 0;
        // create a piece of DMA memory
        if (_fakeDma)
        {
            _shmSampleBits = 16;
            _shmSpeed = 22050;
            _shmChannels = 2;
            _shmSamples = 32768;
            _shmSamplePos = 0;
            _shmBuffer = new byte[1 << 16];
        }
        IndexWavs();
        Con?.Msg("Sound sampling rate: "u8).MsgInt(_shmSpeed).Msg("\n"u8).EndMsg();
        _ambientSfx[AmbientWater] = PrecacheSound("ambience/water1.wav"u8);
        _ambientSfx[AmbientSky] = PrecacheSound("ambience/wind2.wav"u8);
        StopAllSounds(true);
    }

    /// <summary>S_Startup with SNDDMA_Init of edict_snd.c.</summary>
    private void Startup()
    {
        if (!_initialized)
            return;
        if (!_fakeDma)
        {
            _shmSampleBits = 16;
            _shmSpeed = 11025;
            _shmChannels = 2;
            _shmSamples = DmaSamples;
            _shmSamplePos = 0;
            _shmBuffer = _dma;
        }
        _started = true;
    }

    /// <summary>
    /// The search path's .wav files (COM_FindFile decides which file a name means) and the pool their resampled data
    /// needs: what S_LoadSound's Cache_Alloc would take for each, at the DMA's speed.
    /// </summary>
    [NoAllocUnchecked("load path")]
    private void IndexWavs()
    {
        var wavs = new List<WavFile>();
        int total = 0;
        foreach (byte[] name in _files.Names("sound/", ".wav"))
        {
            if (_files.Find(name) is not { } file)
                continue;
            int size = 0;
            WavInfo info = GetWavinfo(name, file.Span, quiet: true);
            if (info.Channels == 1)
                size = Math.Max(CCast.I32(info.Samples / ((float)info.Rate / _shmSpeed)) * info.Width * info.Channels, 0);
            wavs.Add(new WavFile { Name = name, File = file, Offset = total, Size = size });
            total = checked(total + size);
        }
        _wavs = [.. wavs];
        _pool = GC.AllocateArray<byte>(total, pinned: true);
    }

    /// <summary>S_AmbientOff.</summary>
    public void AmbientOff() => _ambient = false;

    /// <summary>S_AmbientOn.</summary>
    public void AmbientOn() => _ambient = true;

    // =======================================================================
    // load a sound
    // =======================================================================

    private static readonly SysErrorException NameTooLong = new("Sound name too long");
    private static readonly SysErrorException OutOfSfx = new("S_FindName: out of sfx_t");

    /// <summary>The length of a name up to its zero or the span's end.</summary>
    private static int Strlen(ReadOnlySpan<byte> s)
    {
        int n = 0;
        while (n < s.Length && s[n] != 0)
            n++;
        return n;
    }

    /// <summary>S_FindName: the sfx of this name, a new one if it is not known.</summary>
    private int FindName(ReadOnlySpan<byte> name)
    {
        int len = Strlen(name);
        if (len >= MaxQPath)
            throw NameTooLong;
        // see if already loaded
        int i;
        for (i = 0; i < _numSfx; i++)
        {
            byte[] n = _sfxNames[i];
            int j = 0;
            while (j < len && n[j] == name[j])
                j++;
            if (j == len && n[j] == 0)
                return i + 1;
        }
        if (_numSfx == MaxSfx)
            throw OutOfSfx;
        byte[] s = _sfxNames[i];
        for (int j = 0; j < len; j++)
            s[j] = name[j];
        s[len] = 0;
        _sfxCache[i] = default;
        _numSfx++;
        return i + 1;
    }

    /// <summary>S_TouchSound: the name known (Cache_Check only moves the cache's entry).</summary>
    public void TouchSound(ReadOnlySpan<byte> name)
    {
        if (!_started)
            return;
        FindName(name);
    }

    /// <summary>S_PrecacheSound: the sfx, loaded when precache is set; 0 when no sound runs.</summary>
    public int PrecacheSound(ReadOnlySpan<byte> name)
    {
        if (!_started || CvarValue("nosound\0"u8) != 0)
            return 0;
        int sfx = FindName(name);
        // cache it in
        if (CvarValue("precache\0"u8) != 0)
            LoadSound(sfx);
        return sfx;
    }

    /// <summary>The name of an sfx (known_sfx[].name), for the client's hash.</summary>
    public ReadOnlySpan<byte> SfxName(int sfx) => NameOf(sfx);

    /// <summary>The sfx's name, without its terminator.</summary>
    private ReadOnlySpan<byte> NameOf(int sfx)
    {
        byte[] n = _sfxNames[sfx - 1];
        return new ReadOnlySpan<byte>(n, 0, Strlen(n));
    }

    /// <summary>The .wav "sound/" + <paramref name="name"/> of the search path, or -1.</summary>
    private int FindWav(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < _wavs.Length; i++)
        {
            byte[] w = _wavs[i].Name;
            if (w.Length != 6 + name.Length)
                continue;
            int j = 0;
            while (j < name.Length && w[6 + j] == name[j])
                j++;
            if (j == name.Length)
                return i;
        }
        return -1;
    }

    private static readonly SysErrorException PoolMismatch = new("S_LoadSound: the sound's size differs from its index entry");

    /// <summary>
    /// S_LoadSound (snd_mem.c): whether the sound is cached, read, resampled and cached now, or (with a console line)
    /// cannot be: not found, not mono, not a PCM .wav.
    /// </summary>
    private bool LoadSound(int sfx)
    {
        // see if still in memory
        ref SfxCache sc = ref _sfxCache[sfx - 1];
        if (sc.Loaded)
            return true;
        // load it in
        ReadOnlySpan<byte> name = NameOf(sfx);
        int w = FindWav(name);
        if (w < 0)
        {
            Con?.Msg("Couldn't load sound/"u8).Msg(name).Msg("\n"u8).EndMsg();
            return false;
        }
        ref WavFile wav = ref _wavs[w];
        ReadOnlySpan<byte> data = new(wav.File.Data, wav.File.Length);
        WavInfo info = GetWavinfo(name, data, quiet: false);
        if (info.Channels != 1)
        {
            Con?.Msg(name).Msg(" is a stereo sample\n"u8).EndMsg();
            return false;
        }
        float stepscale = (float)info.Rate / _shmSpeed;
        int len = CCast.I32(info.Samples / stepscale);
        len = len * info.Width * info.Channels;
        if (Math.Max(len, 0) != wav.Size)
            throw PoolMismatch;
        sc.Length = info.Samples;
        sc.LoopStart = info.LoopStart;
        sc.Speed = info.Rate;
        sc.Width = info.Width;
        sc.Stereo = info.Channels;
        sc.Offset = wav.Offset;
        sc.Loaded = true;
        ResampleSfx(ref sc, sc.Speed, sc.Width, new ReadOnlySpan<byte>(wav.File.Data + info.DataOfs, data.Length - info.DataOfs),
            new Span<byte>(_pool, wav.Offset, wav.Size));
        return true;
    }

    /// <summary>ResampleSfx: the samples to the DMA's speed, 8 or 16 bits as loaded (loadas8bit: 8).</summary>
    private void ResampleSfx(ref SfxCache sc, int inrate, int inwidth, ReadOnlySpan<byte> data, Span<byte> output)
    {
        float stepscale = (float)inrate / _shmSpeed;     // this is usually 0.5, 1, or 2
        int outcount = CCast.I32(sc.Length / stepscale);
        sc.Length = outcount;
        if (sc.LoopStart != -1)
            sc.LoopStart = CCast.I32(sc.LoopStart / stepscale);
        sc.Speed = _shmSpeed;
        sc.Width = CvarValue("loadas8bit\0"u8) != 0 ? 1 : inwidth;
        sc.Stereo = 0;
        // resample / decimate to the current source rate
        if (stepscale == 1 && inwidth == 1 && sc.Width == 1)
        {
            // fast special case
            for (int i = 0; i < outcount; i++)
                output[i] = (byte)(data[i] - 128);
        }
        else
        {
            // general case
            int samplefrac = 0;
            int fracstep = CCast.I32(stepscale * 256);
            for (int i = 0; i < outcount; i++)
            {
                int srcsample = samplefrac >> 8;
                samplefrac += fracstep;
                int sample = inwidth == 2
                    ? (short)(data[srcsample * 2] | data[srcsample * 2 + 1] << 8)
                    : (data[srcsample] - 128) << 8;
                if (sc.Width == 2)
                {
                    output[i * 2] = (byte)sample;
                    output[i * 2 + 1] = (byte)(sample >> 8);
                }
                else
                    output[i] = (byte)(sample >> 8);
            }
        }
    }

    // ---- WAV loading (snd_mem.c): data_p as an offset, -1 for NULL; bytes past the file read as 0 (COM_LoadFile's terminator)

    private static readonly SysErrorException BadLoopLength = new("Sound has a bad loop length");
    private static readonly SysErrorException ZeroWidth = new("GetWavinfo: a width of 0 bits (C divides by it)");

    private struct Iff
    {
        public int DataP, IffEnd, LastChunk, IffData, ChunkLen;
    }

    private static byte At(ReadOnlySpan<byte> wav, int i) => (uint)i < (uint)wav.Length ? wav[i] : (byte)0;

    private static short GetLittleShort(ReadOnlySpan<byte> wav, ref Iff f)
    {
        short val = At(wav, f.DataP);
        val = (short)(val + (At(wav, f.DataP + 1) << 8));
        f.DataP += 2;
        return val;
    }

    private static int GetLittleLong(ReadOnlySpan<byte> wav, ref Iff f)
    {
        int val = At(wav, f.DataP);
        val += At(wav, f.DataP + 1) << 8;
        val += At(wav, f.DataP + 2) << 16;
        val += At(wav, f.DataP + 3) << 24;
        f.DataP += 4;
        return val;
    }

    private static bool Is4(ReadOnlySpan<byte> wav, int p, ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < 4; i++)
            if (At(wav, p + i) != name[i])
                return false;
        return true;
    }

    private static void FindNextChunk(ReadOnlySpan<byte> wav, ref Iff f, ReadOnlySpan<byte> name)
    {
        while (true)
        {
            f.DataP = f.LastChunk;
            if (f.DataP >= f.IffEnd)
            {   // didn't find the chunk
                f.DataP = -1;
                return;
            }
            f.DataP += 4;
            f.ChunkLen = GetLittleLong(wav, ref f);
            if (f.ChunkLen < 0)
            {
                f.DataP = -1;
                return;
            }
            f.DataP -= 8;
            f.LastChunk = f.DataP + 8 + ((f.ChunkLen + 1) & ~1);
            if (Is4(wav, f.DataP, name))
                return;
        }
    }

    private static void FindChunk(ReadOnlySpan<byte> wav, ref Iff f, ReadOnlySpan<byte> name)
    {
        f.LastChunk = f.IffData;
        FindNextChunk(wav, ref f, name);
    }

    private void Print(ReadOnlySpan<byte> text, bool quiet)
    {
        if (!quiet)
            Con?.Printf(text);
    }

    /// <summary>
    /// GetWavinfo: the format, the loop (a cue chunk, and a cooledit "mark" in the LIST after it) and the data's
    /// offset. <paramref name="quiet"/>: for the index, without the console lines and without stopping (a bad file
    /// is one S_LoadSound will refuse when it is named).
    /// </summary>
    private WavInfo GetWavinfo(ReadOnlySpan<byte> name, ReadOnlySpan<byte> wav, bool quiet)
    {
        WavInfo info = default;
        var f = new Iff { IffData = 0, IffEnd = wav.Length };
        // find "RIFF" chunk
        FindChunk(wav, ref f, "RIFF"u8);
        if (!(f.DataP >= 0 && Is4(wav, f.DataP + 8, "WAVE"u8)))
        {
            Print("Missing RIFF/WAVE chunks\n"u8, quiet);
            return info;
        }
        // get "fmt " chunk
        f.IffData = f.DataP + 12;
        FindChunk(wav, ref f, "fmt "u8);
        if (f.DataP < 0)
        {
            Print("Missing fmt chunk\n"u8, quiet);
            return info;
        }
        f.DataP += 8;
        int format = GetLittleShort(wav, ref f);
        if (format != 1)
        {
            Print("Microsoft PCM format only\n"u8, quiet);
            return info;
        }
        info.Channels = GetLittleShort(wav, ref f);
        info.Rate = GetLittleLong(wav, ref f);
        f.DataP += 4 + 2;
        info.Width = GetLittleShort(wav, ref f) / 8;
        // get cue chunk
        FindChunk(wav, ref f, "cue "u8);
        if (f.DataP >= 0)
        {
            f.DataP += 32;
            info.LoopStart = GetLittleLong(wav, ref f);
            // if the next chunk is a LIST chunk, look for a cue length marker
            FindNextChunk(wav, ref f, "LIST"u8);
            if (f.DataP >= 0 && Is4(wav, f.DataP + 28, "mark"u8))
            {   // this is not a proper parse, but it works with cooledit...
                f.DataP += 24;
                int i = GetLittleLong(wav, ref f);     // samples in loop
                info.Samples = info.LoopStart + i;
            }
        }
        else
            info.LoopStart = -1;
        // find data chunk
        FindChunk(wav, ref f, "data"u8);
        if (f.DataP < 0)
        {
            Print("Missing data chunk\n"u8, quiet);
            return info;
        }
        f.DataP += 4;
        if (info.Width == 0)
        {
            if (quiet)
                return default;
            throw ZeroWidth;
        }
        int samples = GetLittleLong(wav, ref f) / info.Width;
        if (info.Samples != 0)
        {
            if (samples < info.Samples)
            {
                if (quiet)
                    return default;
                throw BadLoopLength;
            }
        }
        else
            info.Samples = samples;
        info.DataOfs = f.DataP;
        return info;
    }

    // =======================================================================
    // channels
    // =======================================================================

    /// <summary>SND_PickChannel: the dynamic channel to play on (the entity's own channel, else the one closest to its end), or -1.</summary>
    private int PickChannel(int entnum, int entchannel)
    {
        // Check for replacement sound, or find the best one to replace
        int firstToDie = -1;
        int lifeLeft = 0x7fffffff;
        for (int chIdx = NumAmbients; chIdx < NumAmbients + MaxDynamicChannels; chIdx++)
        {
            ref Channel c = ref _channels[chIdx];
            if (entchannel != 0     // channel 0 never overrides
                && c.EntNum == entnum
                && (c.EntChannel == entchannel || entchannel == -1))
            {   // allways override sound from same entity
                firstToDie = chIdx;
                break;
            }
            // don't let monster sounds override player sounds
            if (c.EntNum == _cl.ViewEntity && entnum != _cl.ViewEntity && c.Sfx != 0)
                continue;
            if (c.End - _paintedTime < lifeLeft)
            {
                lifeLeft = c.End - _paintedTime;
                firstToDie = chIdx;
            }
        }
        if (firstToDie == -1)
            return -1;
        _channels[firstToDie].Sfx = 0;
        return firstToDie;
    }

    /// <summary>SND_Spatialize: the left and right volumes from the distance and the side the sound comes from.</summary>
    private void Spatialize(ref Channel ch)
    {
        // anything coming from the view entity will allways be full volume
        if (ch.EntNum == _cl.ViewEntity)
        {
            ch.LeftVol = ch.MasterVol;
            ch.RightVol = ch.MasterVol;
            return;
        }
        // calculate stereo seperation and distance attenuation
        Vec3 sourceVec = Vec3.Subtract(ch.Origin, _listenerOrigin);
        float dist = Mathlib.VectorNormalize(ref sourceVec) * ch.DistMult;
        float dot = Vec3.Dot(_listenerRight, sourceVec);
        float rscale, lscale;
        if (_shmChannels == 1)
        {
            rscale = 1.0f;
            lscale = 1.0f;
        }
        else
        {
            rscale = (float)(1.0 + dot);
            lscale = (float)(1.0 - dot);
        }
        // add in distance effect
        float scale = (float)((1.0 - dist) * rscale);
        ch.RightVol = CCast.I32(ch.MasterVol * scale);
        if (ch.RightVol < 0)
            ch.RightVol = 0;
        scale = (float)((1.0 - dist) * lscale);
        ch.LeftVol = CCast.I32(ch.MasterVol * scale);
        if (ch.LeftVol < 0)
            ch.LeftVol = 0;
    }

    /// <summary>
    /// S_StartSound: the sound on a dynamic channel, spatialized; when the same sound started on another channel
    /// still at its start, this one starts up to 0.1 s in (rand) so the two do not just add up.
    /// </summary>
    public void StartSound(int entnum, int entchannel, int sfx, in Vec3 origin, float fvol, float attenuation)
    {
        if (!_started)
            return;
        if (sfx == 0)
            return;
        if (CvarValue("nosound\0"u8) != 0)
            return;
        int vol = CCast.I32(fvol * 255);
        // pick a channel to play on
        int t = PickChannel(entnum, entchannel);
        if (t < 0)
            return;
        ref Channel target = ref _channels[t];
        // spatialize
        target = default;
        target.Origin = origin;
        target.DistMult = attenuation / NominalClipDist;
        target.MasterVol = vol;
        target.EntNum = entnum;
        target.EntChannel = entchannel;
        Spatialize(ref target);
        if (target.LeftVol == 0 && target.RightVol == 0)
            return;     // not audible at all
        // new channel
        if (!LoadSound(sfx))
        {
            target.Sfx = 0;
            return;     // couldn't load the sound's data
        }
        target.Sfx = sfx;
        target.Pos = 0;
        target.End = _paintedTime + _sfxCache[sfx - 1].Length;
        // if an identical sound has also been started this frame, offset the pos a bit to keep it from just making
        // the first one louder
        for (int chIdx = NumAmbients; chIdx < NumAmbients + MaxDynamicChannels; chIdx++)
        {
            if (chIdx == t)
                continue;
            ref Channel check = ref _channels[chIdx];
            if (check.Sfx == sfx && check.Pos == 0)
            {
                int skip = _host.Rand().Next() % (int)(0.1 * _shmSpeed);
                if (skip >= target.End)
                    skip = target.End - 1;
                target.Pos += skip;
                target.End -= skip;
                break;
            }
        }
    }

    /// <summary>S_StopSound: the entity's channel, among channels 0 to MAX_DYNAMIC_CHANNELS-1 as C looks (the ambients' and four dynamic ones).</summary>
    public void StopSound(int entnum, int entchannel)
    {
        for (int i = 0; i < MaxDynamicChannels; i++)
        {
            ref Channel c = ref _channels[i];
            if (c.EntNum == entnum && c.EntChannel == entchannel)
            {
                c.End = 0;
                c.Sfx = 0;
                return;
            }
        }
    }

    /// <summary>S_StopAllSounds: no statics, every channel cleared, and with <paramref name="clear"/> the DMA buffer.</summary>
    public void StopAllSounds(bool clear)
    {
        if (!_started)
            return;
        _totalChannels = MaxDynamicChannels + NumAmbients;     // no statics
        new Span<Channel>(_channels).Clear();
        if (clear)
            ClearBuffer();
    }

    /// <summary>S_ClearBuffer.</summary>
    public void ClearBuffer()
    {
        if (!_started || _shmBuffer is null)
            return;
        byte clear = _shmSampleBits == 8 ? (byte)0x80 : (byte)0;
        new Span<byte>(_shmBuffer, 0, _shmSamples * _shmSampleBits / 8).Fill(clear);
    }

    /// <summary>S_StaticSound: a looped sound on the next static channel, from the server's spawnstaticsound.</summary>
    public void StaticSound(int sfx, in Vec3 origin, float vol, float attenuation)
    {
        if (sfx == 0)
            return;
        if (_totalChannels == MaxChannels)
        {
            Con?.Printf("total_channels == MAX_CHANNELS\n"u8);
            return;
        }
        ref Channel ss = ref _channels[_totalChannels];
        _totalChannels++;
        if (!LoadSound(sfx))
            return;
        ref SfxCache sc = ref _sfxCache[sfx - 1];
        if (sc.LoopStart == -1)
        {
            Con?.Msg("Sound "u8).Msg(NameOf(sfx)).Msg(" not looped\n"u8).EndMsg();
            return;
        }
        ss.Sfx = sfx;
        ss.Origin = origin;
        ss.MasterVol = CCast.I32(vol);
        ss.DistMult = attenuation / 64 / NominalClipDist;
        ss.End = _paintedTime + sc.Length;
        Spatialize(ref ss);
    }

    /// <summary>S_LocalSound: a sound of the view entity's, from the console (misc/talk.wav) and the menu.</summary>
    public void LocalSound(ReadOnlySpan<byte> sound)
    {
        if (CvarValue("nosound\0"u8) != 0)
            return;
        if (!_started)
            return;
        int sfx = PrecacheSound(sound);
        if (sfx == 0)
        {
            Con?.Msg("S_LocalSound: can't cache "u8).Msg(sound).Msg("\n"u8).EndMsg();
            return;
        }
        StartSound(_cl.ViewEntity, -1, sfx, default, 1, 1);
    }

    // =======================================================================
    // update
    // =======================================================================

    /// <summary>S_UpdateAmbientSounds: the ambient channels' volumes from the listener's leaf, faded in and out.</summary>
    private void UpdateAmbientSounds()
    {
        if (!_ambient)
            return;
        // calc ambient sound levels
        Model* world = _cl.WorldModel;
        if (world == null)
            return;
        MLeaf* l = world->Leafs + Screen.PointInLeaf(_listenerOrigin, world);
        float level = CvarValue("ambient_level\0"u8);
        if (level == 0)
        {
            for (int a = 0; a < NumAmbients; a++)
                _channels[a].Sfx = 0;
            return;
        }
        float fade = CvarValue("ambient_fade\0"u8);
        double frametime = _host.FrameTime;
        for (int a = 0; a < NumAmbients; a++)
        {
            ref Channel chan = ref _channels[a];
            chan.Sfx = _ambientSfx[a];
            float vol = level * l->AmbientSoundLevel[a];
            if (vol < 8)
                vol = 0;
            // don't adjust volume too fast
            if (chan.MasterVol < vol)
            {
                chan.MasterVol = CCast.I32(chan.MasterVol + frametime * fade);
                if (chan.MasterVol > vol)
                    chan.MasterVol = CCast.I32(vol);
            }
            else if (chan.MasterVol > vol)
            {
                chan.MasterVol = CCast.I32(chan.MasterVol - frametime * fade);
                if (chan.MasterVol < vol)
                    chan.MasterVol = CCast.I32(vol);
            }
            chan.LeftVol = chan.RightVol = chan.MasterVol;
        }
    }

    /// <summary>
    /// S_Update, once each time through the main loop: the listener, the ambients, every sound spatialized again (a
    /// static sound's volume added to the first channel of the same sound), then the mix (S_Update_).
    /// </summary>
    public void Update(in Vec3 origin, in Vec3 forward, in Vec3 right, in Vec3 up)
    {
        if (!_started || _blocked > 0)
            return;
        _listenerOrigin = origin;
        _listenerForward = forward;
        _listenerRight = right;
        _listenerUp = up;
        // update general area ambient sound sources
        UpdateAmbientSounds();
        int combine = -1;
        // update spatialization for static and dynamic sounds
        for (int i = NumAmbients; i < _totalChannels; i++)
        {
            ref Channel ch = ref _channels[i];
            if (ch.Sfx == 0)
                continue;
            Spatialize(ref ch);     // respatialize channel
            if (ch.LeftVol == 0 && ch.RightVol == 0)
                continue;
            // try to combine static sounds with a previous channel of the same sound effect so we don't mix five
            // torches every frame
            if (i >= MaxDynamicChannels + NumAmbients)
            {
                // see if it can just use the last one
                if (combine >= 0 && _channels[combine].Sfx == ch.Sfx)
                {
                    _channels[combine].LeftVol += ch.LeftVol;
                    _channels[combine].RightVol += ch.RightVol;
                    ch.LeftVol = ch.RightVol = 0;
                    continue;
                }
                // search for one
                int j;
                combine = MaxDynamicChannels + NumAmbients;
                for (j = MaxDynamicChannels + NumAmbients; j < i; j++, combine++)
                    if (_channels[combine].Sfx == ch.Sfx)
                        break;
                if (j == _totalChannels)
                    combine = -1;   // never: j stops at i
                else
                {
                    if (combine != i)
                    {
                        _channels[combine].LeftVol += ch.LeftVol;
                        _channels[combine].RightVol += ch.RightVol;
                        ch.LeftVol = ch.RightVol = 0;
                    }
                    continue;
                }
            }
        }
        // debugging output
        if (CvarValue("snd_show\0"u8) != 0)
        {
            int total = 0;
            for (int i = 0; i < _totalChannels; i++)
            {
                ref Channel ch = ref _channels[i];
                if (ch.Sfx != 0 && (ch.LeftVol != 0 || ch.RightVol != 0))
                    total++;
            }
            Con?.Msg("----("u8).MsgInt(total).Msg(")----\n"u8).EndMsg();
        }
        // mix some sound
        Update_();
    }

    /// <summary>
    /// S_ExtraUpdate, from the renderer: the mix up to the DMA position. Only the frame's first call paints (realtime,
    /// and so the position, stays the same through a frame), which is why the port calls it once, at R_RenderView's
    /// first site (after R_MarkLeaves).
    /// </summary>
    public void ExtraUpdate()
    {
        if (CvarValue("snd_noextraupdate\0"u8) != 0)
            return;     // don't pollute timings
        Update_();
    }

    /// <summary>SNDDMA_GetDMAPos of edict_snd.c: the samples (both channels) played, realtime in samples, in the buffer.</summary>
    private int GetDmaPos()
    {
        _shmSamplePos = (CCast.I32(_host.RealTime * _shmSpeed) * _shmChannels) & (_shmSamples - 1);
        return _shmSamplePos;
    }

    /// <summary>GetSoundtime: the DMA time in sample pairs, counting the buffer's wraps.</summary>
    private void GetSoundtime()
    {
        int fullsamples = _shmSamples / _shmChannels;
        // it is possible to miscount buffers if it has wrapped twice between calls to S_Update.  Oh well.
        int samplepos = GetDmaPos();
        if (samplepos < _oldSamplePos)
        {
            _buffers++;     // buffer wrapped
            if (_paintedTime > 0x40000000)
            {   // time to chop things off to avoid 32 bit limits
                _buffers = 0;
                _paintedTime = fullsamples;
                StopAllSounds(true);
            }
        }
        _oldSamplePos = samplepos;
        _soundTime = _buffers * fullsamples + samplepos / _shmChannels;
    }

    /// <summary>S_Update_: the mix from paintedtime to _snd_mixahead seconds past the DMA time.</summary>
    private void Update_()
    {
        if (!_started || _blocked > 0)
            return;
        // Updates DMA time
        GetSoundtime();
        // check to make sure that we haven't overshot
        if (_paintedTime < _soundTime)
            _paintedTime = _soundTime;
        // mix ahead of current position
        uint endtime = CCast.U32(_soundTime + CvarValue("_snd_mixahead\0"u8) * _shmSpeed);
        int samps = _shmSamples >> (_shmChannels - 1);
        if (endtime - (uint)_soundTime > (uint)samps)
            endtime = (uint)(_soundTime + samps);
        PaintChannels((int)endtime);
    }

    // =======================================================================
    // mixing (snd_mix.c)
    // =======================================================================

    /// <summary>SND_InitScaletable.</summary>
    private void InitScaleTable()
    {
        for (int i = 0; i < 32; i++)
            for (int j = 0; j < 256; j++)
                _scaleTable[i * 256 + j] = (sbyte)j * i * 8;
    }

    /// <summary>S_PaintChannels: every channel into the paint buffer, a chunk of at most 512 pairs at a time, each chunk into the DMA buffer.</summary>
    private void PaintChannels(int endtime)
    {
        while (_paintedTime < endtime)
        {
            // if paintbuffer is smaller than DMA buffer
            int end = endtime;
            if (endtime - _paintedTime > PaintBufferSize)
                end = _paintedTime + PaintBufferSize;
            // clear the paint buffer
            new Span<int>(_paintBuffer, 0, (end - _paintedTime) * 2).Clear();
            // paint in the channels.
            for (int i = 0; i < _totalChannels; i++)
            {
                ref Channel ch = ref _channels[i];
                if (ch.Sfx == 0)
                    continue;
                if (ch.LeftVol == 0 && ch.RightVol == 0)
                    continue;
                if (!LoadSound(ch.Sfx))
                    continue;
                ref SfxCache sc = ref _sfxCache[ch.Sfx - 1];
                int ltime = _paintedTime;
                while (ltime < end)
                {   // paint up to end
                    int count = ch.End < end ? ch.End - ltime : end - ltime;
                    if (count > 0)
                    {
                        if (sc.Width == 1)
                            PaintChannelFrom8(ref ch, sc, count);
                        else
                            PaintChannelFrom16(ref ch, sc, count);
                        ltime += count;
                    }
                    // if at end of loop, restart
                    if (ltime >= ch.End)
                    {
                        if (sc.LoopStart >= 0)
                        {
                            ch.Pos = sc.LoopStart;
                            ch.End = ltime + sc.Length - ch.Pos;
                        }
                        else
                        {   // channel just stopped
                            ch.Sfx = 0;
                            break;
                        }
                    }
                }
            }
            // transfer out according to DMA format
            TransferStereo16(end);
            _paintedTime = end;
        }
    }

    private static readonly SysErrorException PaintPastSound = new("SND_PaintChannelFrom: past the sound's data (C reads beyond its cache entry)");

    /// <summary>SND_PaintChannelFrom8: from paintbuffer[0], the volumes clamped to 255 (in the channel).</summary>
    private void PaintChannelFrom8(ref Channel ch, in SfxCache sc, int count)
    {
        if (ch.LeftVol > 255)
            ch.LeftVol = 255;
        if (ch.RightVol > 255)
            ch.RightVol = 255;
        if (ch.Pos < 0 || ch.Pos + count > sc.Length)
            throw PaintPastSound;
        ReadOnlySpan<int> lscale = new(_scaleTable, (ch.LeftVol >> 3) * 256, 256);
        ReadOnlySpan<int> rscale = new(_scaleTable, (ch.RightVol >> 3) * 256, 256);
        ReadOnlySpan<byte> sfx = new(_pool, sc.Offset + ch.Pos, count);
        Span<int> pb = new(_paintBuffer, 0, count * 2);
        for (int i = 0; i < count; i++)
        {
            int data = sfx[i];
            pb[i * 2] += lscale[data];
            pb[i * 2 + 1] += rscale[data];
        }
        ch.Pos += count;
    }

    /// <summary>SND_PaintChannelFrom16: from paintbuffer[0].</summary>
    private void PaintChannelFrom16(ref Channel ch, in SfxCache sc, int count)
    {
        int leftvol = ch.LeftVol;
        int rightvol = ch.RightVol;
        if (ch.Pos < 0 || ch.Pos + count > sc.Length)
            throw PaintPastSound;
        ReadOnlySpan<byte> sfx = new(_pool, sc.Offset + ch.Pos * 2, count * 2);
        Span<int> pb = new(_paintBuffer, 0, count * 2);
        for (int i = 0; i < count; i++)
        {
            int data = (short)(sfx[i * 2] | sfx[i * 2 + 1] << 8);
            pb[i * 2] += (data * leftvol) >> 8;
            pb[i * 2 + 1] += (data * rightvol) >> 8;
        }
        ch.Pos += count;
    }

    /// <summary>S_TransferStereo16 with Snd_WriteLinearBlastStereo16: the paint buffer scaled by volume, clamped, into the DMA ring.</summary>
    private void TransferStereo16(int endtime)
    {
        int sndVol = CCast.I32(CvarValue("volume\0"u8) * 256);
        int p = 0;
        int lpaintedtime = _paintedTime;
        byte[] pbuf = _shmBuffer!;
        while (lpaintedtime < endtime)
        {
            // handle recirculating buffer issues
            int lpos = lpaintedtime & ((_shmSamples >> 1) - 1);
            int outIdx = lpos << 1;
            int count = (_shmSamples >> 1) - lpos;
            if (lpaintedtime + count > endtime)
                count = endtime - lpaintedtime;
            count <<= 1;
            // write a linear blast of samples
            for (int i = 0; i < count; i++)
            {
                int val = (_paintBuffer[p + i] * sndVol) >> 8;
                if (val > 0x7fff)
                    val = 0x7fff;
                else if (val < -0x8000)
                    val = -0x8000;
                pbuf[(outIdx + i) * 2] = (byte)val;
                pbuf[(outIdx + i) * 2 + 1] = (byte)(val >> 8);
            }
            p += count;
            lpaintedtime += count >> 1;
        }
    }

    // =======================================================================
    // console functions
    // =======================================================================

    private static readonly SysErrorException ArgTooLong = new("S_Play: a name longer than name[256]");

    /// <summary>The argument with ".wav" added when it has no '.'.</summary>
    [NoAllocUnchecked("console command")]
    private byte[] WavName(int i)
    {
        byte[] a = _cmd.Argv(i);
        byte[] name = Array.IndexOf(a, (byte)'.') < 0 ? [.. a, .. ".wav"u8] : a;
        if (name.Length >= 256)
            throw ArgTooLong;
        return name;
    }

    /// <summary>S_Play: each argument's sound at the listener, on an entity number of its own (from 345 on).</summary>
    [NoAllocUnchecked("console command")]
    private void Play()
    {
        for (int i = 1; i < _cmd.Argc; i++)
        {
            int sfx = PrecacheSound(WavName(i));
            StartSound(_playHash++, 0, sfx, _listenerOrigin, 1.0f, 1.0f);
        }
    }

    /// <summary>S_PlayVol: pairs of sound and volume.</summary>
    [NoAllocUnchecked("console command")]
    private void PlayVol()
    {
        for (int i = 1; i < _cmd.Argc; i += 2)
        {
            int sfx = PrecacheSound(WavName(i));
            float vol = CAtof.QAtof(_cmd.Argv(i + 1));
            StartSound(_playVolHash++, 0, sfx, _listenerOrigin, vol, 1.0f);
        }
    }

    /// <summary>S_SoundList: every loaded sound, its width and size.</summary>
    [NoAllocUnchecked("console command")]
    private void SoundList()
    {
        int total = 0;
        for (int i = 0; i < _numSfx; i++)
        {
            SfxCache sc = _sfxCache[i];
            if (!sc.Loaded)
                continue;
            int size = sc.Length * sc.Width * (sc.Stereo + 1);
            total += size;
            Con?.Printf(sc.LoopStart >= 0 ? "L"u8 : " "u8);
            Con?.Msg(Encoding.Latin1.GetBytes($"({sc.Width * 8,2}b) {size,6} : ")).Msg(NameOf(i + 1)).Msg("\n"u8).EndMsg();
        }
        Con?.Msg("Total resident: "u8).MsgInt(total).Msg("\n"u8).EndMsg();
    }

    /// <summary>S_SoundInfo_f: the DMA's format; the buffer's address is the port's (C prints its own, which differs from run to run).</summary>
    [NoAllocUnchecked("console command")]
    private void SoundInfo()
    {
        if (!_started || _shmBuffer is null)
        {
            Con?.Printf("sound system not started\n"u8);
            return;
        }
        void Line(int v, string what) => Con?.Msg(Encoding.Latin1.GetBytes($"{v,5} {what}\n")).EndMsg();
        Line(_shmChannels - 1, "stereo");
        Line(_shmSamples, "samples");
        Line(_shmSamplePos, "samplepos");
        Line(_shmSampleBits, "samplebits");
        Line(1, "submission_chunk");
        Line(_shmSpeed, "speed");
        fixed (byte* b = _shmBuffer)
            Con?.Msg(Encoding.Latin1.GetBytes($"0x{(uint)(nuint)b:x} dma buffer\n")).EndMsg();
        Line(_totalChannels, "total_channels");
    }
}
