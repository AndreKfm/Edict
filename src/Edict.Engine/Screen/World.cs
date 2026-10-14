// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// The world renderer's walk as far as the client's state sees it (r_main.c, r_misc.c, r_bsp.c): R_ViewChanged's
/// view frustum, R_SetupFrame, R_MarkLeaves and R_RecursiveWorldNode, which puts the statics of the leafs it reaches
/// into cl_visedicts (R_StoreEfrags), and R_DrawEntitiesOnList's console message for a sprite's missing frame. What
/// they draw (R_RenderFace and the rest) is step 4. Node and leaf indices as in the model: a child is a node, or
/// -1 - leaf.
/// </summary>
public sealed unsafe partial class Screen
{
    private const double XCentering = 1.0 / 2.0, YCentering = 1.0 / 2.0;     // r_refdef.xOrigin, yOrigin
    private const int MaxMapLeafs = ClientState.MaxMapLeafs, MaxMapNodes = 32767;     // bspfile.h

    private int _frameCount = 1;                      // r_framecount: so frame counts initialized to 0 don't match
    private int _visFrameCount;                       // r_visframecount
    private int _viewLeaf = -1, _oldViewLeaf = -1;    // r_viewleaf, r_oldviewleaf (-1: NULL)
    private Model* _world;                            // the world the arrays below are for
    private int[] _nodeVisFrame = null!, _leafVisFrame = null!;
    private int[] _nodeParent = null!, _leafParent = null!;
    private byte[] _decompressed = null!;             // Mod_DecompressVis's static buffer, and room for C's overrun

    private Vec3 _vpn, _vright, _vup;
    private float _horizontalFieldOfView, _verticalFieldOfView;
    private Vec3[] _screenEdge = null!;               // screenedge[4].normal
    private Vec3[] _clipNormal = null!;               // view_clipplanes[4]
    private float[] _clipDist = null!;
    private int[] _frustumIndexes = null!;            // r_frustum_indexes[4*6]

    private static readonly SysErrorException WorldTooLarge = new("R_NewMap: a world with more nodes or leafs than bspfile.h allows");

    /// <summary>The world walk's arrays, sized by bspfile.h's limits, so that no map load allocates in a frame.</summary>
    [NoAllocUnchecked("load path")]
    private void InitWorld()
    {
        _nodeVisFrame = new int[MaxMapNodes];
        _leafVisFrame = new int[MaxMapLeafs];
        _nodeParent = new int[MaxMapNodes];
        _leafParent = new int[MaxMapLeafs];
        _decompressed = new byte[MaxMapLeafs / 8 + 256];
        _screenEdge = new Vec3[4];
        _clipNormal = new Vec3[4];
        _clipDist = new float[4];
        _frustumIndexes = new int[4 * 6];
    }

    private static readonly SysErrorException BadSpriteFrameCount = new("R_GetSpriteframe: a sprite without frames");

    /// <summary>R_NewMap's part: r_viewleaf NULL; the world is loaded anew in C, so its visframes are 0 again.</summary>
    public void NewMap()
    {
        _viewLeaf = -1;
        Model* world = _cl.WorldModel;
        if (world->NumNodes > MaxMapNodes || world->LeafCount > MaxMapLeafs)
            throw WorldTooLarge;
        new Span<int>(_nodeVisFrame, 0, world->NumNodes).Clear();
        new Span<int>(_leafVisFrame, 0, world->LeafCount).Clear();
        if (world == _world)
            return;
        _world = world;
        // Mod_SetParent: the root has no parent
        _nodeParent[0] = -1;
        for (int n = 0; n < world->NumNodes; n++)
            for (int c = 0; c < 2; c++)
            {
                int child = world->Nodes[n].Children[c];
                if (child >= 0)
                    _nodeParent[child] = n;
                else
                    _leafParent[-1 - child] = n;
            }
    }

    /// <summary>R_ViewChanged: the view's rectangle and its frustum's screen edges.</summary>
    private void ViewChanged(VRect pvrect, int lineadj)
    {
        const float aspect = 1.0f;          // vid.aspect
        _recalcRefdef = false;
        SetVrect(pvrect, ref RefdefVrect, lineadj);
        _horizontalFieldOfView = (float)(2.0 * Edict.Exact.CMath.Tan(FovX / 360 * Math.PI));
        float pixelAspect = aspect;
        float xOrigin = (float)XCentering, yOrigin = (float)YCentering;
        float screenAspect = RefdefVrect.Width * pixelAspect / RefdefVrect.Height;
        _verticalFieldOfView = _horizontalFieldOfView / screenAspect;

        _screenEdge[0] = new Vec3((float)(-1.0 / (xOrigin * _horizontalFieldOfView)), 0, 1);     // left side clip
        _screenEdge[1] = new Vec3((float)(1.0 / ((1.0 - xOrigin) * _horizontalFieldOfView)), 0, 1);  // right side clip
        _screenEdge[2] = new Vec3(0, (float)(-1.0 / (yOrigin * _verticalFieldOfView)), 1);       // top side clip
        _screenEdge[3] = new Vec3(0, (float)(1.0 / ((1.0 - yOrigin) * _verticalFieldOfView)), 1);    // bottom side clip
        for (int i = 0; i < 4; i++)
            Mathlib.VectorNormalize(ref _screenEdge[i]);
        // the projection's scales and centers, the alias transition: step 4
    }

    /// <summary>Mod_PointInLeaf: the index of the leaf p is in.</summary>
    private static int PointInLeaf(in Vec3 p, Model* model)
    {
        int child = 0;
        while (child >= 0)
        {
            MNode* node = model->Nodes + child;
            MPlane* plane = model->Planes + node->PlaneNum;
            float d = Vec3.Dot(p, plane->Normal) - plane->Dist;
            child = d > 0 ? node->Children[0] : node->Children[1];
        }
        return -1 - child;
    }

    /// <summary>R_SetupFrame as far as the walk needs it: the frame count, the view vectors, the view leaf, the frustum.</summary>
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
        if (CvarValue("r_numsurfs\0"u8) != 0 || CvarValue("r_numedges\0"u8) != 0)
            throw NotPortedYet;     // the counts' console lines
        _frameCount++;

        // build the transformation matrix for the given view angles
        Mathlib.AngleVectors(ViewAngles, out _vpn, out _vright, out _vup);

        // current viewleaf
        _oldViewLeaf = _viewLeaf;
        _viewLeaf = PointInLeaf(ViewOrg, _cl.WorldModel);
        _viewLeafContents = _cl.WorldModel->Leafs[_viewLeaf].Contents;
        // r_dowarp changing calls R_ViewChanged with the whole screen again: at 320x200 the same rectangle

        TransformFrustum();
        SetUpFrustumIndexes();
        CacheThrash = false;
    }

    /// <summary>R_TransformFrustum: the screen edges rotated into the view.</summary>
    private void TransformFrustum()
    {
        for (int i = 0; i < 4; i++)
        {
            Vec3 e = _screenEdge[i];
            float v0 = e.Z, v1 = -e.X, v2 = e.Y;
            var n = new Vec3(
                v1 * _vright.X + v2 * _vup.X + v0 * _vpn.X,
                v1 * _vright.Y + v2 * _vup.Y + v0 * _vpn.Y,
                v1 * _vright.Z + v2 * _vup.Z + v0 * _vpn.Z);
            _clipNormal[i] = n;
            _clipDist[i] = Vec3.Dot(ViewOrg, n);    // modelorg
        }
    }

    /// <summary>R_SetUpFrustumIndexes: per plane, which box corners are its reject and accept points.</summary>
    private void SetUpFrustumIndexes()
    {
        for (int i = 0; i < 4; i++)
        {
            Vec3 n = _clipNormal[i];
            for (int j = 0; j < 3; j++)
            {
                float c = j == 0 ? n.X : j == 1 ? n.Y : n.Z;
                if (c < 0)
                {
                    _frustumIndexes[i * 6 + j] = j;
                    _frustumIndexes[i * 6 + j + 3] = j + 3;
                }
                else
                {
                    _frustumIndexes[i * 6 + j] = j + 3;
                    _frustumIndexes[i * 6 + j + 3] = j;
                }
            }
        }
    }

    private static readonly SysErrorException VisOverrun = new("Mod_DecompressVis: a run past the port's buffer");

    /// <summary>Mod_LeafPVS: the leaf's row, decompressed (leaf 0 and a world without vis see everything).</summary>
    private byte* LeafPvs(int leaf, Model* model)
    {
        int row = (model->NumLeafs + 7) >> 3;
        fixed (byte* outBase = _decompressed)
        {
            int visofs = model->Leafs[leaf].VisOfs;
            if (leaf == 0 || visofs < 0 || model->VisData == null)
            {
                // mod_novis, or no vis info: make all visible
                new Span<byte>(outBase, row).Fill(0xff);
                return outBase;
            }
            byte* inp = model->VisData + visofs;
            int o = 0;
            do
            {
                if (*inp != 0)
                {
                    outBase[o++] = *inp++;
                    continue;
                }
                int c = inp[1];
                inp += 2;
                if (o + c > _decompressed.Length)
                    throw VisOverrun;
                while (c-- != 0)
                    outBase[o++] = 0;
            } while (o < row);
            return outBase;
        }
    }

    /// <summary>R_MarkLeaves: the leafs the view leaf sees, and their nodes up to the root, for this vis frame.</summary>
    private void MarkLeaves()
    {
        if (_oldViewLeaf == _viewLeaf)
            return;
        _visFrameCount++;
        _oldViewLeaf = _viewLeaf;
        Model* world = _cl.WorldModel;
        byte* vis = LeafPvs(_viewLeaf, world);
        for (int i = 0; i < world->NumLeafs; i++)
        {
            if ((vis[i >> 3] & (1 << (i & 7))) == 0)
                continue;
            int leaf = i + 1;
            if (_leafVisFrame[leaf] == _visFrameCount)
                continue;
            _leafVisFrame[leaf] = _visFrameCount;
            for (int node = _leafParent[leaf]; node >= 0; node = _nodeParent[node])
            {
                if (_nodeVisFrame[node] == _visFrameCount)
                    break;
                _nodeVisFrame[node] = _visFrameCount;
            }
        }
    }

    /// <summary>R_RenderWorld: the walk from the root with all four planes to clip against.</summary>
    private void RenderWorld() => RecursiveWorldNode(0, 15);

    /// <summary>R_RecursiveWorldNode without the drawing: the culling, the leafs' statics, the walk's order.</summary>
    private void RecursiveWorldNode(int child, int clipflags)
    {
        Model* world = _cl.WorldModel;
        short* minmaxs;
        if (child < 0)
        {
            int leaf = -1 - child;
            MLeaf* l = world->Leafs + leaf;
            if (l->Contents == Bsp.ContentsSolid)
                return;     // solid
            if (_leafVisFrame[leaf] != _visFrameCount)
                return;
            minmaxs = l->MinMaxs;
        }
        else
        {
            if (_nodeVisFrame[child] != _visFrameCount)
                return;
            minmaxs = world->Nodes[child].MinMaxs;
        }

        // cull the clipping planes if not trivial accept
        if (clipflags != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if ((clipflags & (1 << i)) == 0)
                    continue;   // don't need to clip against it
                // generate accept and reject points
                int pi = i * 6;
                var rejectpt = new Vec3(minmaxs[_frustumIndexes[pi]], minmaxs[_frustumIndexes[pi + 1]], minmaxs[_frustumIndexes[pi + 2]]);
                double d = Vec3.Dot(rejectpt, _clipNormal[i]);
                d -= _clipDist[i];
                if (d <= 0)
                    return;
                var acceptpt = new Vec3(minmaxs[_frustumIndexes[pi + 3]], minmaxs[_frustumIndexes[pi + 4]], minmaxs[_frustumIndexes[pi + 5]]);
                d = Vec3.Dot(acceptpt, _clipNormal[i]);
                d -= _clipDist[i];
                if (d >= 0)
                    clipflags &= ~(1 << i);     // node is entirely on screen
            }
        }

        // if a leaf node, draw stuff
        if (child < 0)
        {
            int leaf = -1 - child;
            // the leaf's surfaces' visframes, pleaf->key, r_currentkey: step 4
            // deal with model fragments in this leaf
            if (_cl.LeafEfrags[leaf] >= 0)
                StoreEfrags(leaf);
            return;
        }

        // node is just a decision point, so go down the apropriate sides; find which side of the node we are on
        MNode* node = world->Nodes + child;
        MPlane* plane = world->Planes + node->PlaneNum;
        double dot = plane->Type switch
        {
            0 => ViewOrg.X - plane->Dist,
            1 => ViewOrg.Y - plane->Dist,
            2 => ViewOrg.Z - plane->Dist,
            _ => Vec3.Dot(ViewOrg, plane->Normal) - plane->Dist,
        };
        int side = dot >= 0 ? 0 : 1;
        // recurse down the children, front side first
        RecursiveWorldNode(node->Children[side], clipflags);
        // the node's surfaces (R_RenderFace): step 4
        // recurse down the back side
        RecursiveWorldNode(node->Children[side ^ 1], clipflags);
    }

    private const int MaxVisEdicts = 256;

    /// <summary>R_StoreEfrags: the leaf's statics into cl_visedicts, each once a frame.</summary>
    private void StoreEfrags(int leaf)
    {
        for (int ef = _cl.LeafEfrags[leaf]; ef >= 0; ef = _cl.Efrags[ef].LeafNext)
        {
            int stat = _cl.Efrags[ef].Entity;
            Model* m = _cl.StaticEntities[stat].Model;
            if (m->Type is not (ModelType.Alias or ModelType.Brush or ModelType.Sprite))
                throw BadEntityType;
            if (_cl.StaticVisFrame[stat] != _frameCount && _cl.NumVisEdicts < MaxVisEdicts)
            {
                _cl.VisEdicts[_cl.NumVisEdicts++] = new EntityRef { Array = 1, Index = stat };
                // mark that we've recorded this entity for this frame
                _cl.StaticVisFrame[stat] = _frameCount;
            }
        }
    }

    private static readonly SysErrorException BadEntityType = new("R_StoreEfrags: Bad entity type");

    /// <summary>The entity a visible-entity reference names.</summary>
    private ref ClientEntity Entity(EntityRef r)
    {
        if (r.Array == 0)
            return ref _cl.Entities[r.Index];
        if (r.Array == 1)
            return ref _cl.StaticEntities[r.Index];
        return ref _cl.TempEntities[r.Index];
    }

    /// <summary>R_DrawEntitiesOnList's console output: a sprite drawn with a frame it does not have (R_GetSpriteframe).</summary>
    private void DrawEntitiesOnList()
    {
        if (CvarValue("r_drawentities\0"u8) == 0)
            return;
        for (int i = 0; i < _cl.NumVisEdicts; i++)
        {
            EntityRef r = _cl.VisEdicts[i];
            if (r.Array == 0 && r.Index == _cl.ViewEntity)
                continue;   // don't draw the player
            ref ClientEntity e = ref Entity(r);
            if (e.Model->Type != ModelType.Sprite)
                continue;
            int frame = e.Frame;
            if (frame >= e.Model->NumFrames || frame < 0)
                Msg("R_DrawSprite: no such frame "u8).MsgInt(frame).Msg("\n"u8).EndMsg();
            else if (e.Model->NumFrames <= 0)
                throw BadSpriteFrameCount;
        }
    }
}
