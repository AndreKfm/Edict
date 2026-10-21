// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// r_part.c: the particles, their effects and their movement. A particle is taken from the free list and put in
/// front of the active list; R_DrawParticles (<see cref="RunParticles"/>) moves the active ones and returns those
/// whose time is up. Without a screen it never runs (P5's runs), so a particle stays active until R_NewMap clears
/// the list. The lists are indices into arrays (-1 for NULL), in C's order.
/// </summary>
public sealed unsafe partial class ClientState
{
    public const int MaxParticles = 2048;             // r_numparticles without -particles

    // ptype_t
    private const int PtStatic = 0, PtGrav = 1, PtSlowgrav = 2, PtFire = 3, PtExplode = 4, PtExplode2 = 5, PtBlob = 6,
        PtBlob2 = 7;

    private static ReadOnlySpan<int> Ramp1 => [0x6f, 0x6d, 0x6b, 0x69, 0x67, 0x65, 0x63, 0x61];
    private static ReadOnlySpan<int> Ramp2 => [0x6f, 0x6e, 0x6d, 0x6c, 0x6b, 0x6a, 0x68, 0x66];
    private static ReadOnlySpan<int> Ramp3 => [0x6d, 0x6b, 6, 5, 4, 3, 0, 0];

    /// <summary>particle_t: org, color, next, vel, ramp, die, type.</summary>
    public struct Particle
    {
        public Vec3 Org;
        public float Color;
        public int Next;                              // -1: NULL
        public Vec3 Vel;
        public float Ramp;
        public float Die;
        public int Type;
    }

    /// <summary>D_DrawParticle: the renderer's, for each particle R_DrawParticles moves (none without a screen).</summary>
    public interface IParticleDrawer
    {
        [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
        void DrawParticle(in Particle p);
    }

    /// <summary>particles[r_numparticles].</summary>
    public Particle[] ParticleArray = null!;

    /// <summary>active_particles, free_particles: the heads of the lists, -1 for NULL.</summary>
    public int ActiveHead = -1, FreeHead = -1;

    /// <summary>The particles on the active and on the free list.</summary>
    public int ActiveParticles { get; private set; }
    public int FreeParticles { get; private set; }

    private float[] _avelocities = null!;             // avelocities[NUMVERTEXNORMALS][3]
    private int _tracerCount;                         // R_RocketTrail's static tracercount

    private const float BeamLength = 16;

    [NoAllocUnchecked("load path")]
    private void InitParticles()
    {
        // R_InitParticles: the hunk's particles, zeroed; the lists stay NULL until R_ClearParticles at R_NewMap
        ParticleArray = new Particle[MaxParticles];
        _avelocities = new float[VertexNormals.Count * 3];
    }

    /// <summary>R_ClearParticles: every particle free, in array order.</summary>
    private void ClearParticles()
    {
        FreeHead = 0;
        ActiveHead = -1;
        for (int i = 0; i < MaxParticles; i++)
            ParticleArray[i].Next = i + 1;
        ParticleArray[MaxParticles - 1].Next = -1;
        FreeParticles = MaxParticles;
        ActiveParticles = 0;
    }

    /// <summary>A particle from the free list to the front of the active list; -1 if the free list is empty.</summary>
    private int TakeParticle()
    {
        int p = FreeHead;
        if (p < 0)
            return -1;
        ref Particle q = ref ParticleArray[p];
        FreeHead = q.Next;
        q.Next = ActiveHead;
        ActiveHead = p;
        FreeParticles--;
        ActiveParticles++;
        return p;
    }

    private int R() => Rand.Next();

    /// <summary>R_EntityParticles (EF_BRIGHTFIELD).</summary>
    private void EntityParticles(in Vec3 origin)
    {
        const float dist = 64;
        if (_avelocities[0] == 0)
            for (int i = 0; i < VertexNormals.Count * 3; i++)
                _avelocities[i] = (float)((R() & 255) * 0.01);

        ReadOnlySpan<float> normals = VertexNormals.Table;
        for (int i = 0; i < VertexNormals.Count; i++)
        {
            float angle = (float)(Time * _avelocities[i * 3]);
            float sy = (float)CMath.Sin(angle), cy = (float)CMath.Cos(angle);
            angle = (float)(Time * _avelocities[i * 3 + 1]);
            float sp = (float)CMath.Sin(angle), cp = (float)CMath.Cos(angle);
            angle = (float)(Time * _avelocities[i * 3 + 2]);
            // sr, cr: computed in C, not used
            float f0 = cp * cy, f1 = cp * sy, f2 = -sp;

            int n = TakeParticle();
            if (n < 0)
                return;
            ref Particle p = ref ParticleArray[n];
            p.Die = (float)(Time + 0.01);
            p.Color = 0x6f;
            p.Type = PtExplode;
            p.Org.X = origin.X + normals[i * 3] * dist + f0 * BeamLength;
            p.Org.Y = origin.Y + normals[i * 3 + 1] * dist + f1 * BeamLength;
            p.Org.Z = origin.Z + normals[i * 3 + 2] * dist + f2 * BeamLength;
        }
    }

    /// <summary>R_ParseParticleEffect (svc_particle): origin, direction, count (255 means 1024), color.</summary>
    private void ParseParticleEffect()
    {
        MsgReader r = _reader;
        Vec3 org, dir;
        org.X = r.ReadCoord();
        org.Y = r.ReadCoord();
        org.Z = r.ReadCoord();
        dir.X = (float)(r.ReadChar() * (1.0 / 16));
        dir.Y = (float)(r.ReadChar() * (1.0 / 16));
        dir.Z = (float)(r.ReadChar() * (1.0 / 16));
        int msgcount = r.ReadByte();
        int color = r.ReadByte();
        RunParticleEffect(org, dir, color, msgcount == 255 ? 1024 : msgcount);
    }

    private static float Comp(in Vec3 v, int j) => j == 0 ? v.X : j == 1 ? v.Y : v.Z;

    /// <summary>The explosions' particle: around org within 16, a velocity within 256 on each axis.</summary>
    private void Scatter(ref Particle p, in Vec3 org)
    {
        for (int j = 0; j < 3; j++)
        {
            At(ref p.Org, j) = Comp(org, j) + ((R() % 32) - 16);
            At(ref p.Vel, j) = (R() % 512) - 256;
        }
    }

    /// <summary>R_ParticleExplosion.</summary>
    private void ParticleExplosion(in Vec3 org)
    {
        for (int i = 0; i < 1024; i++)
        {
            int n = TakeParticle();
            if (n < 0)
                return;
            ref Particle p = ref ParticleArray[n];
            p.Die = (float)(Time + 5);
            p.Color = Ramp1[0];
            p.Ramp = R() & 3;
            p.Type = (i & 1) != 0 ? PtExplode : PtExplode2;
            Scatter(ref p, org);
        }
    }

    /// <summary>R_ParticleExplosion2: a color mapped explosion.</summary>
    private void ParticleExplosion2(in Vec3 org, int colorStart, int colorLength)
    {
        int colorMod = 0;
        for (int i = 0; i < 512; i++)
        {
            int n = TakeParticle();
            if (n < 0)
                return;
            ref Particle p = ref ParticleArray[n];
            p.Die = (float)(Time + 0.3);
            p.Color = colorStart + (colorMod % colorLength);
            colorMod++;
            p.Type = PtBlob;
            Scatter(ref p, org);
        }
    }

    /// <summary>R_BlobExplosion.</summary>
    private void BlobExplosion(in Vec3 org)
    {
        for (int i = 0; i < 1024; i++)
        {
            int n = TakeParticle();
            if (n < 0)
                return;
            ref Particle p = ref ParticleArray[n];
            p.Die = (float)(Time + 1 + (R() & 8) * 0.05);
            if ((i & 1) != 0)
            {
                p.Type = PtBlob;
                p.Color = 66 + R() % 6;
            }
            else
            {
                p.Type = PtBlob2;
                p.Color = 150 + R() % 6;
            }
            Scatter(ref p, org);
        }
    }

    /// <summary>R_RunParticleEffect.</summary>
    private void RunParticleEffect(in Vec3 org, in Vec3 dir, int color, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int n = TakeParticle();
            if (n < 0)
                return;
            ref Particle p = ref ParticleArray[n];
            if (count == 1024)
            {
                // rocket explosion
                p.Die = (float)(Time + 5);
                p.Color = Ramp1[0];
                p.Ramp = R() & 3;
                p.Type = (i & 1) != 0 ? PtExplode : PtExplode2;
                Scatter(ref p, org);
            }
            else
            {
                p.Die = (float)(Time + 0.1 * (R() % 5));
                p.Color = (color & ~7) + (R() & 7);
                p.Type = PtSlowgrav;
                for (int j = 0; j < 3; j++)
                {
                    At(ref p.Org, j) = Comp(org, j) + ((R() & 15) - 8);
                    At(ref p.Vel, j) = Comp(dir, j) * 15;
                }
            }
        }
    }

    /// <summary>R_LavaSplash.</summary>
    private void LavaSplash(in Vec3 org)
    {
        for (int i = -16; i < 16; i++)
            for (int j = -16; j < 16; j++)
            {
                int n = TakeParticle();
                if (n < 0)
                    return;
                ref Particle p = ref ParticleArray[n];
                p.Die = (float)(Time + 2 + (R() & 31) * 0.02);
                p.Color = 224 + (R() & 7);
                p.Type = PtSlowgrav;
                Vec3 dir;
                dir.X = j * 8 + (R() & 7);
                dir.Y = i * 8 + (R() & 7);
                dir.Z = 256;
                p.Org.X = org.X + dir.X;
                p.Org.Y = org.Y + dir.Y;
                p.Org.Z = org.Z + (R() & 63);
                Mathlib.VectorNormalize(ref dir);
                float vel = 50 + (R() & 63);
                p.Vel = new Vec3(dir.X * vel, dir.Y * vel, dir.Z * vel);
            }
    }

    /// <summary>R_TeleportSplash.</summary>
    private void TeleportSplash(in Vec3 org)
    {
        for (int i = -16; i < 16; i += 4)
            for (int j = -16; j < 16; j += 4)
                for (int k = -24; k < 32; k += 4)
                {
                    int n = TakeParticle();
                    if (n < 0)
                        return;
                    ref Particle p = ref ParticleArray[n];
                    p.Die = (float)(Time + 0.2 + (R() & 7) * 0.02);
                    p.Color = 7 + (R() & 7);
                    p.Type = PtSlowgrav;
                    Vec3 dir = new(j * 8, i * 8, k * 8);
                    p.Org.X = org.X + i + (R() & 3);
                    p.Org.Y = org.Y + j + (R() & 3);
                    p.Org.Z = org.Z + k + (R() & 3);
                    Mathlib.VectorNormalize(ref dir);
                    float vel = 50 + (R() & 63);
                    p.Vel = new Vec3(dir.X * vel, dir.Y * vel, dir.Z * vel);
                }
    }

    /// <summary>R_RocketTrail: a particle every dec units from start to end (start moves along, as C's argument does).</summary>
    private void RocketTrail(Vec3 start, in Vec3 end, int type)
    {
        Vec3 vec = Vec3.Subtract(end, start);
        float len = Mathlib.VectorNormalize(ref vec);
        int dec;
        if (type < 128)
            dec = 3;
        else
        {
            dec = 1;
            type -= 128;
        }

        while (len > 0)
        {
            len -= dec;
            int n = TakeParticle();
            if (n < 0)
                return;
            ref Particle p = ref ParticleArray[n];
            p.Vel = default;
            p.Die = (float)(Time + 2);
            switch (type)
            {
                case 0:     // rocket trail
                    p.Ramp = R() & 3;
                    p.Color = Ramp3[(int)p.Ramp];
                    p.Type = PtFire;
                    Around(ref p, start, 6, 3);
                    break;
                case 1:     // smoke smoke
                    p.Ramp = (R() & 3) + 2;
                    p.Color = Ramp3[(int)p.Ramp];
                    p.Type = PtFire;
                    Around(ref p, start, 6, 3);
                    break;
                case 2:     // blood
                    p.Type = PtGrav;
                    p.Color = 67 + (R() & 3);
                    Around(ref p, start, 6, 3);
                    break;
                case 3:
                case 5:     // tracer
                    p.Die = (float)(Time + 0.5);
                    p.Type = PtStatic;
                    if (type == 3)
                        p.Color = 52 + ((_tracerCount & 4) << 1);
                    else
                        p.Color = 230 + ((_tracerCount & 4) << 1);
                    _tracerCount++;
                    p.Org = start;
                    if ((_tracerCount & 1) != 0)
                    {
                        p.Vel.X = 30 * vec.Y;
                        p.Vel.Y = 30 * -vec.X;
                    }
                    else
                    {
                        p.Vel.X = 30 * -vec.Y;
                        p.Vel.Y = 30 * vec.X;
                    }
                    break;
                case 4:     // slight blood
                    p.Type = PtGrav;
                    p.Color = 67 + (R() & 3);
                    Around(ref p, start, 6, 3);
                    len -= 3;
                    break;
                case 6:     // voor trail
                    p.Color = 9 * 16 + 8 + (R() & 3);
                    p.Type = PtStatic;
                    p.Die = (float)(Time + 0.3);
                    for (int j = 0; j < 3; j++)
                        At(ref p.Org, j) = Comp(start, j) + ((R() & 15) - 8);
                    break;
            }
            start = new Vec3(start.X + vec.X, start.Y + vec.Y, start.Z + vec.Z);
        }

        // p->org[j] = start[j] + ((rand()%mod)-half)
        void Around(ref Particle p, in Vec3 s, int mod, int half)
        {
            for (int j = 0; j < 3; j++)
                At(ref p.Org, j) = Comp(s, j) + ((R() % mod) - half);
        }
    }

    /// <summary>
    /// R_DrawParticles without the drawing (D_DrawParticle is the renderer's, step 5): the particles whose time is up
    /// return to the free list, the others move.
    /// </summary>
    public void RunParticles(float gravity, IParticleDrawer? drawer)
    {
        float frametime = (float)(Time - OldTime);
        float time3 = frametime * 15;
        float time2 = frametime * 10;
        float time1 = frametime * 5;
        float grav = (float)(frametime * gravity * 0.05);
        float dvel = 4 * frametime;

        while (true)
        {
            int kill = ActiveHead;
            if (kill >= 0 && ParticleArray[kill].Die < Time)
            {
                ActiveHead = ParticleArray[kill].Next;
                Free(kill);
                continue;
            }
            break;
        }

        for (int pi = ActiveHead; pi >= 0; pi = ParticleArray[pi].Next)
        {
            ref Particle p = ref ParticleArray[pi];
            while (true)
            {
                int kill = p.Next;
                if (kill >= 0 && ParticleArray[kill].Die < Time)
                {
                    p.Next = ParticleArray[kill].Next;
                    Free(kill);
                    continue;
                }
                break;
            }

            drawer?.DrawParticle(p);     // D_DrawParticle

            p.Org.X += p.Vel.X * frametime;
            p.Org.Y += p.Vel.Y * frametime;
            p.Org.Z += p.Vel.Z * frametime;

            switch (p.Type)
            {
                case PtStatic:
                    break;
                case PtFire:
                    p.Ramp += time1;
                    if (p.Ramp >= 6)
                        p.Die = -1;
                    else
                        p.Color = Ramp3[(int)p.Ramp];
                    p.Vel.Z += grav;
                    break;
                case PtExplode:
                    p.Ramp += time2;
                    if (p.Ramp >= 8)
                        p.Die = -1;
                    else
                        p.Color = Ramp1[(int)p.Ramp];
                    p.Vel.X += p.Vel.X * dvel;
                    p.Vel.Y += p.Vel.Y * dvel;
                    p.Vel.Z += p.Vel.Z * dvel;
                    p.Vel.Z -= grav;
                    break;
                case PtExplode2:
                    p.Ramp += time3;
                    if (p.Ramp >= 8)
                        p.Die = -1;
                    else
                        p.Color = Ramp2[(int)p.Ramp];
                    p.Vel.X -= p.Vel.X * frametime;
                    p.Vel.Y -= p.Vel.Y * frametime;
                    p.Vel.Z -= p.Vel.Z * frametime;
                    p.Vel.Z -= grav;
                    break;
                case PtBlob:
                    p.Vel.X += p.Vel.X * dvel;
                    p.Vel.Y += p.Vel.Y * dvel;
                    p.Vel.Z += p.Vel.Z * dvel;
                    p.Vel.Z -= grav;
                    break;
                case PtBlob2:
                    p.Vel.X -= p.Vel.X * dvel;
                    p.Vel.Y -= p.Vel.Y * dvel;
                    p.Vel.Z -= grav;
                    break;
                case PtGrav:            // without QUAKE2 it falls through to pt_slowgrav
                case PtSlowgrav:
                    p.Vel.Z -= grav;
                    break;
            }
        }

        void Free(int kill)
        {
            ParticleArray[kill].Next = FreeHead;
            FreeHead = kill;
            ActiveParticles--;
            FreeParticles++;
        }
    }
}
