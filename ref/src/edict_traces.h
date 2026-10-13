/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: hull trace corpus (.etr), docs/PLAN.md section C (cut 1). Format in edict_traces.c.
*/
#ifndef EDICT_TRACES_H
#define EDICT_TRACES_H

// Writes the loader dump and <count> traces seeded with <seed> for the map that is running (sv.active).
void EdictTraces_Write (char *path, int count, unsigned long long seed);

// Checks the running map's corpus file against id's SV_ClipMoveToEntity, then times reps passes over it.
void EdictTraces_Bench (char *path, int reps);

#endif
