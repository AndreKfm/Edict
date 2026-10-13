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
# Not a known-bad variant, shown rather than assumed: SV_CheckBottom's midpoint (mins + maxs)*0.5 in float instead of
# double. Halving is exact in both, and rounding the exact value to float gives the same bits (denormals included).

# Known blind spots: mutants the gates do not detect yet, each with the reason; reported, not counted as failures.
# They are run like the others, so that one that becomes detected shows (then move it out of here).
KNOWN = {
    'MoveBounds without the unit': 'the move box only preselects; absmin/absmax are already one unit larger. Needs a '
        'SV_Move fuzz on a world in the oracle (cut 3)',
    'missile box 14': 'MOVE_MISSILE comes from SV_PushEntity (physics, not ported yet); the ported callers never pass it',
    'ClipToLinks clips against own missiles': 'missiles move in physics (not ported); a traceline through its own '
        'missile does not occur in the captures',
    'NewChaseDir with a float abs': 'needs |deltax| and |deltay| that truncate to the same int and rand () & 1 == 0 in the '
        'same call; not in the captures. Needs a SV_NewChaseDir fuzz on a world in the oracle (cut 3)',
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
    'move': (['Edict.Engine.Tests.VmReplayTests'], [
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
