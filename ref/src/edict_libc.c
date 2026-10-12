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

/*
==============================================================================
sprintf with pinned NaN and infinity output
==============================================================================
*/

#undef sprintf
#undef atof

void Sys_Error (char *error, ...);

static int PinnedSpecial (char *out, const char *spec, double v, char conv)
{
	int			left = 0, plus = 0, space = 0, width = 0, n = 0, len, i;
	const char	*p = spec + 1;
	char		body[8];
	int			upper = conv == 'F' || conv == 'E' || conv == 'G' || conv == 'A';

	for ( ; *p == '-' || *p == '+' || *p == ' ' || *p == '#' || *p == '0' ; p++)
	{
		if (*p == '-') left = 1;
		if (*p == '+') plus = 1;
		if (*p == ' ') space = 1;
	}
	for ( ; *p >= '0' && *p <= '9' ; p++)
		width = width * 10 + (*p - '0');

	if (signbit (v))
		body[n++] = '-';
	else if (plus)
		body[n++] = '+';
	else if (space)
		body[n++] = ' ';
	memcpy (body + n, v != v ? (upper ? "NAN" : "nan") : (upper ? "INF" : "inf"), 3);
	n += 3;

	len = n > width ? n : width;
	if (left)
	{
		memcpy (out, body, n);
		for (i = n ; i < len ; i++) out[i] = ' ';
	}
	else
	{
		for (i = 0 ; i < len - n ; i++) out[i] = ' ';
		memcpy (out + len - n, body, n);
	}
	out[len] = 0;
	return len;
}

int edict_sprintf (char *out, const char *fmt, ...)
{
	va_list		ap;
	char		spec[32];
	char		*o = out;
	const char	*p = fmt, *s;
	int			length;
	double		d;

	va_start (ap, fmt);
	while (*p)
	{
		if (*p != '%')
		{
			*o++ = *p++;
			continue;
		}
		s = p++;
		while (*p == '-' || *p == '+' || *p == ' ' || *p == '#' || *p == '0') p++;
		while (*p >= '0' && *p <= '9') p++;
		if (*p == '.')
			for (p++ ; *p >= '0' && *p <= '9' ; p++) ;
		length = 0;
		if (*p == 'l') { length = 1; p++; }
		else if (*p == 'h') p++;
		if (p - s + 1 >= (int)sizeof (spec))
			Sys_Error ("edict_sprintf: conversion too long in \"%s\"", fmt);
		memcpy (spec, s, p - s + 1);
		spec[p - s + 1] = 0;
		switch (*p)
		{
		case '%':
			*o++ = '%';
			break;
		case 'd': case 'i': case 'c':
			o += length ? sprintf (o, spec, va_arg (ap, long)) : sprintf (o, spec, va_arg (ap, int));
			break;
		case 'u': case 'x': case 'X': case 'o':
			o += length ? sprintf (o, spec, va_arg (ap, unsigned long)) : sprintf (o, spec, va_arg (ap, unsigned int));
			break;
		case 's':
			o += sprintf (o, spec, va_arg (ap, char *));
			break;
		case 'p':
			o += sprintf (o, spec, va_arg (ap, void *));
			break;
		case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A':
			d = va_arg (ap, double);
			o += (d != d || d - d != 0) ? PinnedSpecial (o, spec, d, *p) : sprintf (o, spec, d);
			break;
		default:
			Sys_Error ("edict_sprintf: unsupported conversion '%c' in \"%s\"", *p, fmt);
		}
		p++;
	}
	*o = 0;
	va_end (ap);
	return (int)(o - out);
}

double edict_atof (const char *s)
{
	double		v = strtod (s, NULL);
	unsigned long long	bits;

	if (v != v)
	{
		memcpy (&bits, &v, 8);
		bits = (bits & 0x8000000000000000ULL) | 0x7ff8000000000000ULL;
		memcpy (&v, &bits, 8);
	}
	return v;
}
