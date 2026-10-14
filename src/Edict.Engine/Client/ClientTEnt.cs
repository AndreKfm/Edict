// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>cl_tent.c: temporary entities, the lightning beams and their segments (CL_UpdateTEnts). The sounds are output.</summary>
public sealed unsafe partial class ClientState
{
    private const int TeSpike = 0, TeSuperSpike = 1, TeGunshot = 2, TeExplosion = 3, TeTarExplosion = 4,
        TeLightning1 = 5, TeLightning2 = 6, TeWizSpike = 7, TeKnightSpike = 8, TeLightning3 = 9, TeLavaSplash = 10,
        TeTeleport = 11, TeExplosion2 = 12, TeBeam = 13;

    private static readonly SysErrorException BadTEnt = new("CL_ParseTEnt: bad type");
    private static readonly SysErrorException BeamsPastArray =
        new("CL_UpdateTEnts: the beam loop reads past cl_beams (its inner loop reuses the beam index i)");

    /// <summary>CL_ParseBeam: the beam of this entity, or a free one, gets the model, 0.2 s and the two ends.</summary>
    private void ParseBeam(Model* m)
    {
        MsgReader r = _reader;
        int ent = r.ReadShort();
        Vec3 start = new(r.ReadCoord(), 0, 0);
        start.Y = r.ReadCoord();
        start.Z = r.ReadCoord();
        Vec3 end = new(r.ReadCoord(), 0, 0);
        end.Y = r.ReadCoord();
        end.Z = r.ReadCoord();

        // override any beam with the same entity
        for (int i = 0; i < MaxBeams; i++)
        {
            ref Beam b = ref Beams[i];
            if (b.Entity == ent)
            {
                SetBeam(ref b, ent, m, start, end);
                return;
            }
        }

        // find a free beam
        for (int i = 0; i < MaxBeams; i++)
        {
            ref Beam b = ref Beams[i];
            if (b.Model == null || b.EndTime < Time)
            {
                SetBeam(ref b, ent, m, start, end);
                return;
            }
        }
        Con?.Printf("beam list overflow!\n"u8);
    }

    private void SetBeam(ref Beam b, int ent, Model* m, in Vec3 start, in Vec3 end)
    {
        b.Entity = ent;
        b.Model = m;
        b.EndTime = (float)(Time + 0.2);
        b.Start = start;
        b.End = end;
    }

    private Model* BeamModel(ReadOnlySpan<byte> name)
    {
        fixed (byte* n = name)
            return _models.LoadedClientModel(n) is var m && m != null ? m : throw BeamModelMissing;    // Mod_ForName (name, true)
    }

    private static readonly SysErrorException BeamModelMissing = new("Mod_NumForName: a beam model not found");

    /// <summary>CL_ParseTEnt: the effect's particles and light; its sound is output (TE_SPIKE and TE_SUPERSPIKE still draw for it).</summary>
    private void ParseTEnt()
    {
        MsgReader r = _reader;
        int type = r.ReadByte();
        switch (type)
        {
            case TeWizSpike:            // spike hitting wall
                ReadPos();
                RunParticleEffect(30);
                break;
            case TeKnightSpike:         // spike hitting wall
                ReadPos();
                RunParticleEffect(20);
                break;
            case TeSpike:               // spike hitting wall
                ReadPos();
                RunParticleEffect(10);
                if (Rand.Next() % 5 == 0)
                    Rand.Next();        // rnd = rand () & 3: which ricochet sound
                break;
            case TeSuperSpike:          // super spike hitting wall
                ReadPos();
                RunParticleEffect(20);
                if (Rand.Next() % 5 == 0)
                    Rand.Next();
                break;
            case TeGunshot:             // bullet hitting wall
                ReadPos();
                RunParticleEffect(20);
                break;
            case TeExplosion:           // rocket explosion
            {
                Vec3 pos = ReadPos();
                ParticleExplosion();
                ref DLight dl = ref AllocDlight(0);
                dl.Origin = pos;
                dl.Radius = 350;
                dl.Die = (float)(Time + 0.5);
                dl.Decay = 300;
                break;
            }
            case TeTarExplosion:        // tarbaby explosion
                ReadPos();
                BlobExplosion();
                break;
            case TeLightning1:          // lightning bolts
                ParseBeam(BeamModel("progs/bolt.mdl\0"u8));
                break;
            case TeLightning2:
                ParseBeam(BeamModel("progs/bolt2.mdl\0"u8));
                break;
            case TeLightning3:
                ParseBeam(BeamModel("progs/bolt3.mdl\0"u8));
                break;
            case TeBeam:                // grappling hook beam
                ParseBeam(BeamModel("progs/beam.mdl\0"u8));
                break;
            case TeLavaSplash:
                ReadPos();
                LavaSplash();
                break;
            case TeTeleport:
                ReadPos();
                TeleportSplash();
                break;
            case TeExplosion2:          // color mapped explosion
            {
                Vec3 pos = ReadPos();
                r.ReadByte();           // colorStart
                r.ReadByte();           // colorLength
                ParticleExplosion2();
                ref DLight dl = ref AllocDlight(0);
                dl.Origin = pos;
                dl.Radius = 350;
                dl.Die = (float)(Time + 0.5);
                dl.Decay = 300;
                break;
            }
            default:
                throw BadTEnt;
        }
    }

    private Vec3 ReadPos()
    {
        MsgReader r = _reader;
        Vec3 pos;
        pos.X = r.ReadCoord();
        pos.Y = r.ReadCoord();
        pos.Z = r.ReadCoord();
        return pos;
    }

    /// <summary>CL_NewTempEntity: the next temp entity, cleared, with vid.colormap, and listed as visible; -1 if none is left.</summary>
    private int NewTempEntity()
    {
        if (NumVisEdicts == MaxVisEdicts)
            return -1;
        if (NumTempEntities == MaxTempEntities)
            return -1;
        int e = NumTempEntities++;
        TempEntities[e] = default;
        VisEdicts[NumVisEdicts++] = new EntityRef { Array = 2, Index = e };
        TempEntities[e].Colormap = 1;   // vid.colormap
        return e;
    }

    /// <summary>
    /// CL_UpdateTEnts: the live beams cut into 30-unit segments, each a temp entity with a random roll. The segment
    /// loop's "for (i=0 ; i&lt;3 ; i++)" reuses the beam loop's i, so after a beam with segments the beam loop goes on
    /// from i = 4 while its pointer keeps going: the port does the same, and stops where C would read past cl_beams.
    /// </summary>
    private void UpdateTEnts()
    {
        NumTempEntities = 0;

        // update lightning
        int bi = 0;
        for (int i = 0; i < MaxBeams; i++, bi++)
        {
            if (bi >= MaxBeams)
                throw BeamsPastArray;
            ref Beam b = ref Beams[bi];
            if (b.Model == null || b.EndTime < Time)
                continue;

            // if coming from the player, update the start position
            if (b.Entity == ViewEntity)
                b.Start = Entities[ViewEntity].Origin;

            // calculate pitch and yaw
            Vec3 dist = Vec3.Subtract(b.End, b.Start);
            float yaw, pitch;
            if (dist.Y == 0 && dist.X == 0)
            {
                yaw = 0;
                pitch = dist.Z > 0 ? 90 : 270;
            }
            else
            {
                yaw = CCast.I32(CMath.Atan2(dist.Y, dist.X) * 180 / Math.PI);
                if (yaw < 0)
                    yaw += 360;
                float forward = (float)Math.Sqrt(dist.X * dist.X + dist.Y * dist.Y);
                pitch = CCast.I32(CMath.Atan2(dist.Z, forward) * 180 / Math.PI);
                if (pitch < 0)
                    pitch += 360;
            }

            // add new entities for the lightning
            Vec3 org = b.Start;
            float d = Mathlib.VectorNormalize(ref dist);
            while (d > 0)
            {
                int e = NewTempEntity();
                if (e < 0)
                    return;
                ref ClientEntity ent = ref TempEntities[e];
                ent.Origin = org;
                ent.Model = b.Model;
                ent.Angles.X = pitch;
                ent.Angles.Y = yaw;
                ent.Angles.Z = Rand.Next() % 360;
                for (i = 0; i < 3; i++)
                    At(ref org, i) += At(ref dist, i) * 30;
                d -= 30;
            }
        }
    }
}
