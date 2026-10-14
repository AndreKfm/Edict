// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// r_part.c as far as the client's state sees it. Without screen updates R_DrawParticles never runs, so no particle
/// moves or expires: a particle taken from the free list stays active until R_NewMap clears the list. What the state
/// depends on is which particles are taken and how often each draws from rand (the client's lights, syncbases and
/// beams draw from the same stream), not where the particles are; so the port counts them and draws as C does, in
/// the same number per particle and, where a value decides anything (R_EntityParticles' lazy setup), the same values.
/// </summary>
public sealed unsafe partial class ClientState
{
    public const int MaxParticles = 2048;             // r_numparticles without -particles
    private const int NumVertexNormals = 162;

    /// <summary>The particles on the active and on the free list.</summary>
    public int ActiveParticles { get; private set; }
    public int FreeParticles { get; private set; }

    private float _avelocity00;                       // avelocities[0][0]: R_EntityParticles sets them up while it is 0

    private void InitParticles()
    {
        // R_InitParticles: the hunk's particles; R_ClearParticles runs at R_NewMap
        ActiveParticles = 0;
        FreeParticles = 0;
    }

    /// <summary>R_ClearParticles: every particle free.</summary>
    private void ClearParticles()
    {
        FreeParticles = MaxParticles;
        ActiveParticles = 0;
    }

    /// <summary>One particle from the free list to the active list; false if the list is empty.</summary>
    private bool TakeParticle()
    {
        if (FreeParticles == 0)
            return false;
        FreeParticles--;
        ActiveParticles++;
        return true;
    }

    private void Draw(int n)
    {
        for (int i = 0; i < n; i++)
            Rand.Next();
    }

    /// <summary>R_EntityParticles (EF_BRIGHTFIELD): the angular velocities once (486 draws), then 162 particles that draw nothing.</summary>
    private void EntityParticles()
    {
        if (_avelocity00 == 0)
        {
            _avelocity00 = (float)((Rand.Next() & 255) * 0.01);
            Draw(NumVertexNormals * 3 - 1);
        }
        for (int i = 0; i < NumVertexNormals; i++)
            if (!TakeParticle())
                return;
    }

    /// <summary>R_ParseParticleEffect (svc_particle): origin, direction, count (255 means 1024), color.</summary>
    private void ParseParticleEffect()
    {
        MsgReader r = _reader;
        for (int i = 0; i < 3; i++)
            r.ReadCoord();
        for (int i = 0; i < 3; i++)
            r.ReadChar();
        int msgcount = r.ReadByte();
        r.ReadByte();                   // color
        RunParticleEffect(msgcount == 255 ? 1024 : msgcount);
    }

    /// <summary>R_RunParticleEffect: count particles; a rocket explosion's (count 1024) draw 7 each, the others 5.</summary>
    private void RunParticleEffect(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!TakeParticle())
                return;
            Draw(count == 1024 ? 7 : 5);
        }
    }

    /// <summary>R_ParticleExplosion: 1024 particles, 7 draws each.</summary>
    private void ParticleExplosion() => Particles(1024, 7);

    /// <summary>R_ParticleExplosion2: 512 particles, 6 draws each.</summary>
    private void ParticleExplosion2() => Particles(512, 6);

    /// <summary>R_BlobExplosion: 1024 particles, 8 draws each.</summary>
    private void BlobExplosion() => Particles(1024, 8);

    /// <summary>R_LavaSplash: 32 x 32 particles, 6 draws each.</summary>
    private void LavaSplash() => Particles(32 * 32, 6);

    /// <summary>R_TeleportSplash: 8 x 8 x 14 particles, 6 draws each.</summary>
    private void TeleportSplash() => Particles(8 * 8 * 14, 6);

    private void Particles(int count, int draws)
    {
        for (int i = 0; i < count; i++)
        {
            if (!TakeParticle())
                return;
            Draw(draws);
        }
    }

    /// <summary>
    /// R_RocketTrail: a particle every dec units along start to end (the float loop of C: len -= dec while len &gt; 0);
    /// a tracer (3, 5) draws nothing, the others 4 each; slight blood (4) moves 3 units more per particle.
    /// </summary>
    private void RocketTrail(in Vec3 start, in Vec3 end, int type)
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
            if (!TakeParticle())
                return;
            switch (type)
            {
                case 0:         // rocket trail
                case 1:         // smoke smoke
                case 2:         // blood
                case 6:         // voor trail
                    Draw(4);
                    break;
                case 3:
                case 5:         // tracer
                    break;
                case 4:         // slight blood
                    Draw(4);
                    len -= 3;
                    break;
            }
        }
    }
}
