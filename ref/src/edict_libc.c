/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: pinned C library functions, see edict_libc.h.
*/
#include "edict_libc.h"

int edict_strcmp (const char *a, const char *b)
{
	const unsigned char	*x = (const unsigned char *)a;
	const unsigned char	*y = (const unsigned char *)b;

	while (*x && *x == *y)
	{
		x++;
		y++;
	}
	return *x - *y;
}

static unsigned int	rand_seed = 1;

int edict_rand (void)
{
	rand_seed = rand_seed * 214013u + 2531011u;
	return (int)((rand_seed >> 16) & 0x7fff);
}

void edict_srand (unsigned int seed)
{
	rand_seed = seed;
}
