// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// Strings the engine allocated (string offsets outside the progs string block, pointing elsewhere on C's hunk).
/// Supplied by the engine around the server: the replay harness fills it from captures.
/// </summary>
public unsafe interface IEngineStrings
{
    /// <summary><c>pr_strings + ofs</c> for an engine string.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    byte* Get(int ofs);
}

/// <summary>
/// Models by name for precache_model (<c>Mod_ForName (name, true)</c>): brush models with their hulls, alias and
/// sprite models with the bounds WinQuake gives them. The engine around the server supplies it.
/// </summary>
public unsafe interface IModelSource
{
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    Model* ForName(byte* name);
}

/// <summary>
/// The server state the builtins work on (server.h <c>sv</c> and the globals around it), as far as cut 2 has ported
/// it: the VM and its memory, the rand stream, the clock, cvars, the string temp of ftos/vtos and the precache lists.
/// Edict headers, area nodes and messages come with steps 3b and 3c.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe partial class Server
{
    private static readonly HostErrorException NoPrecache = new("no precache");
    private static readonly HostErrorException BackwardsMinsMaxs = new("backwards mins/maxs");
    private readonly Model*[] _models;

    public const int MaxModels = 256, MaxSounds = 256;
    public const int StringTempSize = 128;            // pr_string_temp[128]

    private static readonly HostErrorException PrecacheOutsideSpawn = new("PF_Precache_*: Precache can only be done in spawn functions");
    private static readonly HostErrorException BadString = new("Bad string");
    private static readonly HostErrorException SoundOverflow = new("PF_precache_sound: overflow");
    private static readonly HostErrorException ModelOverflow = new("PF_precache_model: overflow");

    private readonly byte*[] _soundPrecache;
    private readonly byte*[] _modelPrecache;
    private readonly byte* _stringTemp;
    private readonly int _numStrings;
    private readonly int _entityFields, _edictSize;

    public QcVm Vm { get; }
    public Progs Progs { get; }
    public int* Globals { get; }
    public byte* Edicts { get; }
    public int HeaderBytes { get; }
    public Cvars Cvars { get; }
    public IEngineStrings EngineStrings { get; }

    /// <summary>The C library's rand stream, shared by engine and builtins.</summary>
    public CRand Rand;

    /// <summary>sv.time.</summary>
    public double Time { get; set; }

    /// <summary><c>sv.state == ss_active</c>; false while loading (spawn functions run).</summary>
    public bool Active { get; set; }

    /// <summary>The number of the builtin the engine's host was last asked for and does not have (diagnostic).</summary>
    public int UnportedBuiltin { get; set; } = -1;

    /// <summary><c>sv.paused</c>.</summary>
    public bool Paused { get; set; }

    /// <summary><c>svs.serverflags</c>: cross-level information (sigils).</summary>
    public int ServerFlags { get; set; }

    public const int MaxLightstyles = 64;             // MAX_LIGHTSTYLES
    private readonly byte*[] _lightstyles;

    /// <summary><c>sv.lightstyles[i]</c>: the string PF_lightstyle last set, or null.</summary>
    public byte* Lightstyle(int i) => _lightstyles[i];

    /// <summary>Sets <c>sv.lightstyles[i]</c> (PF_lightstyle; the replay, from the capture).</summary>
    public void SetLightstyle(int i, byte* style) => _lightstyles[i] = style;

    /// <summary>pr_string_temp - pr_strings: where ftos and vtos write.</summary>
    public int StringTempOffset { get; }

    [NoAllocUnchecked("map load: sets the state up once")]
    public Server(QcVm vm, Progs progs, int* globals, byte* edicts, int headerBytes, Cvars cvars, IEngineStrings engineStrings,
                  byte* stringTemp, int stringTempOffset)
    {
        Vm = vm;
        Progs = progs;
        Globals = globals;
        Edicts = edicts;
        HeaderBytes = headerBytes;
        Cvars = cvars;
        EngineStrings = engineStrings;
        _stringTemp = stringTemp;
        StringTempOffset = stringTempOffset;
        _numStrings = progs.NumStrings;
        _entityFields = progs.EntityFields;
        _edictSize = headerBytes + _entityFields * 4;
        if (sizeof(EdictHeader) > headerBytes)
            throw new InvalidOperationException("the port's edict header does not fit the reference's");
        _soundPrecache = new byte*[MaxSounds];
        _modelPrecache = new byte*[MaxModels];
        _models = new Model*[MaxModels];
        _areaNodes = new AreaNode[AreaNodes];
        _lightstyles = new byte*[MaxLightstyles];
        _checkPvs = new byte[MaxMapLeafs / 8];
        _decompressed = new byte[MaxMapLeafs / 8];
        _linkPrev = _linkNext = null!;     // sized by ClearWorld, when max_edicts is known
    }

    /// <summary><c>pr_strings + ofs</c>.</summary>
    public byte* String(int ofs) =>
        (uint)ofs < (uint)_numStrings ? Progs.Strings + ofs
        : ofs == StringTempOffset ? _stringTemp
        : EngineStrings.Get(ofs);

    public float* G(int ofs) => (float*)(Globals + ofs);

    /// <summary>The string temp's bytes (zero-terminated).</summary>
    public Span<byte> StringTemp => new(_stringTemp, StringTempSize);

    /// <summary>
    /// The precache list entries SV_SpawnServer sets before the spawn functions: sound 0 and model 0 empty, model 1
    /// the world, models 2.. the inline models "*1", "*2", ... (strings that live as long as the map).
    /// </summary>
    [NoAllocUnchecked("map load")]
    public void InitPrecache(byte* empty, ReadOnlySpan<nint> names, ReadOnlySpan<nint> models)
    {
        Array.Clear(_soundPrecache);
        Array.Clear(_modelPrecache);
        Array.Clear(_models);
        _soundPrecache[0] = empty;
        _modelPrecache[0] = empty;
        for (int i = 0; i < names.Length; i++)
        {
            _modelPrecache[1 + i] = (byte*)names[i];
            _models[1 + i] = (Model*)models[i];
        }
    }

    /// <summary>Where precache_model finds models (Mod_ForName with crash set: a missing model is a Sys_Error).</summary>
    public IModelSource ModelSource { get; set; } = null!;

    /// <summary>sv.models[i].</summary>
    public Model* ModelAt(int i) => (uint)i < MaxModels ? _models[i] : null;

    /// <summary>PF_setmodel's lookup: the index of the precached name, or a PR_RunError ("no precache").</summary>
    public int ModelIndex(byte* m)
    {
        int i;
        for (i = 0; i < MaxModels && _modelPrecache[i] is not null; i++)
            if (Same(_modelPrecache[i], m))
                return i;
        throw Vm.RunError(NoPrecache);
    }

    /// <summary>
    /// SetMinMaxSize: mins, maxs and size, then SV_LinkEdict without touching triggers. The rotate flag is ignored as
    /// in C ("FIXME: implement rotation properly again").
    /// </summary>
    public void SetMinMaxSize(int e, in Vec3 min, in Vec3 max)
    {
        for (int i = 0; i < 3; i++)
            if (Vec3.At(min, i) > Vec3.At(max, i))
                throw Vm.RunError(BackwardsMinsMaxs);

        float* v = Fields(e);
        *(Vec3*)(v + EntVars.Mins) = min;
        *(Vec3*)(v + EntVars.Maxs) = max;
        *(Vec3*)(v + EntVars.Size) = Vec3.Subtract(max, min);

        LinkEdict(e, false);
    }

    private static bool Same(byte* a, byte* b) => CString.Strcmp(a, b) == 0;

    /// <summary><c>PR_CheckEmptyString</c>: a string starting with a byte at or below ' ' is a PR_RunError.</summary>
    private void CheckEmptyString(byte* s)
    {
        if (s[0] <= ' ')
            throw Vm.RunError(BadString);
    }

    /// <summary>
    /// PF_precache_sound after its return value is set: the first free slot, unless the name is there already.
    /// </summary>
    public void PrecacheSound(byte* s)
    {
        if (Active)
            throw Vm.RunError(PrecacheOutsideSpawn);
        CheckEmptyString(s);
        for (int i = 0; i < MaxSounds; i++)
        {
            if (_soundPrecache[i] is null)
            {
                _soundPrecache[i] = s;
                return;
            }
            if (Same(_soundPrecache[i], s))
                return;
        }
        throw Vm.RunError(SoundOverflow);
    }

    /// <summary>The index of <paramref name="s"/> in sv.sound_precache (entry 0 is ""), or -1.</summary>
    public int SoundIndex(byte* s)
    {
        for (int i = 0; i < MaxSounds && _soundPrecache[i] is not null; i++)
            if (Same(_soundPrecache[i], s))
                return i;
        return -1;
    }

    /// <summary>PF_precache_model's list and sv.models: a new name loads its model (Mod_ForName).</summary>
    public void PrecacheModel(byte* s)
    {
        if (Active)
            throw Vm.RunError(PrecacheOutsideSpawn);
        CheckEmptyString(s);
        for (int i = 0; i < MaxModels; i++)
        {
            if (_modelPrecache[i] is null)
            {
                _modelPrecache[i] = s;
                _models[i] = ModelSource.ForName(s);
                if (_clip is not null)
                    _clip.Models[i] = _models[i];
                return;
            }
            if (Same(_modelPrecache[i], s))
                return;
        }
        throw Vm.RunError(ModelOverflow);
    }
}
