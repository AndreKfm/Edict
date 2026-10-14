/*
Copyright (C) 1996-1997 Id Software, Inc.
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: video driver. Derived from WinQuake's vid_null.c; replaces it in the build. The same
320x200 frame buffer, z-buffer and surface cache; in addition it keeps the palette the engine sets
(VID_SetPalette, VID_ShiftPalette), as a display would show the frame through it, and at every VID_Update, the
point where a driver presents the frame, hands the frame buffer and the palette to the screen record of the hash
(edict_hash.c, -edictscreen). Diagnostic: -edictscreendump <first> <last> <file> writes both themselves for
host_framecount first to last:
  record   u32 host_framecount  u8 kind ('V' frame buffer, 'P' palette)  u32 length  byte data[length]
           once per VID_Update, the frame buffer (width x height, row by row) and then the palette (256 x RGB)
*/

#include "quakedef.h"
#include "d_local.h"
#include "edict_hash.h"

viddef_t	vid;				// global video state

#define	BASEWIDTH	320
#define	BASEHEIGHT	200

byte	vid_buffer[BASEWIDTH*BASEHEIGHT];
short	zbuffer[BASEWIDTH*BASEHEIGHT];
byte	surfcache[256*1024];

unsigned short	d_8to16table[256];
unsigned	d_8to24table[256];

static byte	vid_palette[768];		// what the display shows the frame buffer through

static FILE	*screen_dump;
static int	screen_dump_first, screen_dump_last;

void	VID_SetPalette (unsigned char *palette)
{
	memcpy (vid_palette, palette, sizeof (vid_palette));
}

void	VID_ShiftPalette (unsigned char *palette)
{
	memcpy (vid_palette, palette, sizeof (vid_palette));
}

void	VID_Init (unsigned char *palette)
{
	int		i;

	vid.maxwarpwidth = vid.width = vid.conwidth = BASEWIDTH;
	vid.maxwarpheight = vid.height = vid.conheight = BASEHEIGHT;
	vid.aspect = 1.0;
	vid.numpages = 1;
	vid.colormap = host_colormap;
	vid.fullbright = 256 - LittleLong (*((int *)vid.colormap + 2048));
	vid.buffer = vid.conbuffer = vid_buffer;
	vid.rowbytes = vid.conrowbytes = BASEWIDTH;

	d_pzbuffer = zbuffer;
	D_InitCaches (surfcache, sizeof(surfcache));

	VID_SetPalette (palette);
	i = COM_CheckParm ("-edictscreendump");
	if (i && i < com_argc - 3)
	{
		screen_dump = fopen (com_argv[i + 3], "wb");
		if (!screen_dump)
			Sys_Error ("EdictVid: cannot open %s", com_argv[i + 3]);
		screen_dump_first = Q_atoi (com_argv[i + 1]);
		screen_dump_last = Q_atoi (com_argv[i + 2]);
	}
}

void	VID_Shutdown (void)
{
}

static void ScreenDump (int kind, const byte *data, int length)
{
	unsigned char	h[9];
	int				i;

	for (i = 0 ; i < 4 ; i++)
	{
		h[i] = (unsigned char)(host_framecount >> (8 * i));
		h[5 + i] = (unsigned char)(length >> (8 * i));
	}
	h[4] = (unsigned char)kind;
	if (fwrite (h, 1, 9, screen_dump) != 9 || fwrite (data, 1, length, screen_dump) != (size_t)length)
		Sys_Error ("EdictVid: write failed");
}

void	VID_Update (vrect_t *rects)
{
	EdictHash_Screen (vid_buffer, BASEWIDTH, BASEHEIGHT, vid_palette);
	if (screen_dump && host_framecount >= screen_dump_first && host_framecount <= screen_dump_last)
	{
		ScreenDump ('V', vid_buffer, sizeof (vid_buffer));
		ScreenDump ('P', vid_palette, sizeof (vid_palette));
		fflush (screen_dump);
	}
}

/*
================
D_BeginDirectRect
================
*/
void D_BeginDirectRect (int x, int y, byte *pbitmap, int width, int height)
{
}


/*
================
D_EndDirectRect
================
*/
void D_EndDirectRect (int x, int y, int width, int height)
{
}
