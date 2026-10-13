/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: hull trace corpus (.etr), the phase-1 gate (docs/PLAN.md section C, cut 1).

After a map has been spawned, writes the collision data the loader produced (planes, nodes, leafs, clipnodes,
hull 0, every brush model's hulls) and then <count> deterministic traces through id's SV_ClipMoveToEntity,
each with its inputs and the complete trace_t, plus SV_HullPointContents of the world hulls at start and end.
The inputs come from splitmix64 (the tape generator's generator) and are stored in the file, so the port reads
them instead of generating its own (docs/TESTING.md R2). Only model data is read: the traces do not depend on
the game state of the frame that ran before.

Input classes: 70 % against the world, 15 % against a brush model (inline or item box, at its spawn position or
moved), 15 % against a box entity. Sizes: point, player, large, or arbitrary (the hull choice goes by size[0]).
World starts are mostly in open space of the trace's hull, some in liquids; ends are the start, an axis move, a
short move or anywhere. Further world traces are chained (start where the last one stopped), start on a plane,
or are chosen to run the backup loop; why each class exists is at its code.

File layout (all integers little-endian, floats as raw bits):
  header  "ETR1"  u32 version  u64 seed  u32 count  char map[64]
  planes  u32 n, then n x { f32 normal[3]  f32 dist  u8 type  u8 signbits }
  nodes   u32 n, then n x { u32 planenum  i32 children[2] (node index, or -1 - leaf index)  i16 minmaxs[6]
                            u16 firstsurface  u16 numsurfaces }
  leafs   u32 n, then n x { i32 contents  i16 minmaxs[6]  i32 visofs (-1 = none)  u8 ambient[4] }
  clip    u32 n, then n x { u32 planenum  i16 children[2] }        model->clipnodes (hulls 1 and 2)
  hull0   u32 n, then n x { u32 planenum  i16 children[2] }        hulls[0].clipnodes (Mod_MakeHull0)
  models  u32 n, then n x { u32 sv.models index  char name[64]  f32 mins[3]  f32 maxs[3]  f32 radius
                            i32 numleafs  i32 firstmodelsurface  i32 nummodelsurfaces
                            3 x { i32 firstclipnode  i32 lastclipnode  f32 clip_mins[3]  f32 clip_maxs[3] } }
  traces  count x TRACE_RECORD bytes:
          in:  u8 kind (0 world, 1 brush model, 2 box)  u16 modelindex (kind 1)  f32 ent_origin[3]
               f32 ent_mins[3]  f32 ent_maxs[3] (kind 2)  f32 start[3]  f32 mins[3]  f32 maxs[3]  f32 end[3]
          out: u8 allsolid  u8 startsolid  u8 inopen  u8 inwater  f32 fraction  f32 endpos[3]
               f32 plane.normal[3]  f32 plane.dist  i8 ent (-1 NULL, 0 world, 1 the test entity)
               i16 contents_start[3]  i16 contents_end[3] (world hulls 0..2, from their firstclipnode)
*/
#include "quakedef.h"
#include <time.h>
#include "edict_traces.h"

#define ETR_VERSION		1
#define	DIST_EPSILON	(0.03125)	// world.c

trace_t SV_ClipMoveToEntity (edict_t *ent, vec3_t start, vec3_t mins, vec3_t maxs, vec3_t end);	// world.c
hull_t *SV_HullForEntity (edict_t *ent, vec3_t mins, vec3_t maxs, vec3_t offset);				// world.c
int SV_HullPointContents (hull_t *hull, int num, vec3_t p);										// world.c

static FILE		*out;

static void put8 (int v)
{
	unsigned char	b = (unsigned char)v;

	if (fwrite (&b, 1, 1, out) != 1)
		Sys_Error ("EdictTraces: write failed");
}

static void put16 (int v)
{
	put8 (v); put8 (v >> 8);
}

static void put32 (unsigned int v)
{
	put8 (v); put8 (v >> 8); put8 (v >> 16); put8 (v >> 24);
}

static void putfloat (float f)
{
	unsigned int	bits;

	memcpy (&bits, &f, 4);
	put32 (bits);
}

static void putvec (vec3_t v)
{
	putfloat (v[0]); putfloat (v[1]); putfloat (v[2]);
}

static void putname (const char *s)
{
	int		i, n = strlen (s);

	for (i = 0 ; i < 64 ; i++)
		put8 (i < n ? s[i] : 0);
}

/*
==============================================================================
Loader dump
==============================================================================
*/

static void PutClipnodes (dclipnode_t *c, int n)
{
	int		i;

	put32 (n);
	for (i = 0 ; i < n ; i++)
	{
		put32 (c[i].planenum);
		put16 (c[i].children[0]);
		put16 (c[i].children[1]);
	}
}

// The leaf count of the lump: Mod_LoadBrushModel overwrites model->numleafs with the submodel's visleafs.
static int LeafLumpCount (model_t *m)
{
	dheader_t	*header = (dheader_t *)COM_LoadTempFile (m->name);

	if (!header)
		Sys_Error ("EdictTraces: cannot reload %s", m->name);
	return LittleLong (header->lumps[LUMP_LEAFS].filelen) / sizeof (dleaf_t);
}

static void PutWorld (model_t *m)
{
	int		i, j, numleafs = LeafLumpCount (m);
	mnode_t	*child;

	put32 (m->numplanes);
	for (i = 0 ; i < m->numplanes ; i++)
	{
		putvec (m->planes[i].normal);
		putfloat (m->planes[i].dist);
		put8 (m->planes[i].type);
		put8 (m->planes[i].signbits);
	}

	put32 (m->numnodes);
	for (i = 0 ; i < m->numnodes ; i++)
	{
		put32 (m->nodes[i].plane - m->planes);
		for (j = 0 ; j < 2 ; j++)
		{
			child = m->nodes[i].children[j];
			if (child->contents < 0)
				put32 (-1 - (int)((mleaf_t *)child - m->leafs));
			else
				put32 (child - m->nodes);
		}
		for (j = 0 ; j < 6 ; j++)
			put16 (m->nodes[i].minmaxs[j]);
		put16 (m->nodes[i].firstsurface);
		put16 (m->nodes[i].numsurfaces);
	}

	put32 (numleafs);
	for (i = 0 ; i < numleafs ; i++)
	{
		put32 (m->leafs[i].contents);
		for (j = 0 ; j < 6 ; j++)
			put16 (m->leafs[i].minmaxs[j]);
		put32 (m->leafs[i].compressed_vis ? (int)(m->leafs[i].compressed_vis - m->visdata) : -1);
		for (j = 0 ; j < 4 ; j++)
			put8 (m->leafs[i].ambient_sound_level[j]);
	}

	PutClipnodes (m->clipnodes, m->numclipnodes);
	PutClipnodes (m->hulls[0].clipnodes, m->hulls[0].lastclipnode + 1);
}

static int CountBrushModels (void)
{
	int		i, n = 0;

	for (i = 1 ; i < MAX_MODELS && sv.model_precache[i] ; i++)
		if (sv.models[i] && sv.models[i]->type == mod_brush)
			n++;
	return n;
}

static void PutModels (void)
{
	int		i, h;
	model_t	*m;

	put32 (CountBrushModels ());
	for (i = 1 ; i < MAX_MODELS && sv.model_precache[i] ; i++)
	{
		m = sv.models[i];
		if (!m || m->type != mod_brush)
			continue;
		put32 (i);
		putname (m->name);
		putvec (m->mins);
		putvec (m->maxs);
		putfloat (m->radius);
		put32 (m->numleafs);
		put32 (m->firstmodelsurface);
		put32 (m->nummodelsurfaces);
		for (h = 0 ; h < 3 ; h++)
		{
			put32 (m->hulls[h].firstclipnode);
			put32 (m->hulls[h].lastclipnode);
			putvec (m->hulls[h].clip_mins);
			putvec (m->hulls[h].clip_maxs);
		}
	}
}

/*
==============================================================================
Trace inputs
==============================================================================
*/

static unsigned long long	rng;

static unsigned long long Next (void)
{
	unsigned long long	z = (rng += 0x9E3779B97F4A7C15ULL);

	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
	z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
	return z ^ (z >> 31);
}

static double Uniform (void)	// [0, 1)
{
	return (Next () >> 11) * (1.0 / 9007199254740992.0);
}

static int Below (int n)
{
	return (int)(Uniform () * n);
}

// A coordinate in [lo, hi]: raw float, on the integer grid (where axial planes of maps lie, so points start
// exactly on planes), or on the 1/8 grid (MSG_WriteCoord resolution).
static float Coord (float lo, float hi)
{
	double	v = lo + (hi - lo) * Uniform ();
	int		q = Below (10);

	if (q < 4)
		return (float)floor (v + 0.5);
	if (q < 6)
		return (float)(floor (v * 8 + 0.5) / 8);
	return (float)v;
}

static void Point (vec3_t p, vec3_t lo, vec3_t hi)
{
	int		i;

	for (i = 0 ; i < 3 ; i++)
		p[i] = Coord (lo[i], hi[i]);
}

// What a sampled world point should be in. Uniform points in a map's bounds are mostly in solid (62 of 70 %
// of world traces were allsolid in a first run on start), so world points are drawn by rejection sampling
// against the hull the trace uses; the inputs stay what they are, only their distribution changes.
#define WANT_ANY		0
#define WANT_OPEN		1	// not solid in the trace's hull
#define WANT_LIQUID		2	// water, slime or lava in hull 0 (clip hulls have no liquid contents)

static hull_t	*want_hull;		// NULL: no constraint (brush model and box traces)
static vec3_t	want_offset;

static qboolean Wanted (vec3_t p, int want)
{
	vec3_t	l;
	int		c;

	if (!want_hull || want == WANT_ANY)
		return true;
	if (want == WANT_LIQUID)
	{
		c = SV_HullPointContents (&sv.worldmodel->hulls[0], sv.worldmodel->hulls[0].firstclipnode, p);
		return c <= CONTENTS_WATER && c >= CONTENTS_LAVA;
	}
	VectorSubtract (p, want_offset, l);
	return SV_HullPointContents (want_hull, want_hull->firstclipnode, l) != CONTENTS_SOLID;
}

static void Sample (vec3_t p, vec3_t lo, vec3_t hi, int want)
{
	int		tries;

	for (tries = 0 ; tries < 256 ; tries++)
	{
		Point (p, lo, hi);
		if (Wanted (p, want))
			return;
	}
}

static int Want (void)
{
	int		q = Below (20);

	return q < 15 ? WANT_OPEN : q < 17 ? WANT_LIQUID : WANT_ANY;
}

static void End (vec3_t end, vec3_t start, vec3_t lo, vec3_t hi)
{
	int		i, q = Below (10);

	if (q == 0)						// 10 %: start == end (SV_TestEntityPosition, droptofloor checks)
	{
		VectorCopy (start, end);
	}
	else if (q == 1)				// 10 %: along one axis
	{
		VectorCopy (start, end);
		i = Below (3);
		end[i] = Coord (lo[i], hi[i]);
	}
	else if (q < 6)					// 40 %: a short move, as in SV_FlyMove / SV_StepMove
	{
		for (i = 0 ; i < 3 ; i++)
			end[i] = start[i] + Coord (-64, 64);
	}
	else							// 40 %: anywhere in the box
		Sample (end, lo, hi, Want ());
}

static vec3_t	player_mins = {-16, -16, -24}, player_maxs = {16, 16, 32};
static vec3_t	large_mins = {-32, -32, -24}, large_maxs = {32, 32, 64};

static void Size (vec3_t mins, vec3_t maxs)
{
	int		i, q = Below (20);

	if (q < 6)						// point (missiles, traceline)
	{
		VectorCopy (vec3_origin, mins);
		VectorCopy (vec3_origin, maxs);
	}
	else if (q < 13)
	{
		VectorCopy (player_mins, mins);
		VectorCopy (player_maxs, maxs);
	}
	else if (q < 17)
	{
		VectorCopy (large_mins, mins);
		VectorCopy (large_maxs, maxs);
	}
	else							// any size: the hull choice in SV_HullForEntity goes by size[0] alone
	{
		for (i = 0 ; i < 3 ; i++)
		{
			mins[i] = (float)-Below (41);
			maxs[i] = (float)Below (73);
		}
	}
}

// Chained world traces. SV_FlyMove and SV_StepMove start each trace where the previous one stopped, DIST_EPSILON
// off a plane, and slide along it or push into it. Uniform points almost never lie that close to a plane: on the
// first corpus a float DIST_EPSILON and a missing backup loop in SV_RecursiveHullCheck went undetected
// (docs/TESTING.md R7), because the crossing fraction only rounds differently for |t1| < 1/32 and the backup
// loop only runs next to surfaces.
static qboolean	chain_valid;
static vec3_t	chain_mins, chain_maxs, chain_pos, chain_normal, chain_normal2;

static void ChainEnd (vec3_t end, vec3_t start)
{
	int		i, q = Below (10);
	float	d, into;
	vec3_t	delta;

	for (i = 0 ; i < 3 ; i++)
		delta[i] = Coord (-64, 64);
	if (q < 4)						// slide along the plane as ClipVelocity does, pushed into it a little
	{
		d = DotProduct (delta, chain_normal);
		into = (float)(Uniform () * 0.5);
		for (i = 0 ; i < 3 ; i++)
			delta[i] = delta[i] - chain_normal[i] * (d + into);
	}
	else if (q < 6)					// into the plane, slightly sideways
	{
		into = Coord (0, 16);
		for (i = 0 ; i < 3 ; i++)
			delta[i] = delta[i] / 16 - chain_normal[i] * into;
	}
	else if (q < 7)					// into the corner of the last two planes hit
	{
		into = Coord (0, 16);
		for (i = 0 ; i < 3 ; i++)
			delta[i] = delta[i] / 64 - (chain_normal[i] + chain_normal2[i]) * into;
	}
	else if (q < 8)					// straight down: droptofloor, the down move of SV_StepMove
	{
		delta[0] = delta[1] = 0;
		delta[2] = -Coord (0, 256);
	}
	VectorAdd (start, delta, end);
}

// Starts on a plane of the hull. Where a segment starts within rounding distance of a sloped plane, the crossing
// point computed further down can land on the other side of that plane, and SV_RecursiveHullCheck's backup loop
// ("shouldn't really happen, but does occasionally") runs. Chained starts lie 1/32 off a plane, too far for that.
static qboolean OnPlane (vec3_t start, vec3_t end, vec3_t lo, vec3_t hi)
{
	int			i, tries;
	mplane_t	*plane;
	vec3_t		l, delta;
	float		d;

	for (tries = 0 ; tries < 64 ; tries++)
	{
		plane = want_hull->planes
			+ want_hull->clipnodes[want_hull->firstclipnode
				+ Below (want_hull->lastclipnode - want_hull->firstclipnode + 1)].planenum;
		if (plane->type < 3 && Below (4))
			continue;					// mostly sloped planes: axial ones cannot flip a side by rounding
		Sample (start, lo, hi, WANT_OPEN);
		VectorSubtract (start, want_offset, l);
		d = DotProduct (plane->normal, l) - plane->dist;
		for (i = 0 ; i < 3 ; i++)
			start[i] = start[i] - plane->normal[i] * d;
		if (!Wanted (start, WANT_OPEN))
			continue;
		for (i = 0 ; i < 3 ; i++)
			delta[i] = Coord (-64, 64);
		if (Below (2))
		{	// along the plane
			d = DotProduct (delta, plane->normal);
			for (i = 0 ; i < 3 ; i++)
				delta[i] = delta[i] - plane->normal[i] * d;
		}
		VectorAdd (start, delta, end);
		return true;
	}
	return false;
}

/*
==============================================================================
Backup-seeking inputs

The backup loop at the end of SV_RecursiveHullCheck ("shouldn't really happen, but does occasionally") runs about
5 times in 10^6 traces of the classes above, too rarely for the gate to see a wrong step size in it (R7). A
fraction of the world traces is therefore chosen by importance sampling: candidates are drawn until one makes
the loop move the crossing point back. Whether it does is decided by ProbeHullCheck, a copy of
SV_RecursiveHullCheck that only reports it. The copy chooses inputs and nothing else: every record's result
comes from id's SV_ClipMoveToEntity, like every other record's.
==============================================================================
*/

static qboolean	probe_backup;	// the loop moved the crossing point at least once
static int		backups_found;

// SV_RecursiveHullCheck (world.c), unchanged except for the probe_backup line and without the node range check.
static qboolean ProbeHullCheck (hull_t *hull, int num, float p1f, float p2f, vec3_t p1, vec3_t p2, trace_t *trace)
{
	dclipnode_t	*node;
	mplane_t	*plane;
	float		t1, t2;
	float		frac;
	int			i;
	vec3_t		mid;
	int			side;
	float		midf;

	if (num < 0)
	{
		if (num != CONTENTS_SOLID)
		{
			trace->allsolid = false;
			if (num == CONTENTS_EMPTY)
				trace->inopen = true;
			else
				trace->inwater = true;
		}
		else
			trace->startsolid = true;
		return true;
	}

	node = hull->clipnodes + num;
	plane = hull->planes + node->planenum;

	if (plane->type < 3)
	{
		t1 = p1[plane->type] - plane->dist;
		t2 = p2[plane->type] - plane->dist;
	}
	else
	{
		t1 = DotProduct (plane->normal, p1) - plane->dist;
		t2 = DotProduct (plane->normal, p2) - plane->dist;
	}

	if (t1 >= 0 && t2 >= 0)
		return ProbeHullCheck (hull, node->children[0], p1f, p2f, p1, p2, trace);
	if (t1 < 0 && t2 < 0)
		return ProbeHullCheck (hull, node->children[1], p1f, p2f, p1, p2, trace);

	if (t1 < 0)
		frac = (t1 + DIST_EPSILON)/(t1-t2);
	else
		frac = (t1 - DIST_EPSILON)/(t1-t2);
	if (frac < 0)
		frac = 0;
	if (frac > 1)
		frac = 1;

	midf = p1f + (p2f - p1f)*frac;
	for (i=0 ; i<3 ; i++)
		mid[i] = p1[i] + frac*(p2[i] - p1[i]);

	side = (t1 < 0);

	if (!ProbeHullCheck (hull, node->children[side], p1f, midf, p1, mid, trace) )
		return false;

	if (SV_HullPointContents (hull, node->children[side^1], mid) != CONTENTS_SOLID)
		return ProbeHullCheck (hull, node->children[side^1], midf, p2f, mid, p2, trace);

	if (trace->allsolid)
		return false;

	if (!side)
	{
		VectorCopy (plane->normal, trace->plane.normal);
		trace->plane.dist = plane->dist;
	}
	else
	{
		VectorSubtract (vec3_origin, plane->normal, trace->plane.normal);
		trace->plane.dist = -plane->dist;
	}

	while (SV_HullPointContents (hull, hull->firstclipnode, mid) == CONTENTS_SOLID)
	{
		frac -= 0.1;
		if (frac < 0)
		{
			trace->fraction = midf;
			VectorCopy (mid, trace->endpos);
			return false;
		}
		probe_backup = true;
		midf = p1f + (p2f - p1f)*frac;
		for (i=0 ; i<3 ; i++)
			mid[i] = p1[i] + frac*(p2[i] - p1[i]);
	}

	trace->fraction = midf;
	VectorCopy (mid, trace->endpos);

	return false;
}

// Whether a world trace with these inputs moves a crossing point back (SV_ClipMoveToEntity's setup).
static qboolean Backs (vec3_t start, vec3_t mins, vec3_t maxs, vec3_t end)
{
	trace_t		trace;
	vec3_t		offset, start_l, end_l;
	hull_t		*hull;

	memset (&trace, 0, sizeof (trace));
	trace.fraction = 1;
	trace.allsolid = true;
	hull = SV_HullForEntity (sv.edicts, mins, maxs, offset);
	VectorSubtract (start, offset, start_l);
	VectorSubtract (end, offset, end_l);
	probe_backup = false;
	ProbeHullCheck (hull, hull->firstclipnode, 0, 1, start_l, end_l, &trace);
	return probe_backup;
}

#define POOL_SIZE	64
static int		pool_n, pool_next;
static vec3_t	pool_start[POOL_SIZE], pool_end[POOL_SIZE], pool_mins[POOL_SIZE], pool_maxs[POOL_SIZE];

static float Jitter (void)
{
	static const float	scale[3] = {1.0f / 32, 0.25f, 2};

	return (float)(Uniform () * 2 - 1) * scale[Below (3)];
}

// Draws world trace candidates until one backs up (at most 4096); a found case joins a pool whose neighbours
// are tried next, because the cases cluster at convex edges.
static void SeekBackup (vec3_t start, vec3_t end, vec3_t mins, vec3_t maxs, vec3_t lo, vec3_t hi)
{
	int		tries, i, k;

	for (tries = 0 ; tries < 4096 ; tries++)
	{
		if (pool_n && Below (2))
		{
			k = Below (pool_n);
			VectorCopy (pool_mins[k], mins);
			VectorCopy (pool_maxs[k], maxs);
			for (i = 0 ; i < 3 ; i++)
			{
				start[i] = pool_start[k][i] + Jitter ();
				end[i] = pool_end[k][i] + Jitter ();
			}
		}
		else if (chain_valid && Below (2))
		{
			VectorCopy (chain_mins, mins);
			VectorCopy (chain_maxs, maxs);
			VectorCopy (chain_pos, start);
			ChainEnd (end, start);
		}
		else
		{
			Size (mins, maxs);
			want_hull = SV_HullForEntity (sv.edicts, mins, maxs, want_offset);
			if (Below (2) || !OnPlane (start, end, lo, hi))
			{
				Sample (start, lo, hi, WANT_OPEN);
				End (end, start, lo, hi);
			}
		}
		if (Backs (start, mins, maxs, end))
		{
			k = pool_n < POOL_SIZE ? pool_n++ : pool_next++ % POOL_SIZE;
			VectorCopy (start, pool_start[k]);
			VectorCopy (end, pool_end[k]);
			VectorCopy (mins, pool_mins[k]);
			VectorCopy (maxs, pool_maxs[k]);
			backups_found++;
			return;
		}
	}
}

/*
==============================================================================
Driver
==============================================================================
*/

static void Expand (vec3_t lo, vec3_t hi, vec3_t mins, vec3_t maxs, vec3_t origin, float margin)
{
	int		i;

	for (i = 0 ; i < 3 ; i++)
	{
		lo[i] = origin[i] + mins[i] - margin;
		hi[i] = origin[i] + maxs[i] + margin;
	}
}

void EdictTraces_Write (char *path, int count, unsigned long long seed)
{
	static edict_t	test_ent;	// SV_HullForEntity reads only v.solid, movetype, modelindex, origin, mins, maxs
	model_t		*world = sv.worldmodel;
	int			brush[MAX_MODELS], numbrush = 0;
	int			i, h, kind, modelindex;
	vec3_t		lo, hi, start, end, mins, maxs;
	edict_t		*ent;
	trace_t		trace;
	qboolean	chained;

	if (!sv.active || !world)
		Sys_Error ("EdictTraces: no map loaded");
	out = fopen (path, "wb");
	if (!out)
		Sys_Error ("EdictTraces: cannot open %s", path);

	for (i = 2 ; i < MAX_MODELS && sv.model_precache[i] ; i++)	// 1 is the world
		if (sv.models[i] && sv.models[i]->type == mod_brush)
			brush[numbrush++] = i;

	put8 ('E'); put8 ('T'); put8 ('R'); put8 ('1');
	put32 (ETR_VERSION);
	put32 ((unsigned int)seed); put32 ((unsigned int)(seed >> 32));
	put32 (count);
	putname (sv.name);
	PutWorld (world);
	PutModels ();

	rng = seed;
	chain_valid = false;
	pool_n = pool_next = backups_found = 0;
	for (i = 0 ; i < count ; i++)
	{
		memset (&test_ent, 0, sizeof (test_ent));
		modelindex = 0;
		h = Below (20);
		kind = h < 14 ? 0 : (h < 17 && numbrush) ? 1 : 2;
		if (kind == 0)
		{
			ent = sv.edicts;
			Expand (lo, hi, world->mins, world->maxs, vec3_origin, 32);
		}
		else if (kind == 1)
		{
			ent = &test_ent;
			modelindex = brush[Below (numbrush)];
			ent->v.solid = SOLID_BSP;
			ent->v.movetype = MOVETYPE_PUSH;
			ent->v.modelindex = modelindex;
			if (Below (2))			// doors and plats move away from their spawn position
				for (h = 0 ; h < 3 ; h++)
					ent->v.origin[h] = Coord (-128, 128);
			Expand (lo, hi, sv.models[modelindex]->mins, sv.models[modelindex]->maxs, ent->v.origin, 64);
		}
		else
		{
			ent = &test_ent;
			ent->v.solid = SOLID_BBOX;
			ent->v.movetype = MOVETYPE_STEP;
			for (h = 0 ; h < 3 ; h++)
			{
				ent->v.mins[h] = (float)-Below (33);
				ent->v.maxs[h] = (float)Below (57);
			}
			Expand (lo, hi, world->mins, world->maxs, vec3_origin, 0);
			Point (ent->v.origin, lo, hi);
			Expand (lo, hi, ent->v.mins, ent->v.maxs, ent->v.origin, 96);
		}
		chained = kind == 0 && chain_valid && Below (10) < 4;
		if (kind == 0 && Below (25) == 0)		// 4 % of all records: seek the backup loop
			SeekBackup (start, end, mins, maxs, lo, hi);
		else if (chained)
		{
			VectorCopy (chain_mins, mins);
			VectorCopy (chain_maxs, maxs);
			VectorCopy (chain_pos, start);
			ChainEnd (end, start);
		}
		else
		{
			Size (mins, maxs);
			want_hull = kind == 0 ? SV_HullForEntity (ent, mins, maxs, want_offset) : NULL;
			if (!want_hull || Below (10) >= 2 || !OnPlane (start, end, lo, hi))
			{
				Sample (start, lo, hi, Want ());
				End (end, start, lo, hi);
			}
		}

		trace = SV_ClipMoveToEntity (ent, start, mins, maxs, end);

		if (kind == 0)
		{
			if (trace.allsolid || trace.startsolid)
				chain_valid = false;
			else if (trace.fraction < 1 || chain_valid)
			{	// rest where the move stopped; after a free move keep the last plane
				chain_valid = true;
				VectorCopy (mins, chain_mins);
				VectorCopy (maxs, chain_maxs);
				VectorCopy (trace.endpos, chain_pos);
				if (trace.fraction < 1)
				{
					VectorCopy (chain_normal, chain_normal2);
					VectorCopy (trace.plane.normal, chain_normal);
				}
			}
		}

		put8 (kind);
		put16 (modelindex);
		putvec (ent->v.origin);
		putvec (kind == 2 ? ent->v.mins : vec3_origin);
		putvec (kind == 2 ? ent->v.maxs : vec3_origin);
		putvec (start);
		putvec (mins);
		putvec (maxs);
		putvec (end);

		put8 (trace.allsolid);
		put8 (trace.startsolid);
		put8 (trace.inopen);
		put8 (trace.inwater);
		putfloat (trace.fraction);
		putvec (trace.endpos);
		putvec (trace.plane.normal);
		putfloat (trace.plane.dist);
		put8 (!trace.ent ? -1 : trace.ent == sv.edicts ? 0 : 1);
		for (h = 0 ; h < 3 ; h++)
			put16 (SV_HullPointContents (&world->hulls[h], world->hulls[h].firstclipnode, start));
		for (h = 0 ; h < 3 ; h++)
			put16 (SV_HullPointContents (&world->hulls[h], world->hulls[h].firstclipnode, end));
	}

	if (fclose (out))
		Sys_Error ("EdictTraces: write failed");
	out = NULL;
	Con_Printf ("EdictTraces: %i traces on %s written to %s (%i chosen to back up)\n", count, sv.name, path,
		backups_found);
}

/*
==============================================================================
Timing: id's SV_ClipMoveToEntity on the inputs of a corpus file

The comparison point for the port's benchmark (bench/Edict.Benchmarks). Reads the records written above, first
checks every result against the file (so the timing runs on the corpus the gate checks), then calls
SV_ClipMoveToEntity reps times over all records and prints ns per trace. As in the port's benchmark, the inputs
are prepared once; per call only the six entity fields SV_HullForEntity reads are set.
==============================================================================
*/

typedef struct
{
	int		kind, modelindex;
	vec3_t	ent_origin, ent_mins, ent_maxs, start, mins, maxs, end;
	float	fraction;
	vec3_t	endpos;
} benchrec_t;

static unsigned char	*bench_data;
static int				bench_pos, bench_len;

static unsigned int get32 (void)
{
	unsigned char	*p = bench_data + bench_pos;

	if (bench_pos + 4 > bench_len)
		Sys_Error ("EdictTraces: corpus file truncated");
	bench_pos += 4;
	return p[0] | (p[1] << 8) | (p[2] << 16) | ((unsigned int)p[3] << 24);
}

static float getfloat (void)
{
	unsigned int	bits = get32 ();
	float			f;

	memcpy (&f, &bits, 4);
	return f;
}

static void getvec (vec3_t v)
{
	v[0] = getfloat (); v[1] = getfloat (); v[2] = getfloat ();
}

static void Skip (int size)
{
	bench_pos += get32 () * size;
}

void EdictTraces_Bench (char *path, int reps)
{
	static edict_t	test_ent;
	FILE			*f;
	benchrec_t		*recs, *r;
	int				count, i, rep, base;
	unsigned char	*p;
	edict_t			*ent;
	trace_t			trace;
	clock_t			t0, t1;
	double			sum = 0, ns;

	f = fopen (path, "rb");
	if (!f)
		Sys_Error ("EdictTraces: cannot open %s", path);
	fseek (f, 0, SEEK_END);
	bench_len = ftell (f);
	fseek (f, 0, SEEK_SET);
	bench_data = malloc (bench_len);
	if (!bench_data || fread (bench_data, 1, bench_len, f) != (size_t)bench_len)
		Sys_Error ("EdictTraces: cannot read %s", path);
	fclose (f);

	if (memcmp (bench_data, "ETR1", 4))
		Sys_Error ("EdictTraces: %s is not a corpus file", path);
	bench_pos = 4 + 4 + 8;
	count = get32 ();
	if (strncmp ((char *)bench_data + bench_pos, sv.name, 64))
		Sys_Error ("EdictTraces: %s is for another map than %s", path, sv.name);
	bench_pos += 64;
	Skip (18); Skip (28); Skip (24); Skip (8); Skip (8);	// planes, nodes, leafs, clipnodes, hull 0
	Skip (4 + 64 + 12 + 12 + 4 + 12 + 3 * 32);				// models
	if (bench_len - bench_pos != count * 136)
		Sys_Error ("EdictTraces: %s: record section has the wrong size", path);

	recs = malloc (count * sizeof (*recs));
	if (!recs)
		Sys_Error ("EdictTraces: out of memory");
	for (i = 0 ; i < count ; i++)
	{
		r = &recs[i];
		base = bench_pos;
		p = bench_data + bench_pos;
		r->kind = p[0];
		r->modelindex = p[1] | (p[2] << 8);
		bench_pos += 3;
		getvec (r->ent_origin); getvec (r->ent_mins); getvec (r->ent_maxs);
		getvec (r->start); getvec (r->mins); getvec (r->maxs); getvec (r->end);
		bench_pos += 4;				// allsolid, startsolid, inopen, inwater
		r->fraction = getfloat ();
		getvec (r->endpos);
		bench_pos = base + 136;
	}

	for (rep = -1 ; rep < reps ; rep++)		// rep -1: check against the file, untimed
	{
		if (rep == 0)
			t0 = clock ();
		for (i = 0 ; i < count ; i++)
		{
			r = &recs[i];
			if (r->kind == 0)
				ent = sv.edicts;
			else
			{
				ent = &test_ent;
				ent->v.solid = r->kind == 1 ? SOLID_BSP : SOLID_BBOX;
				ent->v.movetype = r->kind == 1 ? MOVETYPE_PUSH : MOVETYPE_STEP;
				ent->v.modelindex = r->modelindex;
				VectorCopy (r->ent_origin, ent->v.origin);
				VectorCopy (r->ent_mins, ent->v.mins);
				VectorCopy (r->ent_maxs, ent->v.maxs);
			}
			trace = SV_ClipMoveToEntity (ent, r->start, r->mins, r->maxs, r->end);
			sum += trace.fraction;
			if (rep < 0 && (memcmp (&trace.fraction, &r->fraction, 4) || memcmp (trace.endpos, r->endpos, 12)))
				Sys_Error ("EdictTraces: record %i differs from %s", i, path);
		}
	}
	t1 = clock ();
	ns = (double)(t1 - t0) / CLOCKS_PER_SEC * 1e9 / ((double)reps * count);
	Con_Printf ("EdictTraces: %s, %i traces x %i: %.1f ns per trace (checksum %g)\n", sv.name, count, reps, ns, sum);
	free (recs);
	free (bench_data);
}
