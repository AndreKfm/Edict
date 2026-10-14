// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Engine;

namespace Edict.Harness;

/// <summary>
/// The client lanes of ref/src/edict_hash.c (.edh version 4, 'C' records) over the port's <see cref="ClientState"/>:
/// the same canonical streams (models by name, colormaps as -1 / 0 / 1 + i, visible entities as array and index, NaN
/// as one value), hashed with XXH64. <see cref="Streams"/> gives the streams themselves, to compare with the
/// reference's -edictclientdump.
/// </summary>
public sealed unsafe class ClientHash
{
    private readonly List<byte> _s = new(1 << 16);

    /// <summary>The three lanes and one hash per entity of cl_entities.</summary>
    public readonly record struct Lanes(ulong Client, ulong Entities, ulong Effects, ulong[] PerEntity);

    /// <summary>The canonical streams of a frame: the client lane, each entity, the entities lane, the effects lane.</summary>
    public readonly record struct StreamSet(byte[] Client, byte[][] Entities, byte[] EntitiesLane, byte[] Effects);

    public Lanes Of(ClientState cl)
    {
        StreamSet s = Streams(cl);
        var per = new ulong[s.Entities.Length];
        for (int i = 0; i < per.Length; i++)
            per[i] = Xxh64.Hash(s.Entities[i]);
        return new Lanes(Xxh64.Hash(s.Client), Xxh64.Hash(s.EntitiesLane), Xxh64.Hash(s.Effects), per);
    }

    public StreamSet Streams(ClientState cl)
    {
        int n = cl.NumEntities;
        var entities = new byte[n][];
        var hashes = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            _s.Clear();
            PutEntity(cl.Entities[i]);
            entities[i] = [.. _s];
            hashes[i] = Xxh64.Hash(entities[i]);
        }

        _s.Clear();
        for (int i = 0; i < n; i++)
        {
            Put32((uint)i);
            Put64(hashes[i]);
        }
        for (int i = 0; i < cl.NumStatics; i++)
            PutEntity(cl.StaticEntities[i]);
        Put32((uint)cl.NumTempEntities);
        for (int i = 0; i < cl.NumTempEntities; i++)
            PutEntity(cl.TempEntities[i]);
        PutEntity(cl.ViewEnt);
        byte[] entitiesLane = [.. _s];

        return new StreamSet(ClientStream(cl), entities, entitiesLane, EffectsStream(cl));
    }

    private byte[] ClientStream(ClientState cl)
    {
        _s.Clear();
        Put32((uint)cl.State);
        Put32((uint)cl.Signon);
        Put32((uint)cl.MoveMessages);
        PutVec(cl.CmdViewAngles);
        PutFloat(cl.CmdForwardMove); PutFloat(cl.CmdSideMove); PutFloat(cl.CmdUpMove);
        for (int i = 0; i < ClientState.MaxClStats; i++)
            Put32((uint)cl.Stats[i]);
        Put32((uint)cl.Items);
        for (int i = 0; i < 32; i++)
            PutFloat(cl.ItemGetTime[i]);
        PutFloat(cl.FaceAnimTime);
        for (int i = 0; i < ClientState.NumCShifts; i++)
        {
            ColorShift c = cl.CShifts[i], p = cl.PrevCShifts[i];
            for (int j = 0; j < 3; j++)
                Put32((uint)c.DestColor[j]);
            Put32((uint)c.Percent);
            for (int j = 0; j < 3; j++)
                Put32((uint)p.DestColor[j]);
            Put32((uint)p.Percent);
        }
        PutVec(cl.MViewAngles0); PutVec(cl.MViewAngles1); PutVec(cl.ViewAngles);
        PutVec(cl.MVelocity0); PutVec(cl.MVelocity1); PutVec(cl.Velocity);
        PutVec(cl.PunchAngle);
        PutFloat(cl.IdealPitch); PutFloat(cl.PitchVel); Put32((uint)cl.NoDrift); PutFloat(cl.DriftMove);
        PutDouble(cl.LastStop);
        PutFloat(cl.ViewHeight); PutFloat(cl.Crouch);
        Put32((uint)cl.Paused); Put32((uint)cl.OnGround); Put32((uint)cl.InWater);
        Put32((uint)cl.Intermission); Put32((uint)cl.CompletedTime);
        PutDouble(cl.MTime0); PutDouble(cl.MTime1); PutDouble(cl.Time); PutDouble(cl.OldTime);
        PutFloat(cl.LastReceivedMessage);
        for (int i = 0; i < ClientState.MaxModels; i++)
            PutModel((Model*)cl.ModelPrecache[i]);
        for (int i = 0; i < ClientState.MaxSounds; i++)
        {   // the sfx's name; "" for none (snd_null precaches no sfx: every name is "")
            if (cl.Sound is { } sound && cl.SoundPrecache[i] != 0)
                foreach (byte b in sound.SfxName(cl.SoundPrecache[i]))
                    Put8(b);
            Put8(0);
        }
        PutString(cl.LevelName);
        Put32((uint)cl.ViewEntity); Put32((uint)cl.MaxClients); Put32((uint)cl.GameType);
        PutModel(cl.WorldModel);
        Put32((uint)cl.NumEntities); Put32((uint)cl.NumStatics);
        Put32((uint)cl.CdTrack); Put32((uint)cl.LoopTrack);
        for (int i = 0; i < cl.MaxClients; i++)
        {
            Score s = cl.Scores[i];
            PutString(s.Name);
            PutFloat(s.EnterTime);
            Put32((uint)s.Frags);
            Put32((uint)s.Colors);
        }
        for (int i = 0; i < ClientState.MaxLightstyles; i++)
        {
            Put32((uint)cl.LightstyleLength[i]);
            PutString(cl.LightstyleMap[i]);
        }
        Put32((uint)cl.NumVisEdicts);
        for (int i = 0; i < cl.NumVisEdicts; i++)
        {
            Put8(cl.VisEdicts[i].Array);
            Put32((uint)cl.VisEdicts[i].Index);
        }
        Put32(cl.Rand.Seed);
        return [.. _s];
    }

    private byte[] EffectsStream(ClientState cl)
    {
        _s.Clear();
        for (int i = 0; i < ClientState.MaxDLights; i++)
        {
            DLight d = cl.DLights[i];
            PutVec(d.Origin);
            PutFloat(d.Radius);
            PutFloat(d.Die);
            PutFloat(d.Decay);
            PutFloat(d.MinLight);
            Put32((uint)d.Key);
        }
        for (int i = 0; i < ClientState.MaxBeams; i++)
        {
            Beam b = cl.Beams[i];
            Put32((uint)b.Entity);
            PutModel(b.Model);
            PutFloat(b.EndTime);
            PutVec(b.Start);
            PutVec(b.End);
        }
        Put32((uint)cl.ActiveParticles);
        Put32((uint)cl.FreeParticles);
        return [.. _s];
    }

    private void PutEntity(in ClientEntity e)
    {
        Put32((uint)e.ForceLink);
        Put32((uint)e.UpdateType);
        PutVec(e.Baseline.Origin);
        PutVec(e.Baseline.Angles);
        Put32((uint)e.Baseline.ModelIndex);
        Put32((uint)e.Baseline.Frame);
        Put32((uint)e.Baseline.Colormap);
        Put32((uint)e.Baseline.Skin);
        Put32((uint)e.Baseline.Effects);
        PutDouble(e.MsgTime);
        PutVec(e.MsgOrigin0);
        PutVec(e.MsgOrigin1);
        PutVec(e.Origin);
        PutVec(e.MsgAngles0);
        PutVec(e.MsgAngles1);
        PutVec(e.Angles);
        PutModel(e.Model);
        Put32((uint)e.Frame);
        PutFloat(e.SyncBase);
        Put32((uint)(e.Colormap - 1));  // -1 none, 0 vid.colormap, 1 + i a translation
        Put32((uint)e.Effects);
        Put32((uint)e.SkinNum);
    }

    private void Put8(int v) => _s.Add((byte)v);

    private void Put32(uint v)
    {
        Put8((int)v); Put8((int)(v >> 8)); Put8((int)(v >> 16)); Put8((int)(v >> 24));
    }

    private void Put64(ulong v)
    {
        Put32((uint)v); Put32((uint)(v >> 32));
    }

    private void PutFloat(float f) => Put32(float.IsNaN(f) ? 0x7fc00000u : BitConverter.SingleToUInt32Bits(f));

    private void PutDouble(double d) => Put64(double.IsNaN(d) ? 0x7ff8000000000000ul : BitConverter.DoubleToUInt64Bits(d));

    private void PutVec(in Vec3 v)
    {
        PutFloat(v.X); PutFloat(v.Y); PutFloat(v.Z);
    }

    private void PutString(byte[] s)
    {
        foreach (byte b in s)
        {
            if (b == 0)
                break;
            Put8(b);
        }
        Put8(0);
    }

    private void PutModel(Model* m)
    {
        if (m != null)
            foreach (byte b in m->Name.Span)
                Put8(b);
        Put8(0);
    }
}
