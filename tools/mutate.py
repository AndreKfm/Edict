# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Source-level known-bad variants (docs/TESTING.md R7) for engine code that has no seam to swap it in a test:
# each mutant is one textual change in src/. The tree is copied once to a work directory; per mutant the file is
# restored, the change applied, the engine tests built and the given test classes run. A mutant must make them fail
# ("detected"); one that passes is a blind spot of the gates. A mutant whose text no longer matches is an error.
#
# Usage: python3 -I tools/mutate.py <work dir> [group ...]      (groups below; default all)
# The tests read the corpora from the checkout (EDICT_TRACES, EDICT_DATA, EDICT_CAPTURES are set to it) and need
# EDICT_ORACLE for the oracle-based classes.
import os, shutil, subprocess, sys

# group: (test classes, [(name, file, old, new), ...])
# Not known-bad variants, shown rather than assumed:
# - SV_CheckBottom's midpoint (mins + maxs)*0.5 in float instead of double. Halving is exact in both, and rounding the
#   exact value to float gives the same bits (denormals included).
# - ED_ParseEdict parsing keys with a leading underscore: ED_FindField does not find them in LibreQuake's progs.dat,
#   which has no field whose name begins with one (another progs could have).
# - SV_SpawnServer setting both the coop and the deathmatch global: with coop, deathmatch has just been set to 0, and
#   without coop, coop is 0; either way the other global gets the 0 it has.
# - PF_checkclient's interval with > instead of >=: sv.time - sv.lastchecktime == 0.1 cannot hold. 0.1 as a double is
#   an odd multiple of 2^-55; with sv.time >= 1 (it starts at 1) the difference is exact and a multiple of 2^-52.

# Known blind spots: mutants the gates do not detect yet, each with the reason; reported, not counted as failures.
# They are run like the others, so that one that becomes detected shows (then move it out of here).
KNOWN = {
    'sprite bounds in float': 'differs only for a sprite of odd width or height; the three sprites of LibreQuake are '
        '16x16, 32x32 and 64x64',
    'aim: target centre in float': 'equivalent except on denormals: origin + 0.5*(mins + maxs) rounded once from double '
        'is the correctly rounded float sum (53 >= 2*24 + 2 bits make the double rounding innocuous), and halving in float '
        'is exact unless the sum is denormal. Needs denormal coordinates with an odd last bit',
}
MUTANTS = {
    'link': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.BoxOnPlaneSideTests'], [
        ('item boxes grow by 16, not 15', 'src/Edict.Engine/Server/World.cs',
         'v[EntVars.Absmin] -= 15;', 'v[EntVars.Absmin] -= 16;'),
        ('triggers inserted at the head', 'src/Edict.Engine/Server/World.cs',
         'InsertLinkBefore(ent, TriggerHead(node));', 'InsertLinkBefore(ent, _linkNext[TriggerHead(node)]);'),
        ('solids inserted at the head', 'src/Edict.Engine/Server/World.cs',
         'InsertLinkBefore(ent, SolidHead(node));', 'InsertLinkBefore(ent, _linkNext[SolidHead(node)]);'),
        ('area split axis on a tie', 'src/Edict.Engine/Server/World.cs',
         'anode.Axis = size.X > size.Y ? 0 : 1;', 'anode.Axis = size.X >= size.Y ? 0 : 1;'),
        ('solid leafs kept', 'src/Edict.Engine/Server/World.cs',
         'if (_world->Leafs[leaf].Contents == Bsp.ContentsSolid)\n                return;', ''),
        ('MAX_ENT_LEAFS 15', 'src/Edict.Engine/Server/Edicts.cs',
         'public const int MaxEntLeafs = 16;', 'public const int MaxEntLeafs = 15;'),
        ('BoxOnPlaneSide: dist1 > dist', 'src/Edict.Engine/Mathlib.cs',
         'if (dist1 >= p->Dist)', 'if (dist1 > p->Dist)'),
        ('BoxOnPlaneSide: dist2 <= dist', 'src/Edict.Engine/Mathlib.cs',
         'if (dist2 < p->Dist)', 'if (dist2 <= p->Dist)'),
        ('BOX_ON_PLANE_SIDE: dist < emins', 'src/Edict.Engine/Mathlib.cs',
         'p->Dist <= Vec3.At(emins, p->Type) ? 1', 'p->Dist < Vec3.At(emins, p->Type) ? 1'),
        ('touch without setting time', 'src/Edict.Engine/Server/World.cs',
         '*G(GlobalVars.Time) = (float)Time;\n            Hooks.ExecuteProgram', 'Hooks.ExecuteProgram'),
        ('touch recursion children swapped', 'src/Edict.Engine/Server/World.cs',
         'TouchLinks(ent, n.Child0);', 'TouchLinks(ent, n.Child1);'),
        ('world linked too', 'src/Edict.Engine/Server/World.cs',
         'if (ent == 0)\n            return;             // don\'t add the world', ''),
        ('free unlinks after clearing', 'src/Edict.Engine/Server/Edicts.cs',
         'UnlinkEdict(n);         // unlink from world bsp\n', ''),
    ]),
    'move': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.WorldFuzzTests'], [
        ('ClipToLinks: <= keeps the later entity', 'src/Edict.Engine/Server/Move.cs',
         'trace.Fraction < clip.Trace.Fraction', 'trace.Fraction <= clip.Trace.Fraction'),
        ('MoveBounds without the unit', 'src/Edict.Engine/Server/Move.cs',
         'bmin = s + Vec3.At(mins, i) - 1;', 'bmin = s + Vec3.At(mins, i);'),
        ('missile box 14', 'src/Edict.Engine/Server/Move.cs',
         'clip.Mins2 = new Vec3(-15, -15, -15);', 'clip.Mins2 = new Vec3(-14, -14, -14);'),
        ('NewChaseDir with a float abs', 'src/Edict.Engine/Server/Move.cs',
         'CAbs(deltay) > CAbs(deltax)', 'MathF.Abs(deltay) > MathF.Abs(deltax)'),
        ('MoveToGoal: step before rand', 'src/Edict.Engine/Server/Move.cs',
         'if ((Rand.Next() & 3) == 1 || !StepDirection(ent, v[EntVars.IdealYaw], dist))',
         'if (!StepDirection(ent, v[EntVars.IdealYaw], dist) || (Rand.Next() & 3) == 1)'),
        ('StepDirection: >= 45', 'src/Edict.Engine/Server/Move.cs',
         'if (delta > 45 && delta < 315)', 'if (delta >= 45 && delta < 315)'),
        ('walk yaw in float', 'src/Edict.Engine/Server/Move.cs',
         'yaw = (float)(yaw * Math.PI * 2 / 360);', 'yaw = yaw * MathF.PI * 2 / 360;'),
        ('traceline: no entity is not world', 'src/Edict.Engine/Server/Builtins.cs',
         'trace.Ent >= 0 ? trace.Ent * sv.EdictSize : 0;', 'trace.Ent * sv.EdictSize;'),
        ('ClipToLinks clips against own missiles', 'src/Edict.Engine/Server/Move.cs',
         "if (((int*)t)[EntVars.Owner] == clip.PassEdict * EdictSize)\n                    continue;   // don't clip against own missiles", ''),
        ('ClipToLinks: points interact', 'src/Edict.Engine/Server/Move.cs',
         'if (clip.PassEdict >= 0 && Fields(clip.PassEdict)[EntVars.Size] != 0 && t[EntVars.Size] == 0)\n                continue;   // points never interact', ''),
    ]),
    'spawn': (['Edict.Engine.Tests.SpawnGateTests', 'Edict.Engine.Tests.SpawnParseTests'], [
        ('COM_Parse: unsigned char', 'src/Edict.Engine/Common/ComParse.cs',
         'while ((c = (sbyte)*data) <= \' \')', 'while ((c = *data) <= \' \')'),
        ('COM_Parse: colon not a single token', 'src/Edict.Engine/Common/ComParse.cs',
         "c == '\\'' || c == ':';", "c == '\\'';"),
        ('COM_Parse: no comments', 'src/Edict.Engine/Common/ComParse.cs',
         "if (c == '/' && data[1] == '/')", "if (c == '/' && data[1] == '/' && len < 0)"),
        ('ED_NewString: no newline escape', 'src/Edict.Engine/Server/Spawn.cs',
         "s[i] == 'n' ? (byte)'\\n' : (byte)'\\\\';", "s[i] == 'n' ? (byte)'n' : (byte)'\\\\';"),
        ('ED_NewString: backslash kept with the next character', 'src/Edict.Engine/Server/Spawn.cs',
         "            if (c == '\\\\' && i < l - 1)", "            if (Environment.ProcessorCount < 0)"),
        ('ED_ParseEdict: no anglehack', 'src/Edict.Engine/Server/Spawn.cs',
         'bool anglehack = token.SequenceEqual("angle"u8);', 'bool anglehack = false;'),
        ('ED_ParseEdict: no light hack', 'src/Edict.Engine/Server/Spawn.cs',
         'if (token.SequenceEqual("light"u8))', 'if (Environment.ProcessorCount < 0)'),
        ('ED_ParseEdict: trailing spaces kept', 'src/Edict.Engine/Server/Spawn.cs',
         "while (n != 0 && keyname[n - 1] == ' ')", "while (Environment.ProcessorCount < 0)"),
        ('ED_ParseEdict: world cleared too', 'src/Edict.Engine/Server/Spawn.cs',
         'if (ent != 0)   // hack', 'if (true)'),
        ('ED_ParseEdict: empty entity not freed', 'src/Edict.Engine/Server/Spawn.cs',
         'if (!init)\n            EdictNum(ent)->Free = 1;', 'if (!init)\n            EdictNum(ent)->Free = 0;'),
        ('ED_ParseEpair: vector split at runs of spaces', 'src/Edict.Engine/Server/Spawn.cs',
         "                    w = v = v + 1;", "                    w = v = v + 1;\n                    while (str[v] == ' ') { v++; w++; }"),
        ('ED_ParseEpair: float through Q_atof', 'src/Edict.Engine/Server/Spawn.cs',
         '*(float*)d = (float)CAtof.Atof(s);', '*(float*)d = CAtof.QAtof(s);'),
        ('ED_LoadFromFile: skill 1 uses not-hard', 'src/Edict.Engine/Server/Spawn.cs',
         '(CurrentSkill == 1 && (spawnflags & SpawnflagNotMedium) != 0)', '(CurrentSkill == 1 && (spawnflags & SpawnflagNotHard) != 0)'),
        ('ED_LoadFromFile: skill 3 not hard', 'src/Edict.Engine/Server/Spawn.cs',
         '(CurrentSkill >= 2 && (spawnflags & SpawnflagNotHard) != 0)', '(CurrentSkill == 2 && (spawnflags & SpawnflagNotHard) != 0)'),
        ('ED_LoadFromFile: deathmatch ignores spawnflags', 'src/Edict.Engine/Server/Spawn.cs',
         'if ((spawnflags & SpawnflagNotDeathmatch) != 0)', 'if (Environment.ProcessorCount < 0)'),
        ('SV_SpawnServer: skill not rounded', 'src/Edict.Engine/Server/Spawn.cs',
         'CurrentSkill = CCast.I32(CvarValue("skill"u8) + 0.5);', 'CurrentSkill = CCast.I32(CvarValue("skill"u8));'),
        ('SV_SpawnServer: coop keeps deathmatch', 'src/Edict.Engine/Server/Spawn.cs',
         'if (CvarValue("coop"u8) != 0)\n            CvarSetValue("deathmatch"u8, 0);', ''),
        ('SV_SpawnServer: sv.time 0', 'src/Edict.Engine/Server/Spawn.cs',
         '        Time = 1.0;', '        Time = 0;'),
        ('SV_SpawnServer: client slots not reserved', 'src/Edict.Engine/Server/Spawn.cs',
         '        NumEdicts = MaxClients + 1;', '        NumEdicts = 1;'),
        ('PF_lightstyle: not stored', 'src/Edict.Engine/Server/Builtins.cs',
         '        sv.SetLightstyle(style, val);', ''),
        ('sprite bounds in float', 'src/Edict.Engine/Model/ModelCache.cs',
         'm->Mins = new Vec3(-width / 2, -width / 2, -height / 2);', 'm->Mins = new Vec3(-width / 2f, -width / 2f, -height / 2f);'),
    ]),
    'check': (['Edict.Engine.Tests.VmReplayTests', 'Edict.Engine.Tests.WorldFuzzTests'], [
        ('PointInLeaf: d >= 0 goes to the front', 'src/Edict.Engine/Server/CheckClient.cs',
         'if (d > 0)', 'if (d >= 0)'),
        ('checkclient: leaf index without the -1', 'src/Edict.Engine/Server/CheckClient.cs',
         'int l = PointInLeaf(view) - 1;', 'int l = PointInLeaf(view);'),
        ('checkclient: PVS bits from the top', 'src/Edict.Engine/Server/CheckClient.cs',
         '(_checkPvs[l >> 3] & (1 << (l & 7)))', '(_checkPvs[l >> 3] & (0x80 >> (l & 7)))'),
        ('checkclient: lastchecktime in float', 'src/Edict.Engine/Server/CheckClient.cs',
         'LastCheckTime = Time;', 'LastCheckTime = (float)Time;'),
        ('checkclient: health < 0', 'src/Edict.Engine/Server/CheckClient.cs',
         '|| Fields(ent)[EntVars.Health] <= 0)', '|| Fields(ent)[EntVars.Health] < 0)'),
        ('newcheckclient: notarget ignored', 'src/Edict.Engine/Server/CheckClient.cs',
         'if ((CCast.I32(f[EntVars.Flags]) & FlNoTarget) != 0)\n                continue;', ''),
        ('newcheckclient: PVS from the origin', 'src/Edict.Engine/Server/CheckClient.cs',
         'Vec3 org = Vec3.Add(*(Vec3*)(v + EntVars.Origin), *(Vec3*)(v + EntVars.ViewOfs));',
         'Vec3 org = *(Vec3*)(v + EntVars.Origin);'),
        ('LeafPvs: zero runs one short', 'src/Edict.Engine/Server/CheckClient.cs',
         'int c = vis[i + 1];', 'int c = vis[i + 1] - 1;'),
        ('aim: start 16 up', 'src/Edict.Engine/Server/Builtins.cs',
         'start.Z += 20;', 'start.Z += 16;'),
        ('aim: <= keeps the later target', 'src/Edict.Engine/Server/Builtins.cs',
         'if (dist < bestdist)', 'if (dist <= bestdist)'),
        ('aim: pitch kept', 'src/Edict.Engine/Server/Builtins.cs',
         '            end.Z = dir.Z;\n', ''),
        ('aim: no straight trace', 'src/Edict.Engine/Server/Builtins.cs',
         'if (tr.Ent >= 0 && sv.Fields(tr.Ent)[EntVars.Takedamage] == DamageAim', 'if (false && tr.Ent >= 0 && sv.Fields(tr.Ent)[EntVars.Takedamage] == DamageAim'),
        ('aim: target centre in float', 'src/Edict.Engine/Server/Builtins.cs',
         'end.X = (float)(cv[EntVars.Origin] + 0.5 * (cv[EntVars.Mins] + cv[EntVars.Maxs]));',
         'end.X = cv[EntVars.Origin] + 0.5f * (cv[EntVars.Mins] + cv[EntVars.Maxs]);'),
    ]),
}

COPY = ['src', 'tests', 'analyzers', 'Directory.Build.props', 'Directory.Packages.props', 'global.json', 'nuget.config', 'Edict.slnx']


def run(cmd, cwd, env):
    return subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')


def main():
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    work = os.path.abspath(sys.argv[1])
    groups = sys.argv[2:] or list(MUTANTS)
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    for c in COPY:
        src = os.path.join(repo, c)
        dst = os.path.join(work, c)
        if os.path.isdir(src):
            shutil.copytree(src, dst, ignore=shutil.ignore_patterns('bin', 'obj'))
        else:
            shutil.copy(src, dst)
    env = dict(os.environ)
    env.setdefault('EDICT_TRACES', os.path.join(repo, 'data', 'traces'))
    env.setdefault('EDICT_DATA', os.path.join(repo, 'data', 'libreq-v0.09-beta'))
    env.setdefault('EDICT_CAPTURES', os.path.join(repo, 'data', 'captures'))
    test = os.path.join('tests', 'Edict.Engine.Tests')
    survived = 0
    for g in groups:
        classes, mutants = MUTANTS[g]
        for name, rel, old, new in mutants:
            path = os.path.join(work, rel)
            pristine = open(os.path.join(repo, rel), encoding='utf-8').read()
            if pristine.count(old) != 1:
                print(f'ERROR   {g}: {name}: the text to mutate occurs {pristine.count(old)} times in {rel}')
                survived += 1
                continue
            open(path, 'w', encoding='utf-8', newline='\n').write(pristine.replace(old, new))
            b = run(['dotnet', 'build', test, '-c', 'Release'], work, env)
            if b.returncode != 0:
                print(f'ERROR   {g}: {name}: does not build')
                survived += 1
            else:
                args = ['dotnet', 'run', '--project', test, '-c', 'Release', '--no-build', '--']
                for c in classes:
                    args += ['-class', c]
                t = run(args, work, env)
                failed = t.returncode != 0
                first = next((l.strip() for l in t.stdout.splitlines() if 'first divergence' in l), '')
                known = name in KNOWN
                status = 'detected' if failed else 'KNOWN BLIND' if known else 'SURVIVED'
                print(f'{status} {g}: {name}' + (f'  -- {first[:160]}' if first else f'  -- {KNOWN[name]}' if known and not failed else ''))
                if failed and known:
                    print(f'         {name} is detected now: move it out of KNOWN')
                survived += 0 if failed or known else 1
            open(path, 'w', encoding='utf-8', newline='\n').write(pristine)
            sys.stdout.flush()
    print('all detected' if survived == 0 else f'{survived} not detected or broken')
    return 1 if survived else 0


if __name__ == '__main__':
    sys.exit(main())
