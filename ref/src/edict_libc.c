/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: pinned C library functions, see edict_libc.h.
*/
#include "edict_libc.h"

#undef strcmp
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
