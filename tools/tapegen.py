# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Generates an input tape (ETP1, format in ref/src/edict_tape.c) from a seed: the signon commands a real client
# sends (CL_SignonReply), then one clc_move per frame following a scripted random walk. Open-loop: nothing depends
# on the game, so the tape is the complete input for both the C reference and the C# port.
# Interim tool (stdlib only, run with python3 -I) until the C# harness exists; the PRNG is splitmix64 so the C#
# port of this generator can reproduce tapes bit for bit.
#
# With --clients <n> (version 2 of the format, P6 step 9): n clients that join one after another, each in sessions:
# the signon, the walk, now and then a host command of a client's (say, say_team, tell, ping, status, pause, kill,
# name, color, ban; with --coop also god, notarget, fly, noclip, give and kick, which deathmatch refuses), windows in
# which the connection cannot send (busy) and one long one, a connection whose sends fail; a session ends with
# clc_disconnect or a read error, so the server has dropped the client before it connects again (a kick, a
# message overflow or a failed send may have dropped it before).
# Usage: python3 -I tools/tapegen.py <out.etp> <seed> <frames> [--clients <n> [--coop]]
import struct
import sys

CLC_DISCONNECT, CLC_MOVE, CLC_STRINGCMD = 2, 3, 4
CONNECT, RELIABLE, UNRELIABLE, ERROR, SENDFAIL, BUSY, READY = 0, 1, 2, 3, 4, 5, 6
MASK = (1 << 64) - 1


class SplitMix64:
    def __init__(self, seed):
        self.state = seed & MASK

    def next(self):
        self.state = (self.state + 0x9E3779B97F4A7C15) & MASK
        z = self.state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
        return z ^ (z >> 31)

    def below(self, n):
        """Uniform integer in [0, n), by the high 32 bits times n (no modulo bias worth caring about here)."""
        return ((self.next() >> 32) * n) >> 32

    def range(self, lo, hi):
        """Uniform integer in [lo, hi]."""
        return lo + self.below(hi - lo + 1)


def stringcmds(*cmds):
    out = bytearray()
    for c in cmds:
        out.append(CLC_STRINGCMD)
        out += c.encode('ascii') + b'\0'
    return bytes(out)


def angle_byte(degrees):
    """What MSG_WriteAngle writes: ((int)f*256/360) & 255, with C truncation toward zero."""
    return (int(degrees) * 256 // 360 if degrees >= 0 else -((-int(degrees)) * 256 // 360)) & 255


def move(pitch, yaw, forward, side, up, buttons, impulse):
    return (bytes([CLC_MOVE]) + struct.pack('<f', 0.0)                      # ping time, ignored by the state
            + bytes([angle_byte(pitch), angle_byte(yaw), 0])               # pitch, yaw, roll
            + struct.pack('<hhh', forward, side, up) + bytes([buttons, impulse]))


def generate(seed, frames):
    rng = SplitMix64(seed)
    records = [(1, CONNECT, b''),
               (3, RELIABLE, stringcmds('prespawn')),
               (5, RELIABLE, stringcmds('name "edict"\n', 'color 0 0\n', 'spawn ')),
               (7, RELIABLE, stringcmds('begin'))]
    walk(rng, 8, frames, records)
    return records


def walk(rng, frame, frames, records):
    """The scripted random walk: one clc_move per frame from frame to frames."""
    yaw, pitch = 0, 0
    while frame <= frames:
        # one segment of the walk: constant intent for 20-200 frames
        length = rng.range(20, 200)
        forward = (0, 200, 400)[rng.below(3)]          # stand, walk, run (cl_forwardspeed 200, run x2)
        side = (-350, 0, 0, 350)[rng.below(4)]
        turn = rng.range(-3, 3)                        # degrees per frame
        target_pitch = rng.range(-30, 30)
        fire = rng.below(100) < 30
        for _ in range(length):
            if frame > frames:
                break
            yaw = (yaw + turn) % 360
            pitch += (target_pitch > pitch) - (target_pitch < pitch)
            buttons = (1 if fire and rng.below(100) < 50 else 0) | (2 if rng.below(100) < 5 else 0)
            impulse = rng.range(1, 8) if rng.below(1000) < 5 else 0   # weapon select only, no cheat impulses
            records.append((frame, UNRELIABLE, move(pitch, yaw, forward, side, 0, buttons, impulse)))
            frame += 1


COMMANDS = ['say hello', 'say "two words"', 'say a line longer than the sixty-four characters say keeps of what it is given',
            'say_team for the team', 'tell edict{o} a word for you', 'tell EDICT{o} in capitals', 'tell nobody hello',
            'tell a_new_name_too_ to the shared name', 'ping', 'status', 'pause', 'kill', 'color {c} {c2}', 'color 15 15',
            'name "edict{c}"', 'name a_new_name_too_long_for_it', 'ban', 'say',
            'god', 'notarget', 'fly', 'fly', 'noclip', 'noclip', 'give s 50', 'give 7', 'give h 250', 'give a 20',
            'kick edict{o}', 'kick edict{o} goodbye now', 'kick # {n}', 'kick # {n} bye', 'god']
COOP = ['give h 0', 'kill', 'fly', 'noclip']     # more of them in coop, where deathmatch's refusal does not apply


def generate_multi(seed, frames, clients, coop):
    """Records (frame, type, client, payload) of n clients in sessions."""
    records = []
    connects = []                                      # the connect frames, for the commands during a signon
    long_session = {0: 2600, 1: 4500}                  # client 0: the flood's target; client 1: over a minute
    for c in range(clients):
        rng = SplitMix64(seed * 1000 + c + 1)
        t = 1 + c * rng.range(15, 60)
        first = True
        while t + 60 < frames:
            connects.append(t)
            records += [(t, CONNECT, c, b''),
                        (t + 2, RELIABLE, c, stringcmds('prespawn')),
                        (t + 4, RELIABLE, c, stringcmds('name "edict%d"\n' % c, 'color %d %d\n' % (c, c * 3 % 14), 'spawn ')),
                        (t + 6, RELIABLE, c, stringcmds('begin'))]
            end = min(frames, t + 8 + rng.range(200, 2400))
            if first and c in long_session:
                end = min(frames, t + long_session[c])
            first = False
            moves = []
            walk(rng, t + 8, end, moves)
            by_frame = {f: m for f, _, m in moves}
            f = t + 8 + rng.range(10, 60)
            while f < end:
                k = rng.below(100)
                if k < 70:
                    pool = COMMANDS + (COOP if coop else [])
                    cmd = pool[rng.below(len(pool))].format(c=c, c2=rng.below(14), o=rng.below(clients),
                                                            n=1 + rng.below(clients))
                    records.append((f, RELIABLE, c, stringcmds(cmd)))
                elif k < 85:
                    records.append((f, BUSY, c, b''))
                    records.append((f + rng.range(1, 60), READY, c, b''))
                elif k < 88:
                    records.append((f, BUSY, c, b''))      # long: the reliable messages pile up, may overflow
                    records.append((f + rng.range(300, 900), READY, c, b''))
                elif k < 93:
                    records.append((f, RELIABLE, c, bytes([1]) + stringcmds('ping')))      # a clc_nop first
                elif k < 96 and c not in long_session:
                    records.append((f, SENDFAIL, c, b''))  # the next send drops the client (crash)
                    end = min(end, f + rng.range(1, 30))
                    break
                f += rng.range(20, 140)
            if c == 1 and end - t > 4400:
                records.append((end - 10, RELIABLE, c, stringcmds('status')))     # a connection over a minute old
            for fr in sorted(by_frame):
                if fr <= end:
                    records.append((fr, UNRELIABLE, c, by_frame[fr]))
            if end >= frames:
                break
            if rng.below(2):
                records.append((end, RELIABLE, c, bytes([CLC_DISCONNECT])))
            else:
                records.append((end, ERROR, c, b''))
            t = end + rng.range(20, 300)
    # client 0's connection cannot send for a long time while the others flood it with prints: its message overflows
    # (SV_SendClientMessages drops it)
    records.append((600, BUSY, 0, b''))
    records.append((2000, READY, 0, b''))
    for c in range(1, clients):
        for f in range(650 + c, 950, 3):
            records.append((f, RELIABLE, c, stringcmds('say ' + 'flood %d ' % c * 7)))
    # two clients of one name: tell reaches the first of them only
    records += [(300, RELIABLE, 0, stringcmds('name twin')), (300, RELIABLE, 1, stringcmds('name twin')),
                (310, RELIABLE, 2, stringcmds('tell twin to the first twin')),
                (320, RELIABLE, 0, stringcmds('name "edict0"')), (320, RELIABLE, 1, stringcmds('name "edict1"'))]
    # the clients' says while another signs on (SV_ClientPrintf skips a client that is not spawned)
    for t in connects:
        for d in (1, 3, 5):
            for c in range(clients):
                records.append((t + d, RELIABLE, c, stringcmds('say during a signon')))
    # sorted by frame, then within a client's frame reliable messages and states before its unreliable one
    order = {CONNECT: 0, RELIABLE: 1, BUSY: 1, READY: 1, SENDFAIL: 1, UNRELIABLE: 2, ERROR: 3}
    records.sort(key=lambda r: (r[0], r[2], order[r[1]]))
    return records


def main():
    out, seed, frames = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    if '--clients' in sys.argv:
        clients = int(sys.argv[sys.argv.index('--clients') + 1])
        data = bytearray(b'ETP1' + struct.pack('<III', 2, seed & 0xFFFFFFFF, frames))
        for frame, kind, client, payload in generate_multi(seed, frames, clients, '--coop' in sys.argv):
            data += struct.pack('<IBBH', frame, kind, client, len(payload)) + payload
    else:
        data = bytearray(b'ETP1' + struct.pack('<III', 1, seed & 0xFFFFFFFF, frames))
        for frame, kind, payload in generate(seed, frames):
            data += struct.pack('<IBH', frame, kind, len(payload)) + payload
    with open(out, 'wb') as f:
        f.write(data)


if __name__ == '__main__':
    main()
