// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>
/// <c>Mod_LoadBrushModel</c>'s lumps for the renderer (model.c): vertexes, edges, surfedges, textures with their
/// animation sequences, lighting, texinfo, faces with CalcSurfaceExtents, marksurfaces; and R_InitTextures'
/// checkerboard for missing textures.
/// </summary>
public static unsafe partial class BrushLoader
{
    private const int AnimCycle = 2;

    /// <summary>r_notexture_mip (R_InitTextures): a 16x16 checkerboard of 0 and 0xff, with its mip levels.</summary>
    public static Texture* NoTextureMip { get; } = MakeNoTexture();

    private static Texture* MakeNoTexture()
    {
        var t = (Texture*)NativeMemory.AllocZeroed((nuint)sizeof(Texture));
        t->Width = t->Height = 16;
        t->Pixels = (byte*)NativeMemory.Alloc(16 * 16 + 8 * 8 + 4 * 4 + 2 * 2);
        int ofs = 0;
        for (int m = 0; m < 4; m++)
        {
            t->MipOffsets[m] = ofs;
            byte* dest = t->Pixels + ofs;
            for (int y = 0; y < (16 >> m); y++)
                for (int x = 0; x < (16 >> m); x++)
                    *dest++ = (y < (8 >> m)) ^ (x < (8 >> m)) ? (byte)0 : (byte)0xff;
            ofs += (16 >> m) * (16 >> m);
        }
        return t;
    }

    /// <summary>The arena bytes the renderer's lumps need.</summary>
    private static nuint RenderSize(Lumps lumps, DHeader* header)
    {
        nuint size = NativeArena.SizeOf<Vec3>(lumps.Count<Vec3>(Bsp.LumpVertexes))
            + NativeArena.SizeOf<MEdge>(lumps.Count<DEdge>(Bsp.LumpEdges) + 1)
            + NativeArena.SizeOf<int>(lumps.Count<int>(Bsp.LumpSurfedges))
            + NativeArena.SizeOf<byte>(header->Lump(Bsp.LumpLighting).FileLen)
            + NativeArena.SizeOf<MTexInfo>(lumps.Count<DTexInfo>(Bsp.LumpTexinfo))
            + NativeArena.SizeOf<MSurface>(lumps.Count<DFace>(Bsp.LumpFaces))
            + NativeArena.SizeOf<nint>(lumps.Count<short>(Bsp.LumpMarksurfaces));
        lumps.Check(Bsp.LumpLighting);
        lumps.Check(Bsp.LumpTextures);
        DLump l = header->Lump(Bsp.LumpTextures);
        if (l.FileLen != 0)
        {
            byte* m = lumps.Data(Bsp.LumpTextures);
            int n = *(int*)m;
            size += NativeArena.SizeOf<nint>(n);
            for (int i = 0; i < n; i++)
            {
                int ofs = ((int*)m)[1 + i];
                if (ofs == -1)
                    continue;
                var mt = (DMipTex*)(m + ofs);
                size += NativeArena.SizeOf<Texture>(1) + NativeArena.SizeOf<byte>((int)(mt->Width * mt->Height / 64 * 85));
            }
        }
        return size;
    }

    private static void LoadRender(Model* mod, NativeArena arena, Lumps lumps, DHeader* header, ReadOnlySpan<byte> name)
    {
        // Mod_LoadVertexes
        int nv = lumps.Count<Vec3>(Bsp.LumpVertexes);
        mod->Vertexes = arena.Alloc<Vec3>(nv);
        mod->NumVertexes = nv;
        new ReadOnlySpan<Vec3>(lumps.Data(Bsp.LumpVertexes), nv).CopyTo(new Span<Vec3>(mod->Vertexes, nv));

        // Mod_LoadEdges: one more than the lump, zeroed
        int ne = lumps.Count<DEdge>(Bsp.LumpEdges);
        mod->Edges = arena.Alloc<MEdge>(ne + 1);
        mod->NumEdges = ne;
        var de = (DEdge*)lumps.Data(Bsp.LumpEdges);
        for (int i = 0; i < ne; i++)
        {
            mod->Edges[i].V[0] = de[i].V[0];
            mod->Edges[i].V[1] = de[i].V[1];
        }

        // Mod_LoadSurfedges
        int nse = lumps.Count<int>(Bsp.LumpSurfedges);
        mod->Surfedges = arena.Alloc<int>(nse);
        mod->NumSurfedges = nse;
        new ReadOnlySpan<int>(lumps.Data(Bsp.LumpSurfedges), nse).CopyTo(new Span<int>(mod->Surfedges, nse));

        LoadTextures(mod, arena, lumps, header, name);

        // Mod_LoadLighting
        int ll = header->Lump(Bsp.LumpLighting).FileLen;
        if (ll != 0)
        {
            mod->LightData = arena.Alloc<byte>(ll);
            new ReadOnlySpan<byte>(lumps.Data(Bsp.LumpLighting), ll).CopyTo(new Span<byte>(mod->LightData, ll));
        }

        LoadTexinfo(mod, arena, lumps);
        LoadFaces(mod, arena, lumps, name);

        // Mod_LoadMarksurfaces
        int nm = lumps.Count<short>(Bsp.LumpMarksurfaces);
        mod->MarkSurfaces = (MSurface**)arena.Alloc<nint>(nm);
        mod->NumMarkSurfaces = nm;
        var ms = (short*)lumps.Data(Bsp.LumpMarksurfaces);
        for (int i = 0; i < nm; i++)
        {
            int j = ms[i];
            if (j >= mod->NumSurfaces)
                throw new InvalidDataException("Mod_ParseMarksurfaces: bad surface number");
            mod->MarkSurfaces[i] = mod->Surfaces + j;
        }
    }

    private static bool Prefix(byte* name, ReadOnlySpan<byte> prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
            if (name[i] != prefix[i])
                return false;
        return true;
    }

    /// <summary>Mod_LoadTextures: the mip textures, and their animation sequences ("+0name".."+9name", "+aname"..).</summary>
    private static void LoadTextures(Model* mod, NativeArena arena, Lumps lumps, DHeader* header, ReadOnlySpan<byte> name)
    {
        if (header->Lump(Bsp.LumpTextures).FileLen == 0)
        {
            mod->Textures = null;
            return;
        }
        byte* m = lumps.Data(Bsp.LumpTextures);
        int n = *(int*)m;
        mod->NumTextures = n;
        mod->Textures = (Texture**)arena.Alloc<nint>(n);
        for (int i = 0; i < n; i++)
        {
            int ofs = ((int*)m)[1 + i];
            if (ofs == -1)
                continue;
            var mt = (DMipTex*)(m + ofs);
            if ((mt->Width & 15) != 0 || (mt->Height & 15) != 0)
                throw new InvalidDataException($"Texture {Latin1(new ReadOnlySpan<byte>(mt->Name, 16))} is not 16 aligned");
            int pixels = (int)(mt->Width * mt->Height / 64 * 85);
            Texture* tx = arena.Alloc<Texture>(1);
            mod->Textures[i] = tx;
            for (int j = 0; j < 16; j++)
                tx->Name[j] = mt->Name[j];
            tx->Width = mt->Width;
            tx->Height = mt->Height;
            // the pixels immediately follow the structures: the offsets count from the miptex_t
            for (int j = 0; j < SurfFlags.MipLevels; j++)
                tx->MipOffsets[j] = (int)mt->Offsets[j] - sizeof(DMipTex);
            tx->Pixels = arena.Alloc<byte>(pixels);
            new ReadOnlySpan<byte>(mt + 1, pixels).CopyTo(new Span<byte>(tx->Pixels, pixels));
            if (Prefix(tx->Name, "sky"u8))
                mod->SkyTexture = tx;   // R_InitSky (tx): the renderer's, at the world's spawn
        }

        // sequence the animations
        Texture** anims = stackalloc Texture*[10];
        Texture** altanims = stackalloc Texture*[10];
        for (int i = 0; i < n; i++)
        {
            Texture* tx = mod->Textures[i];
            if (tx == null || tx->Name[0] != '+')
                continue;
            if (tx->AnimNext != null)
                continue;   // allready sequenced
            // find the number of frames in the animation
            for (int k = 0; k < 10; k++)
                anims[k] = altanims[k] = null;
            int max = tx->Name[1], altmax = 0;
            if (max >= 'a' && max <= 'z')
                max -= 'a' - 'A';
            if (max >= '0' && max <= '9')
            {
                max -= '0';
                altmax = 0;
                anims[max] = tx;
                max++;
            }
            else if (max >= 'A' && max <= 'J')
            {
                altmax = max - 'A';
                max = 0;
                altanims[altmax] = tx;
                altmax++;
            }
            else
                throw new InvalidDataException("Bad animating texture");
            for (int j = i + 1; j < n; j++)
            {
                Texture* tx2 = mod->Textures[j];
                if (tx2 == null || tx2->Name[0] != '+')
                    continue;
                if (Edict.Exact.CString.Strcmp(tx2->Name + 2, tx->Name + 2) != 0)
                    continue;
                int num = tx2->Name[1];
                if (num >= 'a' && num <= 'z')
                    num -= 'a' - 'A';
                if (num >= '0' && num <= '9')
                {
                    num -= '0';
                    anims[num] = tx2;
                    if (num + 1 > max)
                        max = num + 1;
                }
                else if (num >= 'A' && num <= 'J')
                {
                    num -= 'A';
                    altanims[num] = tx2;
                    if (num + 1 > altmax)
                        altmax = num + 1;
                }
                else
                    throw new InvalidDataException("Bad animating texture");
            }
            // link them all together
            for (int j = 0; j < max; j++)
            {
                Texture* tx2 = anims[j] != null ? anims[j] : throw new InvalidDataException("Missing frame of an animating texture");
                tx2->AnimTotal = max * AnimCycle;
                tx2->AnimMin = j * AnimCycle;
                tx2->AnimMax = (j + 1) * AnimCycle;
                tx2->AnimNext = anims[(j + 1) % max];
                if (altmax != 0)
                    tx2->AlternateAnims = altanims[0];
            }
            for (int j = 0; j < altmax; j++)
            {
                Texture* tx2 = altanims[j] != null ? altanims[j] : throw new InvalidDataException("Missing frame of an animating texture");
                tx2->AnimTotal = altmax * AnimCycle;
                tx2->AnimMin = j * AnimCycle;
                tx2->AnimMax = (j + 1) * AnimCycle;
                tx2->AnimNext = altanims[(j + 1) % altmax];
                if (max != 0)
                    tx2->AlternateAnims = anims[0];
            }
        }
    }

    /// <summary>Mod_LoadTexinfo: the vectors, the mip adjustment from their lengths, the texture (or the checkerboard).</summary>
    private static void LoadTexinfo(Model* mod, NativeArena arena, Lumps lumps)
    {
        int count = lumps.Count<DTexInfo>(Bsp.LumpTexinfo);
        var input = (DTexInfo*)lumps.Data(Bsp.LumpTexinfo);
        MTexInfo* output = arena.Alloc<MTexInfo>(count);
        mod->TexInfo = output;
        mod->NumTexInfo = count;
        for (int i = 0; i < count; i++, input++, output++)
        {
            for (int j = 0; j < 8; j++)
                output->Vecs[j] = input->Vecs[j];
            float len1 = Mathlib.Length(new Vec3(output->Vecs[0], output->Vecs[1], output->Vecs[2]));
            float len2 = Mathlib.Length(new Vec3(output->Vecs[4], output->Vecs[5], output->Vecs[6]));
            len1 = (len1 + len2) / 2;
            if (len1 < 0.32)
                output->MipAdjust = 4;
            else if (len1 < 0.49)
                output->MipAdjust = 3;
            else if (len1 < 0.99)
                output->MipAdjust = 2;
            else
                output->MipAdjust = 1;

            int miptex = input->MipTex;
            output->Flags = input->Flags;
            if (mod->Textures == null)
            {
                output->Texture = NoTextureMip;     // checkerboard texture
                output->Flags = 0;
            }
            else
            {
                if (miptex >= mod->NumTextures)
                    throw new InvalidDataException("miptex >= loadmodel->numtextures");
                output->Texture = mod->Textures[miptex];
                if (output->Texture == null)
                {
                    output->Texture = NoTextureMip; // texture not found
                    output->Flags = 0;
                }
            }
        }
    }

    /// <summary>CalcSurfaceExtents: the surface's texture extents in 16-texel steps.</summary>
    private static void CalcSurfaceExtents(Model* mod, MSurface* s)
    {
        float* mins = stackalloc float[2];
        float* maxs = stackalloc float[2];
        mins[0] = mins[1] = 999999;
        maxs[0] = maxs[1] = -99999;
        MTexInfo* tex = s->TexInfo;
        for (int i = 0; i < s->NumEdges; i++)
        {
            int e = mod->Surfedges[s->FirstEdge + i];
            Vec3* v = e >= 0 ? &mod->Vertexes[mod->Edges[e].V[0]] : &mod->Vertexes[mod->Edges[-e].V[1]];
            for (int j = 0; j < 2; j++)
            {
                float val = v->X * tex->Vecs[j * 4] + v->Y * tex->Vecs[j * 4 + 1] + v->Z * tex->Vecs[j * 4 + 2]
                            + tex->Vecs[j * 4 + 3];
                if (val < mins[j])
                    mins[j] = val;
                if (val > maxs[j])
                    maxs[j] = val;
            }
        }
        for (int i = 0; i < 2; i++)
        {
            int bmins = (int)Math.Floor(mins[i] / 16);
            int bmaxs = (int)Math.Ceiling(maxs[i] / 16);
            s->TextureMins[i] = (short)(bmins * 16);
            s->Extents[i] = (short)((bmaxs - bmins) * 16);
            if ((tex->Flags & SurfFlags.TexSpecial) == 0 && s->Extents[i] > 256)
                throw new InvalidDataException("Bad surface extents");
        }
    }

    /// <summary>Mod_LoadFaces: the surfaces, their extents, lighting and drawing flags.</summary>
    private static void LoadFaces(Model* mod, NativeArena arena, Lumps lumps, ReadOnlySpan<byte> name)
    {
        int count = lumps.Count<DFace>(Bsp.LumpFaces);
        var input = (DFace*)lumps.Data(Bsp.LumpFaces);
        MSurface* output = arena.Alloc<MSurface>(count);
        mod->Surfaces = output;
        mod->NumSurfaces = count;
        for (int surfnum = 0; surfnum < count; surfnum++, input++, output++)
        {
            output->FirstEdge = input->FirstEdge;
            output->NumEdges = input->NumEdges;
            output->Flags = 0;
            if (input->Side != 0)
                output->Flags |= SurfFlags.PlaneBack;
            output->Plane = mod->Planes + input->PlaneNum;
            output->TexInfo = mod->TexInfo + input->TexInfo;
            CalcSurfaceExtents(mod, output);
            // lighting info
            for (int i = 0; i < SurfFlags.MaxLightmaps; i++)
                output->Styles[i] = input->Styles[i];
            output->Samples = input->LightOfs == -1 ? null : mod->LightData + input->LightOfs;
            // set the drawing flags flag
            byte* tn = output->TexInfo->Texture->Name;
            if (Prefix(tn, "sky"u8))
            {
                output->Flags |= SurfFlags.DrawSky | SurfFlags.DrawTiled;
                continue;
            }
            if (Prefix(tn, "*"u8))
            {
                // turbulent
                output->Flags |= SurfFlags.DrawTurb | SurfFlags.DrawTiled;
                for (int i = 0; i < 2; i++)
                {
                    output->Extents[i] = 16384;
                    output->TextureMins[i] = -8192;
                }
            }
        }
    }
}
