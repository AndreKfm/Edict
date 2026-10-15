/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

XXH64 (seed 0), implemented from the xxHash specification. No Quake dependencies, so that it can be
tested on its own against System.IO.Hashing.XxHash64.
*/
#include "edict_hash.h"

/*
==============================================================================
XXH64
==============================================================================
*/

#define P1 0x9E3779B185EBCA87ULL
#define P2 0xC2B2AE3D27D4EB4FULL
#define P3 0x165667B19E3779F9ULL
#define P4 0x85EBCA77C2B2AE63ULL
#define P5 0x27D4EB2F165667C5ULL

static edict_u64 rotl64 (edict_u64 x, int r) { return (x << r) | (x >> (64 - r)); }

static edict_u64 read64 (const unsigned char *p)
{
	edict_u64 v = 0;
	int i;
	for (i = 7 ; i >= 0 ; i--)
		v = (v << 8) | p[i];
	return v;
}

static edict_u64 read32 (const unsigned char *p)
{
	return (edict_u64)p[0] | ((edict_u64)p[1] << 8) | ((edict_u64)p[2] << 16) | ((edict_u64)p[3] << 24);
}

static edict_u64 xxround (edict_u64 acc, edict_u64 input)
{
	acc += input * P2;
	acc = rotl64 (acc, 31);
	return acc * P1;
}

static edict_u64 xxmerge (edict_u64 acc, edict_u64 val)
{
	acc ^= xxround (0, val);
	return acc * P1 + P4;
}

edict_u64 EdictHash_XXH64 (const void *data, int length)
{
	const unsigned char *p = (const unsigned char *)data;
	const unsigned char *end = p + length;
	edict_u64 h;

	if (length >= 32)
	{
		edict_u64 v1 = P1 + P2, v2 = P2, v3 = 0, v4 = 0 - P1;
		const unsigned char *limit = end - 32;
		do
		{
			v1 = xxround (v1, read64 (p)); p += 8;
			v2 = xxround (v2, read64 (p)); p += 8;
			v3 = xxround (v3, read64 (p)); p += 8;
			v4 = xxround (v4, read64 (p)); p += 8;
		} while (p <= limit);
		h = rotl64 (v1, 1) + rotl64 (v2, 7) + rotl64 (v3, 12) + rotl64 (v4, 18);
		h = xxmerge (h, v1);
		h = xxmerge (h, v2);
		h = xxmerge (h, v3);
		h = xxmerge (h, v4);
	}
	else
		h = P5;

	h += (edict_u64)length;
	while (p + 8 <= end)
	{
		h ^= xxround (0, read64 (p));
		h = rotl64 (h, 27) * P1 + P4;
		p += 8;
	}
	if (p + 4 <= end)
	{
		h ^= read32 (p) * P1;
		h = rotl64 (h, 23) * P2 + P3;
		p += 4;
	}
	while (p < end)
	{
		h ^= (*p) * P5;
		h = rotl64 (h, 11) * P1;
		p++;
	}
	h ^= h >> 33;
	h *= P2;
	h ^= h >> 29;
	h *= P3;
	h ^= h >> 32;
	return h;
}
