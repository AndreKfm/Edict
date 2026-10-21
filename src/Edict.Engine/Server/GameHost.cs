// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// The engine's own VM host: builtins are the port's (<see cref="Builtins"/>); one the port does not have yet stops
/// with an error and leaves its number in <see cref="Server.UnportedBuiltin"/>.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public readonly unsafe struct GameHost(Server sv) : IProgsHost
{
    private static readonly HostErrorException NotPorted = new("builtin not ported yet (its number: Server.UnportedBuiltin)");

    public void Builtin(int num)
    {
        if (!Builtins.Has(num))
        {
            sv.UnportedBuiltin = num;
            throw NotPorted;
        }
        Builtins.Call(sv, num);
    }

    public byte* String(int ofs) => sv.String(ofs);

    public bool Active => sv.Active;

    public void Called(int fnum) { }
}

/// <summary>PR_ExecuteProgram from engine code with the <see cref="GameHost"/>; no capture points.</summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
public sealed class GameHooks(Server sv) : IServerHooks
{
    public void ExecuteProgram(int fnum)
    {
        var host = new GameHost(sv);
        sv.Vm.Execute(ref host, fnum);
    }

    public void Link(int e, bool touchTriggers) { }

    public void Linked(int e) { }

    public void Unlink(int e) { }
}
