// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>
/// The part of <c>edict_t</c> in front of the progs fields, as far as ported: the free flag and freetime. The port
/// lays it out its own way inside the reference's header size (docs/adr/D7); area links, leafs and the baseline come
/// with step 3c.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct EdictHeader
{
    public int Free;                                  // qboolean
    public float FreeTime;                            // sv.time when the object was freed
}

/// <summary>Edict allocation and numbering (pr_edict.c, progs.h).</summary>
public sealed unsafe partial class Server
{
    public const int MaxEdictsDefault = 600;          // MAX_EDICTS

    private static readonly SysErrorException NoFreeEdicts = new("ED_Alloc: no free edicts");
    private static readonly SysErrorException BadEdictNumber = new("EDICT_NUM: bad number");
    private static readonly SysErrorException BadEdictPointer = new("NUM_FOR_EDICT: bad pointer");

    /// <summary>sv.max_edicts.</summary>
    public int MaxEdicts { get; init; } = MaxEdictsDefault;

    /// <summary>svs.maxclients: edicts 1..maxclients are the clients'.</summary>
    public int MaxClients { get; set; } = 1;

    /// <summary>sv.num_edicts.</summary>
    public int NumEdicts { get; set; }

    /// <summary>pr_edict_size: the reference's header plus the progs fields.</summary>
    public int EdictSize => _edictSize;

    /// <summary>EDICT_NUM: edict n's header; Sys_Error outside 0..max_edicts-1.</summary>
    public EdictHeader* EdictNum(int n)
    {
        if (n < 0 || n >= MaxEdicts)
            throw BadEdictNumber;
        return (EdictHeader*)(Edicts + n * EdictSize);
    }

    /// <summary>NUM_FOR_EDICT of the edict at byte offset e (PROG_TO_EDICT): Sys_Error outside 0..num_edicts-1.</summary>
    public int NumForEdict(int e)
    {
        int b = e / EdictSize;
        if (b < 0 || b >= NumEdicts)
            throw BadEdictPointer;
        return b;
    }

    /// <summary>The fields of edict n (<c>&amp;EDICT_NUM (n)->v</c>).</summary>
    public float* Fields(int n) => (float*)((byte*)EdictNum(n) + HeaderBytes);

    /// <summary>The fields of the edict at byte offset e (<c>&amp;PROG_TO_EDICT (e)->v</c>), unchecked as in C.</summary>
    public float* FieldsAt(int e) => (float*)(Edicts + e + HeaderBytes);

    /// <summary>ED_ClearEdict.</summary>
    private void ClearEdict(int n)
    {
        new Span<byte>(Fields(n), _entityFields * 4).Clear();
        EdictNum(n)->Free = 0;
    }

    /// <summary>
    /// ED_Alloc: the first free edict after the clients that was freed in the first two seconds or more than half
    /// a second ago (the first couple seconds of server time can involve a lot of freeing and allocating, so relax
    /// the replacement policy), else a new one at the end. Returns its number.
    /// </summary>
    public int Alloc()
    {
        int i;
        for (i = MaxClients + 1; i < NumEdicts; i++)
        {
            EdictHeader* e = EdictNum(i);
            if (e->Free != 0 && (e->FreeTime < 2 || Time - e->FreeTime > 0.5))
            {
                ClearEdict(i);
                return i;
            }
        }

        if (i == MaxEdictsDefault)          // C compares with MAX_EDICTS, then EDICT_NUM with sv.max_edicts
            throw NoFreeEdicts;

        NumEdicts++;
        ClearEdict(i);
        return i;
    }

    /// <summary>
    /// ED_Free: marks the edict free and clears the fields the client sees. Unlinking it from the world (the first
    /// statement in C) comes with the area nodes in step 3c.
    /// </summary>
    public void Free(int n)
    {
        EdictHeader* ed = EdictNum(n);
        float* v = Fields(n);

        ed->Free = 1;
        ((int*)v)[EntVars.Model] = 0;
        v[EntVars.Takedamage] = 0;
        v[EntVars.Modelindex] = 0;
        v[EntVars.Colormap] = 0;
        v[EntVars.Skin] = 0;
        v[EntVars.Frame] = 0;
        v[EntVars.Origin] = v[EntVars.Origin + 1] = v[EntVars.Origin + 2] = 0;
        v[EntVars.Angles] = v[EntVars.Angles + 1] = v[EntVars.Angles + 2] = 0;
        v[EntVars.Nextthink] = -1;
        v[EntVars.Solid] = 0;

        ed->FreeTime = (float)Time;
    }
}
