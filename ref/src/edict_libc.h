/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: C library functions whose results reach the simulation state and whose exact values
the C standard leaves to the implementation. Force-included into every file (build.sh: -include), so id's
sources stay unchanged. The C# port implements the same definitions. docs/PLAN.md D3, section A.1.
*/
#ifndef EDICT_LIBC_H
#define EDICT_LIBC_H

// OP_NE_S stores strcmp's return value as a QuakeC float (pr_exec.c: c->_float = strcmp (...)). The standard
// fixes only the sign: glibc returns the difference of the first differing bytes, MSVC and the sanitizer
// interceptors return -1/0/1. Pinned here to the difference of the first differing bytes as unsigned char.
int edict_strcmp (const char *a, const char *b);
#define strcmp edict_strcmp

#endif
