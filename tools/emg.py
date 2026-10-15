# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The messages a reference run sent its client (.emg, ref/src/edict_hash.c -edictmessages), parsed as
# CL_ParseServerMessage reads them (cl_parse.c, cl_tent.c; protocol 15, WinQuake without QUAKE2).
# Usage: python3 -I tools/emg.py census <file.emg> ...      what the client side would parse, counted
#        python3 -I tools/emg.py dem <file.emg> <tape.etp> <out.dem>
#            a Quake demo of what the client received (cl_demo.c's format: the CD track as text, then per message
#            u32 length, three view angles as floats, the bytes), in the order the server sent it; the view angles are
#            those of the tape's last clc_move up to the message's host frame, as a recording client would have had
import struct, sys, collections

SVC = {0: 'bad', 1: 'nop', 2: 'disconnect', 3: 'updatestat', 4: 'version', 5: 'setview', 6: 'sound', 7: 'time',
       8: 'print', 9: 'stufftext', 10: 'setangle', 11: 'serverinfo', 12: 'lightstyle', 13: 'updatename',
       14: 'updatefrags', 15: 'clientdata', 16: 'stopsound', 17: 'updatecolors', 18: 'particle', 19: 'damage',
       20: 'spawnstatic', 22: 'spawnbaseline', 23: 'temp_entity', 24: 'setpause', 25: 'signonnum', 26: 'centerprint',
       27: 'killedmonster', 28: 'foundsecret', 29: 'spawnstaticsound', 30: 'intermission', 31: 'finale', 32: 'cdtrack',
       33: 'sellscreen', 34: 'cutscene'}
TE = {0: 'spike', 1: 'superspike', 2: 'gunshot', 3: 'explosion', 4: 'tarexplosion', 5: 'lightning1', 6: 'lightning2',
      7: 'wizspike', 8: 'knightspike', 9: 'lightning3', 10: 'lavasplash', 11: 'teleport', 12: 'explosion2', 13: 'beam'}


def read(path):
    """The records of an .emg: (kind, host_framecount, data); kind 1/2 a message, 'S'/'F' a state record."""
    d = open(path, 'rb').read()
    assert d[:4] == b'EMG1' and struct.unpack_from('<I', d, 4)[0] == 1, path
    p = 8
    while p < len(d):
        k, fc, n = struct.unpack_from('<BII', d, p)
        p += 9
        yield (k if k in (1, 2) else chr(k)), fc, d[p:p + n]
        p += n


class Reader:
    def __init__(self, data):
        self.d, self.p = data, 0

    def more(self):
        return self.p < len(self.d)

    def byte(self):
        self.p += 1
        return self.d[self.p - 1]

    def char(self):
        return struct.unpack('b', bytes([self.byte()]))[0]

    def short(self):
        self.p += 2
        return struct.unpack_from('<h', self.d, self.p - 2)[0]

    def long(self):
        self.p += 4
        return struct.unpack_from('<i', self.d, self.p - 4)[0]

    def float(self):
        self.p += 4
        return struct.unpack_from('<f', self.d, self.p - 4)[0]

    def string(self):
        e = self.d.index(b'\0', self.p)
        s = self.d[self.p:e]
        self.p = e + 1
        return s

    def coord(self):
        return self.short() / 8


def parse(msg, count):
    """One message, as CL_ParseServerMessage reads it; count gets (what, detail) keys."""
    r = Reader(msg)
    while r.more():
        cmd = r.byte()
        if cmd & 128:                       # CL_ParseUpdate
            bits = cmd & 127
            if bits & 1:
                bits |= r.byte() << 8
            num = r.short() if bits & (1 << 14) else r.byte()
            count['update'] += 1
            for b, name in ((1 << 10, 'model'), (1 << 6, 'frame'), (1 << 11, 'colormap'), (1 << 12, 'skin'),
                            (1 << 13, 'effects'), (1 << 5, 'nolerp'), (1 << 14, 'longentity')):
                if bits & b:
                    count['update ' + name] += 1
            n = sum(1 for b in (1 << 10, 1 << 6, 1 << 11, 1 << 12, 1 << 13) if bits & b)
            n += 2 * sum(1 for b in (2, 4, 8) if bits & b) + sum(1 for b in (1 << 8, 1 << 4, 1 << 9) if bits & b)
            r.p += n
            continue
        name = SVC.get(cmd)
        if name is None:
            raise ValueError('illegible server message %d at %d' % (cmd, r.p - 1))
        count[name] += 1
        if name in ('nop', 'killedmonster', 'foundsecret', 'intermission', 'sellscreen', 'disconnect'):
            pass
        elif name == 'time':
            r.float()
        elif name == 'clientdata':
            bits = r.short() & 0xffff
            for b in (1, 2):                # viewheight, idealpitch
                if bits & b:
                    r.char()
            for i in range(3):
                if bits & (4 << i):
                    r.char()
                if bits & (32 << i):
                    r.char()
            r.long()                        # items (always)
            for b in (1 << 12, 1 << 13, 1 << 14):
                if bits & b:
                    r.byte()
            r.short()
            r.p += 5 + 1                    # currentammo, four ammo, weapon (standard_quake)
            if bits & 2:
                count['clientdata idealpitch'] += 1
        elif name == 'version':
            r.long()
        elif name in ('print', 'centerprint', 'stufftext', 'finale', 'cutscene'):
            r.string()
        elif name == 'damage':
            r.byte(); r.byte(); r.coord(); r.coord(); r.coord()
        elif name == 'serverinfo':
            r.long(); r.byte(); r.byte(); r.string()
            while r.string():
                count['serverinfo model'] += 1
            while r.string():
                count['serverinfo sound'] += 1
        elif name == 'setangle':
            r.p += 3
        elif name == 'setview':
            r.short()
        elif name == 'lightstyle':
            r.byte(); r.string()
        elif name == 'sound':
            mask = r.byte()
            if mask & 1:
                r.byte()
            if mask & 2:
                r.byte()
            r.short(); r.byte(); r.p += 6
        elif name == 'stopsound':
            r.short()
        elif name in ('updatename',):
            r.byte(); r.string()
        elif name == 'updatefrags':
            r.byte(); r.short()
        elif name == 'updatecolors':
            r.byte(); r.byte()
        elif name == 'particle':
            r.p += 6 + 3 + 2
        elif name == 'spawnbaseline':
            r.short(); r.p += 4 + 9
        elif name == 'spawnstatic':
            r.p += 4 + 9
        elif name == 'temp_entity':
            t = r.byte()
            count['temp_entity ' + TE.get(t, str(t))] += 1
            if t in (5, 6, 9, 13):
                r.short(); r.p += 12
            elif t == 12:
                r.p += 6 + 2
            elif t in TE:
                r.p += 6
            else:
                raise ValueError('temp entity %d' % t)
        elif name == 'setpause':
            r.byte()
        elif name == 'signonnum':
            r.byte()
        elif name == 'updatestat':
            count['updatestat %d' % r.byte()] += 1
            r.long()
        elif name == 'spawnstaticsound':
            r.p += 6 + 3
        elif name == 'cdtrack':
            r.byte(); r.byte()
        else:
            raise ValueError(name)
    if r.p != len(msg):
        raise ValueError('message overrun: %d of %d bytes' % (r.p, len(msg)))


def tape_angles(path):
    """(host frame, (pitch, yaw, roll)) of every clc_move on a tape (tools/tapegen.py), in tape order."""
    d = open(path, 'rb').read()
    assert d[:4] == b'ETP1', path
    p, out = 16, []
    while p + 7 <= len(d):
        frame, kind, n = struct.unpack_from('<IBH', d, p)
        m = d[p + 7:p + 7 + n]
        p += 7 + n
        if kind == 2 and m and m[0] == 3:       # clc_move: byte 3, float time, three angles as chars
            a = struct.unpack_from('<bbb', m, 5)
            out.append((frame, tuple(x * (360.0 / 256) for x in a)))
    return out


def dem(emg, tape, out):
    moves = tape_angles(tape)
    i, angles = 0, (0.0, 0.0, 0.0)
    data = bytearray(b'-1\n')                   # CL_Record's forcetrack line: no forced track
    n = 0
    for k, fc, msg in read(emg):
        if k not in (1, 2):
            continue
        while i < len(moves) and moves[i][0] <= fc:
            angles = moves[i][1]
            i += 1
        data += struct.pack('<I3f', len(msg), *angles) + msg
        n += 1
    open(out, 'wb').write(data)
    return n


if __name__ == '__main__':
    if len(sys.argv) == 5 and sys.argv[1] == 'dem':
        print('%s: %d messages' % (sys.argv[4], dem(sys.argv[2], sys.argv[3], sys.argv[4])))
        raise SystemExit(0)
    if len(sys.argv) < 3 or sys.argv[1] != 'census':
        raise SystemExit('usage: emg.py census <file.emg> ... | dem <file.emg> <tape.etp> <out.dem>')
    total = collections.Counter()
    for path in sys.argv[2:]:
        c = collections.Counter()
        for k, fc, data in read(path):
            if k in (1, 2):
                c['messages ' + ('reliable' if k == 1 else 'unreliable')] += 1
                parse(data, c)
        total.update(c)
    for k, v in sorted(total.items(), key=lambda kv: (-kv[1], kv[0])):
        print('%10d  %s' % (v, k))
    print('never:', ', '.join(n for n in SVC.values() if total[n] == 0))
