// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// The edge list (r_edge.c): the edges R_RenderFace emits are sorted into the scan lines they start on, the active
/// edge table steps down the screen, and the surfaces they bound, sorted on their keys, yield each surface's visible
/// spans for the drawers. Edges, surfaces and spans are indices into arrays: edge_t's pointers as indices (-1 for
/// NULL), the four sentinel edges behind the stack edges, surfaces[0] the dummy and surfaces[1] the background.
/// </summary>
public sealed unsafe partial class Screen
{
    private const int NumStackEdges = 2400, NumStackSurfaces = 800, MaxSpans = 3000;   // r_shared.h
    private const int EdgeHead = NumStackEdges, EdgeTail = NumStackEdges + 1, EdgeAftertail = NumStackEdges + 2,
        EdgeSentinel = NumStackEdges + 3;

    /// <summary><c>edge_t</c>; <see cref="U"/> and <see cref="UStep"/> are 12.20 fixed point.</summary>
    private struct REdge
    {
        public int U, UStep;
        public int Prev, Next;
        public ushort Surf0, Surf1;                   // surfs[2]: surface indices, 0 for none
        public int NextRemove;
        public float NearZi;
        public MEdge* Owner;
    }

    /// <summary><c>surf_t</c>.</summary>
    private struct RSurf
    {
        public int Next, Prev;                        // active surface stack
        public int Spans;                             // pointer to linked list of spans to draw
        public int Key;                               // sorting key (BSP order)
        public int LastU;                             // set during tracing
        public int SpanState;                         // 0 = not in active span, 1 = in active span, -1 = in inverted span (end before start)
        public int Flags;
        public MSurface* Data;
        public EntityRef Entity;                      // the entity: cl_entities[0] for the world
        public float NearZi;                          // for mipmap finding
        public bool InSubmodel;
        public float DZiOrigin, DZiStepU, DZiStepV;
    }

    /// <summary><c>espan_t</c>.</summary>
    private struct ESpan
    {
        public int U, V, Count;
        public int Next;
    }

    private REdge[] _edges = null!;                   // r_edges (the stack's ledges) and the four sentinels
    private RSurf[] _surfaces = null!;                // surfaces[0..NUMSTACKSURFACES]
    private ESpan[] _spans = null!;                   // R_ScanEdges' basespans
    private int[] _newEdges = null!, _removeEdges = null!;
    private int _edgeP, _edgeMax;                     // edge_p, edge_max
    private int _surfaceP, _surfMax;                  // surface_p, surf_max (0 until the first frame, as C's NULLs)
    private int _spanP, _maxSpanP;                    // span_p, max_span_p
    private int _currentKey;                          // r_currentkey
    private int _currentIv;                           // current_iv
    private int _edgeHeadUShift20, _edgeTailUShift20;
    private float _fv;
    private int _bmodelActive;                        // r_bmodelactive

    [NoAllocUnchecked("load path")]
    private void InitEdges()
    {
        _edges = new REdge[NumStackEdges + 4];
        _surfaces = new RSurf[NumStackSurfaces + 1];
        _spans = new ESpan[MaxSpans];
        _newEdges = new int[MaxHeight];
        _removeEdges = new int[MaxHeight];
    }

    /// <summary>R_BeginEdgeFrame (with R_EdgeDrawing's surfaces on the stack): no edges, the background surface.</summary>
    private void BeginEdgeFrame()
    {
        _surfMax = NumStackSurfaces + 1;
        _edgeP = 0;
        _edgeMax = NumStackEdges;
        _surfaceP = 2;                      // background is surface 1, surface 0 is a dummy
        _surfaces[1].Spans = -1;            // no background spans yet
        _surfaces[1].Flags = SurfFlags.DrawBackground;
        // put the background behind everything in the world (r_draworder 0: R_GenerateSpans)
        _surfaces[1].Key = 0x7FFFFFFF;
        _currentKey = 0;
        for (int v = RefdefVrect.Y; v < _vrectBottom; v++)
        {
            _newEdges[v] = -1;
            _removeEdges[v] = -1;
        }
    }

    /// <summary>R_InsertNewEdges: the sorted edges to add merged into the active edge table at edgelist.</summary>
    private void InsertNewEdges(int edgestoadd, int edgelist)
    {
        REdge[] e = _edges;
        do
        {
            int nextEdge = e[edgestoadd].Next;
            while (e[edgelist].U < e[edgestoadd].U)
                edgelist = e[edgelist].Next;
            // insert edgestoadd before edgelist
            e[edgestoadd].Next = edgelist;
            e[edgestoadd].Prev = e[edgelist].Prev;
            e[e[edgelist].Prev].Next = edgestoadd;
            e[edgelist].Prev = edgestoadd;
            edgestoadd = nextEdge;
        } while (edgestoadd >= 0);
    }

    /// <summary>R_RemoveEdges: the edges ending on this scan out of the active edge table.</summary>
    private void RemoveEdges(int pedge)
    {
        REdge[] e = _edges;
        do
        {
            e[e[pedge].Next].Prev = e[pedge].Prev;
            e[e[pedge].Prev].Next = e[pedge].Next;
        } while ((pedge = e[pedge].NextRemove) >= 0);
    }

    /// <summary>R_StepActiveU: every active edge one scan down, pushed back where it overtook its predecessor.</summary>
    private void StepActiveU(int pedge)
    {
        REdge[] e = _edges;
        while (true)
        {
            e[pedge].U += e[pedge].UStep;
            if (e[pedge].U >= e[e[pedge].Prev].U)
            {
                pedge = e[pedge].Next;
                continue;
            }
            // pushback:
            if (pedge == EdgeAftertail)
                return;
            // push it back to keep it sorted
            int pnextEdge = e[pedge].Next;
            // pull the edge out of the edge list
            e[e[pedge].Next].Prev = e[pedge].Prev;
            e[e[pedge].Prev].Next = e[pedge].Next;
            // find out where the edge goes in the edge list
            int pwedge = e[e[pedge].Prev].Prev;
            while (e[pwedge].U > e[pedge].U)
                pwedge = e[pwedge].Prev;
            // put the edge back into the edge list
            e[pedge].Next = e[pwedge].Next;
            e[pedge].Prev = pwedge;
            e[e[pedge].Next].Prev = pedge;
            e[pwedge].Next = pedge;
            pedge = pnextEdge;
            if (pedge == EdgeTail)
                return;
        }
    }

    /// <summary>A span on the surface from its last_u to iu on the current scan, at the head of its list.</summary>
    private void EmitSpan(ref RSurf surf, int iu)
    {
        ref ESpan span = ref _spans[_spanP];
        span.U = surf.LastU;
        span.Count = iu - span.U;
        span.V = _currentIv;
        span.Next = surf.Spans;
        surf.Spans = _spanP++;
    }

    /// <summary>R_CleanupSpan: at the right edge, a span for whatever is on top; the stack's span states reset.</summary>
    private void CleanupSpan()
    {
        // now that we've reached the right edge of the screen, we're done with any unfinished surfaces, so emit a
        // span for whatever's on top
        int surf = _surfaces[1].Next;
        int iu = _edgeTailUShift20;
        if (iu > _surfaces[surf].LastU)
            EmitSpan(ref _surfaces[surf], iu);
        // reset spanstate for all surfaces in the surface stack
        do
        {
            _surfaces[surf].SpanState = 0;
            surf = _surfaces[surf].Next;
        } while (surf != 1);
    }

    /// <summary>R_TrailingEdge: the surface going away; a span if it was on top.</summary>
    private void TrailingEdge(int surfIndex, int edge)
    {
        ref RSurf surf = ref _surfaces[surfIndex];
        // don't generate a span if this is an inverted span, with the end edge preceding the start edge (that is,
        // we haven't seen the start edge yet)
        if (--surf.SpanState != 0)
            return;
        if (surf.InSubmodel)
            _bmodelActive--;
        if (surfIndex == _surfaces[1].Next)
        {
            // emit a span (current top going away)
            int iu = _edges[edge].U >> 20;
            if (iu > surf.LastU)
                EmitSpan(ref surf, iu);
            // set last_u on the surface below
            _surfaces[surf.Next].LastU = iu;
        }
        _surfaces[surf.Prev].Next = surf.Next;
        _surfaces[surf.Next].Prev = surf.Prev;
    }

    /// <summary>1/z of a bmodel surface at the edge's u on the current scan (R_LeadingEdge's sort of two bmodels).</summary>
    private double SurfZi(in RSurf s, double fu) => s.DZiOrigin + _fv * s.DZiStepV + fu * s.DZiStepU;

    /// <summary>R_LeadingEdge: a surface coming in, put into the stack by key; a span for the one it obscures.</summary>
    private void LeadingEdge(int edge)
    {
        if (_edges[edge].Surf1 == 0)
            return;
        // it's adding a new surface in, so find the correct place
        int surfIndex = _edges[edge].Surf1;
        ref RSurf surf = ref _surfaces[surfIndex];
        // don't start a span if this is an inverted span, with the end edge preceding the start edge (that is,
        // we've already seen the end edge)
        if (++surf.SpanState != 1)
            return;
        if (surf.InSubmodel)
            _bmodelActive++;
        int surf2 = _surfaces[1].Next;
        bool newtop = false;
        if (surf.Key < _surfaces[surf2].Key)
            newtop = true;
        // if it's two surfaces on the same plane, the one that's already active is in front, so keep going
        // unless it's a bmodel
        else if (surf.InSubmodel && surf.Key == _surfaces[surf2].Key)
        {
            // must be two bmodels in the same leaf; sort on 1/z
            double fu = (float)(_edges[edge].U - 0xFFFFF) * (1.0 / 0x100000);
            double newzi = SurfZi(surf, fu);
            double newzibottom = newzi * 0.99;
            double testzi = SurfZi(_surfaces[surf2], fu);
            if (newzibottom >= testzi)
                newtop = true;
            else
            {
                double newzitop = newzi * 1.01;
                if (newzitop >= testzi && surf.DZiStepU >= _surfaces[surf2].DZiStepU)
                    newtop = true;
            }
        }
        if (!newtop)
        {
            while (true)
            {
                // continue_search:
                do
                {
                    surf2 = _surfaces[surf2].Next;
                } while (surf.Key > _surfaces[surf2].Key);
                if (surf.Key != _surfaces[surf2].Key)
                    break;
                // if it's two surfaces on the same plane, the one that's already active is in front, so keep
                // going unless it's a bmodel
                if (!surf.InSubmodel)
                    continue;
                // must be two bmodels in the same leaf; sort on 1/z
                double fu = (float)(_edges[edge].U - 0xFFFFF) * (1.0 / 0x100000);
                double newzi = SurfZi(surf, fu);
                double newzibottom = newzi * 0.99;
                double testzi = SurfZi(_surfaces[surf2], fu);
                if (newzibottom >= testzi)
                    break;
                double newzitop = newzi * 1.01;
                if (newzitop >= testzi && surf.DZiStepU >= _surfaces[surf2].DZiStepU)
                    break;
            }
        }
        else
        {
            // emit a span (obscures current top)
            int iu = _edges[edge].U >> 20;
            if (iu > _surfaces[surf2].LastU)
                EmitSpan(ref _surfaces[surf2], iu);
            // set last_u on the new span
            surf.LastU = iu;
        }
        // gotposition: insert before surf2
        surf.Next = surf2;
        surf.Prev = _surfaces[surf2].Prev;
        _surfaces[_surfaces[surf2].Prev].Next = surfIndex;
        _surfaces[surf2].Prev = surfIndex;
    }

    /// <summary>R_GenerateSpans: the active edges of the scan from left to right, trailing before leading.</summary>
    private void GenerateSpans()
    {
        _bmodelActive = 0;
        // clear active surfaces to just the background surface
        _surfaces[1].Next = _surfaces[1].Prev = 1;
        _surfaces[1].LastU = _edgeHeadUShift20;
        // generate spans
        for (int edge = _edges[EdgeHead].Next; edge != EdgeTail; edge = _edges[edge].Next)
        {
            if (_edges[edge].Surf0 != 0)
            {
                // it has a left surface, so a surface is going away for this span
                TrailingEdge(_edges[edge].Surf0, edge);
                if (_edges[edge].Surf1 == 0)
                    continue;
            }
            LeadingEdge(edge);
        }
        CleanupSpan();
    }

    /// <summary>
    /// R_ScanEdges: newedges[] in, each surface's linked list of visible spans out; the spans drawn whenever the
    /// span buffer may not hold another scan, and at the end.
    /// </summary>
    private void ScanEdges()
    {
        REdge[] e = _edges;
        _maxSpanP = MaxSpans - RefdefVrect.Width;
        _spanP = 0;

        // clear active edges to just the background edges around the whole screen
        e[EdgeHead].U = RefdefVrect.X << 20;
        _edgeHeadUShift20 = e[EdgeHead].U >> 20;
        e[EdgeHead].UStep = 0;
        e[EdgeHead].Prev = -1;
        e[EdgeHead].Next = EdgeTail;
        e[EdgeHead].Surf0 = 0;
        e[EdgeHead].Surf1 = 1;

        e[EdgeTail].U = (_vrectRight << 20) + 0xFFFFF;
        _edgeTailUShift20 = e[EdgeTail].U >> 20;
        e[EdgeTail].UStep = 0;
        e[EdgeTail].Prev = EdgeHead;
        e[EdgeTail].Next = EdgeAftertail;
        e[EdgeTail].Surf0 = 1;
        e[EdgeTail].Surf1 = 0;

        e[EdgeAftertail].U = -1;    // force a move
        e[EdgeAftertail].UStep = 0;
        e[EdgeAftertail].Next = EdgeSentinel;
        e[EdgeAftertail].Prev = EdgeTail;

        e[EdgeSentinel].U = 2000 << 24;     // make sure nothing sorts past this (C's int overflow: the same wrap)
        e[EdgeSentinel].Prev = EdgeAftertail;

        // process all scan lines
        int bottom = _vrectBottom - 1;
        int iv;
        for (iv = RefdefVrect.Y; iv < bottom; iv++)
        {
            _currentIv = iv;
            _fv = iv;
            // mark that the head (background start) span is pre-included
            _surfaces[1].SpanState = 1;
            if (_newEdges[iv] >= 0)
                InsertNewEdges(_newEdges[iv], e[EdgeHead].Next);
            GenerateSpans();
            // flush the span list if we can't be sure we have enough spans left for the next scan
            if (_spanP >= _maxSpanP)
            {
                DrawSurfaces();
                // clear the surface span pointers
                for (int s = 1; s < _surfaceP; s++)
                    _surfaces[s].Spans = -1;
                _spanP = 0;
            }
            if (_removeEdges[iv] >= 0)
                RemoveEdges(_removeEdges[iv]);
            if (e[EdgeHead].Next != EdgeTail)
                StepActiveU(e[EdgeHead].Next);
        }

        // do the last scan (no need to step or sort or remove on the last scan)
        _currentIv = iv;
        _fv = iv;
        // mark that the head (background start) span is pre-included
        _surfaces[1].SpanState = 1;
        if (_newEdges[iv] >= 0)
            InsertNewEdges(_newEdges[iv], e[EdgeHead].Next);
        GenerateSpans();
        // draw whatever's left in the span list
        DrawSurfaces();
    }
}
