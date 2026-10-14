// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>
/// The software renderer's frame (r_main.c, r_misc.c, r_light.c, d_init.c, d_modech.c, d_part.c): R_ViewChanged's
/// projection, R_SetupFrame, R_RenderView's order, the light styles and dynamic lights, the particles' pixels. The
/// world's surfaces go through the edge list (REdge.cs, RDraw.cs) to the span drawers (DScan.cs, DSurf.cs, RSky.cs).
/// What only draws is skipped with <see cref="Only2D"/>: the reference's -edict2d clears it again. Entities (alias
/// models, sprites, brush entities) and the gun are step 5.
/// </summary>
public sealed unsafe partial class Screen : ClientState.IParticleDrawer
{
    private const double NearClip = 0.01;             // NEAR_CLIP
    private const double BackfaceEpsilon = 0.01;      // BACKFACE_EPSILON
    private const int MaxHeight = 1024, MaxWidth = 1280;
    private const int Cycle = 128, Amp = 8 * 0x10000, Amp2 = 3, Speed = 20;
    private const int SinBufferSize = MaxWidth + Cycle;     // (MAXDIMENSION+CYCLE)
    private const int WarpWidth = 320, WarpHeight = 200;
    private const float ParticleZClip = 8.0f;
    private const int MaxDLights = ClientState.MaxDLights, MaxLightstyles = ClientState.MaxLightstyles;

    // ---- r_main.c, r_misc.c: the view and the projection
    private Vec3 _modelOrg, _baseModelOrg, _rOrigin;  // modelorg, base_modelorg, r_origin
    private Vec3 _baseVpn, _baseVright, _baseVup;
    private float _xcenter, _ycenter, _xscale, _yscale, _xscaleinv, _yscaleinv, _xscaleshrink, _yscaleshrink;
    private float _aliasXScale, _aliasYScale, _aliasXCenter, _aliasYCenter;  // R_ViewChanged's, for step 5
    private float _aliasTransition, _resFudge;        // r_aliastransition, r_resfudge
    private bool _fovGreaterThan90;                   // r_fov_greater_than_90
    private float _pixelAspect;
    private float _fvrectxAdj, _fvrectyAdj, _fvrectrightAdj, _fvrectbottomAdj, _vrectRightEdge;
    private int _vrectXAdjShift20, _vrectRightAdjShift20, _vrectRight, _vrectBottom;
    private VRect _aliasVrect;
    private int _aliasVrectRight, _aliasVrectBottom;
    private bool _doWarp, _doWarpOld, _viewChanged;   // r_dowarp, r_dowarpold, r_viewchanged
    private int _ambientLight;                        // r_refdef.ambientlight
    private float _oldBright;                         // R_CheckVariables' static oldbright
    private int _polyCount, _drawnPolyCount, _faceClip;     // r_polycount, r_drawnpolycount, c_faceclip
    private int _outOfSurfaces, _outOfEdges;          // r_outofsurfaces, r_outofedges
    private int _maxSurfsSeen, _maxEdgesSeen;         // r_maxsurfsseen, r_maxedgesseen
    private int _dlightFrameCount;                    // r_dlightframecount
    private bool _draw;                               // this frame draws (not Only2D)

    private int[] _lightStyleValue = null!;           // d_lightstylevalue[256]: 8.8 fraction of base light value
    private int[] _sinTable = null!, _intSinTable = null!;  // sintable, intsintable (R_InitTurb)

    // ---- d_init.c, d_modech.c: the driver's view
    private byte* _viewBuffer;                        // d_viewbuffer
    private int _screenWidth;                         // screenwidth
    private byte* _warpBuffer;                        // R_RenderView_'s warpbuffer[WARP_WIDTH * WARP_HEIGHT]
    private short* _zBuffer;                          // d_pzbuffer: zbuffer[BASEWIDTH*BASEHEIGHT]
    private const int ZWidth = Width;                 // d_zwidth
    private float _scaleForMip;                       // scale_for_mip
    private int _minMip;                              // d_minmip
    private float* _scaleMip;                         // d_scalemip[NUM_MIPS-1]
    private int _pixMin, _pixMax, _pixShift, _yAspectShift;
    private int _dVrectX, _dVrectY, _vrectRightParticle, _vrectBottomParticle;
    private int* _scanTable;                          // d_scantable[MAXHEIGHT]
    private Vec3 _pright, _pup, _ppn;                 // r_pright, r_pup, r_ppn

    /// <summary>R_Init's tables, D_Init, the driver's buffers (edict_vid.c's zbuffer, surfcache).</summary>
    [NoAllocUnchecked("load path")]
    private void InitRender()
    {
        _lightStyleValue = new int[256];
        _sinTable = new int[SinBufferSize];
        _intSinTable = new int[SinBufferSize];
        // R_InitTurb
        for (int i = 0; i < SinBufferSize; i++)
        {
            _sinTable[i] = CCast.I32(Amp + CMath.Sin(i * 3.14159 * 2 / Cycle) * 8 * 0x10000);
            _intSinTable[i] = CCast.I32(Amp2 + CMath.Sin(i * 3.14159 * 2 / Cycle) * Amp2);     // AMP2, not 20
        }
        _warpBuffer = (byte*)NativeMemory.AllocZeroed(WarpWidth * WarpHeight);
        _zBuffer = (short*)NativeMemory.AllocZeroed(Width * Height, sizeof(short));
        _scaleMip = (float*)NativeMemory.AllocZeroed(3, sizeof(float));
        _scanTable = (int*)NativeMemory.AllocZeroed(MaxHeight, sizeof(int));
        InitEdges();
        InitFaces();
        InitSurfaceCache();
        InitSkyBuffers();
        InitBModels();
        InitSprites();
        InitAlias();
    }

    /// <summary>TransformVector (r_misc.c): into the view's axes.</summary>
    private Vec3 TransformVector(in Vec3 v) => new(Vec3.Dot(v, _vright), Vec3.Dot(v, _vup), Vec3.Dot(v, _vpn));

    /// <summary>R_ViewChanged: the view's rectangle, its projection and its frustum's screen edges.</summary>
    private void ViewChanged(VRect pvrect, int lineadj)
    {
        const float aspect = 1.0f;          // vid.aspect
        const float aliasUvScale = 1.0f;    // r_aliasuvscale
        _viewChanged = true;
        SetVrect(pvrect, ref RefdefVrect, lineadj);
        VRect r = RefdefVrect;
        _horizontalFieldOfView = (float)(2.0 * CMath.Tan(FovX / 360 * Math.PI));
        _fvrectxAdj = (float)((float)r.X - 0.5);
        _vrectXAdjShift20 = (r.X << 20) + (1 << 19) - 1;
        _fvrectyAdj = (float)((float)r.Y - 0.5);
        _vrectRight = r.X + r.Width;
        _vrectRightAdjShift20 = (_vrectRight << 20) + (1 << 19) - 1;
        _fvrectrightAdj = (float)((float)_vrectRight - 0.5);
        _vrectRightEdge = (float)((float)_vrectRight - 0.99);
        _vrectBottom = r.Y + r.Height;
        _fvrectbottomAdj = (float)((float)_vrectBottom - 0.5);

        _aliasVrect.X = CCast.I32(r.X * aliasUvScale);
        _aliasVrect.Y = CCast.I32(r.Y * aliasUvScale);
        _aliasVrect.Width = CCast.I32(r.Width * aliasUvScale);
        _aliasVrect.Height = CCast.I32(r.Height * aliasUvScale);
        _aliasVrectRight = _aliasVrect.X + _aliasVrect.Width;
        _aliasVrectBottom = _aliasVrect.Y + _aliasVrect.Height;

        _pixelAspect = aspect;
        float xOrigin = (float)XCentering, yOrigin = (float)YCentering;
        float screenAspect = r.Width * _pixelAspect / r.Height;
        // 320*200 1.0 pixelAspect = 1.6 screenAspect
        _verticalFieldOfView = _horizontalFieldOfView / screenAspect;

        // values for perspective projection: the buffer origin adjusted for an exact edge to edge fill
        _xcenter = (float)((float)r.Width * XCentering + r.X - 0.5);
        _aliasXCenter = _xcenter * aliasUvScale;
        _ycenter = (float)((float)r.Height * YCentering + r.Y - 0.5);
        _aliasYCenter = _ycenter * aliasUvScale;
        _xscale = r.Width / _horizontalFieldOfView;
        _aliasXScale = _xscale * aliasUvScale;
        _xscaleinv = (float)(1.0 / _xscale);
        _yscale = _xscale * _pixelAspect;
        _aliasYScale = _yscale * aliasUvScale;
        _yscaleinv = (float)(1.0 / _yscale);
        _xscaleshrink = (r.Width - 6) / _horizontalFieldOfView;
        _yscaleshrink = _xscaleshrink * _pixelAspect;

        _screenEdge[0] = new Vec3((float)(-1.0 / (xOrigin * _horizontalFieldOfView)), 0, 1);     // left side clip
        _screenEdge[1] = new Vec3((float)(1.0 / ((1.0 - xOrigin) * _horizontalFieldOfView)), 0, 1);  // right side clip
        _screenEdge[2] = new Vec3(0, (float)(-1.0 / (yOrigin * _verticalFieldOfView)), 1);       // top side clip
        _screenEdge[3] = new Vec3(0, (float)(1.0 / ((1.0 - yOrigin) * _verticalFieldOfView)), 1);    // bottom side clip
        for (int i = 0; i < 4; i++)
            Mathlib.VectorNormalize(ref _screenEdge[i]);

        double resScale = Math.Sqrt((double)(r.Width * r.Height) / (320.0 * 152.0)) * (2.0 / _horizontalFieldOfView);
        _aliasTransition = (float)(CvarValue("r_aliastransbase\0"u8) * resScale);
        _resFudge = (float)(CvarValue("r_aliastransadj\0"u8) * resScale);
        _fovGreaterThan90 = !(CvarValue("fov\0"u8) <= 90.0);

        DViewChanged();
    }

    /// <summary>D_ViewChanged: the driver's mip scale, particle sizes and scan table.</summary>
    private void DViewChanged()
    {
        int rowbytes = _doWarp ? WarpWidth : Width;
        _scaleForMip = _xscale;
        if (_yscale > _xscale)
            _scaleForMip = _yscale;
        VRect r = RefdefVrect;
        _pixMin = r.Width / 320;
        if (_pixMin < 1)
            _pixMin = 1;
        _pixMax = CCast.I32((float)r.Width / (320.0 / 4.0) + 0.5);
        _pixShift = 8 - CCast.I32((float)r.Width / 320.0 + 0.5);
        if (_pixMax < 1)
            _pixMax = 1;
        _yAspectShift = _pixelAspect > 1.4 ? 1 : 0;
        _dVrectX = r.X;
        _dVrectY = r.Y;
        _vrectRightParticle = _vrectRight - _pixMax;
        _vrectBottomParticle = _vrectBottom - (_pixMax << _yAspectShift);
        for (int i = 0; i < Height; i++)
            _scanTable[i] = i * rowbytes;
    }

    /// <summary>R_SetupFrame: the frame's counts, light styles, view vectors, view leaf, water warp and frustum.</summary>
    private void SetupFrame()
    {
        // don't allow cheats in multiplayer
        if (_cl.MaxClients > 1)
        {
            CvarSet("r_draworder\0"u8, "0\0"u8);
            CvarSet("r_fullbright\0"u8, "0\0"u8);
            CvarSet("r_ambient\0"u8, "0\0"u8);
            CvarSet("r_drawflat\0"u8, "0\0"u8);
        }
        if (CvarValue("r_numsurfs\0"u8) != 0)
        {
            if (_surfaceP > _maxSurfsSeen)
                _maxSurfsSeen = _surfaceP;
            Msg("Used "u8).MsgInt(_surfaceP).Msg(" of "u8).MsgInt(_surfMax).Msg(" surfs; "u8).MsgInt(_maxSurfsSeen)
                .Msg(" max\n"u8).EndMsg();
        }
        if (CvarValue("r_numedges\0"u8) != 0)
        {
            int edgecount = _edgeP;
            if (edgecount > _maxEdgesSeen)
                _maxEdgesSeen = edgecount;
            Msg("Used "u8).MsgInt(edgecount).Msg(" of "u8).MsgInt(NumStackEdges).Msg(" edges; "u8).MsgInt(_maxEdgesSeen)
                .Msg(" max\n"u8).EndMsg();
        }

        _ambientLight = CCast.I32(CvarValue("r_ambient\0"u8));
        if (_ambientLight < 0)
            _ambientLight = 0;
        // !sv.active: r_draworder.value = 0, don't let cheaters look behind walls
        if (_cl.ServerActive && CvarValue("r_draworder\0"u8) != 0)
            throw NotPortedYet;     // R_GenerateSpansBackward
        CheckVariables();
        AnimateLight();
        _frameCount++;

        // build the transformation matrix for the given view angles
        _modelOrg = ViewOrg;
        _rOrigin = ViewOrg;
        Mathlib.AngleVectors(ViewAngles, out _vpn, out _vright, out _vup);

        // current viewleaf
        _oldViewLeaf = _viewLeaf;
        _viewLeaf = PointInLeaf(_rOrigin, _cl.WorldModel);
        _viewLeafContents = _cl.WorldModel->Leafs[_viewLeaf].Contents;

        _doWarpOld = _doWarp;
        _doWarp = CvarValue("r_waterwarp\0"u8) != 0 && _viewLeafContents <= Bsp.ContentsWater;
        if (_doWarp != _doWarpOld || _viewChanged || CvarValue("lcd_x\0"u8) != 0)
        {
            // vid.width <= vid.maxwarpwidth and vid.height <= vid.maxwarpheight: the whole screen either way
            ViewChanged(new VRect { X = 0, Y = 0, Width = Width, Height = Height }, _sbLines);
            _viewChanged = false;
        }

        // start off with just the four screen edge clip planes
        TransformFrustum();
        // save base values
        _baseVpn = _vpn;
        _baseVright = _vright;
        _baseVup = _vup;
        _baseModelOrg = _modelOrg;
        SetSkyFrame();
        SetUpFrustumIndexes();
        CacheThrash = false;
        // clear frame counts
        _faceClip = 0;
        _polyCount = 0;
        _drawnPolyCount = 0;
        _outOfSurfaces = 0;
        _outOfEdges = 0;
        DSetupFrame();
    }

    /// <summary>R_CheckVariables: a change of r_fullbright flushes the surface cache, so all lighting changes.</summary>
    private void CheckVariables()
    {
        float fullbright = CvarValue("r_fullbright\0"u8);
        if (fullbright != _oldBright)
        {
            _oldBright = fullbright;
            FlushCaches();
        }
    }

    /// <summary>D_SetupFrame: the buffer drawn into, the rover's start, the mip scales.</summary>
    private void DSetupFrame()
    {
        _viewBuffer = _doWarp ? _warpBuffer : _buffer;
        _screenWidth = _doWarp ? WarpWidth : Width;
        _roverWrapped = false;
        _initialRover = _scRover;
        _minMip = CCast.I32(CvarValue("d_mipcap\0"u8));
        if (_minMip > 3)
            _minMip = 3;
        else if (_minMip < 0)
            _minMip = 0;
        float mipScale = CvarValue("d_mipscale\0"u8);
        _scaleMip[0] = 1.0f * mipScale;
        _scaleMip[1] = (float)(0.5 * 0.8) * mipScale;
        _scaleMip[2] = (float)(0.25 * 0.8) * mipScale;
    }

    /// <summary>R_AnimateLight: 'm' is normal light, 'a' is no light, 'z' is double bright.</summary>
    private void AnimateLight()
    {
        int i = CCast.I32(_cl.Time * 10);
        for (int j = 0; j < MaxLightstyles; j++)
        {
            int length = _cl.LightstyleLength[j];
            if (length == 0)
            {
                _lightStyleValue[j] = 256;
                continue;
            }
            int k = i % length;
            k = (sbyte)_cl.LightstyleMap[j][k] - 'a';     // char is signed
            k *= 22;
            _lightStyleValue[j] = k;
        }
    }

    /// <summary>R_PushDlights: the world's surfaces each live dynamic light reaches.</summary>
    private void PushDlights()
    {
        _dlightFrameCount = _frameCount + 1;    // because the count hasn't advanced yet for this frame
        Model* world = _cl.WorldModel;
        for (int i = 0; i < MaxDLights; i++)
        {
            ref DLight l = ref _cl.DLights[i];
            if (l.Die < _cl.Time || l.Radius == 0)
                continue;
            MarkLights(ref l, 1 << i, world, 0);
        }
    }

    /// <summary>R_MarkLights: down the nodes the light's sphere touches, marking their surfaces.</summary>
    private void MarkLights(ref DLight light, int bit, Model* model, int child)
    {
        while (child >= 0)
        {
            MNode* node = model->Nodes + child;
            MPlane* splitplane = model->Planes + node->PlaneNum;
            float dist = Vec3.Dot(light.Origin, splitplane->Normal) - splitplane->Dist;
            if (dist > light.Radius)
            {
                child = node->Children[0];
                continue;
            }
            if (dist < -light.Radius)
            {
                child = node->Children[1];
                continue;
            }
            // mark the polygons
            MSurface* surf = _cl.WorldModel->Surfaces + node->FirstSurface;
            for (int i = 0; i < node->NumSurfaces; i++, surf++)
            {
                if (surf->DLightFrame != _dlightFrameCount)
                {
                    surf->DLightBits = 0;
                    surf->DLightFrame = _dlightFrameCount;
                }
                surf->DLightBits |= bit;
            }
            MarkLights(ref light, bit, model, node->Children[0]);
            child = node->Children[1];
        }
    }

    private static readonly SysErrorException NullWorldModel = new("R_RenderView: NULL worldmodel");

    /// <summary>
    /// R_RenderView: R_SetupFrame, R_MarkLeaves, R_EdgeDrawing (the world's walk, the edges' scan and the surfaces'
    /// spans), the entities and the gun (step 5), R_DrawParticles, D_WarpScreen, V_SetContentsColor.
    /// </summary>
    private void RenderView3D()
    {
        SetupFrame();
        MarkLeaves();       // done here so we know if we're in water
        if (_cl.Entities[0].Model == null || _cl.WorldModel == null)
            throw NullWorldModel;
        EdgeDrawing();
        DrawEntitiesOnList();
        DrawViewModel();
        DrawParticles();
        if (_doWarp && _draw)
            WarpScreen();
        SetContentsColor(_viewLeafContents);
        if (CvarValue("r_timegraph\0"u8) != 0 || CvarValue("r_polymodelstats\0"u8) != 0 || CvarValue("r_speeds\0"u8) != 0
            || CvarValue("r_dspeeds\0"u8) != 0)
            throw NotPortedYet;     // timings and their console lines
        if (CvarValue("r_reportsurfout\0"u8) != 0 && _outOfSurfaces != 0)
            Msg("Short "u8).MsgInt(_outOfSurfaces).Msg(" surfaces\n"u8).EndMsg();
        if (CvarValue("r_reportedgeout\0"u8) != 0 && _outOfEdges != 0)
            Msg("Short roughly "u8).MsgInt(_outOfEdges * 2 / 3).Msg(" edges\n"u8).EndMsg();
    }

    /// <summary>R_EdgeDrawing: the world into the edge list, the brush entities after it, the scan.</summary>
    private void EdgeDrawing()
    {
        if (_draw)
            BeginEdgeFrame();
        RenderWorld();
        DrawBEntitiesOnList();
        if (_draw)
            ScanEdges();
    }

    private static readonly SysErrorException ViewModelNotAlias = new("R_DrawViewModel: the gun is not an alias model (C reads it as one)");

    /// <summary>R_DrawViewModel: the gun, lit at least 24, its trivial accept never set (cl.viewent's stays 0).</summary>
    private void DrawViewModel()
    {
        if (CvarValue("r_drawviewmodel\0"u8) == 0 || _fovGreaterThan90 || !_draw)
            return;
        if ((_cl.Items & ItInvisibility) != 0)
            return;
        if (_cl.Stats[StatHealth] <= 0)
            return;
        ref ClientEntity e = ref _cl.ViewEnt;
        if (e.Model == null)
            return;
        if (e.Model->Type != ModelType.Alias)
            throw ViewModelNotAlias;
        _currentEntity = new EntityRef { Array = 3 };
        _entOrigin = e.Origin;
        _modelOrg = Vec3.Subtract(_rOrigin, _entOrigin);
        int j = LightPoint(e.Origin);
        if (j < 24)
            j = 24;     // allways give some light on gun
        int ambientlight = j, shadelight = j;
        // add dynamic lights
        ambientlight = AddDlights(ambientlight, e.Origin, true);
        // clamp lighting so it doesn't overbright as much
        if (ambientlight > 128)
            ambientlight = 128;
        if (ambientlight + shadelight > 192)
            shadelight = 192 - ambientlight;
        _trivialAccept = 0;
        _pmdl = (AliasModel*)e.Model->Extra;
        AliasDrawModel(ref e, true, ambientlight, shadelight, new Vec3(-1, 0, 0));
    }

    /// <summary>R_DrawParticles: the particles drawn (D_DrawParticle) and moved, those whose time is up freed.</summary>
    private void DrawParticles()
    {
        _pright = new Vec3(_vright.X * _xscaleshrink, _vright.Y * _xscaleshrink, _vright.Z * _xscaleshrink);
        _pup = new Vec3(_vup.X * _yscaleshrink, _vup.Y * _yscaleshrink, _vup.Z * _yscaleshrink);
        _ppn = _vpn;
        _cl.RunParticles(CvarValue("sv_gravity\0"u8), _draw ? this : null);
    }

    /// <summary>D_DrawParticle: a square of the particle's color, its size by distance, z-buffered.</summary>
    void ClientState.IParticleDrawer.DrawParticle(in ClientState.Particle p)
    {
        // transform point
        Vec3 local = Vec3.Subtract(p.Org, _rOrigin);
        float t0 = Vec3.Dot(local, _pright), t1 = Vec3.Dot(local, _pup), t2 = Vec3.Dot(local, _ppn);
        if (t2 < ParticleZClip)
            return;
        // project the point
        float zi = (float)(1.0 / t2);
        int u = CCast.I32(_xcenter + zi * t0 + 0.5);
        int v = CCast.I32(_ycenter - zi * t1 + 0.5);
        if (v > _vrectBottomParticle || u > _vrectRightParticle || v < _dVrectY || u < _dVrectX)
            return;
        short* pz = _zBuffer + ZWidth * v + u;
        byte* pdest = _viewBuffer + _scanTable[v] + u;
        int izi = CCast.I32(zi * 0x8000);
        int pix = izi >> _pixShift;
        if (pix < _pixMin)
            pix = _pixMin;
        else if (pix > _pixMax)
            pix = _pixMax;
        byte color = CCast.U8(p.Color);    // pdest[0] = pparticle->color: float to unsigned char
        // the cases 1 to 4 and the default draw the same pixels in the same order
        for (int count = pix << _yAspectShift; count != 0; count--, pz += ZWidth, pdest += _screenWidth)
            for (int i = 0; i < pix; i++)
                if (pz[i] <= izi)
                {
                    pz[i] = (short)izi;
                    pdest[i] = color;
                }
    }
}
