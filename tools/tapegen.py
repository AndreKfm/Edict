# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Generates an input tape (ETP1, format in ref/src/edict_tape.c) from a seed: the signon commands a real client
# sends (CL_SignonReply), then one clc_move per frame following a scripted random walk. Open-loop: nothing depends
# on the game, so the tape is the complete input for both the C reference and the C# port.
# Interim tool (stdlib only, run with python3 -I) until the C# harness exists; the PRNG is splitmix64 so the C#
# port of this generator can reproduce tapes bit for bit.
#
# Usage: python3 -I tools/tapegen.py <out.etp> <seed> <frames>
import struct
import sys

CLC_MOVE, CLC_STRINGCMD = 3, 4
CONNECT, RELIABLE, UNRELIABLE = 0, 1, 2
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
    yaw, pitch = 0, 0
    frame = 8
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
    return records


def main():
    out, seed, frames = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    data = bytearray(b'ETP1' + struct.pack('<III', 1, seed & 0xFFFFFFFF, frames))
    for frame, kind, payload in generate(seed, frames):
        data += struct.pack('<IBH', frame, kind, len(payload)) + payload
    with open(out, 'wb') as f:
        f.write(data)


if __name__ == '__main__':
    main()
