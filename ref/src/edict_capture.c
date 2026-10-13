/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: record mode for the QuakeC VM (docs/TESTING.md, Capture), -edictcapture <file>.

Records every entry of the engine into QuakeC, every QuakeC-to-QuakeC call and every builtin call with its
arguments and return value, as raw 32-bit words. Raw words can hold layout-dependent values (entity byte offsets,
string offsets); entity arguments are therefore also recorded as edict numbers where they are known to be
entities (self, other). Version 1 serves the builtin census of cut 2, step 1 (docs/PLAN.md section C): which
QuakeC functions and builtins a map's spawn and its first frames reach. State snapshots for replaying VM calls
in isolation come with step 2.

File layout (all integers little-endian):
  header  "ECP1"  u32 version
  'M'  char map[64]                                       before the first record on a new map
  'F'  u32 fnum  char name[]  NUL                         first use of a function: its name from the progs
  'E'  u32 fnum  u32 host_framecount  u8 sv.state  u32 self (edict number)  u32 other (edict number)
  'L'  u32 fnum  u32 return[3]
  'C'  u32 fnum
  'B'  u16 builtin  u32 fnum  u8 argc  u32 parm[3 * argc]
  'R'  u16 builtin  u32 return[3]
*/
#include "quakedef.h"
#include "edict_capture.h"

#define ECP_VERSION		1
#define BUFFER_SIZE		(1 << 16)

int		edict_capture;

static FILE				*out;
static unsigned char	buffer[BUFFER_SIZE];
static int				buffered;
static unsigned char	*named;			// [progs->numfunctions]: name already written
static char				named_map[64];	// the map the table is for (progs is reloaded per map, often at the same address)

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

// The function's name on first use; a new map starts a new table and an 'M' record.
static void Name (int fnum)
{
	const char	*s;
	int			i, n;

	if (!named || strncmp (named_map, sv.name, sizeof (named_map)))
	{
		n = strlen (sv.name);
		put8 ('M');
		for (i = 0 ; i < 64 ; i++)
			put8 (i < n ? sv.name[i] : 0);
		free (named);
		named = calloc (progs->numfunctions, 1);
		if (!named)
			Sys_Error ("EdictCapture: out of memory");
		strncpy (named_map, sv.name, sizeof (named_map));
	}
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
	Name (fnum);
	put8 ('E');
	put32 (fnum);
	put32 (host_framecount);
	put8 (sv.state);
	put32 (EdictNumber (pr_global_struct->self));
	put32 (EdictNumber (pr_global_struct->other));
}

void EdictCapture_Leave (int fnum)
{
	put8 ('L');
	put32 (fnum);
	putword (OFS_RETURN); putword (OFS_RETURN + 1); putword (OFS_RETURN + 2);
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
}

void EdictCapture_BuiltinDone (int num)
{
	put8 ('R');
	put16 (num);
	putword (OFS_RETURN); putword (OFS_RETURN + 1); putword (OFS_RETURN + 2);
}
