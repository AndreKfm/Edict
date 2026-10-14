/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: per-frame state hashes (.edh), docs/PLAN.md section E.1.
*/
#ifndef EDICT_HASH_H
#define EDICT_HASH_H

typedef unsigned long long edict_u64;

// XXH64 (seed 0), implemented from the xxHash specification; checked against System.IO.Hashing.XxHash64.
edict_u64 EdictHash_XXH64 (const void *data, int length);

// Opens the .edh output; flags: EDH_FLAG_PER_ENTITY adds one hash per edict to every frame record.
#define EDH_FLAG_PER_ENTITY	1
void EdictHash_Open (char *path, int flags);
// Diagnostic: -edictmessages <file>, the messages of the messages lane (.emg); after EdictHash_Open.
void EdictHash_OpenMessages (char *path);
// Called by the tape driver for every message the server sends: kind 1 reliable, 2 unreliable.
void EdictHash_Sent (int kind, const void *data, int length);
// Called after every host frame that ran while sv.active; writes one frame record.
void EdictHash_Frame (void);
// Called once after host frame 0 with -edictclient: the client's inputs (the rand state before frame 0, the cvars).
void EdictHash_ClientStart (unsigned int rand_before);
// Diagnostic: -edictclientdump <first> <last> <file>, the client records' canonical streams for those frames.
void EdictHash_OpenClientDump (char *path, int first, int last);
// Called after every host frame; writes one client record while the client is connected (demo or listen server).
void EdictHash_ClientFrame (void);
// Called after ED_LoadFromFile in SV_SpawnServer (patch 0005): the spawn records of the hash and the capture.
void Edict_Spawned (void);
// Diagnostic: -edictdumpglobals <framecount> <file>
void EdictHash_DumpGlobals (void);
void EdictHash_Close (void);

#endif
