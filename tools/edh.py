# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# Interim .edh reader until the C# edict-diff tool exists (docs/PLAN.md E.2).
# Usage: python3 -I tools/edh.py summary <file.edh> | diff <a.edh> <b.edh>
import struct, sys
def read(path):
    d = open(path, 'rb').read(); p = 12
    assert d[:4] == b'EDH1'; ver, flags = struct.unpack_from('<II', d, 4)
    assert ver in (1, 2, 3, 4), path
    rec = '<IdIQQQQ' if ver >= 3 else '<IdIQQQ'; rl = struct.calcsize(rec)
    maps, frames, spawns, clients = [], [], [], []
    while p < len(d):
        k = d[p:p+1]; p += 1
        if k == b'M':
            name = d[p:p+64].split(b'\0')[0].decode(); crc, ef, ng = struct.unpack_from('<III', d, p+64); p += 76
            maps.append((len(frames), name, crc, ef, ng))
        elif k == b'F':             # version 3: the messages lane (None before)
            fc, t, n, le, lt, lr, *lm = struct.unpack_from(rec, d, p); p += rl
            ents = []
            if flags & 1:
                ents = list(struct.unpack_from('<%dQ' % n, d, p)); p += 8 * n
            frames.append((fc, t, n, le, lt, lr, ents, lm[0] if lm else None))
        elif k == b'S':             # version 2: the frame-0 state after ED_LoadFromFile, always per entity
            fc, t, n, le, lt, lr, *lm = struct.unpack_from(rec, d, p); p += rl
            ents = list(struct.unpack_from('<%dQ' % n, d, p)); p += 8 * n
            spawns.append((fc, t, n, le, lt, lr, ents, maps[-1][1], lm[0] if lm else None))
        elif k == b'C':             # version 4: a client frame of a demo playback
            fc, t, n, lc, le, lf = struct.unpack_from('<IdIQQQ', d, p); p += 40
            ents = []
            if flags & 1:
                ents = list(struct.unpack_from('<%dQ' % n, d, p)); p += 8 * n
            clients.append((fc, t, n, lc, le, lf, ents))
        else: raise SystemExit('bad record at %d' % (p-1))
    return maps, frames, spawns, clients
cmd = sys.argv[1]
if cmd == 'summary':
    maps, fr, sp, cl = read(sys.argv[2])
    if cl:
        print('client frames:', len(cl), 'framecount', cl[0][0], '..', cl[-1][0], ' cl.time', cl[0][1], '..', cl[-1][1],
              ' num_entities', min(c[2] for c in cl), '..', max(c[2] for c in cl), ' distinct client-lane:',
              len({c[3] for c in cl}), ' entities-lane:', len({c[4] for c in cl}), ' effects-lane:', len({c[5] for c in cl}))
    if not fr:
        raise SystemExit(0)
    print('maps:', maps)
    for s in sp:
        print('spawn of %s at framecount %d: %d edicts, lanes %016x %016x' % (s[7], s[0], s[2], s[3], s[4]))
    print('frames:', len(fr), 'framecount', fr[0][0], '..', fr[-1][0], ' sv.time', fr[0][1], '..', fr[-1][1])
    print('num_edicts', fr[0][2], '..', fr[-1][2], ' distinct edicts-lane:', len({f[3] for f in fr}),
          ' typed-globals-lane:', len({f[4] for f in fr}), ' raw-globals-lane:', len({f[5] for f in fr}),
          ' messages-lane:', len({f[7] for f in fr}))
    if fr[0][6]:
        changed = [i for i in range(fr[0][2]) if len({f[6][i] for f in fr if i < f[2]}) > 1]
        print('entities whose hash changes over the run:', len(changed), 'of', fr[-1][2])
elif cmd == 'diff':
    _, a, sa, ca = read(sys.argv[2]); _, b, sb, cb = read(sys.argv[3])
    if ca or cb:
        if len(ca) != len(cb): print('client frame count differs', len(ca), len(cb))
        for i, (x, y) in enumerate(zip(ca, cb)):
            for lane, j in (('cl.time', 1), ('num_entities', 2), ('client', 3), ('entities', 4), ('effects', 5)):
                if x[j] != y[j]:
                    print('first difference: client record %d (framecount %d), lane %s: %r vs %r' % (i, x[0], lane, x[j], y[j]))
                    if x[6] and y[6]:
                        print('  differing entities:', [k for k in range(min(len(x[6]), len(y[6]))) if x[6][k] != y[6][k]][:20])
                    raise SystemExit(1)
        print('identical: %d client frames, all lanes' % len(ca))
    if sa and sb:                   # spawn records only where both files have them (version 1 has none)
        if len(sa) != len(sb): print('spawn count differs', len(sa), len(sb)); raise SystemExit(1)
        for i, (x, y) in enumerate(zip(sa, sb)):
            if x[2:6] != y[2:6] or x[6] != y[6] or (x[8] is not None and y[8] is not None and x[8] != y[8]):
                ents = [k for k in range(min(len(x[6]), len(y[6]))) if x[6][k] != y[6][k]]
                print('spawn %d (%s) differs; entities:' % (i, x[7]), ents[:20]); raise SystemExit(1)
    if len(a) != len(b): print('frame count differs', len(a), len(b))
    for i, (x, y) in enumerate(zip(a, b)):
        for lane, j in (('time', 1), ('num_edicts', 2), ('edicts', 3), ('globals_typed', 4), ('globals_raw', 5),
                        ('messages', 7)):
            if x[j] != y[j] and x[j] is not None and y[j] is not None:     # a version 2 file has no messages lane
                print('first difference: record %d (framecount %d), lane %s: %r vs %r' % (i, x[0], lane, x[j], y[j]))
                if x[6] and y[6]:
                    ents = [k for k in range(min(len(x[6]), len(y[6]))) if x[6][k] != y[6][k]]
                    print('  differing entities:', ents[:20])
                raise SystemExit(1)
    lanes = 'all lanes' if a and a[0][7] is not None and b[0][7] is not None else 'all lanes but messages'
    print('identical: %d frames, %s' % (len(a), lanes) + (', %d spawns' % len(sa) if sa and sb else ''))
