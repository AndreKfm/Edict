// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The surface cache (d_surf.c, r_surf.c): a surface's texture lit by its lightmaps and the dynamic lights, built
/// per mip level into a heap of edict_vid.c's 256k, which the rover walks round and frees as it goes. The heap is
/// C's: blocks with a surfcache_t header of x64's layout (64 bytes to the data) in a native buffer, their owners
/// the surfaces' cachespots, so that allocation, fragments, the rover's wrap and with it r_cache_thrash (the RAM
/// icon) come out as in the reference.
/// </summary>
public sealed unsafe partial class Screen
{
    private const int SurfCacheSize = 256 * 1024;     // edict_vid.c's surfcache[256*1024]
    private const int GuardSize = 4;

    /// <summary><c>surfcache_t</c> as clang lays it out for x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct SurfCache
    {
        [FieldOffset(0)] public SurfCache* Next;
        [FieldOffset(8)] public long* Owner;          // NULL is an empty chunk of memory
        [FieldOffset(16)] public fixed int LightAdj[SurfFlags.MaxLightmaps];   // checked for strobe flush
        [FieldOffset(32)] public int DLight;
        [FieldOffset(36)] public int Size;            // including header
        [FieldOffset(40)] public uint Width;
        [FieldOffset(44)] public uint Height;         // DEBUG only needed for debug
        [FieldOffset(48)] public float MipScale;
        [FieldOffset(56)] public Texture* Texture;    // checked for animating textures
        [FieldOffset(64)] public fixed byte Data[4];  // width*height elements
    }

    private const int SurfCacheHeader = 64;           // offsetof (surfcache_t, data)

    private byte* _surfCache;                         // surfcache[]
    private int _scSize;                              // sc_size
    private SurfCache* _scRover, _scBase;             // sc_rover, sc_base
    private SurfCache* _initialRover;                 // d_initial_rover
    private bool _roverWrapped;                       // d_roverwrapped
    private uint* _blockLights;                       // blocklights[18*18]

    // r_drawsurf
    private Texture* _dsTexture;
    private int* _dsLightAdj;
    private MSurface* _dsSurf;
    private byte* _dsSurfDat;
    private int _dsSurfMip, _dsSurfWidth, _dsSurfHeight, _dsRowBytes;

    private static readonly SysErrorException CacheGuardFailed = new("D_CheckCacheGuard: failed");
    private static readonly SysErrorException BadCacheWidth = new("D_SCAlloc: bad cache width");
    private static readonly SysErrorException BadCacheSize = new("D_SCAlloc: bad cache size");
    private static readonly SysErrorException CacheTooLarge = new("D_SCAlloc: larger than the cache size");
    private static readonly SysErrorException CacheEnd = new("D_SCAlloc: hit the end of memory");
    private static readonly SysErrorException BrokenCycle = new("R_TextureAnimation: broken cycle");
    private static readonly SysErrorException InfiniteCycle = new("R_TextureAnimation: infinite cycle");

    [NoAllocUnchecked("load path")]
    private void InitSurfaceCache()
    {
        _surfCache = (byte*)NativeMemory.AlignedAlloc(SurfCacheSize, 64);
        new Span<byte>(_surfCache, SurfCacheSize).Clear();
        _blockLights = (uint*)NativeMemory.AllocZeroed(18 * 18, sizeof(uint));
        _dsLightAdj = (int*)NativeMemory.AllocZeroed(SurfFlags.MaxLightmaps, sizeof(int));
        InitCaches();
    }

    /// <summary>D_InitCaches: the whole heap one free block, the guard behind it.</summary>
    private void InitCaches()
    {
        _scSize = SurfCacheSize - GuardSize;
        _scBase = (SurfCache*)_surfCache;
        _scRover = _scBase;
        _scBase->Next = null;
        _scBase->Owner = null;
        _scBase->Size = _scSize;
        // D_ClearCacheGuard
        byte* s = (byte*)_scBase + _scSize;
        for (int i = 0; i < GuardSize; i++)
            s[i] = (byte)i;
    }

    /// <summary>D_FlushCaches: every block's owner told, the heap one free block again (Host_ClearMemory, r_fullbright).</summary>
    public void FlushCaches()
    {
        if (_scBase == null)
            return;
        for (SurfCache* c = _scBase; c != null; c = c->Next)
            if (c->Owner != null)
                *c->Owner = 0;
        _scRover = _scBase;
        _scBase->Next = null;
        _scBase->Owner = null;
        _scBase->Size = _scSize;
    }

    /// <summary>D_SCAlloc: a block for width x size/width pixels at the rover, freeing what it runs over.</summary>
    private SurfCache* SCAlloc(int width, int size)
    {
        if (width < 0 || width > 256)
            throw BadCacheWidth;
        if (size <= 0 || size > 0x10000)
            throw BadCacheSize;
        size = SurfCacheHeader + size;
        size = (size + 3) & ~3;
        if (size > _scSize)
            throw CacheTooLarge;

        // if there is not size bytes after the rover, reset to the start
        bool wrappedThisTime = false;
        if (_scRover == null || (byte*)_scRover - (byte*)_scBase > _scSize - size)
        {
            if (_scRover != null)
                wrappedThisTime = true;
            _scRover = _scBase;
        }

        // colect and free surfcache_t blocks until the rover block is large enough
        SurfCache* n = _scRover;
        if (_scRover->Owner != null)
            *_scRover->Owner = 0;
        while (n->Size < size)
        {
            // free another
            _scRover = _scRover->Next;
            if (_scRover == null)
                throw CacheEnd;
            if (_scRover->Owner != null)
                *_scRover->Owner = 0;
            n->Size += _scRover->Size;
            n->Next = _scRover->Next;
        }

        // create a fragment out of any leftovers
        if (n->Size - size > 256)
        {
            _scRover = (SurfCache*)((byte*)n + size);
            _scRover->Size = n->Size - size;
            _scRover->Next = n->Next;
            _scRover->Width = 0;
            _scRover->Owner = null;
            n->Next = _scRover;
            n->Size = size;
        }
        else
            _scRover = n->Next;

        n->Width = (uint)width;
        // DEBUG
        if (width > 0)
            n->Height = (uint)((ulong)(size - sizeof(SurfCache) + 4) / (uint)width);
        n->Owner = null;    // should be set properly after return

        if (_roverWrapped)
        {
            if (wrappedThisTime || _scRover >= _initialRover)
                CacheThrash = true;
        }
        else if (wrappedThisTime)
            _roverWrapped = true;

        // D_CheckCacheGuard (DEBUG)
        byte* g = (byte*)_scBase + _scSize;
        for (int i = 0; i < GuardSize; i++)
            if (g[i] != (byte)i)
                throw CacheGuardFailed;
        return n;
    }

    /// <summary>R_TextureAnimation: the texture of the base's sequence for the time (and the entity's frame).</summary>
    private Texture* TextureAnimation(Texture* b, int entityFrame)
    {
        if (entityFrame != 0 && b->AlternateAnims != null)
            b = b->AlternateAnims;
        if (b->AnimTotal == 0)
            return b;
        int reletive = CCast.I32(_cl.Time * 10) % b->AnimTotal;
        int count = 0;
        while (b->AnimMin > reletive || b->AnimMax <= reletive)
        {
            b = b->AnimNext;
            if (b == null)
                throw BrokenCycle;
            if (++count > 100)
                throw InfiniteCycle;
        }
        return b;
    }

    /// <summary>D_CacheSurface: the surface's block for the mip level, rebuilt if its texture or light changed.</summary>
    private SurfCache* CacheSurface(MSurface* surface, int miplevel)
    {
        // if the surface is animating or flashing, flush the cache
        _dsTexture = TextureAnimation(surface->TexInfo->Texture, _cl.Entities[0].Frame);
        for (int i = 0; i < SurfFlags.MaxLightmaps; i++)
            _dsLightAdj[i] = _lightStyleValue[surface->Styles[i]];

        // see if the cache holds apropriate data
        SurfCache* cache = (SurfCache*)surface->CacheSpots[miplevel];
        if (cache != null && cache->DLight == 0 && surface->DLightFrame != _frameCount
            && cache->Texture == _dsTexture
            && cache->LightAdj[0] == _dsLightAdj[0]
            && cache->LightAdj[1] == _dsLightAdj[1]
            && cache->LightAdj[2] == _dsLightAdj[2]
            && cache->LightAdj[3] == _dsLightAdj[3])
            return cache;

        // determine shape of surface
        float surfscale = (float)(1.0 / (1 << miplevel));
        _dsSurfMip = miplevel;
        _dsSurfWidth = surface->Extents[0] >> miplevel;
        _dsRowBytes = _dsSurfWidth;
        _dsSurfHeight = surface->Extents[1] >> miplevel;

        // allocate memory if needed
        if (cache == null)      // if a texture just animated, don't reallocate it
        {
            cache = SCAlloc(_dsSurfWidth, _dsSurfWidth * _dsSurfHeight);
            surface->CacheSpots[miplevel] = (long)cache;
            cache->Owner = surface->CacheSpots + miplevel;
            cache->MipScale = surfscale;
        }
        cache->DLight = surface->DLightFrame == _frameCount ? 1 : 0;
        _dsSurfDat = cache->Data;
        cache->Texture = _dsTexture;
        for (int i = 0; i < SurfFlags.MaxLightmaps; i++)
            cache->LightAdj[i] = _dsLightAdj[i];

        // draw and light the surface texture
        _dsSurf = surface;
        DrawSurface();
        return (SurfCache*)surface->CacheSpots[miplevel];
    }

    /// <summary>R_AddDynamicLights: the marked dynamic lights onto blocklights, by their distance in the plane.</summary>
    private void AddDynamicLights()
    {
        MSurface* surf = _dsSurf;
        int smax = (surf->Extents[0] >> 4) + 1;
        int tmax = (surf->Extents[1] >> 4) + 1;
        MTexInfo* tex = surf->TexInfo;
        for (int lnum = 0; lnum < MaxDLights; lnum++)
        {
            if ((surf->DLightBits & (1 << lnum)) == 0)
                continue;   // not lit by this light
            ref DLight dl = ref _cl.DLights[lnum];
            float rad = dl.Radius;
            float dist = Vec3.Dot(dl.Origin, surf->Plane->Normal) - surf->Plane->Dist;
            rad = (float)(rad - Math.Abs((double)dist));
            float minlight = dl.MinLight;
            if (rad < minlight)
                continue;
            minlight = rad - minlight;
            var impact = new Vec3(
                dl.Origin.X - surf->Plane->Normal.X * dist,
                dl.Origin.Y - surf->Plane->Normal.Y * dist,
                dl.Origin.Z - surf->Plane->Normal.Z * dist);
            float local0 = impact.X * tex->Vecs[0] + impact.Y * tex->Vecs[1] + impact.Z * tex->Vecs[2] + tex->Vecs[3];
            float local1 = impact.X * tex->Vecs[4] + impact.Y * tex->Vecs[5] + impact.Z * tex->Vecs[6] + tex->Vecs[7];
            local0 -= surf->TextureMins[0];
            local1 -= surf->TextureMins[1];
            for (int t = 0; t < tmax; t++)
            {
                int td = CCast.I32(local1 - t * 16);
                if (td < 0)
                    td = -td;
                for (int s = 0; s < smax; s++)
                {
                    int sd = CCast.I32(local0 - s * 16);
                    if (sd < 0)
                        sd = -sd;
                    if (sd > td)
                        dist = sd + (td >> 1);
                    else
                        dist = td + (sd >> 1);
                    if (dist < minlight)
                        _blockLights[t * smax + s] = CCast.U32(_blockLights[t * smax + s] + (rad - dist) * 256);
                }
            }
        }
    }

    /// <summary>R_BuildLightMap: the lightmaps scaled by their styles, the dynamic lights, bounded and inverted.</summary>
    private void BuildLightMap()
    {
        MSurface* surf = _dsSurf;
        int smax = (surf->Extents[0] >> 4) + 1;
        int tmax = (surf->Extents[1] >> 4) + 1;
        int size = smax * tmax;
        byte* lightmap = surf->Samples;
        if (CvarValue("r_fullbright\0"u8) != 0 || _cl.WorldModel->LightData == null)
        {
            for (int i = 0; i < size; i++)
                _blockLights[i] = 0;
            return;
        }
        // clear to ambient
        for (int i = 0; i < size; i++)
            _blockLights[i] = (uint)(_ambientLight << 8);
        // add all the lightmaps
        if (lightmap != null)
            for (int maps = 0; maps < SurfFlags.MaxLightmaps && surf->Styles[maps] != 255; maps++)
            {
                uint scale = (uint)_dsLightAdj[maps];   // 8.8 fraction
                for (int i = 0; i < size; i++)
                    _blockLights[i] += (uint)lightmap[i] * scale;
                lightmap += size;   // skip to next lightmap
            }
        // add all the dynamic lights
        if (surf->DLightFrame == _frameCount)
            AddDynamicLights();
        // bound, invert, and shift
        for (int i = 0; i < size; i++)
        {
            int t = (255 * 256 - (int)_blockLights[i]) >> (8 - 6);     // VID_CBITS 6
            if (t < 1 << 6)
                t = 1 << 6;
            _blockLights[i] = (uint)t;
        }
    }

    /// <summary>R_DrawSurface: the lit texture into the cache block, 16x16 texels per lightmap sample at mip 0.</summary>
    private void DrawSurface()
    {
        // calculate the lightings
        BuildLightMap();
        int surfrowbytes = _dsRowBytes;
        Texture* mt = _dsTexture;
        int mip = _dsSurfMip;
        byte* source = mt->Pixels + mt->MipOffsets[mip];
        // the fractional light values should range from 0 to (VID_GRADES - 1) << 16 from a source range of 0 - 255
        int texwidth = (int)(mt->Width >> mip);
        int blocksize = 16 >> mip;
        int blockdivshift = 4 - mip;
        int lightwidth = (_dsSurf->Extents[0] >> 4) + 1;
        int numhblocks = _dsSurfWidth >> blockdivshift;
        int numvblocks = _dsSurfHeight >> blockdivshift;
        int horzblockstep = blocksize;
        int smax = (int)(mt->Width >> mip);
        int twidth = texwidth;
        int tmax = (int)(mt->Height >> mip);
        int sourcetstep = texwidth;
        int stepback = tmax * twidth;
        byte* sourcemax = source + tmax * smax;
        int soffset = _dsSurf->TextureMins[0];
        int basetoffset = _dsSurf->TextureMins[1];
        // << 16 components are to guarantee positive values for %
        soffset = ((soffset >> mip) + (smax << 16)) % smax;
        byte* basetptr = source + (((basetoffset >> mip) + (tmax << 16)) % tmax) * twidth;
        byte* pcolumndest = _dsSurfDat;
        fixed (byte* colormap = _colormap)
        {
            for (int u = 0; u < numhblocks; u++)
            {
                SurfaceBlock(_blockLights + u, lightwidth, basetptr + soffset, pcolumndest, numvblocks, blockdivshift,
                    sourcetstep, surfrowbytes, sourcemax, stepback, colormap);
                soffset += blocksize;
                if (soffset >= smax)
                    soffset = 0;
                pcolumndest += horzblockstep;
            }
        }
    }

    /// <summary>
    /// R_DrawSurfaceBlock8_mip0..3: one column of blocks, the light interpolated down the left and right sides and
    /// across. C's light steps mix unsigned and int (r_lightptr is unsigned *): kept as there, as only the
    /// (light &amp; 0xFF00) bits reach the colormap.
    /// </summary>
    private static void SurfaceBlock(uint* lightptr, int lightwidth, byte* psource, byte* prowdest, int numvblocks,
        int shift, int sourcetstep, int surfrowbytes, byte* sourcemax, int stepback, byte* colormap)
    {
        int size = 1 << shift;
        for (int v = 0; v < numvblocks; v++)
        {
            int lightleft = (int)lightptr[0];
            int lightright = (int)lightptr[1];
            lightptr += lightwidth;
            int lightleftstep = (int)((lightptr[0] - (uint)lightleft) >> shift);
            int lightrightstep = (int)((lightptr[1] - (uint)lightright) >> shift);
            for (int i = 0; i < size; i++)
            {
                int lighttemp = lightleft - lightright;
                int lightstep = lighttemp >> shift;
                int light = lightright;
                for (int b = size - 1; b >= 0; b--)
                {
                    byte pix = psource[b];
                    prowdest[b] = colormap[(light & 0xFF00) + pix];
                    light += lightstep;
                }
                psource += sourcetstep;
                lightright += lightrightstep;
                lightleft += lightleftstep;
                prowdest += surfrowbytes;
            }
            if (psource >= sourcemax)
                psource -= stepback;
        }
    }
}
