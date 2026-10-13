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
/// The server state the builtins work on (server.h <c>sv</c> and the globals around it), as far as cut 2 has ported
/// it: the VM and its memory, the rand stream, the clock, cvars, the string temp of ftos/vtos and the precache lists.
/// Edict headers, area nodes and messages come with steps 3b and 3c.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed unsafe partial class Server
{
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
    public void InitPrecache(byte* empty, ReadOnlySpan<nint> models)
    {
        Array.Clear(_soundPrecache);
        Array.Clear(_modelPrecache);
        _soundPrecache[0] = empty;
        _modelPrecache[0] = empty;
        for (int i = 0; i < models.Length; i++)
            _modelPrecache[1 + i] = (byte*)models[i];
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

    /// <summary>PF_precache_model's list. Loading the model (Mod_ForName) comes with setmodel, step 3c.</summary>
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
                return;
            }
            if (Same(_modelPrecache[i], s))
                return;
        }
        throw Vm.RunError(ModelOverflow);
    }
}
