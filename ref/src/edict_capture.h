/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: record mode for the QuakeC VM (docs/TESTING.md, Capture). The hooks in id's code are the
macros below, one line per capture point (ref/patches/0003); they do nothing unless -edictcapture is given.
Format and records in edict_capture.c.
*/
#ifndef EDICT_CAPTURE_H
#define EDICT_CAPTURE_H

extern int	edict_capture;	// nonzero while a capture file is open

void EdictCapture_Open (char *path);
void EdictCapture_Close (void);
void EdictCapture_Enter (int fnum);					// PR_ExecuteProgram: engine calls QuakeC
void EdictCapture_Leave (int fnum);					// PR_ExecuteProgram returns to the engine
void EdictCapture_Call (int fnum);					// OP_CALL of a QuakeC function
void EdictCapture_Builtin (int num, int fnum);		// OP_CALL of a builtin, before it runs
void EdictCapture_BuiltinDone (int num);			// after it returned

#define EDICT_CAPTURE_ENTER(f)			(edict_capture ? EdictCapture_Enter (f) : (void)0)
#define EDICT_CAPTURE_LEAVE(f)			(edict_capture ? EdictCapture_Leave (f) : (void)0)
#define EDICT_CAPTURE_CALL(f)			(edict_capture ? EdictCapture_Call (f) : (void)0)
#define EDICT_CAPTURE_BUILTIN(n, f)		(edict_capture ? EdictCapture_Builtin (n, f) : (void)0)
#define EDICT_CAPTURE_BUILTIN_DONE(n)	(edict_capture ? EdictCapture_BuiltinDone (n) : (void)0)

#endif
