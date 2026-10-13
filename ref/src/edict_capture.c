/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: record mode for the QuakeC VM (docs/TESTING.md, Capture), -edictcapture <file>.

Records every entry of the engine into QuakeC, every QuakeC-to-QuakeC call and every builtin call, and at each of
these boundaries what changed in the memory the VM sees: the progs globals and the fields of every edict in use.
A shadow copy of that memory is kept; each boundary record carries the words that differ from it (then the shadow
is updated). So the stream says, in order:
  'E'  what the engine changed before entering QuakeC          (the replay applies it)
  'B'  what the VM changed up to a builtin call                 (the replay's VM must have changed exactly that)
  'R'  what the builtin changed                                 (the replay applies it; the builtin is not run)
  'L'  what the VM changed up to its return to the engine       (checked like 'B')
A builtin that runs QuakeC itself (touch functions from walkmove, for example) shows as nested 'E' ... 'L' between
its 'B' and 'R'. Replaying the stream runs only the VM: cut 2, step 2 (docs/PLAN.md section C).

Engine state the builtins read but the VM does not see is recorded too: the rand seed (engine and builtins draw
from one stream) and sv.time at every entry and after every builtin, and per map the cvars and the offset of
pr_string_temp, where ftos and vtos write.

Words are raw 32-bit values. With the reference's edict stride in the port (docs/adr/D7) entity offsets and field
pointers are equal on both sides. String offsets are not, for strings outside the progs string block (allocated
on the hunk by the engine): their contents are recorded wherever a string-typed word that changed points to one,
and after every builtin whose return value points to pr_string_temp (ftos, vtos).

File layout (all integers little-endian):
  header  "ECP1"  u32 version
  'M'  char map[64]  u32 progs_crc  u32 numglobals  u32 entityfields  u32 edict_header_bytes  u32 max_edicts
       u32 numstrings  i32 string_temp (pr_string_temp - pr_strings)
       u32 numcvars  numcvars x { char name[] NUL  char string[] NUL }
                                                          before the first record on a new map; resets the shadow
  'F'  u32 fnum  char name[]  NUL                         first use of a function: its name from the progs
  'E'  u32 fnum  u32 host_framecount  u8 sv.state  u32 self (edict number)  u32 other (edict number)
       u32 rand_seed  f64 sv.time  diff
  'L'  u32 fnum  u32 return[3]  diff
  'C'  u32 fnum
  'B'  u16 builtin  u32 fnum  u8 argc  u32 parm[3 * argc]  diff
  'R'  u16 builtin  u32 return[3]  u32 rand_seed  f64 sv.time  diff
  diff:  u32 num_edicts  u32 n  n x { u32 word  u32 value }  u32 m  m x { i32 string offset  char text[]  NUL }
         word w < numglobals is global w; otherwise edict (w - numglobals) / entityfields,
         field word (w - numglobals) % entityfields
*/
#include "quakedef.h"
#include "edict_capture.h"

#define ECP_VERSION		3
#define BUFFER_SIZE		(1 << 16)

extern unsigned short	pr_crc;				// pr_edict.c
extern char				*pr_string_temp;	// pr_cmds.c (on the hunk, patch 0002)

int		edict_capture;

static FILE				*out;
static unsigned char	buffer[BUFFER_SIZE];
static int				buffered;

static char				map_name[64];		// the map the tables below are for
static unsigned char	*named;				// [numfunctions]: name already written
static unsigned int		*shadow;			// [numglobals + max_edicts * entityfields]
static unsigned char	*global_string;		// [numglobals]: covered by an ev_string globaldef
static unsigned char	*field_string;		// [entityfields]: covered by an ev_string fielddef
static int				*changed;			// scratch: indices of the words of one diff

static void Flush (void)
{
	if (buffered && fwrite (buffer, 1, buffered, out) != (size_t)buffered)
		Sys_Error ("EdictCapture: write failed");
	buffered = 0;
}

static void put8 (int v)
{
	if (buffered == BUFFER_SIZE)
		Flush ();
	buffer[buffered++] = (unsigned char)v;
}

static void put16 (int v)
{
	put8 (v); put8 (v >> 8);
}

static void put32 (unsigned int v)
{
	put8 (v); put8 (v >> 8); put8 (v >> 16); put8 (v >> 24);
}

static void putword (int ofs)
{
	put32 (((unsigned int *)pr_globals)[ofs]);
}

static void put64 (unsigned long long v)
{
	put32 ((unsigned int)v); put32 ((unsigned int)(v >> 32));
}

static void putdouble (double d)
{
	unsigned long long	bits;

	memcpy (&bits, &d, 8);
	put64 (bits);
}

static void putname (const char *s)
{
	for ( ; *s ; s++)
		put8 (*s);
	put8 (0);
}

// The engine state the builtins read besides the VM's memory.
static void putengine (void)
{
	put32 (edict_rand_state ());
	putdouble (sv.time);
}

static void putstring (int ofs)
{
	const char	*s;

	put32 (ofs);
	for (s = pr_strings + ofs ; *s ; s++)
		put8 (*s);
	put8 (0);
}

void EdictCapture_Open (char *path)
{
	out = fopen (path, "wb");
	if (!out)
		Sys_Error ("EdictCapture: cannot open %s", path);
	put8 ('E'); put8 ('C'); put8 ('P'); put8 ('1');
	put32 (ECP_VERSION);
	edict_capture = 1;
}

void EdictCapture_Close (void)
{
	if (!out)
		return;
	Flush ();
	fclose (out);
	out = NULL;
	edict_capture = 0;
}

static void *Alloc (int size)
{
	void	*p = calloc (size, 1);

	if (!p)
		Sys_Error ("EdictCapture: out of memory");
	return p;
}

static void MarkStrings (unsigned char *types, int count, ddef_t *defs, int numdefs)
{
	int		i;

	for (i = 0 ; i < numdefs ; i++)
		if ((defs[i].type & ~DEF_SAVEGLOBAL) == ev_string && defs[i].ofs < count)
			types[defs[i].ofs] = 1;
}

// A new map: new progs (reloaded per map, often at the same address), new tables, a zero shadow.
static void NewMap (void)
{
	cvar_t	*var;
	int		i, n = strlen (sv.name);
	int		words = progs->numglobals + sv.max_edicts * progs->entityfields;

	free (named); free (shadow); free (global_string); free (field_string); free (changed);
	named = Alloc (progs->numfunctions);
	shadow = Alloc (words * 4);
	changed = Alloc (words * sizeof (int));
	global_string = Alloc (progs->numglobals);
	field_string = Alloc (progs->entityfields);
	MarkStrings (global_string, progs->numglobals, pr_globaldefs, progs->numglobaldefs);
	MarkStrings (field_string, progs->entityfields, pr_fielddefs, progs->numfielddefs);
	strncpy (map_name, sv.name, sizeof (map_name));

	put8 ('M');
	for (i = 0 ; i < 64 ; i++)
		put8 (i < n ? sv.name[i] : 0);
	put32 (pr_crc);
	put32 (progs->numglobals);
	put32 (progs->entityfields);
	put32 (pr_edict_size - progs->entityfields * 4);
	put32 (sv.max_edicts);
	put32 (progs->numstrings);
	put32 (pr_string_temp - pr_strings);
	for (n = 0, var = cvar_vars ; var ; var = var->next)
		n++;
	put32 (n);
	for (var = cvar_vars ; var ; var = var->next)
	{
		putname (var->name);
		putname (var->string);
	}
}

static void CheckMap (void)
{
	if (!named || strncmp (map_name, sv.name, sizeof (map_name)))
		NewMap ();
}

static unsigned int *Word (int w)
{
	int		e, f;

	if (w < progs->numglobals)
		return (unsigned int *)pr_globals + w;
	e = (w - progs->numglobals) / progs->entityfields;
	f = (w - progs->numglobals) % progs->entityfields;
	return (unsigned int *)&EDICT_NUM (e)->v + f;
}

static qboolean IsString (int w)
{
	if (w < progs->numglobals)
		return global_string[w];
	return field_string[(w - progs->numglobals) % progs->entityfields];
}

static qboolean EngineString (int ofs)
{
	return ofs < 0 || ofs >= progs->numstrings;
}

// The words that differ from the shadow, the engine strings they name, and extra (unless -1): a string offset
// whose text is recorded in any case.
static void Diff (int extra)
{
	int				n = 0, m = 0, i, w, end;
	unsigned int	*p;

	end = progs->numglobals + sv.num_edicts * progs->entityfields;
	for (w = 0 ; w < end ; w++)
	{
		p = Word (w);
		if (*p != shadow[w])
		{
			shadow[w] = *p;
			changed[n++] = w;
		}
	}
	put32 (sv.num_edicts);
	put32 (n);
	for (i = 0 ; i < n ; i++)
	{
		put32 (changed[i]);
		put32 (shadow[changed[i]]);
	}
	for (i = 0 ; i < n ; i++)
		if (IsString (changed[i]) && EngineString ((int)shadow[changed[i]]))
			m++;
	if (extra != -1)
		m++;
	put32 (m);
	for (i = 0 ; i < n ; i++)
		if (IsString (changed[i]) && EngineString ((int)shadow[changed[i]]))
			putstring ((int)shadow[changed[i]]);
	if (extra != -1)
		putstring (extra);
}

static void Name (int fnum)
{
	const char	*s;

	if (fnum < 0 || fnum >= progs->numfunctions || named[fnum])
		return;
	named[fnum] = 1;
	put8 ('F');
	put32 (fnum);
	for (s = pr_strings + pr_functions[fnum].s_name ; *s ; s++)
		put8 (*s);
	put8 (0);
}

static int EdictNumber (int e)
{
	return NUM_FOR_EDICT (PROG_TO_EDICT (e));
}

void EdictCapture_Enter (int fnum)
{
	CheckMap ();
	Name (fnum);
	put8 ('E');
	put32 (fnum);
	put32 (host_framecount);
	put8 (sv.state);
	put32 (EdictNumber (pr_global_struct->self));
	put32 (EdictNumber (pr_global_struct->other));
	putengine ();
	Diff (-1);
}

void EdictCapture_Leave (int fnum)
{
	put8 ('L');
	put32 (fnum);
	putword (OFS_RETURN); putword (OFS_RETURN + 1); putword (OFS_RETURN + 2);
	Diff (-1);
}

void EdictCapture_Call (int fnum)
{
	Name (fnum);
	put8 ('C');
	put32 (fnum);
}

void EdictCapture_Builtin (int num, int fnum)
{
	int		i;

	Name (fnum);
	put8 ('B');
	put16 (num);
	put32 (fnum);
	put8 (pr_argc);
	for (i = 0 ; i < pr_argc * 3 ; i++)
		putword (OFS_PARM0 + i);
	Diff (-1);
}

void EdictCapture_BuiltinDone (int num)
{
	int		ret = G_INT (OFS_RETURN);

	put8 ('R');
	put16 (num);
	putword (OFS_RETURN); putword (OFS_RETURN + 1); putword (OFS_RETURN + 2);
	putengine ();
	Diff (pr_strings + ret == pr_string_temp ? ret : -1);
}
