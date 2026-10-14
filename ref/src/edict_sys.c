/*
Copyright (C) 1996-1997 Id Software, Inc.
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: system driver. Derived from WinQuake's sys_null.c; replaces it in the build.
Differences: a virtual clock instead of 0.1 s per Sys_FloatTime call, isDedicated from -dedicated, and a main
that runs a fixed number of host frames at a fixed frame time and writes the state hashes (edict_hash.c).

Arguments (besides Quake's own):
  -edictframes <n>   run n host frames, then exit (required)
  -edicthash <file>  write per-frame hashes (.edh) to file
  -edictperent       add one hash per edict to every frame record
  -edictmessages <file>
                     write the messages the messages lane hashes (.emg, edict_hash.c); needs -edicthash
  -mem <mb>          hunk size in megabytes (default 32)
  -edicttraces <file> <count> <seed>
                     after the first frame with a running map, write the hull trace corpus (edict_traces.c)
                     instead of continuing, then exit
  -edictcapture <file>
                     record VM entries, QuakeC calls and builtin calls (edict_capture.c)
  -edicttracebench <file> <reps>
                     the same point: check the corpus file against the map, time reps passes over it, exit
  -edictframetime    time host frames 1 to n-1 as one interval (clock (); frame 0 runs the +map command) and
                     print it; for the frame benchmark, run without -edicthash and -edictcapture
  -edictclient       the client without the screen: scr_skipupdate, so SCR_UpdateScreen returns at once (the
                     corpora of P5, whose truth is the client's state alone); for demo playback (+playdemo) without -dedicated. The run ends with the demo (the
                     frame in which CL_StopPlayback ran is the last).
  -edictscreen       the client with the screen: as -edictclient, but SCR_UpdateScreen draws, and every
                     VID_Update writes a screen record (edict_vid.c, edict_hash.c)
  -edictscreendump <first> <last> <file>
                     the frame buffer and palette of every VID_Update for host_framecount first to last
  -edictclientdump <first> <last> <file>
                     the client records' canonical streams for host_framecount first to last (edict_hash.c)
The frame time is fixed by setting host_framerate to EDICT_FRAMETIME before the first frame.
*/
#include "quakedef.h"
#include "errno.h"
#include <time.h>
#include "edict_hash.h"
#include "edict_traces.h"
#include "edict_capture.h"

void EdictTape_UseLoopback (void);	// edict_tape.c

#define EDICT_FRAMETIME	"0.013889"	// 72 Hz, the rate Host_FilterTime is built around

qboolean	isDedicated;
extern cvar_t	host_framerate;	// host.c; not declared in a header

static double	edict_clock;	// virtual time, advanced by the harness once per host frame

/*
===============================================================================

FILE IO (unchanged from sys_null.c)

===============================================================================
*/

#define MAX_HANDLES             10
FILE    *sys_handles[MAX_HANDLES];

int             findhandle (void)
{
	int             i;

	for (i=1 ; i<MAX_HANDLES ; i++)
		if (!sys_handles[i])
			return i;
	Sys_Error ("out of handles");
	return -1;
}

int filelength (FILE *f)
{
	int             pos;
	int             end;

	pos = ftell (f);
	fseek (f, 0, SEEK_END);
	end = ftell (f);
	fseek (f, pos, SEEK_SET);

	return end;
}

int Sys_FileOpenRead (char *path, int *hndl)
{
	FILE    *f;
	int             i;

	i = findhandle ();

	f = fopen(path, "rb");
	if (!f)
	{
		*hndl = -1;
		return -1;
	}
	sys_handles[i] = f;
	*hndl = i;

	return filelength(f);
}

int Sys_FileOpenWrite (char *path)
{
	FILE    *f;
	int             i;

	i = findhandle ();

	f = fopen(path, "wb");
	if (!f)
		Sys_Error ("Error opening %s: %s", path,strerror(errno));
	sys_handles[i] = f;

	return i;
}

void Sys_FileClose (int handle)
{
	fclose (sys_handles[handle]);
	sys_handles[handle] = NULL;
}

void Sys_FileSeek (int handle, int position)
{
	fseek (sys_handles[handle], position, SEEK_SET);
}

int Sys_FileRead (int handle, void *dest, int count)
{
	return fread (dest, 1, count, sys_handles[handle]);
}

int Sys_FileWrite (int handle, void *data, int count)
{
	return fwrite (data, 1, count, sys_handles[handle]);
}

int     Sys_FileTime (char *path)
{
	FILE    *f;

	f = fopen(path, "rb");
	if (f)
	{
		fclose(f);
		return 1;
	}

	return -1;
}

void Sys_mkdir (char *path)
{
}

/*
===============================================================================

SYSTEM IO

===============================================================================
*/

void Sys_MakeCodeWriteable (unsigned long startaddr, unsigned long length)
{
}

void Sys_Error (char *error, ...)
{
	va_list         argptr;

	EdictHash_Close ();
	EdictCapture_Close ();
	fflush (stdout);
	fprintf (stderr, "Sys_Error: ");
	va_start (argptr,error);
	vfprintf (stderr, error,argptr);
	va_end (argptr);
	fprintf (stderr, "\n");
	exit (1);
}

void Sys_Printf (char *fmt, ...)
{
	va_list         argptr;

	va_start (argptr,fmt);
	vprintf (fmt,argptr);
	va_end (argptr);
}

void Sys_Quit (void)
{
	EdictHash_Close ();
	EdictCapture_Close ();
	fflush (stdout);
	exit (0);
}

double Sys_FloatTime (void)
{
	return edict_clock;
}

char *Sys_ConsoleInput (void)
{
	return NULL;
}

void Sys_Sleep (void)
{
}

void Sys_SendKeyEvents (void)
{
}

void Sys_HighFPPrecision (void)
{
}

void Sys_LowFPPrecision (void)
{
}

//=============================================================================

static int ArgValue (char *name, int fallback)
{
	int	i = COM_CheckParm (name);

	return (i && i < com_argc - 1) ? Q_atoi (com_argv[i + 1]) : fallback;
}

int main (int argc, char **argv)
{
	static quakeparms_t	parms;
	int		frames, frame, before, i, traces, bench, timed, played, client;
	unsigned int	client_seed;
	clock_t	start = 0;

	COM_InitArgv (argc, argv);

	parms.memsize = ArgValue ("-mem", 32) * 1024 * 1024;
	parms.membase = malloc (parms.memsize);
	if (!parms.membase)
		Sys_Error ("cannot allocate %i bytes", parms.memsize);
	parms.basedir = ".";
	parms.argc = com_argc;
	parms.argv = com_argv;

	isDedicated = COM_CheckParm ("-dedicated") != 0;
	if (!isDedicated)
		EdictTape_UseLoopback ();	// a listen server's local client connects through net_loop.c
	frames = ArgValue ("-edictframes", 0);
	if (frames <= 0)
		Sys_Error ("-edictframes <n> is required");
	traces = COM_CheckParm ("-edicttraces");
	if (traces && traces >= com_argc - 3)
		Sys_Error ("-edicttraces <file> <count> <seed>");
	timed = COM_CheckParm ("-edictframetime") != 0;
	played = 0;
	bench = COM_CheckParm ("-edicttracebench");
	if (bench && bench >= com_argc - 2)
		Sys_Error ("-edicttracebench <file> <reps>");
	i = COM_CheckParm ("-edicthash");
	if (i && i < com_argc - 1)
		EdictHash_Open (com_argv[i + 1], COM_CheckParm ("-edictperent") ? EDH_FLAG_PER_ENTITY : 0);
	i = COM_CheckParm ("-edictclientdump");
	if (i && i < com_argc - 3)
		EdictHash_OpenClientDump (com_argv[i + 3], Q_atoi (com_argv[i + 1]), Q_atoi (com_argv[i + 2]));
	i = COM_CheckParm ("-edictmessages");
	if (i && i < com_argc - 1)
		EdictHash_OpenMessages (com_argv[i + 1]);

	i = COM_CheckParm ("-edictcapture");
	if (i && i < com_argc - 1)
		EdictCapture_Open (com_argv[i + 1]);

	if (COM_CheckParm ("-edictclient"))
		scr_skipupdate = true;
	Host_Init (&parms);
	Cvar_Set ("host_framerate", EDICT_FRAMETIME);
	client = COM_CheckParm ("-edictclient") != 0 || COM_CheckParm ("-edictscreen") != 0;
	client_seed = edict_rand_state ();

	for (frame = 0 ; frame < frames ; frame++)
	{
		if (timed && frame == 1)
			start = clock ();
		edict_clock += host_framerate.value;
		before = host_framecount;
		Host_Frame (host_framerate.value);
		if (host_framecount != before + 1)
			Sys_Error ("host frame %i did not run (filtered or aborted)", frame);
		if (client && frame == 0)
			EdictHash_ClientStart (client_seed);	// frame 0 ran quake.rc (the configs) and +playdemo
		EdictHash_ClientFrame ();
		if (cls.demoplayback)
			played = 1;
		else if (played)
			break;		// the demo ended: the run ends with it, before CL_NextDemo plays the game's own demos
		if (sv.active)
		{
			EdictHash_Frame ();
			EdictHash_DumpGlobals ();
			if (traces)
			{
				EdictTraces_Write (com_argv[traces + 1], Q_atoi (com_argv[traces + 2]),
					strtoull (com_argv[traces + 3], NULL, 0));
				break;
			}
			if (bench)
			{
				EdictTraces_Bench (com_argv[bench + 1], Q_atoi (com_argv[bench + 2]));
				break;
			}
		}
	}

	if (timed && frame > 1)
	{
		double	seconds = (double)(clock () - start) / CLOCKS_PER_SEC;

		printf ("edict: %d host frames in %.6f s, %.3f us per frame\n", frame - 1, seconds, seconds * 1e6 / (frame - 1));
	}
	EdictHash_Close ();
	EdictCapture_Close ();
	fflush (stdout);
	return 0;
}
