# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# Synthetic demos for the client gate of P5 (docs/PLAN.md): server messages that reach what the corpus's demos
# rarely or never do, written as a Quake demo (cl_demo.c's format). The reference plays them like the corpus demos
# (tools/demos.sh); nothing here needs to be what a server would send, only what CL_ParseServerMessage accepts.
#   - message times: ordinary steps, a gap of exactly 0.1f, gaps above 0.1, the same time twice, jumps back (which
#     snap cl.time to a message time), and a light that is checked exactly at its die time
#   - entities: updates with every bit, numbers from 256 on, moves of 99 to 101 units, angles across +-180, model
#     changes, models with synctype ST_RAND and every trail flag (copies of progs/armor.mdl, written next to the
#     demos), every effect bit
#   - every temp entity type (lightning from the view entity too), particles of every count (255 is 1024), damage,
#     stats, killed monsters and found secrets, light styles, the scoreboard, setangle, setpause, the bonus flash,
#     intermission, finale and cutscene, cdtrack, nops, prints
#   - from seed 5 on, the screen's profile (P6, step 3: what the 2D layer shows): four clients (seed 5 a deathmatch),
#     frags with ties, prints with the chat marks 1 and 2, with \r and with words longer than a line, center prints
#     of one to six lines and of lines longer than 40 characters, and late in the demo the intermission, a finale
#     with a long text and a cutscene, after frames with a view and a gun; seeds 1 to 4 draw as before
#   - from seed 7 on, the renderer's profile (P6, steps 4 and 5): 250 entities kept around the view entity, from 24
#     to 600 units, half of them start.bsp's inline models (the edges and surfaces of the edge list run out, the
#     alias models clip at the near plane), and models LibreQuake's do not reach (write_render_models): every
#     sprite type, a sprite frame group, an alias skin group, alias frame groups and a seam; seeds 1 to 6 stay as
#     they were
#   - seed 8, the sound's profile (P6, step 8), the renderer's with sounds of its own (a second rand stream, so the
#     rest is drawn as for any seed): sounds on entity channels that an svc_stopsound stops while they play (C's
#     S_StopSound only finds channels 0 to 7), the same sound twice in a frame, and looped static sounds near the
#     view, several of one sample (S_Update combines them) and one at 44100 Hz (resampled, its loop start scaled)
# Usage: python3 -I tools/demogen.py <data dir (libreq-v0.09-beta)> <seed> <frames> <out.dem>
import os, random, struct, sys

ARMOR = 'progs/armor.mdl'
FLAGS = [0, 1, 2, 4, 8, 16, 32, 64, 128]


def pak_file(datadir, name):
    for i in range(10):
        path = os.path.join(datadir, 'id1', 'pak%d.pak' % i)
        if not os.path.exists(path):
            continue
        d = open(path, 'rb').read()
        ofs, ln = struct.unpack_from('<ii', d, 4)
        for k in range(ln // 64):
            n, fo, fl = struct.unpack_from('<56sii', d, ofs + 64 * k)
            if n.split(b'\0')[0].decode() == name:
                return d[fo:fo + fl]
    raise SystemExit(name + ' not found in the paks')


def write_models(datadir):
    """Copies of progs/armor.mdl with synctype ST_RAND and each trail flag: edict/rand<flag>.mdl."""
    mdl = bytearray(pak_file(datadir, ARMOR))
    names = []
    for f in FLAGS:
        m = bytearray(mdl)
        struct.pack_into('<ii', m, 72, 1, f)          # mdl_t.synctype = ST_RAND, mdl_t.flags
        name = 'edict/rand%d.mdl' % f
        open(os.path.join(datadir, 'id1', name), 'wb').write(m)
        names.append(name)
    return names


EXPLOD = 'progs/s_explod.spr'
PLAYER = 'progs/player.mdl'


def write_render_models(datadir):
    """
    Seed 7's models, what LibreQuake's do not reach: s_explod.spr (SPR_VP_PARALLEL, six single frames) as each
    sprite type (edict/spr<type>.spr; type 3 with a beam length), and as one frame group and one single frame
    (edict/sprgrp.spr); player.mdl (one skin, single frames) with a skin group of the skin and its inverse beside the
    skin (edict/skingrp.mdl), with its frames in groups of three (edict/framegrp.mdl), and with back-facing
    triangles and seam vertices (edict/seam.mdl).
    """
    out = []
    spr = pak_file(datadir, EXPLOD)
    numframes = struct.unpack_from('<i', spr, 24)[0]
    frames, p = [], 36
    for _ in range(numframes):
        w, h = struct.unpack_from('<ii', spr, p + 12)
        frames.append(spr[p + 4:p + 20 + w * h])        # dspriteframe_t and its pixels
        p += 20 + w * h
    for t in range(5):
        s = bytearray(spr)
        struct.pack_into('<i', s, 8, t)                  # dsprite_t.type
        if t == 3:
            struct.pack_into('<f', s, 28, 8.0)           # dsprite_t.beamlength
        out.append(('edict/spr%d.spr' % t, s))
    g = bytearray(spr[:36])
    struct.pack_into('<i', g, 24, 2)                     # numframes: the group and one single frame
    g += struct.pack('<ii', 1, numframes)                # SPR_GROUP, dspritegroup_t.numframes
    for k in range(numframes):
        g += struct.pack('<f', 0.1 * (k + 1))            # dspriteinterval_t
    for f in frames:
        g += f
    g += struct.pack('<i', 0) + frames[0]                # SPR_SINGLE
    out.append(('edict/sprgrp.spr', g))

    mdl = pak_file(datadir, PLAYER)
    numskins, sw, sh, numverts, numtris, nframes = struct.unpack_from('<6i', mdl, 48)
    assert numskins == 1 and struct.unpack_from('<i', mdl, 84)[0] == 0
    skin = mdl[88:88 + sw * sh]
    rest = 88 + sw * sh
    body_end = rest + numverts * 12 + numtris * 16       # stverts, triangles
    body = mdl[rest:body_end]
    fsize = 4 + 4 + 4 + 16 + numverts * 4                # type, bboxmin, bboxmax, name, verts
    fr = [mdl[body_end + k * fsize + 4:body_end + (k + 1) * fsize] for k in range(nframes)]
    s = bytearray(mdl[:84])
    struct.pack_into('<i', s, 48, 2)                     # numskins: the group and the single skin
    s += struct.pack('<ii', 1, 2) + struct.pack('<ff', 0.3, 0.6)      # ALIAS_SKIN_GROUP of two
    s += skin + bytes(255 - b for b in skin)
    s += struct.pack('<i', 0) + skin
    s += body + mdl[body_end:]
    out.append(('edict/skingrp.mdl', s))
    f = bytearray(mdl[:84])
    groups = [fr[k:k + 3] for k in range(0, nframes, 3)]
    struct.pack_into('<i', f, 68, len(groups))           # numframes
    f += mdl[84:body_end]
    for grp in groups:
        f += struct.pack('<ii', 1, len(grp)) + grp[0][0:8]                # ALIAS_GROUP, numframes, bboxmin, bboxmax
        for k in range(len(grp)):
            f += struct.pack('<f', 0.1 * (k + 1))
        for one in grp:
            f += one
    out.append(('edict/framegrp.mdl', f))
    # LibreQuake's models have no seam: player.mdl with every other triangle back-facing (facesfront 0) and its
    # vertices in the skin's left half on the seam, so that the seam's s + skinwidth/2 stays inside the skin
    m = bytearray(mdl)
    for i in range(numverts):
        if (struct.unpack_from('<i', m, rest + 12 * i + 4)[0]) < sw // 2:
            struct.pack_into('<i', m, rest + 12 * i, 0x20)                # stvert_t.onseam = ALIAS_ONSEAM
    for i in range(0, numtris, 2):
        struct.pack_into('<i', m, rest + numverts * 12 + 16 * i, 0)      # dtriangle_t.facesfront
    out.append(('edict/seam.mdl', m))
    names = []
    for name, data in out:
        open(os.path.join(datadir, 'id1', name), 'wb').write(data)
        names.append(name)
    return names


def f32(x):
    return struct.unpack('<f', struct.pack('<f', x))[0]


class Msg:
    def __init__(self):
        self.b = bytearray()

    def byte(self, v): self.b.append(v & 255)
    def char(self, v): self.b += struct.pack('<b', max(-128, min(127, v)))
    def short(self, v): self.b += struct.pack('<h', v)
    def long(self, v): self.b += struct.pack('<i', v)
    def float(self, v): self.b += struct.pack('<f', v)
    def string(self, s): self.b += s.encode('latin-1') + b'\0'
    def coord(self, v): self.short(int(v * 8))        # MSG_WriteCoord: (int)(f*8)
    def angle(self, v): self.byte(int(v) * 256 // 360) # MSG_WriteAngle: ((int)f*256/360) & 255


class Ent:
    def __init__(self, rng, num, nmodels):
        self.num = num
        self.model = rng.randrange(1, nmodels)
        self.frame = rng.randrange(4)
        self.colormap = 0
        self.skin = 0
        self.effects = 0
        self.origin = [rng.uniform(-1500, 1500) for _ in range(3)]
        self.angles = [rng.choice([0, 90, 180, 270, rng.uniform(0, 359)]) for _ in range(3)]


def generate(datadir, seed, frames):
    rng = random.Random(seed)
    extra = write_models(datadir)
    screen = seed >= 5
    render = seed >= 7
    sound = seed == 8
    srng = random.Random(seed * 7919)                  # the sound profile's own stream
    if render:
        extra += write_render_models(datadir)
    maxclients = rng.choice([1, 2, 4])
    if screen:
        maxclients = 4
    models = ['maps/start.bsp'] + ['*%d' % i for i in range(1, 31)] + [
        'progs/player.mdl', 'progs/armor.mdl', 'progs/grenade.mdl', 'progs/missile.mdl', 'progs/gib1.mdl',
        'progs/zom_gib.mdl', 'progs/w_spike.mdl', 'progs/k_spike.mdl', 'progs/v_spike.mdl', 'progs/lavaball.mdl',
        'progs/s_explod.spr', 'progs/s_bubble.spr'] + extra
    nmodels = 1 + len(models)
    # the screen's profile draws the gun (R_DrawViewModel), which must be an alias model
    weapons = [1 + i for i, n in enumerate(models) if n.endswith('.mdl')] if screen else None
    sounds = ['weapons/r_exp3.wav', 'misc/h2ohit1.wav', 'player/death1.wav']
    if sound:
        sounds += ['ambience/fire1.wav', 'ambience/comp1.wav', 'ambience/drone6.wav']   # 4 to 6: looped
    out = []                                           # (view angles, message bytes)
    view = [0.0, 0.0, 0.0]

    def emit(m):
        for i in range(3):
            if rng.random() < 0.02:
                view[i] += rng.choice([-200, 200, 190, -190])
            else:
                view[i] += rng.uniform(-3, 3)
        out.append((tuple(f32(v) for v in view), bytes(m.b)))

    # signon 1: SV_SendServerinfo
    m = Msg()
    m.byte(8); m.string('\x02\nVERSION 1.09 SERVER (62091 CRC)\n')
    gametype = rng.randrange(2)
    if screen:
        gametype = 1 if seed == 5 else 0
    m.byte(11); m.long(15); m.byte(maxclients); m.byte(gametype); m.string('Synthetic Demo %d' % seed)
    for n in models:
        m.string(n)
    m.string('')
    for n in sounds:
        m.string(n)
    m.string('')
    m.byte(32); m.byte(2); m.byte(2)                   # cdtrack
    m.byte(5); m.short(1)                              # setview
    m.byte(25); m.byte(1)
    emit(m)

    # signon 2: the signon buffer (baselines, statics, static sounds)
    ents = {}
    nums = list(range(1, 41)) + [255, 256, 300, 599]
    if render:
        nums = list(range(1, 251))                  # up to MAX_VISEDICTS around the view: the edges and surfaces run out
    m = Msg()
    for num in nums:
        e = Ent(rng, num, nmodels)
        if render and num != 1:
            e.near = rng.choice([24, 48, 100, 250, 600])    # the distance it keeps from the view entity
            if rng.random() < 0.5:
                e.model = rng.randrange(2, 32)              # an inline model of start.bsp
            e.origin = [ents[1].origin[i] + rng.uniform(-e.near, e.near) for i in range(3)]
        ents[num] = e
        m.byte(22); m.short(num)
        m.byte(e.model); m.byte(e.frame); m.byte(e.colormap); m.byte(e.skin)
        for i in range(3):
            m.coord(e.origin[i]); m.angle(e.angles[i])
    for _ in range(rng.randrange(5, 20)):
        m.byte(20); m.byte(rng.randrange(1, nmodels)); m.byte(rng.randrange(4)); m.byte(0); m.byte(0)
        for i in range(3):
            m.coord(rng.uniform(-1000, 1000)); m.angle(rng.uniform(0, 359))
    for _ in range(3):
        m.byte(29)
        for i in range(3):
            m.coord(rng.uniform(-1000, 1000))
        m.byte(rng.randrange(1, 4)); m.byte(255); m.byte(64)
    if sound:
        for s in [4, 4, 4, 5, 6, 4, 6]:                 # looped statics around the view: S_Update combines a sample's
            m.byte(29)
            for i in range(3):
                m.coord(max(-4000, min(4000, ents[1].origin[i] + srng.uniform(-300, 300))))
            m.byte(s); m.byte(srng.choice([128, 255])); m.byte(srng.choice([32, 64, 128]))
    m.byte(25); m.byte(2)
    emit(m)

    # signon 3: the spawn reply. CL_ParseServerInfo set cl.time to 0 in the middle of the frame, and until the
    # signon is complete the client reads without looking at the times. In a steady playback the client reads until
    # a message's time reaches cl.time, after a message it had to read, so at CL_LerpPoint mtime[1] < cl.time <=
    # mtime[0]: frac is in [0, 1] and f is never 0. In this frame the spawn reply's time is mtime[1] and the first
    # datagram's (>= 0, so that the reading stops there) mtime[0], which hits one edge per seed; a time that goes
    # back (mtime[0] < mtime[1]) makes f negative and frac 1 or more
    edge = seed % 4
    spawn_time, first_time = {1: (0.05, 0.0001),      # frac = 0.05 / 0.0499: in (1, 1.01]
                               2: (0.05, 0.002),      # frac = 0.05 / 0.048 > 1.01: cl.time snaps to mtime[0]
                               3: (0.0, f32(0.1)),    # f exactly 0.1f
                               0: (0.5, 0.5)}[edge]   # f == 0: cl.time = 0.5 = an explosion's die (0.5f)
    t = f32(spawn_time)
    m = Msg()
    m.byte(7); m.float(t)
    for i in range(maxclients):
        m.byte(13); m.byte(i); m.string('client%d' % i)
        m.byte(14); m.byte(i); m.short(0)
        m.byte(17); m.byte(i); m.byte(rng.randrange(256))
    for i in range(64):
        m.byte(12); m.byte(i); m.string(rng.choice(['m', 'abcdefghijklmnopqrstuvwxyzyx', 'mmnmmommommnonmmonqnmmo']))
    for s in (11, 12, 13, 14):
        m.byte(3); m.byte(s); m.long(rng.randrange(100))
    m.byte(10); m.angle(10); m.angle(200); m.angle(0)
    clientdata(m, rng, weapons)
    m.byte(25); m.byte(3)
    emit(m)

    # the frames: the first at the edge's time (with the explosion of edge 0, parsed while cl.time is 0), then on
    # from 1.0
    last_light = None
    playing = []                                       # seed 8: the (entity, channel) pairs of its sounds
    for frame in range(frames):
        r = rng.random()
        if frame == 0:
            t = f32(first_time)
        elif frame == 1:
            t = 1.0
        elif r < 0.70:
            t = f32(t + rng.uniform(0.005, 0.03))       # ordinary steps, around the client's 1/72
        elif r < 0.75:
            t = f32(t + f32(0.1))                       # a gap of 0.1f: f is 0.1f exactly when the sum is exact
        elif r < 0.80:
            t = f32(t + rng.uniform(0.11, 0.4))         # above 0.1: a dropped packet
        elif r < 0.85:
            pass                                        # the same time again: f == 0
        elif r < 0.93:
            t = f32(t - rng.uniform(0.01, 0.06))        # back: cl.time snaps to a message time
        elif last_light is not None:
            t = last_light                              # exactly a light's die time
            last_light = None
        m = Msg()
        m.byte(7); m.float(t)
        clientdata(m, rng, weapons)
        for num, e in ents.items():
            if rng.random() < 0.08:
                continue                                # left out of a packet: CL_RelinkEntities drops its model
            update(m, rng, e, nmodels, maxclients, ents[1] if render and num != 1 else None)
            if e.effects & 2 and rng.random() < 0.5:
                last_light = f32(t + 0.1)               # the muzzle flash's die, (float)(cl.time + 0.1) once snapped
        if frame == 0 and edge == 0:
            m.byte(23); m.byte(3)                        # TE_EXPLOSION: die = cl.time + 0.5 = 0.5f, decay 300
            for i in range(3):
                m.coord(0)
        else:
            extras(m, rng, ents, maxclients, nmodels, screen, weapons)
        if sound:
            sound_extras(m, srng, ents, playing)
        if screen and frame == frames * 6 // 10:
            m.byte(30)                                   # svc_intermission, after frames with a view and a gun
        elif screen and frame == frames * 75 // 100:
            m.byte(31); m.string(LONG_FINALE)            # svc_finale: printed a character at a time
        elif screen and frame == frames * 9 // 10:
            m.byte(34); m.string('a cutscene\nof two lines')
        emit(m)
    return out


def sound_extras(m, srng, ents, playing):
    """Seed 8: sounds on entity channels near the view, stopped by svc_stopsound while they play, and one twice."""
    def start(num, channel, snd):
        e = ents[num]
        mask = srng.randrange(4)
        m.byte(6); m.byte(mask)
        if mask & 1: m.byte(srng.choice([64, 128, 255]))
        if mask & 2: m.byte(srng.choice([32, 64, 128]))
        m.short((num << 3) | channel); m.byte(snd)
        for i in range(3):
            m.coord(max(-4000, min(4000, e.origin[i] + srng.uniform(-200, 200))))
        playing.append((num, channel))
    r = srng.random()
    if r < 0.12:
        start(srng.choice([1, 1] + list(range(2, 41))), srng.randrange(1, 8), srng.randrange(1, 4))
    elif r < 0.16:
        snd = srng.randrange(1, 4)                     # the same sound on two channels in one frame
        start(srng.randrange(2, 41), srng.randrange(1, 8), snd)
        start(srng.randrange(2, 41), srng.randrange(1, 8), snd)
    if playing and srng.random() < 0.15:
        num, channel = playing.pop(srng.randrange(len(playing)) if srng.random() < 0.3 else -1)
        m.byte(16); m.short((num << 3) | channel)     # svc_stopsound
    del playing[:-8]


def clientdata(m, rng, weapons=None):
    bits = 1 << 9                                       # SU_ITEMS always
    for b in range(15):
        if b != 8 and b != 9 and rng.random() < 0.4:
            bits |= 1 << b
    m.byte(15); m.short(bits)
    if bits & 1:
        m.char(rng.randrange(-10, 40))
    if bits & 2:
        m.char(rng.randrange(-40, 40))
    for i in range(3):
        if bits & (4 << i):
            m.char(rng.randrange(-20, 20))
        if bits & (32 << i):
            m.char(rng.randrange(-128, 128))
    m.long(rng.getrandbits(31))
    for b in (1 << 12, 1 << 13, 1 << 14):
        if bits & b:
            v = rng.randrange(256)
            m.byte(weapons[v % len(weapons)] if weapons and b == 1 << 14 else v)
    m.short(rng.randrange(-50, 300))
    for _ in range(6):
        m.byte(rng.randrange(256))


def update(m, rng, e, nmodels, maxclients, follow=None):
    bits = 0
    r = rng.random()
    for i in range(3):
        if rng.random() < 0.6:
            bits |= 2 << i                              # U_ORIGIN1..3
            if follow is not None:
                e.origin[i] = follow.origin[i] + rng.uniform(-e.near, e.near)     # seed 7: around the view
            elif r < 0.15:
                e.origin[i] += rng.choice([99, 99.875, 100, 100.125, 101, -99, -100, -100.125, -101])
            else:
                e.origin[i] += rng.uniform(-20, 20)
            e.origin[i] = max(-4000, min(4000, e.origin[i]))
    for i, b in ((0, 1 << 8), (1, 1 << 4), (2, 1 << 9)):
        if rng.random() < 0.5:
            bits |= b
            e.angles[i] = (e.angles[i] + rng.choice([rng.uniform(-30, 30), 179, -179, 181, -181])) % 360
    if rng.random() < 0.05:
        bits |= 1 << 10                                 # U_MODEL
        e.model = rng.randrange(0, nmodels)             # 0: no model
    if rng.random() < 0.3:
        bits |= 1 << 6
        e.frame = rng.randrange(8)
    if rng.random() < 0.05:
        bits |= 1 << 11
        e.colormap = rng.randrange(maxclients + 1)
    if rng.random() < 0.05:
        bits |= 1 << 12
        e.skin = rng.randrange(3)
    if rng.random() < 0.15:
        bits |= 1 << 13
        e.effects = rng.choice([0, 1, 2, 4, 8, 2 | 8, 15])
    if rng.random() < 0.2:
        bits |= 1 << 5                                  # U_NOLERP
    if e.num >= 256:
        bits |= 1 << 14
    if bits >= 256:
        bits |= 1
    m.byte((bits & 127) | 128)
    if bits & 1:
        m.byte(bits >> 8)
    if bits & (1 << 14):
        m.short(e.num)
    else:
        m.byte(e.num)
    if bits & (1 << 10): m.byte(e.model)
    if bits & (1 << 6): m.byte(e.frame)
    if bits & (1 << 11): m.byte(e.colormap)
    if bits & (1 << 12): m.byte(e.skin)
    if bits & (1 << 13): m.byte(e.effects)
    for i, ob, ab in ((0, 2, 1 << 8), (1, 4, 1 << 4), (2, 8, 1 << 9)):
        if bits & ob: m.coord(e.origin[i])
        if bits & ab: m.angle(e.angles[i])


LONG_FINALE = ('Congratulations and well done! You have beaten the hideous Shub-Niggurath, and her hundreds\n'
               'of ugly changelings and monsters. You have proven that your skill and your cunning are greater\n'
               'than all the powers of Quake. You are the master now.\nThe end.')

PRINTS = ['a print\n', '\x01a chat line\n', '\x02a colored line\n', 'a carriage\rreturn\n',
          'a word longer than the console is wide: abcdefghijklmnopqrstuvwxyzabcdefghijklmnop\n',
          'two\nlines\n', 'no newline', '\x02', '']

CENTERS = ['a centerprint', 'two\nlines', 'three\nshort\nlines', 'exactly\nfour\nlines\nhere',
           'five\nlines\nare\nmore\nthan four', 'one\ntwo\nthree\nfour\nfive\nsix',
           'a line of more than forty characters, which the scan cuts', '', '\n\n']


def extras(m, rng, ents, maxclients, nmodels, screen=False, weapons=None):
    for _ in range(rng.choice([0, 0, 1, 2, 3])):
        k = rng.randrange(20)
        if k < 6:
            te = rng.randrange(13)
            m.byte(23); m.byte(te)
            if te in (5, 6, 9):
                m.short(rng.choice([1, rng.randrange(1, 40)]))
                for i in range(6):
                    m.coord(rng.uniform(-800, 800))
            else:
                for i in range(3):
                    m.coord(rng.uniform(-800, 800))
                if te == 12:
                    m.byte(rng.randrange(256)); m.byte(rng.randrange(1, 16))
        elif k == 6:
            m.byte(18)
            for i in range(3): m.coord(rng.uniform(-800, 800))
            for i in range(3): m.char(rng.randrange(-128, 128))
            m.byte(rng.choice([1, 20, 255])); m.byte(rng.randrange(256))
        elif k == 7:
            m.byte(19); m.byte(rng.randrange(100)); m.byte(rng.randrange(100))
            for i in range(3): m.coord(rng.uniform(-800, 800))
        elif k == 8:
            m.byte(6); mask = rng.randrange(4); m.byte(mask)
            if mask & 1: m.byte(rng.randrange(256))
            if mask & 2: m.byte(rng.randrange(256))
            m.short((rng.randrange(1, 40) << 3) | rng.randrange(8)); m.byte(rng.randrange(1, 4))
            for i in range(3): m.coord(rng.uniform(-800, 800))
        elif k == 9:
            stat, value = rng.randrange(32), rng.randrange(-5, 200)
            if weapons and stat == 2:
                value = weapons[value % len(weapons)]     # STAT_WEAPON: the gun's model
            m.byte(3); m.byte(stat); m.long(value)
        elif k == 10:
            m.byte(rng.choice([27, 28]))
        elif k == 11:
            m.byte(12); m.byte(rng.randrange(64)); m.string(rng.choice(['', 'a', 'zmzmz', 'abcdefghijklmnopqrstuvwxyz']))
        elif k == 12:
            i = rng.randrange(maxclients)
            c = rng.randrange(3)
            if c == 0: m.byte(13); m.byte(i); m.string(rng.choice(['x', 'player', 'a much longer name']))
            elif c == 1: m.byte(14); m.byte(i); m.short(rng.choice([0, 3, 3, -2, 12]) if screen else rng.randrange(-5, 50))
            else: m.byte(17); m.byte(i); m.byte(rng.randrange(256))
        elif k == 13:
            m.byte(10)
            for i in range(3): m.angle(rng.uniform(0, 359))
        elif k == 14:
            m.byte(24); m.byte(rng.randrange(2))
        elif k == 15:
            m.byte(9); m.string('bf\n')
        elif k == 16 and screen:
            m.byte(8); m.string(rng.choice(PRINTS))      # the intermission comes late, from the frames
        elif k == 16:
            c = rng.randrange(6)
            if c == 0: m.byte(30)
            elif c == 1: m.byte(31); m.string('the end')
            elif c == 2: m.byte(34); m.string('a cutscene')
            elif c == 3: m.byte(32); m.byte(rng.randrange(10)); m.byte(rng.randrange(10))
            else: m.byte(1)
        elif k == 17:
            m.byte(8); m.string(rng.choice(PRINTS) if screen else 'a print\n')
        elif k == 18:
            m.byte(26); m.string(rng.choice(CENTERS) if screen else 'a centerprint')
        else:
            m.byte(16); m.short(rng.randrange(1, 300))  # stopsound


def main():
    datadir, seed, frames, path = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
    out = generate(datadir, seed, frames)
    data = bytearray(b'-1\n')
    for angles, msg in out:
        assert len(msg) <= 8000, len(msg)
        data += struct.pack('<I3f', len(msg), *angles) + msg
    open(path, 'wb').write(data)
    print('%s: %d messages' % (path, len(out)))


if __name__ == '__main__':
    main()
