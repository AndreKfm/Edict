# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The frame buffers of a screen dump (-edictscreendump, ref/src/edict_vid.c) as PNG images, shown through their
# palettes, and where two dumps differ.
# Usage: python3 -I tools/screen.py png <dump> <out-dir>          one <framecount>_<n>.png per present
#        python3 -I tools/screen.py diff <dump a> <dump b> [<out.png>]
#                                                                 the first present that differs, its differing
#                                                                 pixels' bounding box; the two side by side
import os, struct, sys, zlib

W, H = 320, 200


def presents(path):
    """(framecount, frame buffer, palette) of every VID_Update in a dump."""
    d = open(path, 'rb').read()
    p, frame = 0, None
    while p < len(d):
        fc, kind, n = struct.unpack_from('<IBI', d, p)
        p += 9
        data = d[p:p + n]
        p += n
        if kind == ord('V'):
            frame = data
        elif kind == ord('P'):
            yield fc, frame, data


def png(path, width, height, rgb_rows):
    raw = b''.join(b'\0' + row for row in rgb_rows)

    def chunk(t, data):
        return struct.pack('>I', len(data)) + t + data + struct.pack('>I', zlib.crc32(t + data) & 0xffffffff)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
                + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def rows(frame, palette):
    for y in range(H):
        yield b''.join(palette[3 * c:3 * c + 3] for c in frame[y * W:(y + 1) * W])


cmd = sys.argv[1]
if cmd == 'png':
    os.makedirs(sys.argv[3], exist_ok=True)
    seen = {}
    for fc, frame, pal in presents(sys.argv[2]):
        n = seen.get(fc, 0)
        seen[fc] = n + 1
        png(os.path.join(sys.argv[3], '%06d_%d.png' % (fc, n)), W, H, rows(frame, pal))
    print(sum(seen.values()), 'images')
elif cmd == 'diff':
    for i, (x, y) in enumerate(zip(presents(sys.argv[2]), presents(sys.argv[3]))):
        if x[1] == y[1] and x[2] == y[2]:
            continue
        px = [j for j in range(W * H) if x[1][j] != y[1][j]]
        print('present %d (framecount %d vs %d): %d pixels differ' % (i, x[0], y[0], len(px))
              + (', box x %d..%d y %d..%d' % (min(j % W for j in px), max(j % W for j in px), px[0] // W, px[-1] // W)
                 if px else '') + ('' if x[2] == y[2] else ', palette differs'))
        if len(sys.argv) > 4:
            png(sys.argv[4], 2 * W, H, (a + b for a, b in zip(rows(x[1], x[2]), rows(y[1], y[2]))))
        raise SystemExit(1)
    print('identical')
