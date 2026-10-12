# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# Interim .edh reader until the C# edict-diff tool exists (docs/PLAN.md E.2).
# Usage: python3 -I tools/edh.py summary <file.edh> | diff <a.edh> <b.edh>
import struct, sys
def read(path):
    d = open(path, 'rb').read(); p = 12
    assert d[:4] == b'EDH1'; ver, flags = struct.unpack_from('<II', d, 4)
    maps, frames = [], []
    while p < len(d):
        k = d[p:p+1]; p += 1
        if k == b'M':
            name = d[p:p+64].split(b'\0')[0].decode(); crc, ef, ng = struct.unpack_from('<III', d, p+64); p += 76
            maps.append((len(frames), name, crc, ef, ng))
        elif k == b'F':
            fc, t, n, le, lt, lr = struct.unpack_from('<IdIQQQ', d, p); p += 40
            ents = []
            if flags & 1:
                ents = list(struct.unpack_from('<%dQ' % n, d, p)); p += 8 * n
            frames.append((fc, t, n, le, lt, lr, ents))
        else: raise SystemExit('bad record at %d' % (p-1))
    return maps, frames
cmd = sys.argv[1]
if cmd == 'summary':
    maps, fr = read(sys.argv[2])
    print('maps:', maps)
    print('frames:', len(fr), 'framecount', fr[0][0], '..', fr[-1][0], ' sv.time', fr[0][1], '..', fr[-1][1])
    print('num_edicts', fr[0][2], '..', fr[-1][2], ' distinct edicts-lane:', len({f[3] for f in fr}),
          ' typed-globals-lane:', len({f[4] for f in fr}), ' raw-globals-lane:', len({f[5] for f in fr}))
    if fr[0][6]:
        changed = [i for i in range(fr[0][2]) if len({f[6][i] for f in fr if i < f[2]}) > 1]
        print('entities whose hash changes over the run:', len(changed), 'of', fr[-1][2])
elif cmd == 'diff':
    _, a = read(sys.argv[2]); _, b = read(sys.argv[3])
    if len(a) != len(b): print('frame count differs', len(a), len(b))
    for i, (x, y) in enumerate(zip(a, b)):
        for lane, j in (('time', 1), ('num_edicts', 2), ('edicts', 3), ('globals_typed', 4), ('globals_raw', 5)):
            if x[j] != y[j]:
                print('first difference: record %d (framecount %d), lane %s: %r vs %r' % (i, x[0], lane, x[j], y[j]))
                if x[6] and y[6]:
                    ents = [k for k in range(min(len(x[6]), len(y[6]))) if x[6][k] != y[6][k]]
                    print('  differing entities:', ents[:20])
                raise SystemExit(1)
    print('identical: %d frames, all lanes' % len(a))
