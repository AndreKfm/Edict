# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# Interim reader for VM capture files (.ecp, ref/src/edict_capture.c) until the C# harness reads them.
# Usage: python3 -I tools/ecp.py census <file.ecp>...
#   per builtin: calls during spawn (sv.state loading) and during frames, over all files; then the QuakeC
#   functions the engine enters, and the number of distinct QuakeC functions reached.
import os, re, struct, sys
from collections import Counter

def builtin_names():
    # LibreQuake's progs.dat carries no names for its builtins: take them from the engine's pr_builtin[] table
    src = open(os.path.join(os.path.dirname(__file__), '..', 'ref', 'upstream', 'WinQuake', 'pr_cmds.c'), encoding='latin-1').read()
    body = src[src.index('builtin_t pr_builtin[] =') :]
    body = body[body.index('{') + 1 : body.index('};')]
    names, keep = [], True          # compiled without QUAKE2: the #ifdef QUAKE2 branch is dropped
    for line in body.splitlines():
        t = line.strip()
        if t.startswith('#ifdef QUAKE2'):
            keep = False
        elif t.startswith('#else'):
            keep = not keep
        elif t.startswith('#endif'):
            keep = True
        elif keep:
            names += [n.strip() for n in re.sub(r'//.*', '', t).split(',') if n.strip()]
    return names

def read(path):
    d = open(path, 'rb').read()
    version = struct.unpack_from('<I', d, 4)[0]
    assert d[:4] == b'ECP1' and version in (1, 2, 3, 4, 5, 6, 7, 8), path
    p = 8; names = {}; state = 0; m = None

    def diff(p):                    # version 2: a state diff after E, L, B and R
        if version < 2:
            return p
        _, n = struct.unpack_from('<II', d, p); p += 8 + 8 * n
        strings, = struct.unpack_from('<I', d, p); p += 4
        for _ in range(strings):
            p = d.index(b'\0', p + 4) + 1
        if version >= 4:            # edict headers
            h, = struct.unpack_from('<I', d, p); p += 4 + 12 * h
        return p

    while p < len(d):
        k = d[p:p+1]; p += 1
        if k == b'M':
            m = d[p:p+64].split(b'\0')[0].decode(); p += 64 + (24 if version >= 2 else 0); names = {}
            if version >= 3:
                p += 4 + (4 if version >= 4 else 0)
                ncvars, = struct.unpack_from('<I', d, p); p += 4
                for _ in range(2 * ncvars):
                    p = d.index(b'\0', p) + 1
        elif k == b'F':
            fnum, = struct.unpack_from('<I', d, p); p += 4
            e = d.index(b'\0', p); names[fnum] = d[p:e].decode('latin-1'); p = e + 1
        elif k == b'E':
            fnum, frame, state, self_, other = struct.unpack_from('<IIBII', d, p); p = diff(p + 17 + (12 if version >= 3 else 0))
            yield ('enter', m, state, names.get(fnum, fnum), frame)
        elif k == b'L':
            p = diff(p + 16)
        elif k == b'C':
            fnum, = struct.unpack_from('<I', d, p); p += 4
            yield ('call', m, state, names.get(fnum, fnum), None)
        elif k == b'B':
            num, fnum, argc = struct.unpack_from('<HIB', d, p); p = diff(p + 7 + 12 * argc)
            yield ('builtin', m, state, (num, names.get(fnum, fnum)), None)
        elif k == b'K':
            p = diff(p + 5)
        elif k == b'k':
            nl, = struct.unpack_from('<I', d, p + 4); p = diff(p + 8 + 2 * nl + 5 + (4 if version >= 6 else 0))
        elif k == b'U':
            p += 4
        elif k == b'H':             # version 8: Host_ServerFrame begins
            p = diff(p + 24)
            yield ('frame', m, state, None, None)
        elif k == b'S':             # version 7: the frame-0 point after ED_LoadFromFile
            p += 16
            for _ in range(64):
                p += 1
                if d[p - 1]:
                    p = d.index(b'\0', p) + 1
            p = diff(p)
            yield ('spawn', m, state, None, None)
        elif k == b'R':
            p = diff(p + 14 + (12 if version >= 3 else 0))
        else:
            raise SystemExit('%s: bad record %r at %d' % (path, k, p - 1))

if sys.argv[1] == 'census':
    spawn, frames, enters, qc = Counter(), Counter(), Counter(), set()
    maps_per_builtin = {}
    for path in sys.argv[2:]:
        for kind, m, state, what, _ in read(path):
            if kind == 'builtin':
                (spawn if state == 0 else frames)[what] += 1
                maps_per_builtin.setdefault(what, set()).add(m)
            elif kind == 'enter':
                enters[(state, what)] += 1
                qc.add(what)
            else:
                qc.add(what)
    bnames = builtin_names()
    allb = sorted(set(spawn) | set(frames), key=lambda b: -(spawn[b] + frames[b]))
    print('builtins reached: %d (spawn %d, frames %d)' % (len(allb), len(spawn), len(frames)))
    print('%4s %-20s %10s %10s %5s' % ('#', 'name', 'spawn', 'frames', 'maps'))
    for b in allb:
        print('%4d %-20s %10d %10d %5d' % (b[0], b[1] or bnames[b[0]], spawn[b], frames[b], len(maps_per_builtin[b])))
    print('QuakeC functions reached: %d' % len(qc))
    print('engine entries (state, function): %d kinds' % len(enters))
    for (state, f), n in sorted(enters.items(), key=lambda x: -x[1])[:25]:
        print('  %-8s %-28s %8d' % ('spawn' if state == 0 else 'frames', f, n))
