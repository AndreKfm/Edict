// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// What a GPU renderer reads of the view (P6, step 7): with <see cref="GpuView"/> the screen runs R_RenderView's
/// exact part (the refdef, R_SetupFrame, R_MarkLeaves, the world's walk with the statics' efrags, R_DrawParticles'
/// motion, V_SetContentsColor) without drawing it, and clears the view's rectangle before the crosshair and the 2D
/// layer; the GPU renderer draws the view from this state and puts the 2D layer over it. Read only: a renderer that
/// wrote here would change the game.
/// </summary>
public sealed unsafe partial class Screen
{
    /// <summary>The view is the GPU renderer's: R_RenderView's state only, the rectangle cleared to 0.</summary>
    public bool GpuView { get; set; }

    /// <summary>This update rendered the view (V_RenderView ran: not with the console forced up or while loading).</summary>
    public bool ViewDrawn { get; private set; }

    /// <summary>vpn, vright, vup of the frame (R_SetupFrame's AngleVectors of r_refdef.viewangles).</summary>
    public Vec3 Vpn => _baseVpn;
    public Vec3 Vright => _baseVright;
    public Vec3 Vup => _baseVup;

    /// <summary>r_framecount and r_visframecount.</summary>
    public int FrameCountR => _frameCount;
    public int VisFrameCount => _visFrameCount;

    /// <summary>The visframes of the world's leafs and nodes: those equal to <see cref="VisFrameCount"/> are in the PVS.</summary>
    public ReadOnlySpan<int> LeafVisFrames => _leafVisFrame;
    public ReadOnlySpan<int> NodeVisFrames => _nodeVisFrame;

    /// <summary>d_lightstylevalue: 8.8 fractions of the base light per style.</summary>
    public ReadOnlySpan<int> LightStyleValues => _lightStyleValue;

    /// <summary>The view leaf's contents (r_viewleaf->contents).</summary>
    public int ViewLeafContents => _viewLeafContents;

    /// <summary>host_basepal, the palette before the color shifts and gamma.</summary>
    public ReadOnlySpan<byte> BasePalette => _basePal;

    /// <summary>vid.colormap (64 light levels of 256 colors).</summary>
    public ReadOnlySpan<byte> Colormap => _colormap;

    /// <summary>The cvar's value, for a renderer's switches (r_drawentities, r_drawviewmodel, ...).</summary>
    public float Cvar(ReadOnlySpan<byte> zeroTerminatedName) => CvarValue(zeroTerminatedName);

    /// <summary>The entity a visible-entity reference names (cl_visedicts' entries).</summary>
    public ref ClientEntity EntityOf(EntityRef r) => ref Entity(r);

    /// <summary>R_LightPoint: the light below the point (read only: the light styles' values of the frame).</summary>
    public int LightAt(Vec3 p) => LightPoint(p);

    private int[] _drawnSurface = null!;              // MAX_MAP_FACES: the frame each world surface last had a span
    private EntityRef[] _drawnAlias = null!;
    private int _numDrawnAlias;

    /// <summary>
    /// The software renderer drew a span of the world's surface in this frame (<see cref="FrameCountR"/>): what a GPU
    /// renderer's test compares its own visible set with.
    /// </summary>
    public bool SoftwareDrewSurface(int surface) => surface < _drawnSurface.Length && _drawnSurface[surface] == _frameCount;

    /// <summary>The alias entities the software renderer drew in this frame (R_AliasDrawModel ran for them; not the gun).</summary>
    public ReadOnlySpan<EntityRef> SoftwareDrewAlias => new(_drawnAlias, 0, _numDrawnAlias);

    /// <summary>The client state the view is of.</summary>
    public ClientState Client => _cl;
}
